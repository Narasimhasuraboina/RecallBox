using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RecallBox.App;

public sealed class ArchiveService(WhatsAppUiSource source, OfficialExportImporter importer, ILogger<ArchiveService> logger)
{
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly ConcurrentDictionary<string, string> _stagedImports = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();
    private readonly SemaphoreSlim _readerLock = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLines = new(JsonSerializerDefaults.Web);

    public IReadOnlyCollection<DownloadJob> Jobs => _jobs.Values.OrderByDescending(j => j.StartedAt).ToArray();
    public DownloadJob? Get(string id) => _jobs.GetValueOrDefault(id);

    public DownloadJob Start(string phone, string destination)
    {
        var digits = Regex.Replace(phone, "\\D", "");
        var normalized = "+" + digits;
        var root = Path.GetFullPath(destination);
        var folder = "Contact-" + digits;
        var job = new DownloadJob { PhoneNumber = normalized, DestinationFolder = root, ContactFolder = folder };
        _jobs[job.Id] = job;
        var cancellation = new CancellationTokenSource();
        _cancellations[job.Id] = cancellation;
        _ = Task.Run(() => RunAsync(job, cancellation.Token));
        return job;
    }

    public bool Cancel(string id) => _cancellations.TryGetValue(id, out var cancellation) && !cancellation.IsCancellationRequested && CancelCore(cancellation);

    private static bool CancelCore(CancellationTokenSource cancellation)
    {
        try { cancellation.Cancel(); return true; }
        catch (ObjectDisposedException) { return false; }
    }

    public DownloadJob StartImport(string phone, string destination, string stagedFile)
    {
        var digits = Regex.Replace(phone, "\\D", "");
        var root = Path.GetFullPath(destination);
        var job = new DownloadJob
        {
            PhoneNumber = "+" + digits,
            DestinationFolder = root,
            ContactFolder = "Contact-" + digits,
            AcquisitionMethod = "Official WhatsApp chat export file"
        };
        _jobs[job.Id] = job;
        _stagedImports[job.Id] = stagedFile;
        _ = Task.Run(() => RunImportAsync(job, stagedFile));
        return job;
    }

    public DownloadJob? Retry(string id)
    {
        var previous = Get(id);
        if (previous is null) return null;
        return _stagedImports.TryGetValue(id, out var importPath) && File.Exists(importPath)
            ? StartImport(previous.PhoneNumber, previous.DestinationFolder, importPath)
            : Start(previous.PhoneNumber, previous.DestinationFolder);
    }

    private async Task RunImportAsync(DownloadJob job, string stagedFile)
    {
        await _readerLock.WaitAsync();
        try
        {
            job.Status = "running";
            job.Phase = "Parsing official WhatsApp export";
            var archive = Path.Combine(job.DestinationFolder, job.ContactFolder);
            job.ResultPath = archive;
            Directory.CreateDirectory(archive);
            Directory.CreateDirectory(Path.Combine(archive, ".recallbox"));
            foreach (var directory in new[] { "images", "videos", "audio", "documents", "other" })
                Directory.CreateDirectory(Path.Combine(archive, "media", directory));

            var imported = await importer.ImportAsync(stagedFile, archive, (lines, messages) =>
            {
                job.Phase = $"Parsing export: {lines:N0} lines, {messages:N0} messages";
                job.MessagesFound = messages;
            });
            var indexPath = Path.Combine(archive, ".recallbox", "messages.jsonl");
            var existing = LoadRecords(indexPath);
            PersistRecords(indexPath, existing); // Also migrates older pretty-printed checkpoint files.
            var known = existing.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            var added = new List<ArchiveMessage>();
            foreach (var message in imported.Messages)
            {
                if (!known.Add(message.Id)) continue;
                message.Sequence = existing.Count == 0 ? message.Sequence : existing.Max(m => m.Sequence) + 1;
                existing.Add(message);
                added.Add(message);
            }
            AppendRecords(indexPath, added);
            job.MessagesSaved = existing.Count;
            job.MessagesFound = imported.Messages.Count;
            job.AttachmentsSaved = imported.AttachmentsSaved;
            job.ItemsSkipped = imported.ItemsSkipped;
            job.PagesRead = 1;
            foreach (var warning in imported.Warnings) AddWarning(job, warning);
            job.Coverage = "source-export-file";
            job.Phase = "Writing archive files";
            WriteArchive(archive, job, existing);
            job.Status = "completed-with-limitations";
            job.FinishedAt = DateTimeOffset.Now;
            job.Phase = "Export archived; this does not prove full account history";
            WriteMetadata(archive, job, existing);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Export import job {JobId} failed", job.Id);
            job.Errors.Enqueue(ex.Message);
            job.Status = "failed";
            job.Phase = "Export import failed; retry by selecting the file again";
            job.FinishedAt = DateTimeOffset.Now;
        }
        finally
        {
            try
            {
                if (job.Status != "failed")
                {
                    File.Delete(stagedFile);
                    foreach (var item in _stagedImports.Where(pair => pair.Value == stagedFile).ToArray()) _stagedImports.TryRemove(item.Key, out _);
                }
            }
            catch { }
            _readerLock.Release();
        }
    }

    private async Task RunAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        await _readerLock.WaitAsync();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            job.Status = "running";
            job.Phase = "Opening WhatsApp UI";
            var archive = Path.Combine(job.DestinationFolder, job.ContactFolder);
            job.ResultPath = archive;
            var internalDir = Path.Combine(archive, ".recallbox");
            Directory.CreateDirectory(internalDir);
            foreach (var directory in new[] { "images", "videos", "audio", "documents", "other" })
                Directory.CreateDirectory(Path.Combine(archive, "media", directory));

            var indexPath = Path.Combine(internalDir, "messages.jsonl");
            var records = LoadRecords(indexPath);
            PersistRecords(indexPath, records); // Rewrite legacy checkpoints as valid one-object-per-line JSONL.
            var seenIds = records.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            job.MessagesSaved = records.Count;
            var sequence = records.Count == 0 ? 0 : records.Max(r => r.Sequence) + 1;

            await foreach (var page in source.ReadChatAsync(job.PhoneNumber, cancellationToken))
            {
                job.PagesRead++;
                job.Phase = $"Reading accessible conversation view {page.PageNumber}";
                if (!string.IsNullOrWhiteSpace(page.Warning)) AddWarning(job, page.Warning);
                job.MessagesFound += page.Messages.Count;
                var added = new List<ArchiveMessage>();
                var occurrence = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var item in page.Messages)
                {
                    var key = item.Text.Trim();
                    var isOcr = item.ControlType.Equals("Visible OCR", StringComparison.OrdinalIgnoreCase);
                    var identity = isOcr && !string.IsNullOrWhiteSpace(item.TimestampText) && !string.IsNullOrWhiteSpace(item.Sender)
                        ? string.Join("\n", key, item.TimestampText.Trim(), item.Sender.Trim())
                        : key;
                    var ordinal = occurrence.GetValueOrDefault(identity);
                    occurrence[identity] = ordinal + 1;
                    var id = isOcr && !string.IsNullOrWhiteSpace(item.TimestampText) && !string.IsNullOrWhiteSpace(item.Sender)
                        ? Fingerprint("ocr-stable\n" + identity + "\n#" + ordinal)
                        : Fingerprint(identity + "\n#" + ordinal + (isOcr ? $"\npage:{page.PageNumber}" : ""));
                    if (!seenIds.Add(id)) continue;
                    var limitation = item.UnsupportedReason;
                    if (limitation is not null) job.ItemsSkipped++;
                    var mediaCategory = WhatsAppUiSource.GetMediaCategory(item.Text);
                    var metadata = Regex.IsMatch(item.Text, @"\b(edited|replied|replying to|quoted)\b", RegexOptions.IgnoreCase) ? item.Text : null;
                    var record = new ArchiveMessage
                    {
                        Id = id,
                        Sequence = sequence++,
                        SourcePageNumber = page.PageNumber,
                        Text = item.Text,
                        Sender = item.Sender,
                        TimestampText = item.TimestampText,
                        ReplyOrEditMetadata = metadata,
                        MessageType = mediaCategory ?? "text-or-unknown",
                        SourceControlType = item.ControlType,
                        SourceAutomationId = item.AutomationId,
                        Media = limitation is null || mediaCategory is null ? [] : [new ArchiveMedia { Category = mediaCategory, Downloaded = false, Limitation = limitation }],
                        Limitation = limitation
                    };
                    if (item.ControlType.Equals("Visible OCR", StringComparison.OrdinalIgnoreCase))
                    {
                        job.AcquisitionMethod = "WhatsApp Desktop visible-window OCR (best effort)";
                        if (page.PageNumber == 1)
                            AddWarning(job, "OCR has no stable message IDs. Exact matches with a visible timestamp and sender direction are deduplicated across overlapping views while preserving repeated copies within one view; records without both fields remain page-scoped and may duplicate.");
                    }
                    records.Add(record);
                    added.Add(record);
                    job.MessagesSaved = records.Count;
                }
                AppendRecords(indexPath, added);
                job.ResultPath = archive;
            }

            job.Phase = "Writing archive files";
            WriteArchive(archive, job, records);
            job.Coverage = "unknown";
            job.Status = "completed-with-limitations";
            job.Phase = "Finished with coverage unknown";
            job.FinishedAt = DateTimeOffset.Now;
            WriteMetadata(archive, job, records);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            job.Status = "cancelled-with-checkpoint";
            job.Phase = "Cancelled; captured messages are checkpointed and can be resumed";
            job.Coverage = "unknown";
            job.FinishedAt = DateTimeOffset.Now;
            if (job.ResultPath is not null)
            {
                try
                {
                    var records = LoadRecords(Path.Combine(job.ResultPath, ".recallbox", "messages.jsonl"));
                    job.MessagesSaved = records.Count;
                    WriteArchive(job.ResultPath, job, records);
                    WriteMetadata(job.ResultPath, job, records);
                }
                catch (Exception ex) { logger.LogWarning(ex, "Could not write the cancelled archive summary for job {JobId}", job.Id); }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Archive job {JobId} failed", job.Id);
            job.Errors.Enqueue(ex.Message);
            job.Status = "failed";
            job.Phase = "Failed; retry is available";
            job.FinishedAt = DateTimeOffset.Now;
            if (job.ResultPath is not null)
            {
                try
                {
            var records = LoadRecords(Path.Combine(job.ResultPath, ".recallbox", "messages.jsonl"));
                    WriteArchive(job.ResultPath, job, records);
                    WriteMetadata(job.ResultPath, job, records);
                }
                catch { }
            }
        }
        finally
        {
            _readerLock.Release();
            if (_cancellations.TryRemove(job.Id, out var cancellation)) cancellation.Dispose();
        }
    }

    private static List<ArchiveMessage> LoadRecords(string path)
    {
        var records = new List<ArchiveMessage>();
        if (!File.Exists(path)) return records;
        try
        {
            var compactLines = true;
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var item = JsonSerializer.Deserialize<ArchiveMessage>(line, Json);
                    if (item is not null) records.Add(item);
                }
                catch (JsonException) { compactLines = false; break; }
            }
            if (compactLines) return NormalizeOcrDuplicates(records);
        }
        catch (IOException) { }

        // Migrate checkpoints created by older versions, which wrote each JSON
        // object with indentation and therefore split it across multiple lines.
        try { return NormalizeOcrDuplicates(ParseConcatenatedRecords(File.ReadAllText(path))); }
        catch (IOException) { }
        return records;
    }

    private static List<ArchiveMessage> NormalizeOcrDuplicates(List<ArchiveMessage> records)
    {
        // Old OCR checkpoints used page-scoped IDs. Recover append-order scan runs,
        // then deduplicate only records with both visible timestamp and direction.
        var normalized = new List<ArchiveMessage>(records.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var perPage = new Dictionary<(int Run, int Page, string Signature), int>();
        var run = 0;
        var previousPage = -1;
        foreach (var record in records)
        {
            var isOcr = record.SourceControlType.Equals("Visible OCR", StringComparison.OrdinalIgnoreCase);
            if (isOcr && previousPage >= 0 && record.SourcePageNumber < previousPage)
            {
                run++;
                perPage.Clear();
            }
            if (isOcr) previousPage = record.SourcePageNumber;

            if (isOcr && !string.IsNullOrWhiteSpace(record.TimestampText) && !string.IsNullOrWhiteSpace(record.Sender))
            {
                var signature = string.Join("\n", record.Text.Trim(), record.TimestampText.Trim(), record.Sender.Trim());
                var pageKey = (run, record.SourcePageNumber, signature);
                var ordinal = perPage.GetValueOrDefault(pageKey);
                perPage[pageKey] = ordinal + 1;
                var id = Fingerprint("ocr-stable\n" + signature + "\n#" + ordinal);
                if (!seen.Add(id)) continue;
                record.Id = id;
            }
            else if (!seen.Add(record.Id)) continue;
            normalized.Add(record);
        }
        return normalized;
    }

    private static List<ArchiveMessage> ParseConcatenatedRecords(string content)
    {
        var records = new List<ArchiveMessage>();
        var start = -1;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];
            if (start < 0)
            {
                if (character == '{') { start = index; depth = 1; }
                continue;
            }
            if (inString)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }
            if (character == '"') inString = true;
            else if (character == '{') depth++;
            else if (character == '}' && --depth == 0)
            {
                try
                {
                    var item = JsonSerializer.Deserialize<ArchiveMessage>(content[start..(index + 1)], Json);
                    if (item is not null) records.Add(item);
                }
                catch (JsonException) { }
                start = -1;
            }
        }
        return records;
    }

    private static void PersistRecords(string path, IReadOnlyCollection<ArchiveMessage> records)
    {
        var content = string.Join(Environment.NewLine, records.Select(record => JsonSerializer.Serialize(record, JsonLines)));
        if (records.Count > 0) content += Environment.NewLine;
        SettingsStore.AtomicWrite(path, content);
    }

    private static void AppendRecords(string path, IReadOnlyCollection<ArchiveMessage> records)
    {
        if (records.Count == 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = string.Join(Environment.NewLine, records.Select(record => JsonSerializer.Serialize(record, JsonLines))) + Environment.NewLine;
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 16 * 1024, leaveOpen: true);
        writer.Write(content);
        writer.Flush();
        stream.Flush(true);
    }

    private static void AddWarning(DownloadJob job, string warning)
    {
        if (!job.Warnings.Any(existing => existing.Equals(warning, StringComparison.Ordinal)))
            job.Warnings.Enqueue(warning);
    }

    private static void WriteArchive(string archive, DownloadJob job, List<ArchiveMessage> records)
    {
        Directory.CreateDirectory(archive);
        var sorted = records.OrderByDescending(r => r.SourcePageNumber).ThenBy(r => r.Sequence).ToList();
        var exported = new
        {
            contactIdentifier = job.PhoneNumber,
            acquisitionMethod = job.AcquisitionMethod,
            ordering = job.AcquisitionMethod.Contains("export", StringComparison.OrdinalIgnoreCase)
                ? "Original transcript order from the imported export; parsed local timestamps are included when available."
                : "Messages retain their order in the currently visible WhatsApp view. Older and newer history outside that view was not collected; full dates could not be verified from available UI elements.",
            messages = sorted
        };
        SettingsStore.AtomicWrite(Path.Combine(archive, "messages.json"), JsonSerializer.Serialize(exported, Json));

        var text = new StringBuilder();
        foreach (var message in sorted)
        {
            text.Append('[').Append(message.TimestampText ?? "time unavailable").Append("] ")
                .Append(message.Sender ?? "sender unavailable").Append(": ").AppendLine(message.Text);
            foreach (var media in message.Media)
                text.Append("    ").Append(media.Downloaded ? "attachment saved" : "attachment unavailable")
                    .Append(": ").AppendLine(media.RelativePath ?? media.FileName ?? media.Limitation ?? "unknown file");
        }
        SettingsStore.AtomicWrite(Path.Combine(archive, "messages.txt"), text.ToString());

        var isExport = job.AcquisitionMethod.Contains("export", StringComparison.OrdinalIgnoreCase);
        var isOcr = job.AcquisitionMethod.Contains("OCR", StringComparison.OrdinalIgnoreCase);
        var notice = isExport
            ? "Imported from an official WhatsApp chat export. This archive covers only the content included in that export and does not establish complete account history. See metadata.json."
            : isOcr
                ? "Best-effort archive from OCR of visible WhatsApp screens. OCR text, inferred direction, media, and history coverage may be incomplete. See metadata.json."
                : "Best-effort archive from a linked Windows UI. Message history completeness, chronology, sender labels, and media availability are not guaranteed. See metadata.json.";
        var html = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>WhatsApp archive</title><style>body{font:16px system-ui;margin:0;background:#e9e5dc;color:#17201b}.wrap{max-width:900px;margin:auto;padding:24px}.notice{padding:14px;background:#fff3cd;border-radius:12px;margin-bottom:16px}.day{margin:22px auto 10px;width:max-content;background:#d6e8df;padding:5px 12px;border-radius:20px;font-size:12px}.message{background:#fff;border-radius:12px;padding:12px 16px;margin:8px 0;max-width:78%;box-shadow:0 1px 2px #0002;white-space:pre-wrap;overflow-wrap:anywhere}.sender{font-weight:700;color:#087e72}.time{color:#68736f;font-size:12px;margin-top:8px}.media{font-size:13px;color:#805c00}.media img,.media video{display:block;max-width:100%;max-height:420px;border-radius:8px;margin:7px 0}.media a{color:#087e72}@media(max-width:600px){.wrap{padding:12px}.message{max-width:92%}}</style></head><body><main class=\"wrap\"><h1>WhatsApp chat archive</h1><div class=\"notice\">" );
        html.Append(HtmlEncoder.Default.Encode(notice)).Append("</div>");
        string? currentDay = null;
        foreach (var message in sorted)
        {
            var day = message.LocalTimestamp?.ToString("yyyy-MM-dd") ?? "Date unavailable";
            if (day != currentDay)
            {
                currentDay = day;
                html.Append("<div class=\"day\">").Append(HtmlEncoder.Default.Encode(day)).Append("</div>");
            }
            html.Append("<article class=\"message\"><div class=\"sender\">").Append(HtmlEncoder.Default.Encode(message.Sender ?? "Sender unavailable"))
                .Append("</div><div>").Append(HtmlEncoder.Default.Encode(message.Text)).Append("</div><div class=\"time\">")
                .Append(HtmlEncoder.Default.Encode(message.TimestampText ?? "Timestamp unavailable")).Append("</div>");
            foreach (var media in message.Media.Where(m => m.RelativePath is not null))
            {
                var relative = HtmlEncoder.Default.Encode(media.RelativePath!);
                if (media.Category == "images") html.Append("<div class=\"media\"><a href=\"").Append(relative).Append("\"><img loading=\"lazy\" src=\"").Append(relative).Append("\" alt=\"Chat image\"></a></div>");
                else if (media.Category == "videos") html.Append("<div class=\"media\"><video controls preload=\"metadata\" src=\"").Append(relative).Append("\"></video></div>");
                else if (media.Category == "audio") html.Append("<div class=\"media\"><audio controls preload=\"metadata\" src=\"").Append(relative).Append("\"></audio></div>");
                else html.Append("<div class=\"media\"><a href=\"").Append(relative).Append("\">").Append(HtmlEncoder.Default.Encode(media.FileName ?? "Open attachment")).Append("</a></div>");
            }
            if (message.Limitation is not null) html.Append("<div class=\"media\">").Append(HtmlEncoder.Default.Encode(message.Limitation)).Append("</div>");
            html.Append("</article>");
        }
        html.Append("</main></body></html>");
        SettingsStore.AtomicWrite(Path.Combine(archive, "chat.html"), html.ToString());
    }

    private static void WriteMetadata(string archive, DownloadJob job, List<ArchiveMessage> records)
    {
        var isOcr = job.AcquisitionMethod.Contains("OCR", StringComparison.OrdinalIgnoreCase);
        var limitations = new List<string>
        {
            job.AcquisitionMethod.Contains("export", StringComparison.OrdinalIgnoreCase)
                ? "This archive includes only records and media present in the selected WhatsApp export; WhatsApp may omit some history or attachment types."
                : "Linked-device UI may not contain the full primary-phone history.",
            job.AcquisitionMethod.Contains("export", StringComparison.OrdinalIgnoreCase)
                ? "Exported media is included only when it was present in the supplied ZIP and referenced by the transcript."
                : "The installed linked client exposes only its accessible UI or currently visible screen; archive coverage is unknown.",
            job.AcquisitionMethod.Contains("export", StringComparison.OrdinalIgnoreCase)
                ? "Single-view messages and other items omitted by WhatsApp are not recoverable from the text export."
                : "Media files are not retrieved unless the client UI exposes an attachment stream.",
            job.AcquisitionMethod.Contains("export", StringComparison.OrdinalIgnoreCase)
                ? "Transcript timestamps and sender labels are preserved as exported; timezone data is not provided by the text format."
                : isOcr
                    ? "OCR may misread visible timestamps. Incoming/outgoing direction labels are inferred from screen alignment and do not identify a person. Message ordering follows captured screen pages; full-date chronology is not verified."
                    : "Sender identity and timestamps are preserved only when exposed in accessible message text. Message ordering follows the scroll views but full-date chronology is not verified."
        };
        limitations.AddRange(job.Warnings.Distinct(StringComparer.Ordinal));
        if (isOcr)
            limitations.Add("OCR has no stable message IDs. Exact text, timestamp, and sender-direction matches were deduplicated across overlapping views while preserving repeats within a view; records missing either timestamp or direction remain page-scoped, so duplicate or merged records remain possible.");
        var metadata = new ArchiveMetadata
        {
            ContactIdentifier = job.PhoneNumber,
            DownloadedAt = job.FinishedAt ?? DateTimeOffset.Now,
            RetrievalStatus = job.Status,
            Coverage = job.Coverage,
            MessagesFound = records.Count,
            AttachmentsSaved = records.SelectMany(r => r.Media).Where(m => m.Downloaded && m.RelativePath is not null).Select(m => m.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ItemsSkipped = job.AcquisitionMethod.Contains("export", StringComparison.OrdinalIgnoreCase) ? job.ItemsSkipped : records.Count(r => r.Limitation is not null),
            Source = job.AcquisitionMethod,
            KnownLimitations = limitations.ToArray(),
            Warnings = isOcr
                ? job.Warnings.Append("OCR overlap deduplication used exact text, visible timestamp, and inferred sender direction; uncertain records may still duplicate or be merged.").Distinct(StringComparer.Ordinal).ToArray()
                : job.Warnings.ToArray(),
            Errors = job.Errors.ToArray()
        };
        SettingsStore.AtomicWrite(Path.Combine(archive, "metadata.json"), JsonSerializer.Serialize(metadata, Json));
    }

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
