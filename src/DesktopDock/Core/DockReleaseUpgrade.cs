using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DesktopDock;

internal static partial class DockReleaseUpgrade
{
    private const int ManifestLimit = 1024 * 1024;
    private const long FileLimit = 32 * 1024 * 1024;
    private const long TotalLimit = 256 * 1024 * 1024;
    private const int FileCountLimit = 4096;
    private const string DockFile = "dock.json";
    private const string ThemesFile = "themes.json";
    private const string DisplayIndex = "display-profiles.json";
    private const string DesktopJournal = "desktop-icons.json";
    private const string RuntimeLog = "runtime.log";
    private const string ProfileDirectory = "profile";
    private const string ManifestFile = "backup.json";
    private const string Format = "desktop-dock-profile-backup";

    private sealed record Entry(string Path, long Length, string Sha256);
    private sealed record Manifest(string Format, int SchemaVersion, string FromVersion, string ToVersion,
        DateTimeOffset CreatedUtc, Entry[] Files);

    internal static void Validate(string configurationDirectory) =>
        ValidateCompatibility(AbsoluteDirectory(configurationDirectory));

    internal static string Prepare(string configurationDirectory, string backupRoot, string fromVersion, string toVersion)
    {
        Version from = ParseVersion(fromVersion);
        Version to = ParseVersion(toVersion);
        if (from > to)
            throw new InvalidOperationException("当前安装版本高于目标版本。请先按降级说明处理配置。");

        string profile = AbsoluteDirectory(configurationDirectory);
        string backups = OutsideProfile(profile, backupRoot);
        using var leases = new ProfileLeases(profile);
        ValidateCompatibility(profile);
        return WriteBackup(profile, backups, from.ToString(), to.ToString(), requireCompatible: true);
    }

    internal static string Restore(string configurationDirectory, string snapshotDirectory)
    {
        string destination = AbsoluteDirectory(configurationDirectory);
        string snapshot = OutsideProfile(destination, snapshotDirectory);
        Manifest manifest = ReadBackup(snapshot);
        string source = Path.Combine(snapshot, ProfileDirectory);
        ValidateCompatibility(source);

        using var leases = new ProfileLeases(destination, createRoot: true);
        string checkpoint = WriteBackup(destination, Path.GetDirectoryName(snapshot)!, manifest.ToVersion,
            manifest.ToVersion, requireCompatible: false);
        string[] currentFiles = OwnedFiles(destination);
        try
        {
            ApplyBackup(source, destination, manifest, currentFiles);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            try
            {
                Manifest before = ReadBackup(checkpoint);
                ApplyBackup(Path.Combine(checkpoint, ProfileDirectory), destination, before, OwnedFiles(destination));
            }
            catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                throw new IOException($"恢复未完成。先前的配置保存在 {checkpoint}。", new AggregateException(error, rollbackError));
            }
            throw new IOException($"恢复失败，恢复前的配置已撤回。检查点位于 {checkpoint}。", error);
        }
        return checkpoint;
    }

    internal static void RemoveOwnedData(string configurationDirectory)
    {
        string profile = AbsoluteDirectory(configurationDirectory);
        using var leases = new ProfileLeases(profile);
        ValidateCompatibility(profile);
        if (DesktopIconService.HasUnresolvedRecovery(profile))
            throw new InvalidOperationException("桌面图标仍有待核查的恢复记录。已保留用户配置。");
        foreach (string file in OwnedFiles(profile, includeRuntimeLog: true))
            File.Delete(SafePath(profile, file));
    }

    private static void ValidateCompatibility(string profile)
    {
        string[] files = OwnedFiles(profile);
        foreach (string file in files.Where(name => name.Equals(DockFile, StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("/" + DockFile, StringComparison.OrdinalIgnoreCase)))
            DockSession.VerifyReleaseConfiguration(SafePath(profile, file));
        if (files.Contains(ThemesFile, StringComparer.OrdinalIgnoreCase) && DockThemeStore.Open(profile).LoadError is string themeError)
            throw new InvalidDataException(themeError);
        if (files.Contains(DisplayIndex, StringComparer.OrdinalIgnoreCase) && DockDisplayProfiles.Open(profile).LoadError is string displayError)
            throw new InvalidDataException(displayError);
        if (files.Contains(DesktopJournal, StringComparer.OrdinalIgnoreCase))
            _ = DesktopIconService.HasUnresolvedRecovery(profile);
    }

    private static string WriteBackup(string source, string backupRoot, string from, string to, bool requireCompatible)
    {
        Directory.CreateDirectory(backupRoot);
        string id = $"backup-{DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff'Z'", CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}";
        string pending = SafePath(backupRoot, id + ".pending");
        string completed = SafePath(backupRoot, id);
        string profile = Path.Combine(pending, ProfileDirectory);
        var created = new List<string>();
        Directory.CreateDirectory(profile);
        try
        {
            var entries = new List<Entry>();
            long total = 0;
            foreach (string file in OwnedFiles(source))
            {
                string destination = SafePath(profile, file);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = new FileStream(SafePath(source, file), FileMode.Open, FileAccess.Read, FileShare.Read);
                total += input.Length;
                if (input.Length > FileLimit || total > TotalLimit)
                    throw new InvalidDataException("配置超出升级备份的大小限制。");
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created.Add(destination);
                    input.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }
                input.Position = 0;
                string hash = Convert.ToHexString(SHA256.HashData(input));
                if (new FileInfo(destination).Length != input.Length ||
                    !string.Equals(Hash(destination), hash, StringComparison.Ordinal))
                    throw new InvalidDataException($"复制后校验失败。{file}");
                entries.Add(new(file, input.Length, hash));
            }

            string manifestPath = Path.Combine(pending, ManifestFile);
            DockThemeJson.Write(manifestPath, new Manifest(Format, 1, from, to, DateTimeOffset.UtcNow, [.. entries]), ManifestLimit);
            created.Add(manifestPath);
            _ = ReadBackup(pending);
            if (requireCompatible)
                ValidateCompatibility(profile);
            Directory.Move(pending, completed);
            return completed;
        }
        catch
        {
            // Remove only the files created by this attempt. An unknown or linked entry is preserved.
            foreach (string path in created)
                TryCleanup(() => File.Delete(SafePath(pending, Path.GetRelativePath(pending, path))));
            foreach (string directory in created.Select(path => Path.GetDirectoryName(path)!).Distinct(StringComparer.OrdinalIgnoreCase)
                .Concat([Path.Combine(profile, "display-docks"), profile, pending]).OrderByDescending(path => path.Length))
                TryCleanup(() => Directory.Delete(SafePath(backupRoot, Path.GetRelativePath(backupRoot, directory))));
            throw;
        }
    }

    private static Manifest ReadBackup(string snapshot)
    {
        string profile = SafePath(snapshot, ProfileDirectory);
        Manifest manifest = DockThemeJson.Read<Manifest>(SafePath(snapshot, ManifestFile), ManifestLimit);
        if (manifest.Format != Format || manifest.SchemaVersion != 1 || manifest.CreatedUtc == default ||
            manifest.Files.Length > FileCountLimit || ParseVersion(manifest.FromVersion) > ParseVersion(manifest.ToVersion))
            throw new InvalidDataException("升级备份清单无效。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (Entry file in manifest.Files)
        {
            if (file == null || !IsOwnedPath(file.Path) || !seen.Add(file.Path) ||
                file.Length < 0 || file.Length > FileLimit || (total += file.Length) > TotalLimit ||
                file.Sha256.Length != 64 || !file.Sha256.All(char.IsAsciiHexDigit))
                throw new InvalidDataException("升级备份包含无效文件条目。");
            string path = SafePath(profile, file.Path);
            if (new FileInfo(path).Length != file.Length || !string.Equals(Hash(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"升级备份的 {file.Path} 校验失败。");
        }
        if (!Directory.Exists(profile) || !seen.SetEquals(OwnedFiles(profile)))
            throw new InvalidDataException("备份文件与清单不一致。");
        return manifest;
    }

    private static void ApplyBackup(string source, string destination, Manifest manifest, IReadOnlyList<string> currentFiles)
    {
        var restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Entry file in manifest.Files)
        {
            if (IsDesktopJournal(file.Path))
                continue;
            string target = SafePath(destination, file.Path);
            if (File.Exists(target) && new FileInfo(target).Length == file.Length &&
                string.Equals(Hash(target), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                restored.Add(file.Path);
                continue;
            }
            string directory = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, $".dock-restore-{Guid.NewGuid():N}.tmp");
            bool created = false;
            try
            {
                using (var input = new FileStream(SafePath(source, file.Path), FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    if (input.Length != file.Length)
                        throw new InvalidDataException($"恢复来源的 {file.Path} 在操作期间发生变化。");
                    input.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }
                if (!string.Equals(Hash(temporary), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"恢复来源的 {file.Path} 在操作期间发生变化。");
                if (File.Exists(target))
                    File.Replace(temporary, target, null);
                else
                    File.Move(temporary, target);
                restored.Add(file.Path);
            }
            finally
            {
                if (created)
                    TryCleanup(() => File.Delete(temporary));
            }
        }

        foreach (string file in currentFiles)
            if (!IsDesktopJournal(file) && !restored.Contains(file))
                File.Delete(SafePath(destination, file));
    }

    private static string[] OwnedFiles(string root, bool includeRuntimeLog = false)
    {
        if (!Directory.Exists(root))
            return [];
        var files = new List<string>();
        long total = 0;
        foreach (string folder in ProfileFolders(root))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(folder))
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (!IsOwnedPath(relative) && !(includeRuntimeLog && relative.Equals(RuntimeLog, StringComparison.OrdinalIgnoreCase)))
                    continue;
                string fullPath = SafePath(root, relative);
                var file = new FileInfo(fullPath);
                if ((file.Attributes & FileAttributes.Directory) != 0 || file.Length > FileLimit ||
                    (total += file.Length) > TotalLimit || files.Count >= FileCountLimit)
                    throw new InvalidDataException($"配置文件无效或超出升级备份限制。{relative}");
                files.Add(relative);
            }
        }
        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> ProfileFolders(string root)
    {
        yield return root;
        string displays = SafePath(root, "display-docks");
        if (File.Exists(displays))
            throw new InvalidDataException("显示器配置位置不是目录。");
        if (!Directory.Exists(displays))
            yield break;
        foreach (string folder in Directory.EnumerateFileSystemEntries(displays))
        {
            string name = Path.GetFileName(folder);
            if (!DisplayFolderPattern().IsMatch(name))
                continue;
            string checkedPath = SafePath(displays, name);
            if (!Directory.Exists(checkedPath))
                throw new InvalidDataException($"显示器配置目录无效。{name}");
            yield return checkedPath;
        }
    }

    private static bool IsOwnedPath(string path)
    {
        string[] parts = path.Split('/');
        return parts.Length switch
        {
            1 => parts[0].ToLowerInvariant() is DockFile or ThemesFile or DisplayIndex or DesktopJournal ||
                MigrationBackupPattern().IsMatch(parts[0]),
            3 => parts[0].Equals("display-docks", StringComparison.OrdinalIgnoreCase) &&
                DisplayFolderPattern().IsMatch(parts[1]) &&
                (parts[2].Equals(DockFile, StringComparison.OrdinalIgnoreCase) || MigrationBackupPattern().IsMatch(parts[2])),
            _ => false
        };
    }

    private static bool IsDesktopJournal(string file) => file.Equals(DesktopJournal, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\Adisplay-[0-9A-Fa-f]{32}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DisplayFolderPattern();

    [GeneratedRegex(@"\Adock\.v[1-9][0-9]{0,2}-[0-9A-Fa-f]{32}\.bak\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MigrationBackupPattern();

    private static Version ParseVersion(string version)
    {
        if (!Version.TryParse(version, out Version? parsed) || parsed.Build < 0 || parsed.Revision != -1)
            throw new InvalidDataException("应用版本应为 major.minor.patch。");
        return parsed;
    }

    private static string AbsoluteDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || (OperatingSystem.IsWindows() &&
            (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))))
            throw new ArgumentException("配置与备份目录需要常规绝对路径。");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        RejectLinks(full);
        if (File.Exists(full))
            throw new InvalidDataException("配置和升级备份位置必须是目录。");
        return full;
    }

    private static string OutsideProfile(string profile, string path)
    {
        string full = AbsoluteDirectory(path);
        string relative = Path.GetRelativePath(profile, full);
        if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("备份目录须位于正在处理的配置目录之外。");
        return full;
    }

    private static string SafePath(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative));
        string boundary = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("备份路径超出预定目录。");
        RejectLinks(path);
        return path;
    }

    private static void RejectLinks(string path)
    {
        for (string? part = path; part != null; part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"升级操作拒绝链接或重解析点。{part}");
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        }
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > FileLimit)
            throw new InvalidDataException("备份文件超出大小限制。");
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryCleanup(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    private sealed class ProfileLeases : IDisposable
    {
        private readonly List<FileStream> streams = [];

        internal ProfileLeases(string root, bool createRoot = false)
        {
            if (createRoot)
                Directory.CreateDirectory(root);
            if (!Directory.Exists(root))
                return;
            try
            {
                foreach (string folder in ProfileFolders(root))
                    streams.Add(new FileStream(SafePath(folder, "dock.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException error)
            {
                Dispose();
                throw new IOException("无法取得配置独占锁。请关闭使用该配置的 Desktop Dock，或检查配置目录。", error);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (FileStream stream in streams)
                stream.Dispose();
            streams.Clear();
        }
    }
}

public sealed partial class DockSession
{
    internal static void VerifyReleaseConfiguration(string path) => _ = Read(path);
}
