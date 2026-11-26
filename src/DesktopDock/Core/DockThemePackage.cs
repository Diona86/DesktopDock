using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopDock;

public static class DockThemePackage
{
    public const string Extension = ".docktheme";
    public const int SchemaVersion = 1;
    public const int MaximumBytes = 32 * 1024;
    private const string Format = "desktop-dock-theme";

    private sealed record Package(string Format, int SchemaVersion, DockTheme Theme);

    public static DockTheme Read(string path)
    {
        path = ThemePath(path);
        return ReadPackage(DockThemeJson.ReadBytes(path, MaximumBytes));
    }

    public static DockTheme Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("剪贴板中没有有效的 Desktop Dock 主题文本。");

        string unwrapped = UnwrapCodeFence(text);
        byte[] bytes = Encoding.UTF8.GetBytes(unwrapped);
        if (bytes.Length == 0 || bytes.Length > MaximumBytes)
            throw new InvalidDataException($"主题文本不可为空，且最多 {MaximumBytes / 1024} KiB。");

        return ReadPackage(bytes);
    }

    public static void Write(string path, DockTheme theme)
    {
        path = ThemePath(path);
        theme.Validate();
        DockThemeJson.Write(path, new Package(Format, SchemaVersion, theme), MaximumBytes);
    }

    public static string Serialize(DockTheme theme)
    {
        theme.Validate();
        byte[] bytes = DockThemeJson.SerializeToBytes(new Package(Format, SchemaVersion, theme), MaximumBytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private static DockTheme ReadPackage(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using JsonDocument document = DockThemeJson.ParseDocument(bytes);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out JsonElement format) ||
                format.ValueKind != JsonValueKind.String || format.GetString() != Format)
                throw new InvalidDataException("选择的内容不是 Desktop Dock 主题。");
            if (!root.TryGetProperty("schemaVersion", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number))
                throw new InvalidDataException("主题缺少有效版本。");
            if (number != SchemaVersion)
                throw new InvalidDataException($"主题版本 {number} 与当前版本 {SchemaVersion} 不兼容。");

            Package package = DockThemeJson.Deserialize<Package>(root);
            package.Theme.Validate();
            return package.Theme;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw new InvalidDataException("主题包含无效参数或不受支持的字段。", error);
        }
    }

    private static string UnwrapCodeFence(string text)
    {
        string trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        int firstBreak = trimmed.IndexOf('\n');
        int lastBreak = trimmed.LastIndexOf('\n');
        if (firstBreak < 0 || lastBreak <= firstBreak)
            return trimmed;

        string closingLine = trimmed[(lastBreak + 1)..].Trim();
        if (closingLine != "```")
            return trimmed;

        return trimmed[(firstBreak + 1)..lastBreak].Trim();
    }

    private static string ThemePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"请选择绝对路径的 {Extension} 主题文件。");
        return Path.GetFullPath(path);
    }
}

internal static class DockThemeJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        MaxDepth = 12,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    internal static T Read<T>(string path, int maximumBytes) where T : class
    {
        using JsonDocument document = ReadDocument(path, maximumBytes);
        return Deserialize<T>(document.RootElement);
    }

    internal static T Deserialize<T>(JsonElement root) where T : class => root.Deserialize<T>(Options)
        ?? throw new InvalidDataException("主题文件缺少有效数据。");

    internal static JsonDocument ReadDocument(string path, int maximumBytes) =>
        ParseDocument(ReadBytes(path, maximumBytes));

    internal static byte[] ReadBytes(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length == 0 || stream.Length > maximumBytes)
            throw new InvalidDataException($"主题文件不可为空，且最多 {maximumBytes / 1024} KiB。");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    internal static JsonDocument ParseDocument(ReadOnlyMemory<byte> json)
    {
        if (json.Span.StartsWith("\uFEFF"u8))
            json = json[3..];
        return JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = Options.MaxDepth });
    }

    internal static byte[] SerializeToBytes<T>(T value, int maximumBytes)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > maximumBytes)
            throw new InvalidDataException($"主题内容超出 {maximumBytes / 1024} KiB 限制。");
        return bytes;
    }

    internal static void Write<T>(string path, T value, int maximumBytes)
    {
        byte[] bytes = SerializeToBytes(value, maximumBytes);

        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".themes-{Guid.NewGuid():N}.tmp");
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
        }
        finally
        {
            if (created)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
