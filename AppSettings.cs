using System.IO;
using System.Text.Json;

namespace ILPatchStudio;

internal sealed class AssemblySession
{
    public string Path { get; set; } = "";
    public List<string> Dependencies { get; set; } = [];
    public string Search { get; set; } = "";
    public string? Method { get; set; }
}

internal sealed class AppSettings
{
    public string Language { get; set; } = "system";
    public bool RestoreLastSession { get; set; } = true;
    public bool IncludeUsings { get; set; } = true;
    public bool SkipMissing { get; set; }
    public bool CheckDuplicates { get; set; }
    public bool CheckBoundaries { get; set; }
    public bool PreferMember { get; set; }
    public double CodeFontSize { get; set; } = 13;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 1260;
    public double Height { get; set; } = 860;
    public double SidebarWidth { get; set; } = 255;
    public double SourceRatio { get; set; } = 0.51;
    public double DraftRatio { get; set; } = 1d / 3;
    public bool Maximized { get; set; }
    public string? LastAssembly { get; set; }
    public List<AssemblySession> Recent { get; set; } = [];

    public AssemblySession? Find(string path) =>
        Recent.Find(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static string DataDirectory => Path.Combine(AppContext.BaseDirectory, "data");
    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    public static AppSettings Load(out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options)
                ?? throw new JsonException("Expected a settings object.");
            settings.Recent = (settings.Recent ?? []).Where(entry => !string.IsNullOrWhiteSpace(entry?.Path))
                .DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).Take(20).ToList();
            foreach (var entry in settings.Recent)
            {
                entry.Search ??= "";
                entry.Dependencies = (entry.Dependencies ?? []).Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = exception.Message;
            if (exception is JsonException)
            {
                try
                {
                    string backup = Path.Combine(DataDirectory, $"settings.invalid-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json");
                    File.Move(FilePath, backup);
                }
                catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
                {
                    problem = backupError.Message;
                    CanSave = false;
                }
            }
            return new AppSettings();
        }
    }

    private static bool CanSave { get; set; } = true;

    public static bool TrySave(AppSettings settings, out string? error)
    {
        if (!CanSave)
        {
            error = Ui.Text("无法备份损坏的配置。", "Unable to back up the damaged settings file.");
            return false;
        }

        string temporary = Path.Combine(DataDirectory, $"settings-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
            File.Move(temporary, FilePath, overwrite: true);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
