namespace DesktopDock;

public enum DockThemeMaterial { Solid, Frosted, Technical, Transparent }
public enum DockThemeTooltip { Minimal, Floating, Technical }
public enum DockThemeGrip { Dots, Bar, Lines }
public enum DockThemeHover { None, Scale, Lift, Glow, Bounce }
public enum DockThemeAnimation { None, Fade, Slide, Scale }
public enum DockThemeLayout { Dock, Auto, SingleRow, SingleColumn, MultiRowGrid, MultiColumnGrid, GroupExpand }
public enum DockThemePattern { None, Dots, Grid, Diagonal }

public sealed record DockThemeTokens
{
    public DockThemeMaterial Material { get; init; }
    public string BackgroundColor { get; init; } = "#202422";
    public string SecondaryColor { get; init; } = "#323B35";
    public string ForegroundColor { get; init; } = "#F3F3ED";
    public string AccentColor { get; init; } = "#B6CDBD";
    public string BorderColor { get; init; } = "#C9525B54";
    public string ShadowColor { get; init; } = "#000000";
    public double Opacity { get; init; } = 240d / 255;
    public double Blur { get; init; }
    public double CornerRadius { get; init; } = 13;
    public double BorderThickness { get; init; } = 1;
    public double ShadowBlur { get; init; }
    public double ShadowDepth { get; init; }
    public double ShadowOpacity { get; init; }
    public double IconSize { get; init; }
    public double IconSpacing { get; init; } = -1;
    public double Padding { get; init; } = -1;
    public DockThemeTooltip Tooltip { get; init; }
    public DockThemeGrip Grip { get; init; }
    public DockThemeHover Hover { get; init; }
    public DockThemeAnimation RevealAnimation { get; init; }
    public DockThemeAnimation HideAnimation { get; init; }
    public DockThemeAnimation GroupAnimation { get; init; } = DockThemeAnimation.Scale;
    public int RevealMilliseconds { get; init; }
    public int HideMilliseconds { get; init; }
    public int GroupMilliseconds { get; init; } = 170;
    public DockThemeLayout LayoutPreset { get; init; }
    public DockThemePattern Pattern { get; init; }
    public string PatternColor { get; init; } = "#FFFFFF";
    public double PatternOpacity { get; init; } = .12;
    public double PatternSize { get; init; } = 12;

    public void Validate()
    {
        if (!Enum.IsDefined(Material) || !Enum.IsDefined(Tooltip) || !Enum.IsDefined(Grip) ||
            !Enum.IsDefined(Hover) || !Enum.IsDefined(RevealAnimation) || !Enum.IsDefined(HideAnimation) ||
            !Enum.IsDefined(GroupAnimation) || !Enum.IsDefined(LayoutPreset) || !Enum.IsDefined(Pattern))
            throw new ArgumentException("主题包含不支持的材质、布局或效果。");

        foreach (string color in new[] { BackgroundColor, SecondaryColor, ForegroundColor, AccentColor, BorderColor, ShadowColor, PatternColor })
        {
            if (color == null || color.Length is not (7 or 9) || color[0] != '#' ||
                color.Skip(1).Any(value => !char.IsAsciiHexDigit(value)))
                throw new ArgumentException("主题颜色须使用 #RRGGBB 或 #AARRGGBB。");
        }
        Range(Opacity, 0, 1, "背景透明度");
        Range(Blur, 0, 24, "模糊强度");
        Range(CornerRadius, 0, 32, "圆角");
        Range(BorderThickness, 0, 4, "边框");
        Range(ShadowBlur, 0, 32, "阴影半径");
        Range(ShadowDepth, 0, 16, "阴影深度");
        Range(ShadowOpacity, 0, 1, "阴影透明度");
        if (IconSize != 0)
            Range(IconSize, DockOptions.MinIconSize, DockOptions.MaxIconSize, "图标尺寸");
        if (IconSpacing != -1)
            Range(IconSpacing, 0, DockLayoutOptions.MaxIconSpacing, "图标间距");
        if (Padding != -1)
            Range(Padding, 0, 24, "内边距");
        Range(RevealMilliseconds, 0, 500, "呼出动画");
        Range(HideMilliseconds, 0, 500, "收起动画");
        Range(GroupMilliseconds, 0, 500, "分组动画");
        Range(PatternOpacity, 0, 1, "纹理透明度");
        Range(PatternSize, 4, 48, "纹理尺寸");
    }

    private static void Range(double value, double min, double max, string label)
    {
        if (!(value >= min && value <= max))
            throw new ArgumentException($"{label}须在 {min} 到 {max} 之间。");
    }
}

public sealed record DockTheme(string Id, string Name, DockThemeTokens Tokens)
{
    public void Validate()
    {
        ValidateId(Id);
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 64 || Name.Any(char.IsControl) || Tokens == null)
            throw new ArgumentException("主题须有有效参数和 1–64 字符的名称。");
        Tokens.Validate();
    }

    internal static void ValidateId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 ||
            id.Any(value => value is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
            throw new ArgumentException("主题或 Dock 标识须由 1–64 个小写字母、数字、下划线或连字符组成。");
    }
}

public static class DockThemeBuiltIns
{
    public const string MinimalId = "minimal";
    public const string GlassId = "glass";
    public const string TechId = "tech";

    public static DockTheme Minimal { get; } = new(MinimalId, "极简", new());

    public static DockTheme Glass { get; } = new(GlassId, "玻璃", new()
    {
        Material = DockThemeMaterial.Frosted,
        BackgroundColor = "#283B45",
        SecondaryColor = "#71888E",
        ForegroundColor = "#F3F8F6",
        AccentColor = "#CBEEEE",
        BorderColor = "#AAE6F7F5",
        Opacity = .76,
        Blur = 16,
        CornerRadius = 23,
        BorderThickness = 1,
        ShadowBlur = 22,
        ShadowDepth = 5,
        ShadowOpacity = .28,
        IconSpacing = 7,
        Padding = 8,
        Tooltip = DockThemeTooltip.Floating,
        Grip = DockThemeGrip.Bar,
        Hover = DockThemeHover.Lift,
        RevealAnimation = DockThemeAnimation.Slide,
        RevealMilliseconds = 190,
        HideAnimation = DockThemeAnimation.Fade,
        HideMilliseconds = 155,
        GroupMilliseconds = 195
    });

    public static DockTheme Tech { get; } = new(TechId, "科技", new()
    {
        Material = DockThemeMaterial.Technical,
        BackgroundColor = "#121C22",
        SecondaryColor = "#0C1419",
        ForegroundColor = "#DBF5EE",
        AccentColor = "#75D7C4",
        BorderColor = "#AE4D9C91",
        Opacity = .97,
        CornerRadius = 3,
        BorderThickness = 2,
        ShadowBlur = 13,
        ShadowDepth = 3,
        ShadowOpacity = .28,
        IconSpacing = 5,
        Padding = 7,
        Tooltip = DockThemeTooltip.Technical,
        Grip = DockThemeGrip.Lines,
        Hover = DockThemeHover.Glow,
        RevealAnimation = DockThemeAnimation.Fade,
        RevealMilliseconds = 160,
        HideAnimation = DockThemeAnimation.Fade,
        HideMilliseconds = 140,
        GroupAnimation = DockThemeAnimation.Slide,
        GroupMilliseconds = 180,
        Pattern = DockThemePattern.Grid,
        PatternColor = "#659C91",
        PatternOpacity = .22,
        PatternSize = 10,
        LayoutPreset = DockThemeLayout.MultiRowGrid
    });

    public static IEnumerable<DockTheme> All
    {
        get
        {
            yield return Minimal;
            yield return Glass;
            yield return Tech;
        }
    }
}
