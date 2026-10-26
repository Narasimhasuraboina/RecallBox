using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace RecallBox.App;

public sealed record VisibleOcrLine(string Text, double Left, double Top, double Width, double Height);
public sealed record VisibleOcrSnapshot(int Width, int Height, bool WindowIsForeground, IReadOnlyList<VisibleOcrLine> Lines);

public static class VisibleWindowOcr
{
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

    private struct Rect { public int Left, Top, Right, Bottom; }

    public static bool IsAvailable
    {
        get
        {
            try { return OperatingSystem.IsWindows() && OcrEngine.TryCreateFromUserProfileLanguages() is not null; }
            catch { return false; }
        }
    }

    public static async Task<VisibleOcrSnapshot> CaptureAsync(IntPtr windowHandle)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Visible-window OCR is available only on Windows.");
        if (!GetWindowRect(windowHandle, out var rect)) throw new InvalidOperationException("WhatsApp window bounds are unavailable.");
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 100 || height < 100 || width > OcrEngine.MaxImageDimension || height > OcrEngine.MaxImageDimension)
            throw new InvalidOperationException($"WhatsApp window size is outside OCR limits: {width}x{height}.");

        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("Windows OCR is unavailable for the current user language settings.");
        var foreground = GetForegroundWindow() == windowHandle;
        using var image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        bool printed;
        using (var graphics = Graphics.FromImage(image))
        {
            var hdc = graphics.GetHdc();
            try { printed = PrintWindow(windowHandle, hdc, 2); }
            finally { graphics.ReleaseHdc(hdc); }
            if (!printed && foreground)
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                printed = true;
            }
        }
        if (!printed) throw new InvalidOperationException("Windows could not capture the already-running WhatsApp window in the background. RecallBox will not bring it forward; import an official WhatsApp export for a noninteractive archive.");

        var locked = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(locked.Stride) * height];
            Marshal.Copy(locked.Scan0, bytes, 0, bytes.Length);
            using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
                CryptographicBuffer.CreateFromByteArray(bytes), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
            var recognized = await engine.RecognizeAsync(softwareBitmap);
            var lines = recognized.Lines.Select(line =>
            {
                var words = line.Words;
                if (words.Count == 0) return new VisibleOcrLine(line.Text, 0, 0, 0, 0);
                var left = words.Min(word => word.BoundingRect.X);
                var top = words.Min(word => word.BoundingRect.Y);
                var right = words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
                var bottom = words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);
                return new VisibleOcrLine(line.Text, left, top, right - left, bottom - top);
            }).ToArray();
            return new VisibleOcrSnapshot(width, height, foreground, lines);
        }
        finally { image.UnlockBits(locked); }
    }

}
