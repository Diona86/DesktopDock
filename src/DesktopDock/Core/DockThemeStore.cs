using System.Text.Json;

namespace DesktopDock;

public sealed class DockThemeStore
{
    private const int SchemaVersion = 1;
    private const int MaximumBytes = 1024 * 1024;

    private sealed record Configuration(int SchemaVersion, DockThemeState State);

    private readonly object gate = new();
    private readonly string path;
    private volatile DockThemeState current;

    private DockThemeStore(string path, DockThemeState state, string? loadError = null)
    {
        this.path = path;
        current = state;
        LoadError = loadError;
    }

    public DockThemeState Current => current;
    public string? LoadError { get; }
    public event EventHandler? Changed;

    public static DockThemeStore Open(string configurationDirectory)
    {
        string path = Path.Combine(Directory.CreateDirectory(configurationDirectory).FullName, "themes.json");
        try
        {
            Configuration configuration = DockThemeJson.Read<Configuration>(path, MaximumBytes);
            if (configuration.SchemaVersion != SchemaVersion)
                throw new InvalidDataException($"主题配置版本 {configuration.SchemaVersion} 与当前版本不兼容。");
            configuration.State.Validate();
            return new DockThemeStore(path, configuration.State);
        }
        catch (FileNotFoundException)
        {
            return new DockThemeStore(path, new());
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new DockThemeStore(path, new(), $"无法读取主题配置。已使用默认主题并保留原文件。{error.Message}");
        }
    }

    public void Save(DockThemeState state)
    {
        lock (gate)
        {
            if (LoadError != null)
                throw new InvalidOperationException(LoadError);
            if (state == current)
                return;

            state.Validate();
            DockThemeJson.Write(path, new Configuration(SchemaVersion, state), MaximumBytes);
            current = state;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
