using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WpfBorder = System.Windows.Controls.Border;

namespace DesktopDock;

internal sealed class DockThemeVisuals
{
    private readonly ControlTemplate tooltipTemplate;
    private readonly Brush tooltipBackground;
    private readonly Brush tooltipBorder;
    private readonly Brush finish;
    private readonly Brush pattern;
    private readonly BlurEffect? blur;
    private readonly DropShadowEffect? shadow;

    internal DockThemeVisuals(DockThemeTokens tokens, bool performanceMode)
    {
        Tokens = tokens;
        Color background = Parse(tokens.BackgroundColor);
        Color secondary = Parse(tokens.SecondaryColor);
        Color accent = Parse(tokens.AccentColor);
        Foreground = Solid(Parse(tokens.ForegroundColor));
        Accent = Solid(accent);
        Border = Solid(Parse(tokens.BorderColor));
        Background = Solid(Alpha(background, tokens.Material == DockThemeMaterial.Transparent ? 0 : tokens.Opacity));
        finish = tokens.Material switch
        {
            DockThemeMaterial.Frosted => Freeze(new LinearGradientBrush(Alpha(secondary, tokens.Opacity * .66),
                Alpha(secondary, tokens.Opacity * .13), 112)),
            DockThemeMaterial.Technical => Freeze(new LinearGradientBrush(Alpha(secondary, tokens.Opacity * .55),
                Alpha(background, 0), 42)),
            _ => Brushes.Transparent
        };
        pattern = BuildPattern(tokens);
        if (!performanceMode && tokens.Blur > 0 && tokens.Material is DockThemeMaterial.Frosted or DockThemeMaterial.Technical)
            blur = Freeze(new BlurEffect { Radius = tokens.Blur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance });
        if (!performanceMode && tokens.ShadowOpacity > 0 && (tokens.ShadowBlur > 0 || tokens.ShadowDepth > 0))
        {
            shadow = Freeze(new DropShadowEffect
            {
                Color = Parse(tokens.ShadowColor),
                BlurRadius = tokens.ShadowBlur,
                ShadowDepth = tokens.ShadowDepth,
                Opacity = tokens.ShadowOpacity,
                Direction = 270,
                RenderingBias = RenderingBias.Performance
            });
            ShadowInset = Math.Ceiling(tokens.ShadowBlur + tokens.ShadowDepth);
        }
        HoverBackground = tokens.Hover == DockThemeHover.None ? Brushes.Transparent
            : Solid(Alpha(accent, tokens.Hover == DockThemeHover.Glow ? .27 : .15));
        HoverBorder = tokens.Hover == DockThemeHover.None ? Brushes.Transparent
            : Solid(Alpha(accent, tokens.Hover == DockThemeHover.Glow ? .88 : .43));
        Pressed = Solid(Alpha(accent, .32));
        Expanded = Solid(Alpha(accent, .12));
        ExpandedBorder = Solid(Alpha(accent, .28));
        tooltipBackground = Solid(Alpha(tokens.Tooltip == DockThemeTooltip.Floating ? secondary : background, 1));
        tooltipBorder = tokens.Tooltip == DockThemeTooltip.Technical ? Accent : Border;
        tooltipTemplate = CreateTooltipTemplate(tokens.Tooltip);
    }

    internal DockThemeTokens Tokens { get; }
    internal Brush Background { get; }
    internal Brush Foreground { get; }
    internal Brush Accent { get; }
    internal Brush Border { get; }
    internal Brush HoverBackground { get; }
    internal Brush HoverBorder { get; }
    internal Brush Pressed { get; }
    internal Brush Expanded { get; }
    internal Brush ExpandedBorder { get; }
    internal double ShadowInset { get; }
    internal double BlurRadius => blur?.Radius ?? 0;

    internal void ApplyButtonResources(ResourceDictionary resources)
    {
        resources["DockForeground"] = Foreground;
        resources["DockAccent"] = Accent;
        resources["DockHoverBackground"] = HoverBackground;
        resources["DockHoverBorder"] = HoverBorder;
        resources["DockPressed"] = Pressed;
        resources["DockExpanded"] = Expanded;
        resources["DockExpandedBorder"] = ExpandedBorder;
        resources["DockResizeBackground"] = tooltipBackground;
        resources["DockTileCorner"] = new CornerRadius(Math.Min(8, Tokens.CornerRadius));
    }

    internal void ApplySurface(Border surface, Border treatment, Border? texture = null, Border? shadowPlate = null)
    {
        var radius = new CornerRadius(Tokens.CornerRadius);
        surface.CornerRadius = treatment.CornerRadius = radius;
        surface.BorderThickness = new Thickness(Tokens.BorderThickness);
        surface.BorderBrush = Border;
        surface.Background = shadowPlate == null ? Background : Brushes.Transparent;
        treatment.Background = texture == null && pattern != Brushes.Transparent ? pattern : finish;
        treatment.Effect = blur;

        if (texture != null)
        {
            texture.Background = pattern;
            texture.CornerRadius = radius;
        }
        if (shadowPlate != null)
        {
            shadowPlate.Background = Background;
            shadowPlate.Effect = shadow;
            shadowPlate.CornerRadius = radius;
        }
    }

    internal void ApplyTooltip(ToolTip tooltip)
    {
        tooltip.Template = tooltipTemplate;
        tooltip.Foreground = Foreground;
        tooltip.Background = tooltipBackground;
        tooltip.BorderBrush = tooltipBorder;
        tooltip.BorderThickness = new Thickness(1);
        tooltip.Padding = Tokens.Tooltip == DockThemeTooltip.Floating ? new Thickness(11, 6, 11, 6) : new Thickness(8, 4, 8, 4);
        tooltip.FontSize = 12;
        if (Tokens.Tooltip == DockThemeTooltip.Technical)
            tooltip.FontFamily = new FontFamily("Cascadia Mono, Microsoft YaHei UI");
        else
            tooltip.ClearValue(Control.FontFamilyProperty);
    }

    private static ControlTemplate CreateTooltipTemplate(DockThemeTooltip design)
    {
        var border = new FrameworkElementFactory(typeof(WpfBorder));
        border.SetValue(WpfBorder.CornerRadiusProperty, new CornerRadius(design switch
        {
            DockThemeTooltip.Floating => 9,
            DockThemeTooltip.Technical => 0,
            _ => 4
        }));
        border.SetValue(WpfBorder.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(WpfBorder.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(WpfBorder.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(WpfBorder.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        return new ControlTemplate(typeof(ToolTip)) { VisualTree = border };
    }

    private static Brush BuildPattern(DockThemeTokens tokens)
    {
        if (tokens.Pattern == DockThemePattern.None || tokens.PatternOpacity <= 0 || tokens.Opacity <= 0)
            return Brushes.Transparent;

        double size = tokens.PatternSize;
        Brush ink = Solid(Alpha(Parse(tokens.PatternColor), tokens.PatternOpacity * tokens.Opacity));
        var lines = new GeometryGroup();
        switch (tokens.Pattern)
        {
            case DockThemePattern.Dots:
                lines.Children.Add(new EllipseGeometry(new Point(size / 2, size / 2), 1, 1));
                break;
            case DockThemePattern.Grid:
                lines.Children.Add(new LineGeometry(new Point(0.5, 0), new Point(.5, size)));
                lines.Children.Add(new LineGeometry(new Point(0, .5), new Point(size, .5)));
                break;
            case DockThemePattern.Diagonal:
                lines.Children.Add(new LineGeometry(new Point(-.5, size + .5), new Point(size + .5, -.5)));
                break;
        }

        var drawing = new DrawingGroup();
        using (DrawingContext context = drawing.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, size, size));
            bool dots = tokens.Pattern == DockThemePattern.Dots;
            context.DrawGeometry(dots ? ink : null, dots ? null : new Pen(ink, 1), lines);
        }
        return Freeze(new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, size, size),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, size, size),
            Stretch = Stretch.Fill
        });
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
    private static Color Alpha(Color color, double opacity)
    {
        color.A = (byte)Math.Round(color.A * opacity);
        return color;
    }

    private static Brush Solid(Color color) => Freeze(new SolidColorBrush(color));

    private static T Freeze<T>(T resource) where T : Freezable
    {
        resource.Freeze();
        return resource;
    }
}
