using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopDock;

public sealed class DockSession : IDisposable
{
    private const int CurrentVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private readonly string _configPath;
    private readonly object _gate = new();
    private FileStream? _lease;
    private volatile Configuration _state;

    private sealed record Configuration(int SchemaVersion, DockOptions Options, DockItem[] Items);

    private DockSession(string configPath, FileStream lease, Configuration state)
    {
        _configPath = configPath;
        _lease = lease;
        _state = state;
    }

    public IReadOnlyList<DockItem> Items => Array.AsReadOnly(_state.Items);
    public DockOptions Options => _state.Options;
    public event EventHandler? Changed;

    public static DockSession Open(string configDirectory)
    {
        string directory = Directory.CreateDirectory(configDirectory).FullName;
        string configPath = Path.Combine(directory, "dock.json");
        FileStream lease;
        try
        {
            lease = new FileStream(Path.Combine(directory, "dock.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new IOException("无法独占打开 Dock 配置。请关闭使用该配置的 Dock 实例后重试。", error);
        }

        try
        {
            return new DockSession(configPath, lease, Read(configPath));
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public DockItem AddItem(string target, string? name = null)
    {
        var (kind, value) = DockTargets.Resolve(target);
        string itemName = string.IsNullOrWhiteSpace(name) ? DockTargets.DefaultName(kind, value) : name.Trim();
        var item = new DockItem(Guid.NewGuid(), itemName, kind, value);
        Change(state => state with { Items = [.. state.Items, item] });
        return item;
    }

    public void UpdateItem(Guid id, string target, string name, string? customIconPath)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("请输入项目名称。");

        EditItem(id, item =>
        {
            // An unavailable reference remains editable and can be moved to a valid target later.
            var (kind, value) = string.Equals(target.Trim(), item.Target, StringComparison.Ordinal)
                ? (item.Kind, item.Target)
                : DockTargets.Resolve(target);
            string? icon = string.Equals(customIconPath, item.CustomIconPath, StringComparison.Ordinal)
                ? item.CustomIconPath
                : DockTargets.CustomIconPath(customIconPath, requireFile: true);
            return new DockItem(id, name.Trim(), kind, value, icon);
        });
    }

    public void SetIcon(Guid id, string? path)
    {
        string? icon = DockTargets.CustomIconPath(path, requireFile: true);
        EditItem(id, item => item with { CustomIconPath = icon });
    }

    public void RemoveItem(Guid id) => Change(state =>
    {
        int index = FindItem(state.Items, id);
        var items = state.Items.ToList();
        items.RemoveAt(index);
        return state with { Items = [.. items] };
    });

    public void MoveItem(Guid itemId, Guid targetId, bool after)
    {
        if (itemId == targetId)
            return;

        Change(state =>
        {
            int sourceIndex = FindItem(state.Items, itemId);
            int targetIndex = FindItem(state.Items, targetId);
            int insertIndex = targetIndex + (after ? 1 : 0) - (sourceIndex < targetIndex ? 1 : 0);
            if (insertIndex == sourceIndex)
                return state;

            var items = state.Items.ToList();
            items.RemoveAt(sourceIndex);
            items.Insert(insertIndex, state.Items[sourceIndex]);
            return state with { Items = [.. items] };
        });
    }

    public void UpdateOptions(DockOptions options)
    {
        options.Validate();
        Change(state => state.Options == options ? state : state with { Options = options });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _lease?.Dispose();
            _lease = null;
        }
    }

    private void EditItem(Guid id, Func<DockItem, DockItem> edit) => Change(state =>
    {
        int index = FindItem(state.Items, id);
        DockItem item = edit(state.Items[index]);
        if (item == state.Items[index])
            return state;

        DockItem[] items = (DockItem[])state.Items.Clone();
        items[index] = item;
        return state with { Items = items };
    });

    private static int FindItem(DockItem[] items, Guid id)
    {
        int index = Array.FindIndex(items, item => item.Id == id);
        if (index < 0)
            throw new InvalidOperationException("该 Dock 项目已移除。");

        return index;
    }

    private void Change(Func<Configuration, Configuration> edit)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_lease is null, this);
            Configuration next = edit(_state);
            if (ReferenceEquals(next, _state))
                return;

            bool migrate = _state.SchemaVersion == 1;
            next = next with { SchemaVersion = CurrentVersion };
            Write(next, migrate);
            _state = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static Configuration Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number))
                throw new InvalidDataException("Dock 配置缺少有效的版本号，原文件已保留。");

            if (number is not (1 or CurrentVersion))
                throw new InvalidDataException($"Dock 配置版本 {number} 与当前版本 {CurrentVersion} 不兼容，原文件已保留。");

            Configuration? state = root.Deserialize<Configuration>(JsonOptions);
            if (state?.Options is null || state.Items is null)
                throw new InvalidDataException("Dock 配置缺少设置或项目列表，原文件已保留。");

            state.Options.Validate();
            var ids = new HashSet<Guid>();
            foreach (DockItem item in state.Items)
            {
                if (item is null || !ids.Add(item.Id))
                    throw new InvalidDataException("Dock 配置包含空项目或重复项目标识，原文件已保留。");

                DockTargets.ValidateReference(item);
            }

            return state;
        }
        catch (FileNotFoundException)
        {
            return new Configuration(CurrentVersion, new DockOptions(), []);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw new InvalidDataException("Dock 配置内容无效，原文件已保留。", error);
        }
    }

    private void Write(Configuration state, bool migrate)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(_configPath)!, $".dock-{Guid.NewGuid():N}.tmp");
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                JsonSerializer.Serialize(stream, state, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_configPath))
            {
                string? backup = migrate ? Path.Combine(Path.GetDirectoryName(_configPath)!, $"dock.v1-{Guid.NewGuid():N}.bak") : null;
                File.Replace(temporary, _configPath, backup);
            }
            else
                File.Move(temporary, _configPath);
        }
        catch
        {
            if (created)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
    }
}
