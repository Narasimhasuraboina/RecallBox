using System.Text.Json;

namespace RecallBox.App;

public sealed class SettingsStore
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecallBox", "settings.json");
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AppSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), _json);
            if (!string.IsNullOrWhiteSpace(settings?.PreferredFolder)) return settings;
        }
        catch { }
        return new AppSettings(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WhatsApp-Archive"));
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicWrite(_path, JsonSerializer.Serialize(settings, _json));
    }

    public static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(Path.GetTempPath(), $"RecallBox-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, content);
            try
            {
                if (File.Exists(path)) File.Replace(temporary, path, null, true);
                else File.Move(temporary, path);
            }
            catch (UnauthorizedAccessException)
            {
                // Some synced or managed folders allow writing existing files but
                // deny delete/rename on the directory. Copy truncates the file in
                // place without requiring delete-child permission.
                File.Copy(temporary, path, true);
            }
            catch (IOException) when (File.Exists(path))
            {
                File.Copy(temporary, path, true);
            }
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }
}
