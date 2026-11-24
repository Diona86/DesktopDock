namespace DesktopDock;

public enum DockGroupExpandMode { Grid, Fan, Horizontal, Vertical }
public enum DockExpandDirection { Auto, Up, Down, Left, Right }
public enum DockLayoutMode { Auto, SingleRow, SingleColumn, MultiRowGrid, MultiColumnGrid, GroupExpand }
public enum DockGroupDeleteChoice { MoveContentsToParent, RemoveReferences }

public sealed record DockGroup(Guid Id, string Name, DockGroupExpandMode ExpandMode = DockGroupExpandMode.Grid,
    string? CustomIconPath = null, DockExpandDirection Direction = DockExpandDirection.Auto);

public sealed record DockEntry(Guid Id, Guid? ParentId = null);

public sealed record DockLayoutOptions
{
    public const int MaxIconLimit = 128;
    public const double MaxIconSpacing = 32;
    public const double MaxPanelSpacing = 48;
    public const int MinGridTracks = 2;
    public const int MaxGridTracks = 8;

    public DockLayoutMode Mode { get; init; } = DockLayoutMode.Auto;
    public int MaxVisibleIcons { get; init; }
    public double IconSpacing { get; init; } = 2;
    public double PanelSpacing { get; init; } = 8;
    public int GridTracks { get; init; } = 2;
    public DockExpandDirection ExpansionDirection { get; init; } = DockExpandDirection.Auto;

    internal void Validate()
    {
        if (!Enum.IsDefined(Mode) || !Enum.IsDefined(ExpansionDirection))
            throw new ArgumentException("请选择有效的布局和展开方向。");
        if (MaxVisibleIcons is < 0 or > MaxIconLimit)
            throw new ArgumentException($"最大图标数须为 0（自动）或 1 到 {MaxIconLimit}。");
        if (!(IconSpacing >= 0 && IconSpacing <= MaxIconSpacing))
            throw new ArgumentException($"图标间距须在 0 到 {MaxIconSpacing} DIP 之间。");
        if (!(PanelSpacing >= 0 && PanelSpacing <= MaxPanelSpacing))
            throw new ArgumentException($"面板间距须在 0 到 {MaxPanelSpacing} DIP 之间。");
        if (GridTracks is < MinGridTracks or > MaxGridTracks)
            throw new ArgumentException($"网格行数或列数须在 {MinGridTracks} 到 {MaxGridTracks} 之间。");
    }
}
