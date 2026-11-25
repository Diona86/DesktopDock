using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace DesktopDock;

public partial class DockHotZoneMapWindow : Window
{
    private static readonly StringComparer DisplayIds = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions ValidationJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private readonly Func<IReadOnlyList<DockHotZoneMapDisplay>> readDisplays;
    private readonly Action<string, DockHotZone[]?> saveZones;
    private readonly Dictionary<string, DockHotZone[]?> drafts = new(DisplayIds);
    private DockHotZoneMapDisplay[] displays = [];
    private string? selectedDisplayId;
    private int zoneIndex;
    private bool updating;

    internal DockHotZoneMapWindow(Func<IReadOnlyList<DockHotZoneMapDisplay>> readDisplays,
        Action<string, DockHotZone[]?> saveZones, string? selectedDisplayId = null)
    {
        this.readDisplays = readDisplays;
        this.saveZones = saveZones;
        this.selectedDisplayId = selectedDisplayId;
        InitializeComponent();
        RefreshDisplays();
    }

    private DockHotZoneMapDisplay? SelectedDisplay => displays.FirstOrDefault(display => DisplayIds.Equals(display.Monitor.Id, selectedDisplayId));

    internal void RefreshDisplays()
    {
        CancelDrag();
        displays = readDisplays().Where(display => display.Monitor is { Scale: > 0 } monitor &&
            double.IsFinite(monitor.Scale) && monitor.Right > monitor.Left && monitor.Bottom > monitor.Top).ToArray();
        selectedDisplayId = SelectedDisplay?.Monitor.Id ??
            displays.FirstOrDefault(display => display.Monitor.Primary)?.Monitor.Id ?? displays.FirstOrDefault()?.Monitor.Id;
        UpdateEditor();
    }

    private void SelectDisplay(string id, int index = 0)
    {
        if (!displays.Any(display => DisplayIds.Equals(display.Monitor.Id, id)))
            throw new ArgumentException("显示器当前未连接。");
        selectedDisplayId = id;
        zoneIndex = index;
        UpdateEditor();
    }

    private void DisplayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updating && DisplayInput.SelectedItem is DisplayRow row)
            SelectDisplay(row.Display.Monitor.Id);
    }

    private void ZoneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating)
            return;
        zoneIndex = ZoneInput.SelectedIndex;
        UpdateZoneStatus();
        DrawMap();
    }

    private DockHotZone[]? ConfiguredZones(DockHotZoneMapDisplay display) =>
        drafts.TryGetValue(display.Monitor.Id, out DockHotZone[]? draft) ? draft :
        display.Options.Behavior.Monitors.FirstOrDefault(profile => DisplayIds.Equals(profile.MonitorId, display.Monitor.Id))?.Zones;

    private DockHotZone[] Zones(DockHotZoneMapDisplay display) => ConfiguredZones(display) ??
        ResolvedZones(display, null).Select(zone => zone.Zone).ToArray();

    private static ResolvedDockHotZone[] ResolvedZones(DockHotZoneMapDisplay display, DockHotZone[]? zones)
    {
        DockBehaviorOptions basis = display.Options.Behavior with
        {
            Monitors = zones == null ? [] : [new MonitorHotZones(display.Monitor.Id, zones)]
        };
        return DockHotZoneGeometry.ResolveZones(basis, display.Options.Placement, display.Monitor);
    }

    private static HashSet<DockHotZone> ConflictingZones(ResolvedDockHotZone[] zones)
    {
        var conflicts = new HashSet<DockHotZone>();
        for (int first = 0; first < zones.Length; first++)
            for (int second = first + 1; second < zones.Length; second++)
                if (zones[first].Overlaps(zones[second]))
                {
                    conflicts.Add(zones[first].Zone);
                    conflicts.Add(zones[second].Zone);
                }
        return conflicts;
    }

    private void UpdateEditor(string? message = null)
    {
        if (Map == null || ZoneInput == null)
            return;

        updating = true;
        if (!displays.Select(display => display).SequenceEqual(
            (DisplayInput.ItemsSource as DisplayRow[] ?? []).Select(row => row.Display)))
        {
            DisplayInput.ItemsSource = displays.Select(display => new DisplayRow(display)).ToArray();
        }
        DisplayInput.SelectedItem = (DisplayInput.ItemsSource as DisplayRow[])?
            .FirstOrDefault(row => DisplayIds.Equals(row.Display.Monitor.Id, selectedDisplayId));
        DockHotZone[] zones = SelectedDisplay is DockHotZoneMapDisplay selected ? Zones(selected) : [];
        ZoneInput.ItemsSource = zones.Select(zone => new ZoneRow(zone)).ToArray();
        zoneIndex = zones.Length == 0 ? -1 : Math.Clamp(zoneIndex, 0, zones.Length - 1);
        ZoneInput.SelectedIndex = zoneIndex;
        TopologySummary.Text = $"{displays.Length} 台显示器";
        DisplayDetails.Text = SelectedDisplay is DockHotZoneMapDisplay display
            ? $"{Resolution(display.Monitor)} · {PlacementName(display.Options.Placement)}"
            : "当前没有连接的显示器";
        Status.Text = message ?? (drafts.Count == 0 ? "" : $"{drafts.Count} 台显示器有未应用的修改");
        CloseButton.Content = drafts.Count == 0 ? "关闭" : "放弃未应用修改";
        UpdateZoneStatus();
        updating = false;
        DrawMap();
    }

    private void UpdateZoneStatus()
    {
        var row = ZoneInput.SelectedItem as ZoneRow;
        ZoneDetails.Text = row?.Details ?? "点击「添加」创建热区。";
        bool hasScreen = SelectedDisplay != null;
        AddButton.IsEnabled = hasScreen;
        ApplyButton.IsEnabled = hasScreen && drafts.ContainsKey(selectedDisplayId!);
        EditButton.IsEnabled = RemoveButton.IsEnabled = hasScreen && row != null;
        ResetButton.IsEnabled = false;
        ConflictStatus.Text = "";
        if (SelectedDisplay is not DockHotZoneMapDisplay display)
            return;
        DockHotZone[]? configured = ConfiguredZones(display);
        ResolvedDockHotZone[] resolved = ResolvedZones(display, configured);
        int conflicts = ConflictingZones(resolved).Count;
        ResetButton.IsEnabled = configured != null;
        if (conflicts > 0)
            ConflictStatus.Text = $"{conflicts} 个热区有冲突，命中时优先使用列表靠前的一项。";
        else if (configured != null && configured.Length > resolved.Length)
            ConflictStatus.Text = "部分热区超出屏幕范围，无法触发。";
        ConflictStatus.Foreground = ConflictBrush;
    }

    private void AddZone(object sender, RoutedEventArgs e) => Guard(() =>
    {
        DockHotZoneMapDisplay display = CurrentDisplay();
        DockHotZone[] zones = Zones(display);
        var used = zones.Select(zone => zone.Edge).ToHashSet();
        HotZoneEdge edge = Enum.GetValues<HotZoneEdge>().FirstOrDefault(candidate => !used.Contains(candidate));
        var dialog = new HotZoneEditorWindow(new DockHotZone(Guid.NewGuid(), edge, 35, 65, 6, 0, 250, 450))
        {
            Title = "添加热区",
            Owner = this
        };
        if (dialog.ShowDialog() == true && dialog.Result is DockHotZone result)
            Add(result);
    });

    private void EditZone(object sender, RoutedEventArgs e) => Guard(() =>
    {
        DockHotZone[] zones = Zones(CurrentDisplay());
        if (zoneIndex < 0 || zoneIndex >= zones.Length)
            return;
        var dialog = new HotZoneEditorWindow(zones[zoneIndex]) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is DockHotZone result)
            Edit(result);
    });

    private void RemoveZone(object sender, RoutedEventArgs e) => Guard(Remove);
    private void ResetZones(object sender, RoutedEventArgs e) => Guard(Reset);
    private void SaveZones(object sender, RoutedEventArgs e) => Guard(Save);
    private void CloseEditor(object sender, RoutedEventArgs e) => Close();

    private DockHotZoneMapDisplay CurrentDisplay() => SelectedDisplay ?? throw new InvalidOperationException("请先选择显示器。");

    private void Add(DockHotZone zone)
    {
        DockHotZone[] zones = Zones(CurrentDisplay());
        Stage([.. zones, zone], zones.Length);
    }

    private void Edit(DockHotZone zone)
    {
        DockHotZone[] zones = Zones(CurrentDisplay()).ToArray();
        if (zoneIndex < 0 || zoneIndex >= zones.Length)
            throw new ArgumentException("请先选择要编辑的热区。");
        zones[zoneIndex] = zone with { Id = zones[zoneIndex].Id };
        Stage(zones);
    }

    private void Remove()
    {
        DockHotZone[] zones = Zones(CurrentDisplay());
        if (zoneIndex < 0 || zoneIndex >= zones.Length)
            throw new ArgumentException("请先选择要删除的热区。");
        Stage(zones.Where((_, index) => index != zoneIndex).ToArray());
    }

    private void Reset() => Stage(null);

    private static DockHotZone[] Normalize(DockHotZone[] zones)
    {
        DockHotZone[] configured = zones.Select(zone => zone.Id == Guid.Empty ? zone with { Id = Guid.NewGuid() } : zone).ToArray();
        var ids = new HashSet<Guid>();
        foreach (DockHotZone zone in configured)
        {
            zone.Validate();
            if (!ids.Add(zone.Id))
                throw new ArgumentException("这个显示器已有同一标识的热区。");
        }
        return configured;
    }

    private void Stage(DockHotZone[]? zones, int? selectedIndex = null)
    {
        string id = CurrentDisplay().Monitor.Id;
        drafts[id] = zones == null ? null : Normalize(zones);
        if (selectedIndex is int index)
            zoneIndex = index;
        UpdateEditor();
    }

    private void Save()
    {
        string id = CurrentDisplay().Monitor.Id;
        if (!drafts.TryGetValue(id, out DockHotZone[]? value))
            return;
        saveZones(id, value);
        drafts.Remove(id);
        RefreshDisplays();
        UpdateEditor(drafts.Count == 0 ? "已应用到当前显示器。" : $"已应用到当前屏，另外 {drafts.Count} 台还有未应用的修改。");
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception error) when (DockDialogs.IsUserFacing(error)) { Status.Text = error.Message; }
    }

    private static string Resolution(DockMonitor monitor) => string.Create(CultureInfo.CurrentCulture,
        $"{monitor.Right - monitor.Left} × {monitor.Bottom - monitor.Top} · {monitor.Scale:P0}");

    private static string PlacementName(DockPlacement placement) => placement switch
    {
        DockPlacement.Top => "上方 Dock", DockPlacement.Bottom => "下方 Dock",
        DockPlacement.Left => "左侧 Dock", DockPlacement.Right => "右侧 Dock",
        _ => "悬浮 Dock"
    };

    private sealed record DisplayRow(DockHotZoneMapDisplay Display)
    {
        public override string ToString() => $"{(Display.Monitor.Primary ? "主屏 · " : "")}{Display.Monitor.Name}";
    }

    private sealed record ZoneRow(DockHotZone Zone)
    {
        private string EdgeName => Zone.Edge switch
        {
            HotZoneEdge.Top => "上边缘", HotZoneEdge.Bottom => "下边缘", HotZoneEdge.Left => "左边缘", _ => "右边缘"
        };

        public string Label => $"{(Zone.Id == Guid.Empty ? "默认 · " : "")}{EdgeName} · {Zone.StartPercent:G}–{Zone.EndPercent:G}%";
        public string Details => $"厚度 {Zone.Thickness:G} DIP · 偏移 {Zone.Offset:G} DIP\n呼出 {Zone.RevealDelayMs} ms，收起 {Zone.HideDelayMs} ms";
    }

    internal object RunValidationCommand(string action, JsonElement args)
    {
        T Argument<T>(string key) => args.TryGetProperty(key, out JsonElement element) && element.Deserialize<T>(ValidationJson) is T value
            ? value : throw new ArgumentException($"缺少热区编辑参数 {key}。");
        switch (action)
        {
            case "state": break;
            case "select":
                SelectDisplay(Argument<string>("monitorId"), args.TryGetProperty("index", out JsonElement index) ? index.GetInt32() : 0);
                break;
            case "drag":
                DockHotZoneMapDisplay display = CurrentDisplay();
                DockHotZone zone = Zones(display).ElementAtOrDefault(zoneIndex) ?? throw new ArgumentException("请先选择热区。");
                Edit(DockHotZoneMapGeometry.Drag(display.Monitor, zone, Argument<DockHotZoneMapHandle>("handle"),
                    Argument<double>("deltaX"), Argument<double>("deltaY")));
                break;
            case "add": Add(Argument<DockHotZone>("zone")); break;
            case "edit": Edit(Argument<DockHotZone>("zone")); break;
            case "remove": Remove(); break;
            case "reset": Reset(); break;
            case "save": Save(); break;
            case "cancel": Close(); break;
            default: throw new ArgumentException("未知的热区编辑操作。");
        }

        UpdateLayout();
        return MapState();
    }
}
