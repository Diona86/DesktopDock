namespace DesktopDock;

public enum DockPlacement
{
    Bottom,
    Top,
    Left,
    Right,
    Floating
}

public enum DockItemKind
{
    Application,
    Shortcut,
    File,
    Folder,
    Url
}

public sealed record DockItem(Guid Id, string Name, DockItemKind Kind, string Target, string? CustomIconPath = null);

public sealed record DockOptions
{
    public const double MinIconSize = 16;
    public const double MaxIconSize = 96;
    public const double MinThickness = 32;
    public const double MaxThickness = 168;
    public const double MinLength = 80;
    public const double MaxLength = 4096;
    public const double IconClearance = 10;

    public DockPlacement Placement { get; init; } = DockPlacement.Bottom;
    public double? FloatingLeft { get; init; }
    public double? FloatingTop { get; init; }
    public bool FloatingVertical { get; init; }
    public double IconSize { get; init; } = 40;
    public double DockThickness { get; init; } = 64;
    public double DockLength { get; init; }
    public bool GlobalPositionLocked { get; init; }
    public bool DockPositionLocked { get; init; }
    public bool ShowDockOnStartup { get; init; } = true;

    internal void Validate()
    {
        if (!Enum.IsDefined(Placement))
            throw new ArgumentException("请选择有效的 Dock 位置。");
        if (!InRange(IconSize, MinIconSize, MaxIconSize))
            throw new ArgumentException($"图标尺寸须在 {MinIconSize} 到 {MaxIconSize} DIP 之间。");
        if (!InRange(DockThickness, MinThickness, MaxThickness) || DockThickness < IconSize + IconClearance)
            throw new ArgumentException($"Dock 厚度须在 {MinThickness} 到 {MaxThickness} DIP 之间，且至少比图标大 {IconClearance} DIP。");
        if (DockLength != 0 && !InRange(DockLength, MinLength, MaxLength))
            throw new ArgumentException($"Dock 长度须为 0（自动）或 {MinLength} 到 {MaxLength} DIP。");
        if ((FloatingLeft is double left && !double.IsFinite(left)) ||
            (FloatingTop is double top && !double.IsFinite(top)))
            throw new ArgumentException("悬浮坐标必须为有限数值。");
    }

    private static bool InRange(double value, double minimum, double maximum) =>
        value >= minimum && value <= maximum;
}
