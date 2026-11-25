using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopDock;

internal sealed record DockDisplayProfile(DockMonitor Monitor, string ProfileDirectory, string ThemeDockId, bool UsesLegacyProfile);

internal sealed class DockDisplayProfiles
{
    private const int SchemaVersion = 1;
    private const int MaximumBytes = 4096;
    private readonly string directory;
    private readonly string indexPath;

    private sealed record Index(int SchemaVersion, string LegacyMonitorId);

    private DockDisplayProfiles(string directory, string? legacyMonitorId, string? loadError)
    {
        this.directory = directory;
        indexPath = Path.Combine(directory, "display-profiles.json");
        LegacyMonitorId = legacyMonitorId;
        LoadError = loadError;
    }

    internal string? LegacyMonitorId { get; private set; }
    internal string? LoadError { get; }

    internal static DockDisplayProfiles Open(string configDirectory)
    {
        string directory = Directory.CreateDirectory(configDirectory).FullName;
        try
        {
            Index index = DockThemeJson.Read<Index>(Path.Combine(directory, "display-profiles.json"), MaximumBytes);
            if (index.SchemaVersion != SchemaVersion)
                throw new InvalidDataException($"显示器配置索引版本 {index.SchemaVersion} 不受支持。");
            ValidateId(index.LegacyMonitorId);
            return new DockDisplayProfiles(directory, index.LegacyMonitorId, null);
        }
        catch (FileNotFoundException)
        {
            return new DockDisplayProfiles(directory, null, null);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new DockDisplayProfiles(directory, null, $"无法读取显示器配置索引，原文件已保留。{error.Message}");
        }
    }

    internal IReadOnlyList<DockDisplayProfile> Resolve(IReadOnlyList<DockMonitor> monitors, string? preferredLegacyMonitorId = null)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DockMonitor monitor in monitors)
        {
            if (monitor == null)
                throw new ArgumentException("显示器列表含有空值。");
            ValidateId(monitor.Id);
            if (!identities.Add(monitor.Id))
                throw new ArgumentException("显示器列表含有重复标识。");
        }

        if (LegacyMonitorId == null && monitors.Count > 0)
        {
            DockMonitor first = monitors.FirstOrDefault(monitor => SameId(monitor.Id, preferredLegacyMonitorId))
                ?? monitors.FirstOrDefault(monitor => monitor.Primary) ?? monitors[0];
            if (LoadError == null)
                DockThemeJson.Write(indexPath, new Index(SchemaVersion, first.Id), MaximumBytes);
            LegacyMonitorId = first.Id;
        }

        return monitors.Select(monitor =>
        {
            if (SameId(monitor.Id, LegacyMonitorId))
                return new DockDisplayProfile(monitor, directory, DockThemeState.PrimaryDockId, true);

            string key = "display-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(monitor.Id.ToUpperInvariant())))[..32];
            return new DockDisplayProfile(monitor, Path.Combine(directory, "display-docks", key), key, false);
        }).ToArray();
    }

    private static bool SameId(string left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl))
            throw new ArgumentException("显示器标识不完整。");
    }
}
