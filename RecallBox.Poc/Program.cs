using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using RecallBox.App;

internal static class Program
{
    private const int MaxNodes = 350;
    private const int MaxDepth = 14;
    private const int MaxTextLength = 700;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);


    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extraInfo);

    private delegate bool EnumChildProc(IntPtr window, IntPtr parameter);
    private struct Rect { public int Left, Top, Right, Bottom; }


    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Contains("--summary-only", StringComparer.OrdinalIgnoreCase))
            return InspectRedactedSummary();
        if (args.Contains("--ocr-summary", StringComparer.OrdinalIgnoreCase))
            return InspectOcrSummary().GetAwaiter().GetResult();
        if (args.Contains("--ocr-layout-summary", StringComparer.OrdinalIgnoreCase))
            return InspectOcrLayoutSummary().GetAwaiter().GetResult();
        if (args.Contains("--ocr-window-layout-summary", StringComparer.OrdinalIgnoreCase))
            return InspectOcrWindowLayoutSummary().GetAwaiter().GetResult();
        if (args.Contains("--ocr-redacted-layout", StringComparer.OrdinalIgnoreCase))
            return InspectOcrRedactedLayout().GetAwaiter().GetResult();
        if (args.Contains("--try-ui-focus", StringComparer.OrdinalIgnoreCase))
            return TryUiFocus();
        var searchDiagnosticAt = Array.FindIndex(args, item => item.Equals("--search-diagnostic", StringComparison.OrdinalIgnoreCase));
        if (searchDiagnosticAt >= 0)
            return SearchDiagnosticAsync(args.ElementAtOrDefault(searchDiagnosticAt + 1) ?? "").GetAwaiter().GetResult();
        if (args.Contains("--ocr-scroll-summary", StringComparer.OrdinalIgnoreCase))
            return ScrollAndInspectOcrSummary().GetAwaiter().GetResult();
        var analyzeAt = Array.FindIndex(args, item => item.Equals("--analyze-visible-chat", StringComparison.OrdinalIgnoreCase));
        if (analyzeAt >= 0)
            return AnalyzeVisibleChatAsync(args.ElementAtOrDefault(analyzeAt + 1) ?? "").GetAwaiter().GetResult();

        Console.WriteLine("RecallBox WhatsApp UI Automation feasibility POC");
        Console.WriteLine("This reads accessible UI text from the selected window and prints it here.");
        Console.WriteLine("Use only with your own signed-in account. Chat text may appear below; do not share unredacted output. Nothing is written to a file.\n");

        PrintEnvironment();

        Console.Write("Enter the target phone number in international format (for example +919876543210): ");
        var number = Console.ReadLine()?.Trim() ?? "";
        var normalized = NormalizePhone(number);
        if (normalized is null)
        {
            Console.Error.WriteLine("That does not look like a phone number with a country code. No window was inspected.");
            return 2;
        }

        Console.WriteLine($"Target number recorded for this run: {normalized}");
        Console.WriteLine("In WhatsApp, open the conversation you believe matches this number. Verify the contact details in WhatsApp itself.");
        Console.WriteLine("Return here and press Enter to inspect that window. The POC does not search or navigate your account.\n");
        Console.ReadLine();

        var windows = FindWhatsAppWindows();
        if (windows.Count == 0)
        {
            Console.Error.WriteLine("No visible WhatsApp process window was found. Start WhatsApp Desktop, open a conversation, and run this POC again.");
            return 3;
        }

        var foreground = GetForegroundWindow();
        var selected = windows.FirstOrDefault(w => w.Handle == foreground) ?? windows[0];
        if (windows.Count > 1)
        {
            Console.WriteLine("WhatsApp windows found:");
            for (var i = 0; i < windows.Count; i++)
                Console.WriteLine($"  {i + 1}. {windows[i].ProcessName} | {windows[i].Title} | PID {windows[i].ProcessId}" + (windows[i].Handle == foreground ? " [foreground]" : ""));
            Console.Write("Choose window number (Enter uses the foreground WhatsApp window, or the first match): ");
            var choice = Console.ReadLine();
            if (int.TryParse(choice, out var index) && index >= 1 && index <= windows.Count)
                selected = windows[index - 1];
        }

        Console.WriteLine($"\nSelected: {selected.ProcessName}, PID {selected.ProcessId}, title: {selected.Title}");
        Console.WriteLine($"Process file version: {selected.FileVersion ?? "not available"}");
        Console.WriteLine("Capture snapshots while viewing the conversation. To test history access, scroll upward in WhatsApp between snapshots.");
        Console.WriteLine("Enter = capture current accessibility tree; q = finish. Keep the POC window open while scrolling in WhatsApp.\n");

        var snapshot = 0;
        while (true)
        {
            Console.Write($"Snapshot {snapshot + 1} (Enter/q): ");
            var input = Console.ReadLine();
            if (input is null || input.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) break;
            snapshot++;
            Capture(selected, normalized, snapshot);
        }

        Console.WriteLine("\nPOC finished. This output describes only the UI exposed during these snapshots; it cannot establish complete chat history.");
        Console.WriteLine("No chat content or report was saved by the POC.");
        return 0;
    }

    private static int InspectRedactedSummary()
    {
        Console.WriteLine("RecallBox redacted WhatsApp UI Automation summary");
        Console.WriteLine("This mode reads control types and supported UI patterns only; it does not read or print chat text, names, or numbers.");
        var window = FindWhatsAppWindows().FirstOrDefault();
        if (window is null)
        {
            Console.WriteLine("No visible WhatsApp window found.");
            return 3;
        }
        Console.WriteLine($"Process: {window.ProcessName}; version: {window.FileVersion ?? "not available"}");
        try
        {
            var root = AutomationElement.FromHandle(window.Handle);
            if (root is null) return 4;
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            Summarize(root, TreeWalker.ControlViewWalker, counts, out var total, out var named, out var offscreen, out var textPattern, out var valuePattern, out var scrollPattern);
            Console.WriteLine($"Control view: {total} controls; {named} with a name; {offscreen} offscreen; TextPattern {textPattern}; ValuePattern {valuePattern}; ScrollPattern {scrollPattern}");
            Console.WriteLine("Control view types:");
            foreach (var item in counts) Console.WriteLine($"  {item.Key}: {item.Value}");
            var contentCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            Summarize(root, TreeWalker.ContentViewWalker, contentCounts, out total, out named, out offscreen, out textPattern, out valuePattern, out scrollPattern);
            Console.WriteLine($"Content view: {total} controls; {named} with a name; {offscreen} offscreen; TextPattern {textPattern}; ValuePattern {valuePattern}; ScrollPattern {scrollPattern}");
            Console.WriteLine("Content view types:");
            foreach (var item in contentCounts) Console.WriteLine($"  {item.Key}: {item.Value}");
            var rawCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            Summarize(root, TreeWalker.RawViewWalker, rawCounts, out total, out named, out offscreen, out textPattern, out valuePattern, out scrollPattern);
            Console.WriteLine($"Raw view: {total} controls; {named} with a name; {offscreen} offscreen; TextPattern {textPattern}; ValuePattern {valuePattern}; ScrollPattern {scrollPattern}");
            Console.WriteLine("Raw view types:");
            foreach (var item in rawCounts) Console.WriteLine($"  {item.Key}: {item.Value}");
            var searchControls = FindControls(root, element => Safe(() => element.Current.Name, "").Contains("search", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"Controls whose accessible label mentions search: {searchControls.Count}");
            foreach (var control in searchControls.Take(5))
            {
                var type = Safe(() => control.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""), "Unknown");
                var bounds = Safe(() => control.Current.BoundingRectangle, System.Windows.Rect.Empty);
                var invoke = Safe(() => control.TryGetCurrentPattern(InvokePattern.Pattern, out _), false);
                Console.WriteLine($"  {type}: bounds=({bounds.X:F0},{bounds.Y:F0},{bounds.Width:F0},{bounds.Height:F0}), invoke={invoke}");
            }
            var nativeChildren = new List<IntPtr>();
            EnumChildWindows(window.Handle, (child, _) => { nativeChildren.Add(child); return true; }, IntPtr.Zero);
            Console.WriteLine($"Native child windows: {nativeChildren.Count}");
            foreach (var group in nativeChildren.Select(handle => new { Handle = handle, Class = GetWindowClass(handle), Visible = IsWindowVisible(handle), Rect = GetRect(handle) })
                         .GroupBy(item => (item.Class, item.Visible, item.Rect.Width, item.Rect.Height))
                         .OrderByDescending(group => group.Count()).Take(30))
                Console.WriteLine($"  {group.Key.Class}: {group.Count()} window(s), visible={group.Key.Visible}, size={group.Key.Width}x{group.Key.Height}");
            foreach (var child in nativeChildren.Where(IsWindowVisible).Take(20))
            {
                try
                {
                    var childRoot = AutomationElement.FromHandle(child);
                    if (childRoot is null) continue;
                    var childCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
                    Summarize(childRoot, TreeWalker.ControlViewWalker, childCounts, out total, out named, out offscreen, out textPattern, out valuePattern, out scrollPattern);
                    if (total > 1 || textPattern > 0 || scrollPattern > 0)
                        Console.WriteLine($"  Child UIA: class={GetWindowClass(child)}, controls={total}, text={textPattern}, value={valuePattern}, scroll={scrollPattern}");
                }
                catch { }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"UI Automation summary failed: {ex.GetType().Name}: {ex.Message}");
            return 5;
        }
    }

    private static List<AutomationElement> FindControls(AutomationElement root, Func<AutomationElement, bool> predicate)
    {
        var matches = new List<AutomationElement>();
        var stack = new Stack<AutomationElement>();
        stack.Push(root);
        var walker = TreeWalker.ControlViewWalker;
        while (stack.Count > 0 && matches.Count < 20)
        {
            var current = stack.Pop();
            try
            {
                if (predicate(current)) matches.Add(current);
                var children = new List<AutomationElement>();
                var child = walker.GetFirstChild(current);
                while (child is not null && children.Count < 500)
                {
                    children.Add(child);
                    child = walker.GetNextSibling(child);
                }
                for (var index = children.Count - 1; index >= 0; index--) stack.Push(children[index]);
            }
            catch { }
        }
        return matches;
    }

    private static async Task<int> InspectOcrSummary()
    {
        Console.WriteLine("RecallBox redacted visible-window OCR summary");
        Console.WriteLine("The WhatsApp window is captured in memory. OCR text is not printed, saved, or transmitted.");
        var window = FindWhatsAppWindows().FirstOrDefault();
        if (window is null)
        {
            Console.WriteLine("No WhatsApp window bounds are available.");
            return 3;
        }
        try
        {
            var capture = await CaptureOcrAsync(window.Handle);
            PrintOcrCounts(capture);
            Console.WriteLine("OCR words were counted in memory and discarded. No screenshot file was created.");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine($"Visible-window OCR failed: {error.GetType().Name} (0x{error.HResult:X8}).");
            return 6;
        }
    }

    private static async Task<int> InspectOcrLayoutSummary()
    {
        var window = FindWhatsAppWindows().FirstOrDefault();
        if (window is null) return 3;
        try
        {
            var capture = await CaptureOcrAsync(window.Handle);
            var index = 0;
            foreach (var line in capture.Lines.Where(item => IsChatPaneLine(item, capture.Width, capture.Height)))
            {
                var left = line.Words.Min(word => word.BoundingRect.X);
                var top = line.Words.Min(word => word.BoundingRect.Y);
                var right = line.Words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
                var bottom = line.Words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);
                var timeLike = Regex.IsMatch(line.Text, @"\b\d{1,2}:[0-5]\d\b");
                var colon = line.Text.Contains(':');
                Console.WriteLine($"Line {++index}: x={left:F0}, y={top:F0}, w={right - left:F0}, h={bottom - top:F0}, words={line.Words.Count}, chars={line.Text.Length}, timeLike={timeLike}, colon={colon}.");
            }
            Console.WriteLine("OCR text was analyzed in memory; line text is not printed or saved.");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine($"Layout scan failed: {error.GetType().Name} (0x{error.HResult:X8}).");
            return 4;
        }
    }

    private static async Task<int> InspectOcrWindowLayoutSummary()
    {
        var window = FindWhatsAppWindows().FirstOrDefault();
        if (window is null) return 3;
        try
        {
            var capture = await CaptureOcrAsync(window.Handle);
            var index = 0;
            foreach (var line in capture.Lines)
            {
                if (line.Words.Count == 0) continue;
                var left = line.Words.Min(word => word.BoundingRect.X);
                var top = line.Words.Min(word => word.BoundingRect.Y);
                var right = line.Words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
                var bottom = line.Words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);
                var searchLike = line.Text.Contains("search", StringComparison.OrdinalIgnoreCase);
                var numericLike = Regex.IsMatch(line.Text, @"^[+\d\s().-]+$");
                Console.WriteLine($"Line {++index}: x={left:F0}, y={top:F0}, w={right - left:F0}, h={bottom - top:F0}, words={line.Words.Count}, chars={line.Text.Length}, searchLike={searchLike}, numericLike={numericLike}.");
            }
            Console.WriteLine("No OCR line text was printed or saved.");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine($"Layout scan failed: {error.GetType().Name} (0x{error.HResult:X8}).");
            return 4;
        }
    }

    private static async Task<int> ScrollAndInspectOcrSummary()
    {
        var window = FindWhatsAppWindows().FirstOrDefault();
        if (window is null || !GetWindowRect(window.Handle, out var rect)) return 3;
        var before = await CaptureOcrAsync(window.Handle);
        var previousForeground = GetForegroundWindow();
        var x = rect.Left + (int)((rect.Right - rect.Left) * 0.68);
        var y = rect.Top + (int)((rect.Bottom - rect.Top) * 0.5);
        var activated = SetForegroundWindow(window.Handle);
        await Task.Delay(200);
        var foreground = GetForegroundWindow() == window.Handle;
        if (foreground)
        {
            SetCursorPos(x, y);
            mouse_event(0x0800, 0, 0, 120, UIntPtr.Zero);
        }
        Console.WriteLine($"Activated WhatsApp: {activated}; foreground confirmed: {foreground}; sent one normal wheel-up event: {foreground}. No message text is emitted.");
        await Task.Delay(900);
        if (previousForeground != IntPtr.Zero && previousForeground != window.Handle) SetForegroundWindow(previousForeground);
        var after = await CaptureOcrAsync(window.Handle);
        var changed = !string.Equals(string.Join("\n", before.Lines.Select(line => line.Text)), string.Join("\n", after.Lines.Select(line => line.Text)), StringComparison.Ordinal);
        PrintOcrCounts(after);
        Console.WriteLine($"OCR-visible content changed after the wheel event: {changed}.");
        Console.WriteLine("OCR words were counted in memory and discarded. No screenshot file was created.");
        return 0;
    }

    private static async Task<int> InspectOcrRedactedLayout()
    {
        var window = FindWhatsAppWindows().FirstOrDefault();
        if (window is null) return 3;
        var capture = await CaptureOcrAsync(window.Handle);
        Console.WriteLine($"Foreground={capture.Foreground}; window={capture.Width}x{capture.Height}; lines={capture.Lines.Length}; no screenshot or unredacted text is emitted.");
        foreach (var line in capture.Lines)
        {
            var left = line.Words.Count == 0 ? 0 : line.Words.Min(word => word.BoundingRect.X);
            var top = line.Words.Count == 0 ? 0 : line.Words.Min(word => word.BoundingRect.Y);
            var masked = Regex.Replace(line.Text, @"\d+", match =>
            {
                var digits = match.Value;
                var visible = Math.Min(4, digits.Length);
                return new string('*', digits.Length - visible) + digits[^visible..];
            });
            masked = Regex.Replace(masked, @"(?i)[a-z]", "x");
            Console.WriteLine($"x={left:F0}, y={top:F0}, chars={line.Text.Length}, digits={Regex.Matches(line.Text, @"\d").Count}: {masked}");
        }
        return 0;
    }

    private static async Task<int> SearchDiagnosticAsync(string phone)
    {
        var normalized = NormalizePhone(phone);
        var window = FindWhatsAppWindows().FirstOrDefault();
        if (normalized is null || window is null) return 3;
        try
        {
            VisibleWindowOcr.TryFocusWindow(window.Handle);
            await VisibleWindowOcr.WaitForForegroundAsync(window.Handle, TimeSpan.FromSeconds(30), CancellationToken.None);
            var selected = await VisibleWindowOcr.SearchAndSelectChatAsync(window.Handle, normalized, CancellationToken.None);
            var capture = await CaptureOcrAsync(window.Handle);
            Console.WriteLine($"Search returned a phone-exact result: {selected}; foreground={capture.Foreground}; dimensions={capture.Width}x{capture.Height}; OCR lines={capture.Lines.Length}.");
            foreach (var line in capture.Lines)
            {
                var left = line.Words.Count == 0 ? 0 : line.Words.Min(word => word.BoundingRect.X);
                var top = line.Words.Count == 0 ? 0 : line.Words.Min(word => word.BoundingRect.Y);
                var masked = Regex.Replace(line.Text, @"\d+", match =>
                {
                    var digits = match.Value;
                    var visible = Math.Min(4, digits.Length);
                    return new string('*', digits.Length - visible) + digits[^visible..];
                });
                masked = Regex.Replace(masked, @"(?i)[a-z]", "x");
                Console.WriteLine($"x={left:F0}, y={top:F0}, chars={line.Text.Length}, digits={Regex.Matches(line.Text, @"\d").Count}: {masked}");
            }
            Console.WriteLine("OCR text was masked in memory; no screenshot or unredacted text was written.");
            return selected ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.WriteLine($"Search diagnostic failed: {error.Message}");
            return 2;
        }
    }

    private static int TryUiFocus()
    {
        var window = FindWhatsAppWindows().FirstOrDefault();
        if (window is null) return 3;
        var before = GetForegroundWindow();
        var result = "no element";
        try
        {
            var element = AutomationElement.FromHandle(window.Handle);
            if (element is not null)
            {
                element.SetFocus();
                result = "UI Automation SetFocus returned";
            }
        }
        catch (Exception error) { result = $"UI Automation SetFocus threw {error.GetType().Name}"; }
        Thread.Sleep(500);
        var after = GetForegroundWindow();
        Console.WriteLine($"Target={window.ProcessName}:{window.Title}; before={before}; after={after}; {result}; targetFocused={after == window.Handle}.");
        return after == window.Handle ? 0 : 1;
    }


    private static async Task<int> AnalyzeVisibleChatAsync(string phone)
    {
        var normalized = NormalizePhone(phone);
        if (normalized is null)
        {
            Console.Error.WriteLine("Use --analyze-visible-chat followed by a country-coded phone number.");
            return 2;
        }
        Console.WriteLine("Analyzing the currently open WhatsApp view without printing or saving message text.");
        var source = new WhatsAppUiSource();
        var pages = 0;
        var messages = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        try
        {
            await foreach (var page in source.ReadChatAsync(normalized, timeout.Token))
            {
                pages++;
                messages += page.Messages.Count;
                var timed = page.Messages.Count(message => !string.IsNullOrWhiteSpace(message.TimestampText));
                var withSender = page.Messages.Count(message => !string.IsNullOrWhiteSpace(message.Sender));
                var withAttachmentHint = page.Messages.Count(message => !string.IsNullOrWhiteSpace(message.UnsupportedReason));
                Console.WriteLine($"Page {page.PageNumber}: {page.Messages.Count} candidate record(s); timestamps={timed}; sender labels={withSender}; attachment hints={withAttachmentHint}." +
                                  (page.Warning is null ? "" : $" Note: {page.Warning}"));
            }
        }
        catch (Exception error)
        {
            Console.WriteLine($"Visible scan failed: {error.Message}");
            return 1;
        }
        Console.WriteLine($"Visible scan result: {pages} pages, {messages} candidate records. This does not prove complete history.");
        return messages > 0 ? 0 : 1;
    }

    private static async Task<WindowOcr> CaptureOcrAsync(IntPtr handle)
    {
        if (!GetWindowRect(handle, out var rect)) throw new InvalidOperationException("WhatsApp window bounds are unavailable.");
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 100 || height < 100 || width > OcrEngine.MaxImageDimension || height > OcrEngine.MaxImageDimension)
            throw new InvalidOperationException($"Window size is outside supported OCR bounds: {width}x{height}.");
        var engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? throw new InvalidOperationException("No Windows OCR engine is available for the current user languages.");
        var foreground = GetForegroundWindow() == handle;
        using var image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        bool printed;
        using (var graphics = Graphics.FromImage(image))
        {
            var hdc = graphics.GetHdc();
            try { printed = PrintWindow(handle, hdc, 2); }
            finally { graphics.ReleaseHdc(hdc); }
            if (!printed && foreground)
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                printed = true;
            }
        }
        if (!printed) throw new InvalidOperationException("The WhatsApp window could not be captured without changing foreground focus.");
        var locked = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(locked.Stride) * height];
            Marshal.Copy(locked.Scan0, bytes, 0, bytes.Length);
            using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
                CryptographicBuffer.CreateFromByteArray(bytes), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
            var recognized = await engine.RecognizeAsync(softwareBitmap);
            return new WindowOcr(width, height, foreground, printed, recognized.Lines.ToArray());
        }
        finally { image.UnlockBits(locked); }
    }

    private static void PrintOcrCounts(WindowOcr capture)
    {
        var words = capture.Lines.Sum(line => line.Words.Count);
        var pane = capture.Lines.Where(line => IsChatPaneLine(line, capture.Width, capture.Height)).ToArray();
        var timeLike = pane.Count(line => Regex.IsMatch(line.Text, @"\b\d{1,2}:[0-5]\d\b"));
        var senderLike = pane.Count(line => line.Text.Contains(':'));
        Console.WriteLine($"Window size: {capture.Width}x{capture.Height}; foreground={capture.Foreground}; capturedByPrintWindow={capture.Printed}; OCR lines: {capture.Lines.Length}; words: {words}; candidate chat-pane lines: {pane.Length}; time-like lines: {timeLike}; colon lines: {senderLike}.");
    }

    private static bool IsChatPaneLine(Windows.Media.Ocr.OcrLine line, int width, int height)
    {
        if (line.Words.Count == 0) return false;
        var left = line.Words.Min(word => word.BoundingRect.X);
        var top = line.Words.Min(word => word.BoundingRect.Y);
        var bottom = line.Words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);
        return left >= width * 0.28 && top >= height * 0.08 && bottom <= height * 0.91;
    }

    private sealed record WindowOcr(int Width, int Height, bool Foreground, bool Printed, Windows.Media.Ocr.OcrLine[] Lines);

    private static string GetWindowClass(IntPtr handle)
    {
        var name = new StringBuilder(256);
        return GetClassName(handle, name, name.Capacity) == 0 ? "(unknown)" : name.ToString();
    }

    private static (int Width, int Height) GetRect(IntPtr handle)
    {
        if (!GetWindowRect(handle, out var rect)) return (0, 0);
        return (Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
    }

    private static void Summarize(AutomationElement root, TreeWalker walker, SortedDictionary<string, int> counts,
        out int total, out int named, out int offscreen, out int textPattern, out int valuePattern, out int scrollPattern)
    {
        total = named = offscreen = textPattern = valuePattern = scrollPattern = 0;
        var stack = new Stack<(AutomationElement Element, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && total < 6000)
        {
            var (element, depth) = stack.Pop();
            if (depth > 35) continue;
            try
            {
                total++;
                var type = element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
                counts[type] = counts.GetValueOrDefault(type) + 1;
                if (!string.IsNullOrWhiteSpace(element.Current.Name)) named++;
                if (element.Current.IsOffscreen) offscreen++;
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out _)) textPattern++;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out _)) valuePattern++;
                if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out _)) scrollPattern++;
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
    }

    private static void PrintEnvironment()
    {
        Console.WriteLine($"Windows: {Environment.OSVersion.VersionString}");
        Console.WriteLine($"Runtime: {Environment.Version}");
        try
        {
            var output = RunPowerShell("$p = Get-AppxPackage *WhatsApp* | Select-Object -First 10 Name,Version,PackageFullName; if ($p) { $p | Format-Table -HideTableHeaders | Out-String } else { 'No matching WhatsApp MSIX package found.' }");
            Console.WriteLine("WhatsApp package version(s):");
            Console.WriteLine(output.Trim().Length == 0 ? "  (no package information returned)" : output.Trim());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WhatsApp package version lookup unavailable: {ex.Message}");
        }
        Console.WriteLine();
    }

    private static string RunPowerShell(string command)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -Command \"" + command.Replace("\"", "`\"") + "\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(8000);
        return process.ExitCode == 0 ? stdout : $"{stdout}\n{stderr}";
    }

    private static string? NormalizePhone(string value)
    {
        var compact = Regex.Replace(value, "[\\s().-]", "");
        if (!compact.StartsWith('+')) return null;
        var digits = compact[1..];
        return digits.Length is >= 7 and <= 15 && digits.All(char.IsDigit) ? "+" + digits : null;
    }

    private static List<WhatsAppWindow> FindWhatsAppWindows()
    {
        var found = new List<WhatsAppWindow>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var title = process.MainWindowTitle;
                var processName = process.ProcessName;
                if (process.MainWindowHandle == IntPtr.Zero) continue;
                var isWhatsAppProcess = processName.Contains("whatsapp", StringComparison.OrdinalIgnoreCase);
                var isWhatsAppWindow = title.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase) ||
                                       Regex.IsMatch(title, @"^\(\d+\)\s*WhatsApp$", RegexOptions.IgnoreCase);
                if (!isWhatsAppProcess && !isWhatsAppWindow) continue;
                string? fileVersion = null;
                try
                {
                    var executable = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(executable))
                        fileVersion = FileVersionInfo.GetVersionInfo(executable).ProductVersion;
                }
                catch { /* Packaged apps may deny access to the executable path. */ }
                found.Add(new WhatsAppWindow(process.Id, processName, title, process.MainWindowHandle, fileVersion));
            }
            catch { /* Process may have exited while enumerating. */ }
            finally { process.Dispose(); }
        }
        return found.OrderByDescending(w => w.Handle == GetForegroundWindow()).ToList();
    }

    private static void Capture(WhatsAppWindow window, string targetPhone, int snapshotNumber)
    {
        Console.WriteLine($"\n--- Snapshot {snapshotNumber} | {DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)} ---");
        Console.WriteLine($"Target number: {targetPhone}");
        try
        {
            var root = AutomationElement.FromHandle(window.Handle);
            if (root is null)
            {
                Console.WriteLine("UI Automation could not attach to the selected window.");
                return;
            }
            Console.WriteLine($"Window name: {ReadName(root)}");
            var count = 0;
            Walk(root, 0, ref count);
            Console.WriteLine($"Nodes inspected: {count} (limit {MaxNodes}; depth limit {MaxDepth})");
            Console.WriteLine($"Target number appears in accessible text: {WindowContainsPhone(root, targetPhone)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Snapshot failed: {ex.GetType().Name}: {ex.Message}");
        }
        Console.WriteLine("--- End snapshot ---\n");
    }

    private static void Walk(AutomationElement element, int depth, ref int count)
    {
        if (count >= MaxNodes || depth > MaxDepth) return;
        count++;
        try
        {
            var indent = new string(' ', depth * 2);
            var type = element.Current.ControlType?.ProgrammaticName ?? "UnknownControl";
            var name = Clip(ReadName(element));
            var id = Clip(Safe(() => element.Current.AutomationId, ""));
            var offscreen = Safe(() => element.Current.IsOffscreen, false);
            var text = Clip(ReadText(element));
            var value = Clip(ReadValue(element));
            Console.WriteLine($"{indent}{type} | name=\"{name}\" | id=\"{id}\" | offscreen={offscreen}" +
                              (value.Length > 0 ? $" | value=\"{value}\"" : "") +
                              (text.Length > 0 ? $" | text=\"{text}\"" : ""));

            if (depth == MaxDepth || count >= MaxNodes) return;
            var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
            while (child is not null && count < MaxNodes)
            {
                Walk(child, depth + 1, ref count);
                child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            }
        }
        catch (ElementNotAvailableException) { }
        catch (COMException ex) { Console.WriteLine($"{new string(' ', depth * 2)}[UIA error: {ex.Message}]"); }
    }

    private static bool WindowContainsPhone(AutomationElement root, string phone)
    {
        var count = 0;
        return ContainsPhone(root, phone, 0, ref count);
    }

    private static bool ContainsPhone(AutomationElement element, string phone, int depth, ref int count)
    {
        if (count++ >= MaxNodes || depth > MaxDepth) return false;
        try
        {
            var digits = Regex.Replace(ReadName(element) + " " + ReadText(element) + " " + ReadValue(element), "\\D", "");
            var target = Regex.Replace(phone, "\\D", "");
            if (digits.Contains(target, StringComparison.Ordinal)) return true;
            var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
            while (child is not null)
            {
                if (ContainsPhone(child, phone, depth + 1, ref count)) return true;
                child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            }
        }
        catch { }
        return false;
    }

    private static string ReadName(AutomationElement element) => Safe(() => element.Current.Name, "");

    private static string ReadValue(AutomationElement element)
    {
        try { return element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? ((ValuePattern)pattern).Current.Value : ""; }
        catch { return ""; }
    }

    private static string ReadText(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return "";
            return ((TextPattern)pattern).DocumentRange.GetText(MaxTextLength);
        }
        catch { return ""; }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    private static string Clip(string value)
    {
        value = value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        return value.Length <= MaxTextLength ? value : value[..MaxTextLength] + "…";
    }

    private sealed record WhatsAppWindow(int ProcessId, string ProcessName, string Title, IntPtr Handle, string? FileVersion);
}
