using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RecallBox.App;

public sealed record ExportImportResult(List<ArchiveMessage> Messages, List<string> Warnings, int AttachmentsSaved, int ItemsSkipped);

public sealed class OfficialExportImporter
{
    private static readonly Regex AndroidLine = new(@"^\u200e?(?<timestamp>\d{1,4}[/-]\d{1,2}[/-]\d{1,4},?\s+\d{1,2}:\d{2}(?::\d{2})?(?:\s?[AP]M)?)\s+-\s*(?<rest>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex IosLine = new(@"^\u200e?\[(?<timestamp>[^\]]{8,48})\]\s*(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex SenderLine = new(@"^(?<sender>[^:\r\n]{1,100}):\s?(?<body>[\s\S]*)$", RegexOptions.Compiled);
    private static readonly Regex AttachedFile = new(@"<attached:\s*(?<name>[^>]+)>|(?<name2>[^\r\n<>]{1,180}\.(?:jpe?g|png|gif|webp|mp4|mov|3gp|m4a|opus|mp3|aac|pdf|docx?|xlsx?|pptx?|zip|vcf))\s*\(file attached\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<ExportImportResult> ImportAsync(string sourcePath, string archiveFolder, Action<int, int> reportProgress)
    {
        using var input = File.OpenRead(sourcePath);
        ZipArchive? zip = null;
        Stream transcriptStream;
        string transcriptName;
        if (Path.GetExtension(sourcePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > 20000) throw new InvalidDataException("This export contains too many ZIP entries to process safely.");
            long expandedBytes = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.Length > 16L * 1024 * 1024 * 1024 - expandedBytes)
                    throw new InvalidDataException("This ZIP expands beyond the 16 GB safety limit.");
                expandedBytes += entry.Length;
            }
            var textEntries = zip.Entries.Where(e => e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && e.Length < 512L * 1024 * 1024).ToList();
            if (textEntries.Count == 0) throw new InvalidDataException("No WhatsApp chat transcript (.txt) was found in this ZIP.");
            var transcript = textEntries.OrderByDescending(e => e.Name.Contains("chat", StringComparison.OrdinalIgnoreCase)).ThenByDescending(e => e.Length).First();
            transcriptName = transcript.FullName;
            transcriptStream = transcript.Open();
        }
        else
        {
            transcriptName = Path.GetFileName(sourcePath);
            transcriptStream = input;
        }

        await using (transcriptStream.ConfigureAwait(false))
        using (zip)
        using (var reader = new StreamReader(transcriptStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            var parsed = await ParseTranscriptAsync(reader, reportProgress);
            if (parsed.Messages.Count == 0) throw new InvalidDataException("No WhatsApp-style timestamped messages were recognized. The selected file was not archived as a successful chat.");
            var warnings = parsed.Warnings;
            if (zip is null)
            {
                var skipped = MarkMediaUnavailable(parsed.Messages);
                warnings.Add("This was a text-only export. No media files were supplied.");
                return new ExportImportResult(parsed.Messages, warnings, 0, skipped);
            }
            else
            {
                if (zip.Entries.Count(e => e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) > 1)
                    warnings.Add($"The ZIP contained multiple text files; '{transcriptName}' was selected as the transcript.");
                var media = await AttachMediaAsync(zip, transcriptName, parsed.Messages, archiveFolder, warnings);
                return new ExportImportResult(parsed.Messages, warnings, media.Saved, media.Skipped);
            }
        }
    }

    private static async Task<(List<ArchiveMessage> Messages, List<string> Warnings)> ParseTranscriptAsync(StreamReader reader, Action<int, int> reportProgress)
    {
        var messages = new List<ArchiveMessage>();
        var warnings = new List<string>();
        ArchiveMessage? current = null;
        var lineNumber = 0;
        while (await reader.ReadLineAsync() is { } line)
        {
            lineNumber++;
            var parsed = ParseStart(line);
            if (parsed is not null)
            {
                if (current is not null) messages.Add(current);
                var idSeed = $"{messages.Count}\n{parsed.TimestampText}\n{parsed.Sender}\n{parsed.Body}";
                current = new ArchiveMessage
                {
                    Id = Hash(idSeed),
                    Sequence = messages.Count,
                    SourcePageNumber = 0,
                    Text = parsed.Body,
                    Sender = parsed.Sender,
                    TimestampText = parsed.TimestampText,
                    LocalTimestamp = ParseTimestamp(parsed.TimestampText),
                    SourceControlType = "Official WhatsApp export text",
                    MessageType = DetectMessageType(parsed.Body)
                };
            }
            else if (current is not null)
            {
                current.Text += "\n" + line;
            }
            if (lineNumber % 250 == 0) reportProgress(lineNumber, messages.Count + (current is null ? 0 : 1));
        }
        if (current is not null) messages.Add(current);
        reportProgress(lineNumber, messages.Count);

        if (messages.Count > 0 && messages.Count(m => m.LocalTimestamp.HasValue) < messages.Count)
            warnings.Add("Some export timestamps could not be parsed as dates. Original timestamp text is retained.");
        if (messages.Any(m => m.Text.Contains("<Media omitted>", StringComparison.OrdinalIgnoreCase) || m.Text.Contains("(file attached)", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("The transcript marks media that may be omitted from the export. Missing attachments are listed on their original messages.");
        return (messages, warnings);
    }

    private static ParsedLine? ParseStart(string line)
    {
        var match = AndroidLine.Match(line);
        string timestamp;
        string remainder;
        if (match.Success)
        {
            timestamp = match.Groups["timestamp"].Value;
            remainder = match.Groups["rest"].Value;
        }
        else
        {
            match = IosLine.Match(line);
            if (!match.Success) return null;
            timestamp = match.Groups["timestamp"].Value;
            remainder = match.Groups["rest"].Value;
        }

        remainder = remainder.TrimStart('\u200e', '\u200f', ' ');
        var sender = SenderLine.Match(remainder);
        if (!sender.Success)
            return new ParsedLine(timestamp, null, remainder);
        return new ParsedLine(timestamp, sender.Groups["sender"].Value, sender.Groups["body"].Value);
    }

    private static DateTime? ParseTimestamp(string value)
    {
        var dateParts = Regex.Match(value, @"^(?<first>\d{1,2})/(?<second>\d{1,2})/(?<year>\d{2,4})");
        if (dateParts.Success && int.TryParse(dateParts.Groups["first"].Value, out var first) && int.TryParse(dateParts.Groups["second"].Value, out var second) &&
            first <= 12 && second <= 12 && first != second)
            return null;
        var formats = new[]
        {
            "d/M/yy, H:mm", "d/M/yyyy, H:mm", "dd/MM/yy, HH:mm", "dd/MM/yyyy, HH:mm",
            "d/M/yy, H:mm:ss", "d/M/yyyy, H:mm:ss", "dd/MM/yy, HH:mm:ss", "dd/MM/yyyy, HH:mm:ss",
            "M/d/yy, h:mm tt", "M/d/yyyy, h:mm tt", "M/d/yy, h:mm:ss tt", "M/d/yyyy, h:mm:ss tt",
            "d/M/yy, h:mm tt", "d/M/yyyy, h:mm tt", "d/M/yy, h:mm:ss tt", "d/M/yyyy, h:mm:ss tt",
            "M/d/yy, H:mm", "M/d/yyyy, H:mm", "M/d/yy, H:mm:ss", "M/d/yyyy, H:mm:ss"
        };
        foreach (var culture in new[] { CultureInfo.CurrentCulture, CultureInfo.GetCultureInfo("en-IN"), CultureInfo.GetCultureInfo("en-US"), CultureInfo.InvariantCulture })
            if (DateTime.TryParseExact(value, formats, culture, DateTimeStyles.AllowWhiteSpaces, out var parsed)) return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        return null;
    }

    private static async Task<(int Saved, int Skipped)> AttachMediaAsync(ZipArchive zip, string transcriptName, List<ArchiveMessage> messages, string archiveFolder, List<string> warnings)
    {
        var saved = 0;
        var skipped = 0;
        var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name) && !e.FullName.Equals(transcriptName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => new Queue<ZipArchiveEntry>(g), StringComparer.OrdinalIgnoreCase);
        var copied = new Dictionary<ZipArchiveEntry, string>();
        foreach (var message in messages)
        {
            var matches = AttachedFile.Matches(message.Text);
            var named = matches.Select(m => NormalizeFileName(m.Groups["name"].Success ? m.Groups["name"].Value : m.Groups["name2"].Value)).Where(n => n.Length > 0).ToList();
            var hasOmitted = message.Text.Contains("<Media omitted>", StringComparison.OrdinalIgnoreCase) || message.Text.Contains("(file attached)", StringComparison.OrdinalIgnoreCase);
            if (named.Count == 0 && !hasOmitted) continue;

            foreach (var requested in named.DefaultIfEmpty(""))
            {
                var baseName = Path.GetFileName(requested);
                ZipArchiveEntry? entry = null;
                if (baseName.Length > 0 && entries.TryGetValue(baseName, out var queue) && queue.Count > 0)
                    entry = queue.Dequeue();
                if (entry is null)
                {
                    var categoryMissing = MediaCategory(requested.Length > 0 ? requested : message.Text);
                    message.Media.Add(new ArchiveMedia { Category = categoryMissing, FileName = baseName.Length == 0 ? null : baseName, Downloaded = false, Limitation = "The export references an attachment that was not present in the supplied ZIP." });
                    message.Limitation = "One or more referenced attachments were not present in the supplied ZIP.";
                    skipped++;
                    continue;
                }

                if (!copied.TryGetValue(entry, out var relativePath))
                {
                    var category = MediaCategory(entry.Name);
                    var mediaFolder = Path.Combine(archiveFolder, "media", category);
                    Directory.CreateDirectory(mediaFolder);
                    var preferredPath = Path.Combine(mediaFolder, SafeFileName(entry.Name));
                    if (File.Exists(preferredPath) && new FileInfo(preferredPath).Length == entry.Length && await SameContentAsync(entry, preferredPath))
                    {
                        relativePath = Path.GetRelativePath(archiveFolder, preferredPath).Replace(Path.DirectorySeparatorChar, '/');
                        copied[entry] = relativePath;
                        message.Media.Add(new ArchiveMedia { Category = category, FileName = Path.GetFileName(relativePath), RelativePath = relativePath, Downloaded = true });
                        continue;
                    }
                    var safeName = UniqueSafeName(mediaFolder, entry.Name);
                    if (entry.Length > 4L * 1024 * 1024 * 1024) throw new InvalidDataException($"Attachment '{entry.Name}' exceeds the 4 GB per-file safety limit.");
                    await using (var source = entry.Open())
                    await using (var target = new FileStream(safeName, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                        await source.CopyToAsync(target);
                    relativePath = Path.GetRelativePath(archiveFolder, safeName).Replace(Path.DirectorySeparatorChar, '/');
                    copied[entry] = relativePath;
                    saved++;
                }
                message.Media.Add(new ArchiveMedia { Category = MediaCategory(entry.Name), FileName = Path.GetFileName(relativePath), RelativePath = relativePath, Downloaded = true });
            }
            if (named.Count == 0 && hasOmitted)
            {
                message.Media.Add(new ArchiveMedia { Category = MediaCategory(message.Text), Downloaded = false, Limitation = "WhatsApp marked this attachment as omitted from the export." });
                message.Limitation = "WhatsApp marked an attachment as omitted from the export.";
                skipped++;
            }
            if (message.Media.Any(m => !m.Downloaded)) message.MessageType = "attachment-with-gaps";
            else if (message.Media.Count > 0) message.MessageType = "attachment";
        }
        if (saved > 0) warnings.Add($"Saved {saved} attachment file(s) supplied in the export ZIP.");
        return (saved, skipped);
    }

    private static int MarkMediaUnavailable(List<ArchiveMessage> messages)
    {
        var skipped = 0;
        foreach (var message in messages)
        {
            var matches = AttachedFile.Matches(message.Text);
            var names = matches.Select(m => NormalizeFileName(m.Groups["name"].Success ? m.Groups["name"].Value : m.Groups["name2"].Value)).Where(n => n.Length > 0).ToList();
            var omitted = message.Text.Contains("<Media omitted>", StringComparison.OrdinalIgnoreCase) || message.Text.Contains("(file attached)", StringComparison.OrdinalIgnoreCase);
            if (names.Count == 0 && !omitted) continue;
            foreach (var name in names.DefaultIfEmpty(""))
            {
                message.Media.Add(new ArchiveMedia
                {
                    Category = MediaCategory(name.Length > 0 ? name : message.Text),
                    FileName = name.Length == 0 ? null : Path.GetFileName(name),
                    Downloaded = false,
                    Limitation = "The selected export contains no media file for this message."
                });
                message.Limitation = "The export did not include one or more referenced media files.";
                skipped++;
            }
            message.MessageType = "attachment-with-gaps";
        }
        return skipped;
    }

    private static string UniqueSafeName(string directory, string original)
    {
        var filename = SafeFileName(original);
        var extension = Path.GetExtension(filename);
        var stem = Path.GetFileNameWithoutExtension(filename);
        stem = Regex.Replace(stem, @"[^\p{L}\p{N}._ -]", "_").Trim(' ', '.');
        if (stem.Length == 0) stem = "attachment";
        if (stem.Length > 100) stem = stem[..100];
        var candidate = Path.Combine(directory, stem + extension);
        for (var suffix = 2; File.Exists(candidate); suffix++) candidate = Path.Combine(directory, $"{stem} ({suffix}){extension}");
        return candidate;
    }

    private static string SafeFileName(string original)
    {
        var name = Path.GetFileName(NormalizeFileName(original));
        var extension = Path.GetExtension(name);
        if (!Regex.IsMatch(extension, @"^\.[A-Za-z0-9]{1,12}$")) extension = ".bin";
        var stem = Path.GetFileNameWithoutExtension(name);
        stem = Regex.Replace(stem, @"[^\p{L}\p{N}._ -]", "_").Trim(' ', '.');
        if (stem.Length == 0) stem = "attachment";
        if (stem.Length > 100) stem = stem[..100];
        return stem + extension;
    }

    private static string NormalizeFileName(string value) => value.Trim().Trim('\u200e', '\u200f', '\u202a', '\u202c', '\u2066', '\u2069');

    private static async Task<bool> SameContentAsync(ZipArchiveEntry entry, string path)
    {
        await using var left = entry.Open();
        await using var right = File.OpenRead(path);
        var leftHash = await SHA256.HashDataAsync(left);
        var rightHash = await SHA256.HashDataAsync(right);
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private static string MediaCategory(string filename)
    {
        return Path.GetExtension(filename).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".heic" => "images",
            ".mp4" or ".mov" or ".3gp" or ".mkv" => "videos",
            ".opus" or ".m4a" or ".mp3" or ".aac" or ".wav" or ".ogg" => "audio",
            ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".vcf" => "documents",
            _ => "other"
        };
    }

    private static string DetectMessageType(string body) => AttachedFile.IsMatch(body) || body.Contains("<Media omitted>", StringComparison.OrdinalIgnoreCase) ? "attachment" : "text";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private sealed record ParsedLine(string TimestampText, string? Sender, string Body);
}
