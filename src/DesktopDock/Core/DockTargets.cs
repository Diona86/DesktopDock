namespace DesktopDock;

internal static class DockTargets
{
    internal static (DockItemKind Kind, string Target) Resolve(string target)
    {
        string value = TargetText(target);
        if (!Path.IsPathFullyQualified(value))
            return (DockItemKind.Url, ParseUrl(value).AbsoluteUri);

        string path = FullPath(value);
        if (Directory.Exists(path))
            return (DockItemKind.Folder, Path.TrimEndingDirectorySeparator(path));
        if (File.Exists(path))
            return (FileKind(path), path);

        throw new FileNotFoundException("找不到项目目标，请确认文件或文件夹仍然存在。", path);
    }

    internal static Uri ParseUrl(string target)
    {
        string value = TargetText(target);
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            uri.Host.Length > 0)
            return uri;

        throw new ArgumentException("请输入完整的文件路径，或以 http://、https:// 开头的网址。");
    }

    internal static string TargetPath(string target)
    {
        string value = TargetText(target);
        if (!Path.IsPathFullyQualified(value))
            throw new ArgumentException("请输入文件或文件夹的完整路径。");

        return FullPath(value);
    }

    internal static string? CustomIconPath(string? path, bool requireFile)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string normalized = TargetPath(path);
        if (Path.GetExtension(normalized).ToLowerInvariant() is not (".ico" or ".png" or ".jpg" or ".jpeg" or ".bmp"))
            throw new ArgumentException("请选择 ICO、PNG、JPG 或 BMP 图像作为图标。");
        if (requireFile && !File.Exists(normalized))
            throw new FileNotFoundException("找不到所选图标文件。", normalized);

        return normalized;
    }

    internal static void ValidateReference(DockItem item)
    {
        if (item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.Name) || !Enum.IsDefined(item.Kind))
            throw new ArgumentException("项目的标识、名称或类型无效。");

        if (item.Kind == DockItemKind.Url)
            ParseUrl(item.Target);
        else
        {
            string path = TargetPath(item.Target);
            if (item.Kind != DockItemKind.Folder && item.Kind != FileKind(path))
                throw new ArgumentException("项目类型与文件扩展名不一致。");
        }

        CustomIconPath(item.CustomIconPath, requireFile: false);
    }

    internal static string ExistingPath(DockItem item)
    {
        string path = TargetPath(item.Target);
        if (item.Kind == DockItemKind.Folder ? Directory.Exists(path) : File.Exists(path) && FileKind(path) == item.Kind)
            return path;

        throw new FileNotFoundException("项目目标已失效。请编辑项目并重新选择路径。", path);
    }

    internal static string DefaultName(DockItemKind kind, string target)
    {
        if (kind == DockItemKind.Url)
            return ParseUrl(target).Host;

        string path = Path.TrimEndingDirectorySeparator(target);
        string name = Path.GetFileName(path);
        if (kind is DockItemKind.Application or DockItemKind.Shortcut)
            name = Path.GetFileNameWithoutExtension(path);

        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static DockItemKind FileKind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".exe" => DockItemKind.Application,
        ".lnk" => DockItemKind.Shortcut,
        _ => DockItemKind.File
    };

    private static string FullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("文件路径格式无效或过长。", error);
        }
    }

    private static string TargetText(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Any(char.IsControl))
            throw new ArgumentException("请输入有效的项目路径或网址。");

        return target.Trim();
    }
}
