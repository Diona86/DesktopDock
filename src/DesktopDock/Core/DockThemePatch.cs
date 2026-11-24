namespace DesktopDock;

public sealed record DockThemePatch
{
    public DockThemeMaterial? Material { get; init; }
    public string? BackgroundColor { get; init; }
    public string? SecondaryColor { get; init; }
    public string? ForegroundColor { get; init; }
    public string? AccentColor { get; init; }
    public string? BorderColor { get; init; }
    public string? ShadowColor { get; init; }
    public double? Opacity { get; init; }
    public double? Blur { get; init; }
    public double? CornerRadius { get; init; }
    public double? BorderThickness { get; init; }
    public double? ShadowBlur { get; init; }
    public double? ShadowDepth { get; init; }
    public double? ShadowOpacity { get; init; }
    public double? IconSize { get; init; }
    public double? IconSpacing { get; init; }
    public double? Padding { get; init; }
    public DockThemeTooltip? Tooltip { get; init; }
    public DockThemeGrip? Grip { get; init; }
    public DockThemeHover? Hover { get; init; }
    public DockThemeAnimation? RevealAnimation { get; init; }
    public DockThemeAnimation? HideAnimation { get; init; }
    public DockThemeAnimation? GroupAnimation { get; init; }
    public int? RevealMilliseconds { get; init; }
    public int? HideMilliseconds { get; init; }
    public int? GroupMilliseconds { get; init; }
    public DockThemeLayout? LayoutPreset { get; init; }
    public DockThemePattern? Pattern { get; init; }
    public string? PatternColor { get; init; }
    public double? PatternOpacity { get; init; }
    public double? PatternSize { get; init; }

    public DockThemeTokens Apply(DockThemeTokens basis) => basis with
    {
        Material = Material ?? basis.Material,
        BackgroundColor = BackgroundColor ?? basis.BackgroundColor,
        SecondaryColor = SecondaryColor ?? basis.SecondaryColor,
        ForegroundColor = ForegroundColor ?? basis.ForegroundColor,
        AccentColor = AccentColor ?? basis.AccentColor,
        BorderColor = BorderColor ?? basis.BorderColor,
        ShadowColor = ShadowColor ?? basis.ShadowColor,
        Opacity = Opacity ?? basis.Opacity,
        Blur = Blur ?? basis.Blur,
        CornerRadius = CornerRadius ?? basis.CornerRadius,
        BorderThickness = BorderThickness ?? basis.BorderThickness,
        ShadowBlur = ShadowBlur ?? basis.ShadowBlur,
        ShadowDepth = ShadowDepth ?? basis.ShadowDepth,
        ShadowOpacity = ShadowOpacity ?? basis.ShadowOpacity,
        IconSize = IconSize ?? basis.IconSize,
        IconSpacing = IconSpacing ?? basis.IconSpacing,
        Padding = Padding ?? basis.Padding,
        Tooltip = Tooltip ?? basis.Tooltip,
        Grip = Grip ?? basis.Grip,
        Hover = Hover ?? basis.Hover,
        RevealAnimation = RevealAnimation ?? basis.RevealAnimation,
        HideAnimation = HideAnimation ?? basis.HideAnimation,
        GroupAnimation = GroupAnimation ?? basis.GroupAnimation,
        RevealMilliseconds = RevealMilliseconds ?? basis.RevealMilliseconds,
        HideMilliseconds = HideMilliseconds ?? basis.HideMilliseconds,
        GroupMilliseconds = GroupMilliseconds ?? basis.GroupMilliseconds,
        LayoutPreset = LayoutPreset ?? basis.LayoutPreset,
        Pattern = Pattern ?? basis.Pattern,
        PatternColor = PatternColor ?? basis.PatternColor,
        PatternOpacity = PatternOpacity ?? basis.PatternOpacity,
        PatternSize = PatternSize ?? basis.PatternSize,
    };

    public static DockThemePatch Difference(DockThemeTokens basis, DockThemeTokens edited) => new()
    {
        Material = basis.Material == edited.Material ? null : edited.Material,
        BackgroundColor = basis.BackgroundColor == edited.BackgroundColor ? null : edited.BackgroundColor,
        SecondaryColor = basis.SecondaryColor == edited.SecondaryColor ? null : edited.SecondaryColor,
        ForegroundColor = basis.ForegroundColor == edited.ForegroundColor ? null : edited.ForegroundColor,
        AccentColor = basis.AccentColor == edited.AccentColor ? null : edited.AccentColor,
        BorderColor = basis.BorderColor == edited.BorderColor ? null : edited.BorderColor,
        ShadowColor = basis.ShadowColor == edited.ShadowColor ? null : edited.ShadowColor,
        Opacity = basis.Opacity == edited.Opacity ? null : edited.Opacity,
        Blur = basis.Blur == edited.Blur ? null : edited.Blur,
        CornerRadius = basis.CornerRadius == edited.CornerRadius ? null : edited.CornerRadius,
        BorderThickness = basis.BorderThickness == edited.BorderThickness ? null : edited.BorderThickness,
        ShadowBlur = basis.ShadowBlur == edited.ShadowBlur ? null : edited.ShadowBlur,
        ShadowDepth = basis.ShadowDepth == edited.ShadowDepth ? null : edited.ShadowDepth,
        ShadowOpacity = basis.ShadowOpacity == edited.ShadowOpacity ? null : edited.ShadowOpacity,
        IconSize = basis.IconSize == edited.IconSize ? null : edited.IconSize,
        IconSpacing = basis.IconSpacing == edited.IconSpacing ? null : edited.IconSpacing,
        Padding = basis.Padding == edited.Padding ? null : edited.Padding,
        Tooltip = basis.Tooltip == edited.Tooltip ? null : edited.Tooltip,
        Grip = basis.Grip == edited.Grip ? null : edited.Grip,
        Hover = basis.Hover == edited.Hover ? null : edited.Hover,
        RevealAnimation = basis.RevealAnimation == edited.RevealAnimation ? null : edited.RevealAnimation,
        HideAnimation = basis.HideAnimation == edited.HideAnimation ? null : edited.HideAnimation,
        GroupAnimation = basis.GroupAnimation == edited.GroupAnimation ? null : edited.GroupAnimation,
        RevealMilliseconds = basis.RevealMilliseconds == edited.RevealMilliseconds ? null : edited.RevealMilliseconds,
        HideMilliseconds = basis.HideMilliseconds == edited.HideMilliseconds ? null : edited.HideMilliseconds,
        GroupMilliseconds = basis.GroupMilliseconds == edited.GroupMilliseconds ? null : edited.GroupMilliseconds,
        LayoutPreset = basis.LayoutPreset == edited.LayoutPreset ? null : edited.LayoutPreset,
        Pattern = basis.Pattern == edited.Pattern ? null : edited.Pattern,
        PatternColor = basis.PatternColor == edited.PatternColor ? null : edited.PatternColor,
        PatternOpacity = basis.PatternOpacity == edited.PatternOpacity ? null : edited.PatternOpacity,
        PatternSize = basis.PatternSize == edited.PatternSize ? null : edited.PatternSize,
    };
}

public sealed record DockThemeOverride(string? ThemeId, DockThemePatch Patch)
{
    public DockThemeOverride() : this(null, new()) { }
}
