using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using System.Windows.Forms;
using RecallBox.App;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("RecallBox's WhatsApp UI reader requires Windows.");

var lanMode = args.Contains("--lan", StringComparer.OrdinalIgnoreCase);
var port = 5180;
var accessToken = lanMode ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_') : null;
var builderArgs = args.Where(arg => !arg.Equals("--lan", StringComparison.OrdinalIgnoreCase)).ToArray();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = builderArgs,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
const long maxImportBytes = 4L * 1024 * 1024 * 1024;
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = maxImportBytes);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxImportBytes;
    options.ListenLocalhost(port);
    if (lanMode)
    {
        foreach (var address in PrivateAddresses())
            if (!IPAddress.IsLoopback(address)) options.Listen(address, port);
    }
});
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<WhatsAppUiSource>();
builder.Services.AddSingleton<OfficialExportImporter>();
builder.Services.AddSingleton<ArchiveService>();

var app = builder.Build();
var settingsStore = app.Services.GetRequiredService<SettingsStore>();
var settings = settingsStore.Load();
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    if (lanMode && context.Request.Path.StartsWithSegments("/api") && context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote))
    {
        var supplied = context.Request.Headers["Authorization"].ToString();
        var expected = "Bearer " + accessToken;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Enter the LAN access token shown by RecallBox on the Windows computer." });
            return;
        }
    }
    await next();
});

app.MapGet("/api/status", () =>
{
    var whatsapp = WhatsAppUiSource.InspectAvailability();
    return Results.Ok(new
    {
        lanEnabled = lanMode,
        preferredFolder = settings.PreferredFolder,
        localUrl = $"http://localhost:{port}",
        networkUrls = lanMode ? PrivateAddresses().Where(a => !IPAddress.IsLoopback(a)).Select(a => $"http://{FormatAddress(a)}:{port}").ToArray() : Array.Empty<string>(),
        whatsapp = new { found = whatsapp.Found, accessibleMessageControls = whatsapp.AccessibleMessageControls, canScroll = whatsapp.CanScroll, ocrFallbackAvailable = whatsapp.OcrAvailable },
        whatsappFound = whatsapp.Found,
        chatScanAvailable = whatsapp.AccessibleMessageControls > 0 || whatsapp.OcrAvailable,
        jobs = app.Services.GetRequiredService<ArchiveService>().Jobs
    });
});

app.MapPost("/api/settings", (AppSettings update) =>
{
    if (string.IsNullOrWhiteSpace(update.PreferredFolder)) return Results.BadRequest(new { error = "Choose an archive destination folder." });
    var fullPath = Path.GetFullPath(update.PreferredFolder.Trim());
    Directory.CreateDirectory(fullPath);
    settings = new AppSettings(fullPath);
    settingsStore.Save(settings);
    return Results.Ok(settings);
});

app.MapPost("/api/browse", async () =>
{
    var selected = await ShowFolderPickerAsync(settings.PreferredFolder);
    return selected is null ? Results.Json(new { cancelled = true }) : Results.Json(new { cancelled = false, path = selected });
});

app.MapPost("/api/download", (DownloadRequest request, ArchiveService archives) =>
{
    var phone = NormalizePhone(request.PhoneNumber);
    if (phone is null) return Results.BadRequest(new { error = "Enter a valid international phone number, for example +919876543210." });
    if (!request.ConfirmedOwnChat) return Results.BadRequest(new { error = "Confirm that the open WhatsApp chat belongs to your own account and matches this number." });
    var folder = string.IsNullOrWhiteSpace(request.DestinationFolder) ? settings.PreferredFolder : request.DestinationFolder.Trim();
    try
    {
        Directory.CreateDirectory(Path.GetFullPath(folder));
        settings = new AppSettings(Path.GetFullPath(folder));
        settingsStore.Save(settings);
        return Results.Accepted($"/api/jobs", archives.Start(phone, folder));
    }
    catch (Exception ex) { return Results.BadRequest(new { error = $"Could not use the destination folder: {ex.Message}" }); }
});

app.MapPost("/api/import", async (HttpRequest request, ArchiveService archives) =>
{
    try
    {
        var form = await request.ReadFormAsync();
        var phone = NormalizePhone(form["phoneNumber"].ToString());
        if (phone is null) return Results.BadRequest(new { error = "Enter a valid international phone number." });
        if (!bool.TryParse(form["confirmedOwnChat"], out var confirmed) || !confirmed)
            return Results.BadRequest(new { error = "Confirm that the export is from a conversation in your own account and matches this number." });
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Choose a non-empty WhatsApp .txt or .zip export." });
        var extension = Path.GetExtension(file.FileName);
        if (!extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Only WhatsApp .txt and .zip exports are supported." });
        var folder = string.IsNullOrWhiteSpace(form["destinationFolder"]) ? settings.PreferredFolder : form["destinationFolder"].ToString().Trim();
        var root = Path.GetFullPath(folder);
        Directory.CreateDirectory(root);
        settings = new AppSettings(root);
        settingsStore.Save(settings);
        var staging = Path.Combine(root, ".recallbox", "imports");
        Directory.CreateDirectory(staging);
        var stagedFile = Path.Combine(staging, Guid.NewGuid().ToString("N") + extension.ToLowerInvariant());
        try
        {
            await using var output = File.Create(stagedFile);
            await file.CopyToAsync(output);
        }
        catch
        {
            try { File.Delete(stagedFile); } catch { }
            throw;
        }
        return Results.Accepted("/api/jobs", archives.StartImport(phone, root, stagedFile));
    }
    catch (Exception ex) { return Results.BadRequest(new { error = $"Could not import this export: {ex.Message}" }); }
});

app.MapGet("/api/jobs", (ArchiveService archives) => Results.Ok(archives.Jobs));
app.MapGet("/api/jobs/{id}", (string id, ArchiveService archives) => archives.Get(id) is { } job ? Results.Ok(job) : Results.NotFound());
app.MapPost("/api/jobs/{id}/retry", (string id, ArchiveService archives) =>
{
    var retry = archives.Retry(id);
    return retry is null ? Results.NotFound() : Results.Accepted("/api/jobs", retry);
});
app.MapPost("/api/jobs/{id}/cancel", (string id, ArchiveService archives) =>
{
    if (archives.Get(id) is not { } job) return Results.NotFound();
    return archives.Cancel(id)
        ? Results.Accepted($"/api/jobs/{id}", job)
        : Results.Conflict(new { error = "This job is no longer running or cannot be cancelled." });
});

Console.WriteLine("RecallBox WhatsApp Archive");
Console.WriteLine($"Local UI: http://localhost:{port}");
Console.WriteLine(lanMode ? "LAN access: enabled on private interface addresses only." : "LAN access: disabled (localhost only). Start with --lan to enable authenticated private-network access.");
if (lanMode)
{
    Console.WriteLine("Private network URLs:");
    foreach (var address in PrivateAddresses().Where(a => !IPAddress.IsLoopback(a))) Console.WriteLine($"  http://{FormatAddress(address)}:{port}");
    Console.WriteLine($"LAN access token (enter on Android): {accessToken}");
}
Console.WriteLine("Keep this window open while using RecallBox.");
await app.RunAsync();

static string? NormalizePhone(string value)
{
    var compact = System.Text.RegularExpressions.Regex.Replace(value ?? "", "[\\s().-]", "");
    if (!compact.StartsWith('+')) return null;
    var digits = compact[1..];
    return digits.Length is >= 7 and <= 15 && digits.All(char.IsDigit) ? "+" + digits : null;
}

static IEnumerable<IPAddress> PrivateAddresses()
{
    foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
        foreach (var item in network.GetIPProperties().UnicastAddresses)
        {
            var ip = item.Address;
            if (ip.AddressFamily == AddressFamily.InterNetwork && IsPrivateV4(ip)) yield return ip;
            if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv6UniqueLocal) yield return ip;
        }
    }
}

static bool IsPrivateV4(IPAddress address)
{
    var b = address.GetAddressBytes();
    return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31;
}

static string FormatAddress(IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();

static Task<string?> ShowFolderPickerAsync(string initialPath)
{
    var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        try
        {
            using var dialog = new FolderBrowserDialog { Description = "Choose the WhatsApp archive folder", UseDescriptionForTitle = true, SelectedPath = initialPath, ShowNewFolderButton = true };
            completion.TrySetResult(dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null);
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();
    return completion.Task;
}
