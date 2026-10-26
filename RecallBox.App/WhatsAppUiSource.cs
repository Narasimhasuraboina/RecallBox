using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace RecallBox.App;

public sealed class WhatsAppUiSource
{
    private const int MaxNodes = 6000;
    private const int MaxText = 2400;

    public static bool IsWhatsAppOpen() => FindWhatsAppWindow() is not null;

    public static (bool Found, int AccessibleMessageControls, bool CanScroll, bool OcrAvailable) InspectAvailability()
    {
        if (!OperatingSystem.IsWindows()) return (false, 0, false, false);
        var window = FindWhatsAppWindow();
        if (window is null) return (false, 0, false, false);
        try
        {
            var root = AutomationElement.FromHandle(window.Value.Handle);
            if (root is null) return (true, 0, false, VisibleWindowOcr.IsAvailable);
            var nodes = ReadTree(root);
            var messageControls = nodes.Count(node => node.Text.Length > 0 && !node.Offscreen &&
                (node.ControlType.Equals("ListItem", StringComparison.OrdinalIgnoreCase) ||
                 node.ControlType.Equals("Text", StringComparison.OrdinalIgnoreCase) ||
                 node.ControlType.Equals("Custom", StringComparison.OrdinalIgnoreCase)) && !IsNavigationText(node.Text));
            var canScroll = nodes.Any(node =>
            {
                try { return node.Element.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) && ((ScrollPattern)pattern).Current.VerticallyScrollable; }
                catch { return false; }
            });
            return (true, messageControls, canScroll, VisibleWindowOcr.IsAvailable);
        }
        catch { return (true, 0, false, VisibleWindowOcr.IsAvailable); }
    }

    public async IAsyncEnumerable<SourcePage> ReadChatAsync(string phone, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WhatsApp UI Automation is available only on Windows.");
        var window = FindWhatsAppWindow();
        if (window is null) throw new InvalidOperationException("RecallBox did not find an already-running WhatsApp window. It will not launch WhatsApp. Import an official chat export to archive without opening WhatsApp on this computer.");

        var root = AutomationElement.FromHandle(window.Value.Handle) ?? throw new InvalidOperationException("Could not attach Windows UI Automation to WhatsApp.");
        var tree = ReadTree(root);
        var firstUiMessages = ExtractMessages(tree);
        var useOcr = firstUiMessages.Count == 0;
        var firstOcr = useOcr ? await VisibleWindowOcr.CaptureAsync(window.Value.Handle) : null;
        var phoneDigits = Digits(phone);
        var numberVisible = tree.Any(node => Digits(node.Text).Contains(phoneDigits, StringComparison.Ordinal)) ||
                            firstOcr is { } visibleScreenshot && visibleScreenshot.Lines.Any(line =>
                                line.Left >= visibleScreenshot.Width * 0.28 && line.Top < visibleScreenshot.Height * 0.16 &&
                                Digits(line.Text).Contains(phoneDigits, StringComparison.Ordinal));

        cancellationToken.ThrowIfCancellationRequested();
        var messages = useOcr
            ? ExtractOcrMessages(firstOcr ?? await VisibleWindowOcr.CaptureAsync(window.Value.Handle))
            : firstUiMessages;
        if (messages.Count == 0)
            throw new InvalidOperationException("No readable messages were found in the current WhatsApp view. RecallBox did not change the window. Import an official WhatsApp export for a full available transcript.");

        var warning = useOcr
            ? "RecallBox captured only the currently visible WhatsApp view using local OCR. It did not launch, focus, search, or scroll WhatsApp. OCR, timestamps, inferred sender direction, replies, edits, media, and history coverage may be incomplete. Screenshots are discarded immediately."
            : "RecallBox captured message controls already exposed in the current WhatsApp view. It did not launch, focus, search, or scroll WhatsApp; full history and media are not established.";
        if (!numberVisible)
            warning += " The phone number could not be independently verified in the visible client; the selected chat is associated with this archive based on your confirmation.";
        yield return new SourcePage(messages, false, warning, 1);
    }

    internal static (IntPtr Handle, string Title)? FindWhatsAppWindow()
    {
        var windows = new List<(IntPtr Handle, string Title, string ProcessName)>();
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (process.MainWindowHandle == IntPtr.Zero) continue;
                var title = process.MainWindowTitle;
                var isWhatsAppProcess = process.ProcessName.Contains("whatsapp", StringComparison.OrdinalIgnoreCase);
                var isWhatsAppWindow = title.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase) ||
                                       Regex.IsMatch(title, @"^\(\d+\)\s*WhatsApp$", RegexOptions.IgnoreCase);
                if (isWhatsAppProcess || isWhatsAppWindow)
                    windows.Add((process.MainWindowHandle, title, process.ProcessName));
            }
            catch { }
            finally { process.Dispose(); }
        }
        return windows
            .OrderByDescending(window => window.ProcessName.Equals("WhatsApp.Root", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(window => window.Title.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase))
            .Select(window => ((IntPtr Handle, string Title)?)(window.Handle, window.Title))
            .FirstOrDefault();
    }

    private static List<UiNode> ReadTree(AutomationElement root)
    {
        var result = new List<UiNode>();
        var stack = new Stack<(AutomationElement Element, int Depth)>();
        stack.Push((root, 0));
        var walker = TreeWalker.ControlViewWalker;
        while (stack.Count > 0 && result.Count < MaxNodes)
        {
            var (element, depth) = stack.Pop();
            if (depth > 35) continue;
            try
            {
                var name = Safe(() => element.Current.Name, "");
                var control = Safe(() => element.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""), "Unknown");
                var id = Safe(() => element.Current.AutomationId, "");
                var offscreen = Safe(() => element.Current.IsOffscreen, false);
                var value = ReadValue(element);
                var text = ReadText(element);
                var content = string.Join(" ", new[] { name, value, text }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                result.Add(new UiNode(element, control, id, Clip(content), offscreen));
                var children = new List<AutomationElement>();
                var child = walker.GetFirstChild(element);
                while (child is not null && children.Count < 500)
                {
                    children.Add(child);
                    child = walker.GetNextSibling(child);
                }
                for (var i = children.Count - 1; i >= 0; i--) stack.Push((children[i], depth + 1));
            }
            catch (ElementNotAvailableException) { }
            catch (COMException) { }
        }
        return result;
    }

    private static IReadOnlyList<UiMessage> ExtractMessages(List<UiNode> nodes)
    {
        var candidates = nodes.Where(n => n.Text.Length > 0 && !n.Offscreen &&
            (n.ControlType.Equals("ListItem", StringComparison.OrdinalIgnoreCase) ||
             n.ControlType.Equals("Text", StringComparison.OrdinalIgnoreCase) ||
             n.ControlType.Equals("Custom", StringComparison.OrdinalIgnoreCase))).ToList();
        var listItems = candidates.Where(n => n.ControlType.Equals("ListItem", StringComparison.OrdinalIgnoreCase)).ToList();
        if (listItems.Count > 0) candidates = listItems;
        var messages = new List<UiMessage>();
        foreach (var item in candidates)
        {
            var text = Normalize(item.Text);
            if (text.Length < 2 || IsNavigationText(text)) continue;
            string? sender = TrySender(text, out var extractedSender) ? extractedSender : null;
            var timestamp = TryExtractTimestamp(text);
            var unsupported = DetectUnsupportedMedia(text);
            messages.Add(new UiMessage(text, item.ControlType, item.AutomationId, timestamp, sender, unsupported));
        }
        return messages;
    }

    private static IReadOnlyList<UiMessage> ExtractOcrMessages(VisibleOcrSnapshot snapshot)
    {
        var visible = snapshot.Lines.Where(line => line.Left >= snapshot.Width * 0.28 &&
                                                   line.Top >= snapshot.Height * 0.08 &&
                                                   line.Top + line.Height <= snapshot.Height * 0.91 &&
                                                   !string.IsNullOrWhiteSpace(line.Text))
            .OrderBy(line => line.Top).ThenBy(line => line.Left).ToList();
        var standaloneTimes = visible.Where(line => Regex.IsMatch(Normalize(line.Text), @"^\d{1,2}:[0-5]\d(?:\s?[AP]M)?$", RegexOptions.IgnoreCase)).ToList();
        visible = visible.Except(standaloneTimes).ToList();
        var groups = new List<List<VisibleOcrLine>>();
        foreach (var line in visible)
        {
            if (groups.Count == 0) { groups.Add([line]); continue; }
            var priorGroup = groups[^1];
            var prior = priorGroup[^1];
            var gap = line.Top - (prior.Top + prior.Height);
            var closeColumn = Math.Abs(line.Left - prior.Left) <= snapshot.Width * 0.22;
            if (gap <= Math.Max(18, prior.Height * 1.45) && closeColumn) priorGroup.Add(line);
            else groups.Add([line]);
        }

        var messages = new List<UiMessage>();
        var usedTimes = new HashSet<VisibleOcrLine>();
        foreach (var group in groups)
        {
            var lines = group.Select(line => Normalize(line.Text)).Where(text => text.Length > 0).ToList();
            if (lines.Count == 0) continue;
            var text = string.Join("\n", lines);
            if (text.Length < 2 || IsNavigationText(text)) continue;
            var sender = TrySender(text, out var extractedSender) ? extractedSender : null;
            var groupLeft = group.Min(line => line.Left);
            var groupRight = group.Max(line => line.Left + line.Width);
            var groupCenter = (groupLeft + groupRight) / 2;
            if (sender is null && groupCenter < snapshot.Width * 0.46) sender = "Incoming (inferred from alignment)";
            else if (sender is null && groupCenter > snapshot.Width * 0.62) sender = "Outgoing (inferred from alignment)";
            var groupTop = group.Min(line => line.Top);
            var groupBottom = group.Max(line => line.Top + line.Height);
            var groupVerticalCenter = (groupTop + groupBottom) / 2;
            var nearbyTime = standaloneTimes.Where(line => !usedTimes.Contains(line))
                .Select(line => new
                {
                    Line = line,
                    VerticalDistance = Math.Abs(line.Top + line.Height / 2 - groupVerticalCenter),
                    HorizontalDistance = Math.Abs(line.Left + line.Width / 2 - groupCenter)
                })
                .Where(item => item.VerticalDistance <= snapshot.Height * 0.29 && item.HorizontalDistance <= snapshot.Width * 0.35)
                .OrderBy(item => item.VerticalDistance).ThenBy(item => item.HorizontalDistance).FirstOrDefault();
            if (nearbyTime is not null) usedTimes.Add(nearbyTime.Line);
            var timestamp = nearbyTime?.Line.Text ?? TryExtractTimestamp(text);
            var limitation = DetectUnsupportedMedia(text);
            messages.Add(new UiMessage(text, "Visible OCR", "", timestamp, sender, limitation));
        }
        return messages;
    }

    private static string? DetectUnsupportedMedia(string text)
    {
        var lower = text.ToLowerInvariant();
        if (new[] { "photo", "image", "video", "voice message", "audio", "document", "sticker", "gif", "attachment", "location message", "contact card", "poll" }.Any(word => lower.Contains(word, StringComparison.Ordinal)))
            return "Attachment is visible in the UI, but no local file stream was exposed. Media was not saved.";
        return null;
    }

    public static string? GetMediaCategory(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("photo") || lower.Contains("image")) return "images";
        if (lower.Contains("video") || lower.Contains("gif")) return "videos";
        if (lower.Contains("voice") || lower.Contains("audio")) return "audio";
        if (lower.Contains("document") || lower.Contains("pdf") || lower.Contains("file")) return "documents";
        return DetectUnsupportedMedia(text) is null ? null : "other";
    }

    private static string? TryExtractTimestamp(string text)
    {
        var match = Regex.Match(text, @"\b(?:[01]?\d|2[0-3]):[0-5]\d(?:\s?[AP]M)?\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : null;
    }

    private static bool TrySender(string text, out string sender)
    {
        var match = Regex.Match(text, @"^(?<sender>[^:\r\n]{1,80}):\s+.+$");
        sender = match.Success ? match.Groups["sender"].Value : "";
        return match.Success;
    }

    private static bool IsNavigationText(string text) => new[] { "search", "new chat", "menu", "settings", "more options", "message list" }.Contains(text, StringComparer.OrdinalIgnoreCase);
    private static string Digits(string text) => Regex.Replace(text, "\\D", "");
    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ").Trim();
    private static string Clip(string value) => value.Length <= MaxText ? value : value[..MaxText];

    private static string ReadValue(AutomationElement element)
    {
        try { return element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? ((ValuePattern)pattern).Current.Value : ""; }
        catch { return ""; }
    }

    private static string ReadText(AutomationElement element)
    {
        try { return element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) ? ((TextPattern)pattern).DocumentRange.GetText(MaxText) : ""; }
        catch { return ""; }
    }

    private static T Safe<T>(Func<T> get, T fallback)
    {
        try { return get(); }
        catch { return fallback; }
    }

    private sealed class UiNode(AutomationElement element, string controlType, string automationId, string text, bool offscreen)
    {
        public AutomationElement Element { get; } = element;
        public string ControlType { get; } = controlType;
        public string AutomationId { get; } = automationId;
        public string Text { get; } = text;
        public bool Offscreen { get; } = offscreen;
    }
}
