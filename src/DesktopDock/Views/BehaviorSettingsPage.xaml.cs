using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace DesktopDock;

public partial class BehaviorSettingsPage : UserControl
{
    private DockBehaviorOptions draft = new();
    private DockPlacement placement;
    private ResolvedDockHotZone[] resolvedZones = [];
    private bool dirty;
    private bool loading;

    public BehaviorSettingsPage() => InitializeComponent();

    internal void LoadOptions(DockBehaviorOptions options, DockPlacement dockPlacement, IReadOnlyList<DockMonitor> monitors)
    {
        if (!dirty)
            draft = options;
        placement = dockPlacement;
        string? selected = SelectedMonitor?.Id;
        MonitorRow[] rows = monitors.Select(monitor => new MonitorRow(monitor)).ToArray();
        loading = true;
        ModeInput.SelectedIndex = (int)draft.Mode;
        MonitorInput.ItemsSource = rows;
        MonitorInput.SelectedItem = rows.FirstOrDefault(row => row.Monitor.Id == selected)
            ?? rows.FirstOrDefault(row => row.Monitor.Primary) ?? rows.FirstOrDefault();
        loading = false;
        RefreshZones();
        RefreshRules();
    }

    internal DockBehaviorOptions ApplyTo(DockBehaviorOptions current) => dirty
        ? current with { Mode = draft.Mode, Monitors = draft.Monitors, ApplicationRules = draft.ApplicationRules }
        : current;

    internal void MarkSaved() => dirty = false;

    private DockMonitor? SelectedMonitor => (MonitorInput.SelectedItem as MonitorRow)?.Monitor;
    private MonitorHotZones? SelectedProfile => SelectedMonitor is DockMonitor monitor
        ? draft.Monitors.FirstOrDefault(entry => string.Equals(entry.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase))
        : null;

    private void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || ModeInput.SelectedIndex < 0)
            return;
        draft = draft with { Mode = (DockMode)ModeInput.SelectedIndex };
        dirty = true;
    }

    private void MonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loading)
            RefreshZones();
    }

    private void RefreshZones(int index = 0)
    {
        DockMonitor? monitor = SelectedMonitor;
        MonitorHotZones? profile = SelectedProfile;
        resolvedZones = monitor == null ? [] : DockHotZoneGeometry.ResolveZones(draft, placement, monitor);
        DockHotZone[] zones = profile?.Zones ?? resolvedZones.Select(zone => zone.Zone).ToArray();
        ZoneList.ItemsSource = zones.Select(zone => new ZoneRow(zone)).ToArray();
        ZoneList.SelectedIndex = zones.Length == 0 ? -1 : Math.Clamp(index, 0, zones.Length - 1);
        AddZoneButton.IsEnabled = monitor != null;
        ResetZonesButton.IsEnabled = profile != null;

        if (monitor == null)
            ZoneStatus.Text = "没有可用的显示器。";
        else if (HasOverlap(resolvedZones))
            ZoneStatus.Text = "热区有重叠，命中时优先使用列表中靠前的一项。";
        else if (resolvedZones.Length < zones.Length)
            ZoneStatus.Text = "部分热区超出屏幕范围，无法触发。";
        else if (profile == null)
            ZoneStatus.Text = "此显示器正在使用默认热区。";
        else if (zones.Length == 0)
            ZoneStatus.Text = "此显示器没有热区。";
        else
            ZoneStatus.Text = "热区按列表顺序匹配。";

        UpdateZoneButtons();
        DrawPreview();
    }

    private static bool HasOverlap(ResolvedDockHotZone[] zones)
    {
        for (int first = 0; first < zones.Length; first++)
            for (int second = first + 1; second < zones.Length; second++)
                if (zones[first].Overlaps(zones[second]))
                    return true;
        return false;
    }

    private void ZoneChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateZoneButtons();
        DrawPreview();
    }

    private void UpdateZoneButtons()
    {
        if (EditZoneButton == null)
            return;
        int index = ZoneList.SelectedIndex;
        EditZoneButton.IsEnabled = index >= 0;
        RemoveZoneButton.IsEnabled = index >= 0;
        RaiseZoneButton.IsEnabled = index > 0;
        LowerZoneButton.IsEnabled = index >= 0 && index < ZoneList.Items.Count - 1;
    }

    private void PreviewSizeChanged(object sender, SizeChangedEventArgs e) => DrawPreview();

    private void DrawPreview()
    {
        if (ZonePreview == null || SelectedMonitor is not DockMonitor monitor)
        {
            ZonePreview?.Children.Clear();
            return;
        }
        ZonePreview.Children.Clear();
        double monitorWidth = monitor.Right - monitor.Left;
        double monitorHeight = monitor.Bottom - monitor.Top;
        double scale = Math.Min(ZonePreview.ActualWidth / monitorWidth, ZonePreview.ActualHeight / monitorHeight);
        if (!double.IsFinite(scale) || scale <= 0)
            return;

        double originX = (ZonePreview.ActualWidth - monitorWidth * scale) / 2;
        double originY = (ZonePreview.ActualHeight - monitorHeight * scale) / 2;
        AddPreviewRectangle(originX, originY, monitorWidth * scale, monitorHeight * scale,
            TryFindResource("Sidebar") as Brush ?? Brushes.Gainsboro, TryFindResource("Rule") as Brush ?? Brushes.Gray);

        Guid? selected = (ZoneList.SelectedItem as ZoneRow)?.Zone.Id;
        Brush accent = TryFindResource("Accent") as Brush ?? Brushes.SeaGreen;
        foreach (ResolvedDockHotZone zone in resolvedZones)
        {
            if (zone.Right <= zone.Left || zone.Bottom <= zone.Top)
                continue;
            double width = (zone.Right - zone.Left) * scale;
            double height = (zone.Bottom - zone.Top) * scale;
            double x = originX + (zone.Left - monitor.Left) * scale;
            double y = originY + (zone.Top - monitor.Top) * scale;
            // Make cells thinner than a preview pixel visible without shifting their monitor alignment.
            width = Math.Max(2, width);
            height = Math.Max(2, height);
            x = Math.Clamp(x, originX, Math.Max(originX, originX + monitorWidth * scale - width));
            y = Math.Clamp(y, originY, Math.Max(originY, originY + monitorHeight * scale - height));
            Rectangle rectangle = AddPreviewRectangle(x, y, width, height, accent);
            rectangle.Opacity = selected == zone.Zone.Id ? 1 : 0.48;
            rectangle.ToolTip = new ZoneRow(zone.Zone).Details;
        }
    }

    private Rectangle AddPreviewRectangle(double x, double y, double width, double height, Brush fill, Brush? stroke = null)
    {
        var rectangle = new Rectangle { Width = width, Height = height, Fill = fill, Stroke = stroke, StrokeThickness = 1 };
        Canvas.SetLeft(rectangle, x);
        Canvas.SetTop(rectangle, y);
        ZonePreview.Children.Add(rectangle);
        return rectangle;
    }

    private DockHotZone[] ReadZones() => ZoneList.Items.Cast<ZoneRow>().Select(row => row.Zone).ToArray();

    private void AddZone(object sender, RoutedEventArgs e)
    {
        if (SelectedMonitor == null)
            return;
        DockHotZone[] zones = ReadZones();
        var usedEdges = zones.Select(zone => zone.Edge).ToHashSet();
        HotZoneEdge edge = Enum.GetValues<HotZoneEdge>().FirstOrDefault(candidate => !usedEdges.Contains(candidate));
        DockHotZone? added = EditZoneDialog(new DockHotZone(Guid.NewGuid(), edge, 35, 65, 6, 0, 250, 450), "添加热区");
        if (added != null)
            SetZones([.. zones, added], zones.Length);
    }

    private void EditZone(object sender, RoutedEventArgs e)
    {
        int index = ZoneList.SelectedIndex;
        if (index < 0)
            return;
        DockHotZone[] zones = ReadZones();
        DockHotZone? edited = EditZoneDialog(zones[index], "编辑热区");
        if (edited != null)
        {
            zones[index] = edited;
            SetZones(zones, index);
        }
    }

    private DockHotZone? EditZoneDialog(DockHotZone existing, string title)
    {
        var editor = new HotZoneEditorWindow(existing) { Title = title, Owner = Window.GetWindow(this) };
        return editor.ShowDialog() == true ? editor.Result : null;
    }

    private void RemoveZone(object sender, RoutedEventArgs e)
    {
        int index = ZoneList.SelectedIndex;
        if (index >= 0)
            SetZones(ReadZones().Where((_, position) => position != index).ToArray(), index);
    }

    private void RaiseZone(object sender, RoutedEventArgs e) => MoveZone(-1);
    private void LowerZone(object sender, RoutedEventArgs e) => MoveZone(1);

    private void MoveZone(int direction)
    {
        DockHotZone[] zones = ReadZones();
        int index = ZoneList.SelectedIndex;
        int next = index + direction;
        if (index >= 0 && next >= 0 && next < zones.Length)
        {
            (zones[index], zones[next]) = (zones[next], zones[index]);
            SetZones(zones, next);
        }
    }

    private void ResetZones(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is MonitorHotZones selected)
        {
            draft = draft with { Monitors = draft.Monitors.Where(profile => !ReferenceEquals(profile, selected)).ToArray() };
            dirty = true;
            RefreshZones();
        }
    }

    private void SetZones(DockHotZone[] zones, int index)
    {
        if (SelectedMonitor is not DockMonitor monitor)
            return;
        DockHotZone[] configured = zones.Select(zone => zone.Id == Guid.Empty ? zone with { Id = Guid.NewGuid() } : zone).ToArray();
        draft = draft with
        {
            Monitors = [.. draft.Monitors.Where(profile => !string.Equals(profile.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase)),
                new MonitorHotZones(monitor.Id, configured)]
        };
        dirty = true;
        RefreshZones(index);
    }

    private void RefreshRules(int index = 0)
    {
        RuleRow[] rows = draft.ApplicationRules.Select(rule => new RuleRow(rule)).ToArray();
        RulesList.ItemsSource = rows;
        RulesList.SelectedIndex = rows.Length == 0 ? -1 : Math.Clamp(index, 0, rows.Length - 1);
        UpdateRuleButtons();
    }

    private void RuleChanged(object sender, SelectionChangedEventArgs e) => UpdateRuleButtons();

    private void UpdateRuleButtons()
    {
        if (EditRuleButton == null)
            return;
        EditRuleButton.IsEnabled = RulesList.SelectedIndex >= 0;
        RemoveRuleButton.IsEnabled = RulesList.SelectedIndex >= 0;
    }

    private void AddRule(object sender, RoutedEventArgs e) => EditRuleAt(-1);
    private void EditRule(object sender, RoutedEventArgs e)
    {
        if (RulesList.SelectedIndex >= 0)
            EditRuleAt(RulesList.SelectedIndex);
    }

    private void EditRuleAt(int index)
    {
        var rules = draft.ApplicationRules.ToList();
        var editor = new ApplicationRuleEditorWindow(index < 0 ? null : rules[index]) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() != true || editor.Result is not DockApplicationRule rule)
            return;
        if (index < 0)
        {
            index = rules.Count;
            rules.Add(rule);
        }
        else
        {
            rules[index] = rule;
        }
        SetRules(rules.ToArray(), index);
    }

    private void RemoveRule(object sender, RoutedEventArgs e)
    {
        int index = RulesList.SelectedIndex;
        if (index >= 0)
            SetRules(draft.ApplicationRules.Where((_, position) => position != index).ToArray(), index);
    }

    private void SetRules(DockApplicationRule[] rules, int index)
    {
        draft = draft with { ApplicationRules = rules };
        dirty = true;
        RefreshRules(index);
    }

    private sealed record MonitorRow(DockMonitor Monitor)
    {
        public string Label => $"{(Monitor.Primary ? "主屏 · " : "")}{Monitor.Name} ({Monitor.Right - Monitor.Left} × {Monitor.Bottom - Monitor.Top})";
    }

    private sealed record ZoneRow(DockHotZone Zone)
    {
        private string Edge => Zone.Edge switch
        {
            HotZoneEdge.Top => "上边缘",
            HotZoneEdge.Bottom => "下边缘",
            HotZoneEdge.Left => "左边缘",
            _ => "右边缘"
        };

        public string Label => $"{(Zone.Id == Guid.Empty ? "默认 · " : "")}{Edge} · {Zone.StartPercent:G}–{Zone.EndPercent:G}%";
        public string Details => $"{Label}\n厚度 {Zone.Thickness:G} DIP，偏移 {Zone.Offset:G} DIP\n呼出 {Zone.RevealDelayMs} ms，收起 {Zone.HideDelayMs} ms";
    }

    private sealed record RuleRow(DockApplicationRule Rule)
    {
        public string Label => $"{(Rule.Action == DockRuleAction.AllowAutomatic ? "允许" : "禁止")}自动呼出 · {Path.GetFileName(Rule.ExecutablePath)}";
    }
}
