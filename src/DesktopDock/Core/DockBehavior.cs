namespace DesktopDock;

public enum DockMode { Resident, AutoHide }

public enum HotZoneEdge { Top, Bottom, Left, Right }

public enum DockRuleAction { AllowAutomatic, BlockAutomatic }

public sealed record DockHotZone(Guid Id, HotZoneEdge Edge, double StartPercent, double EndPercent,
    double Thickness, double Offset, int RevealDelayMs, int HideDelayMs)
{
    internal void Validate()
    {
        if (Id == Guid.Empty || !Enum.IsDefined(Edge))
            throw new ArgumentException("热区缺少有效的标识或边缘。");
        if (!double.IsFinite(StartPercent) || !double.IsFinite(EndPercent) ||
            StartPercent < 0 || EndPercent > 100 || StartPercent >= EndPercent)
            throw new ArgumentException("热区起点和终点须为 0–100% 的递增数值。");
        if (!double.IsFinite(Thickness) || Thickness <= 0 || !double.IsFinite(Offset) || Offset < 0)
            throw new ArgumentException("热区厚度须大于 0 DIP，偏移须为非负 DIP。");
        if (RevealDelayMs < 0 || HideDelayMs < 0)
            throw new ArgumentException("呼出和收起延迟须为非负毫秒数。");
    }
}

public sealed record MonitorHotZones(string MonitorId, DockHotZone[] Zones);

public sealed record DockMonitor(string Id, string Name, int Left, int Top, int Right, int Bottom,
    int WorkLeft, int WorkTop, int WorkRight, int WorkBottom, double Scale, bool Primary);

public sealed record HotkeyChord(int Modifiers, int VirtualKey);

public sealed record DockHotkeys
{
    private const int DefaultModifiers = 0x0007;

    public HotkeyChord? Toggle { get; init; } = new(DefaultModifiers, 0x75);
    public HotkeyChord? Peek { get; init; } = new(DefaultModifiers, 0x76);
    public HotkeyChord? Lock { get; init; } = new(DefaultModifiers, 0x77);
    public HotkeyChord? Pause { get; init; } = new(DefaultModifiers, 0x78);

    public void Validate()
    {
        var seen = new HashSet<HotkeyChord>();
        foreach (HotkeyChord? chord in new[] { Toggle, Peek, Lock, Pause })
        {
            if (chord == null)
                continue;
            if ((chord.Modifiers & ~0x000F) != 0 || chord.VirtualKey is < 1 or > 254 or
                0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5)
                throw new ArgumentException("请选择有效的快捷键组合，主键不能是修饰键。");
            if (chord.VirtualKey == 0x7B)
                throw new ArgumentException("F12 保留给 Windows 调试器，请选择其他主键。");
            if (!seen.Add(chord))
                throw new ArgumentException("多个 Dock 操作使用了相同的全局快捷键。");
        }
    }
}

public sealed record DockApplicationRule(string ExecutablePath, DockRuleAction Action);

public sealed record DockBehaviorOptions
{
    public DockMode Mode { get; init; } = DockMode.Resident;
    public MonitorHotZones[] Monitors { get; init; } = [];
    public DockHotkeys Hotkeys { get; init; } = new();
    public DockApplicationRule[] ApplicationRules { get; init; } = [];

    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || Monitors == null || Hotkeys == null || ApplicationRules == null)
            throw new ArgumentException("Dock 行为设置不完整。");

        Hotkeys.Validate();
        var monitors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MonitorHotZones monitor in Monitors)
        {
            if (monitor == null || string.IsNullOrWhiteSpace(monitor.MonitorId) || monitor.Zones == null || !monitors.Add(monitor.MonitorId))
                throw new ArgumentException("显示器热区配置不完整或包含重复显示器。");

            var zones = new HashSet<Guid>();
            foreach (DockHotZone zone in monitor.Zones)
            {
                if (zone == null || !zones.Add(zone.Id))
                    throw new ArgumentException("显示器包含空热区或重复热区标识。");
                zone.Validate();
            }
        }

        var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DockApplicationRule rule in ApplicationRules)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.ExecutablePath) || !Enum.IsDefined(rule.Action) ||
                !Path.IsPathFullyQualified(rule.ExecutablePath) ||
                !string.Equals(Path.GetExtension(rule.ExecutablePath), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("应用例外须指定完整的 EXE 路径和有效规则。");
            if (!apps.Add(Path.GetFullPath(rule.ExecutablePath)))
                throw new ArgumentException("同一应用只能配置一条例外规则。");
        }
    }
}
