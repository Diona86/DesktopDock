using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DesktopDock;

public partial class DockWindow
{
    private const int ExtendedStyleIndex = -20;
    private const int TransparentWindowStyle = 0x20;
    private DockThemeMotion? presentationMotion;
    private DockThemeVisuals? themeVisuals;
    private bool themeAnimationsEnabled = true;
    private bool themePerformanceMode;
    private bool allowPointerInput = true;

    internal bool IsPresentationRequested { get; private set; }

    private DockThemeTokens? ThemeTokens => themeVisuals?.Tokens;
    private double ThemedIconSize => ThemeTokens?.IconSize is double icon && icon > 0 ? icon : session.Options.IconSize;
    private double ThemedIconSpacing => ThemeTokens?.IconSpacing is double spacing && spacing >= 0 ? spacing : session.Options.Layout.IconSpacing;
    private double ThemeShadowInset => themeVisuals?.ShadowInset ?? 0;

    private DockLayoutOptions ThemedLayout()
    {
        DockLayoutOptions layout = session.Options.Layout;
        DockLayoutMode mode = ThemeTokens?.LayoutPreset switch
        {
            DockThemeLayout.Auto => DockLayoutMode.Auto,
            DockThemeLayout.SingleRow => DockLayoutMode.SingleRow,
            DockThemeLayout.SingleColumn => DockLayoutMode.SingleColumn,
            DockThemeLayout.MultiRowGrid => DockLayoutMode.MultiRowGrid,
            DockThemeLayout.MultiColumnGrid => DockLayoutMode.MultiColumnGrid,
            DockThemeLayout.GroupExpand => DockLayoutMode.GroupExpand,
            _ => layout.Mode
        };
        return layout with { Mode = mode, IconSpacing = ThemedIconSpacing };
    }

    internal void ApplyTheme(DockThemeTokens tokens, bool animationsEnabled, bool performanceMode)
    {
        if (ThemeTokens == tokens && themeAnimationsEnabled == animationsEnabled && themePerformanceMode == performanceMode)
            return;

        DockThemeTokens? previous = ThemeTokens;
        double previousInset = ThemeShadowInset;
        bool geometryChanged = previous == null || previous.IconSize != tokens.IconSize || previous.IconSpacing != tokens.IconSpacing ||
            previous.Padding != tokens.Padding || previous.BorderThickness != tokens.BorderThickness || previous.LayoutPreset != tokens.LayoutPreset;
        if (previous != tokens || themePerformanceMode != performanceMode)
        {
            themeVisuals = new DockThemeVisuals(tokens, performanceMode);
            themeVisuals.ApplySurface(Surface, ThemeTreatment, ThemeTexture, ThemeShadow);
            themeVisuals.ApplyButtonResources(Resources);
            DockChrome.Margin = new Thickness(ThemeShadowInset);
            ApplyGripDesign();
            ApplyEntryTooltip(MoveHandle);
        }
        themeAnimationsEnabled = animationsEnabled;
        themePerformanceMode = performanceMode;
        presentationMotion?.Stop();
        if (!IsPresentationRequested && IsVisible)
            Hide();

        foreach (DockGroupPanel panel in groupPanels)
            panel.ApplyTheme(themeVisuals!, animationsEnabled, performanceMode);

        if (geometryChanged || previousInset != ThemeShadowInset)
        {
            Refresh();
            RefreshGroupPanels();
        }
        else
        {
            var dockButtons = (HasTileGrid ? TileCanvas.Children : ItemStrip.Children).OfType<Button>();
            foreach (Button button in dockButtons.Concat(groupPanels.SelectMany(panel => panel.EntryButtons)))
            {
                ApplyEntryTooltip(button);
                if ((!animationsEnabled || performanceMode || previous?.Hover != tokens.Hover) &&
                    EntryImage(button) is { RenderTransform: TransformGroup transform })
                    ClearHoverAnimation((ScaleTransform)transform.Children[0], (TranslateTransform)transform.Children[1]);
            }
        }
    }

    internal void SetThemedVisibility(bool visible)
    {
        if (IsPresentationRequested == visible)
            return;

        IsPresentationRequested = visible;
        presentationMotion ??= new DockThemeMotion(DockChrome);
        if (visible)
        {
            SetPointerInput(true);
            if (!IsVisible)
                Show();
        }
        else
        {
            SetPointerInput(false);
            CloseInteractionMenus();
            CloseGroups();
        }

        DockThemeTokens? tokens = ThemeTokens;
        DockThemeAnimation mode = (visible ? tokens?.RevealAnimation : tokens?.HideAnimation) ?? DockThemeAnimation.None;
        int time = (visible ? tokens?.RevealMilliseconds : tokens?.HideMilliseconds) ?? 0;
        (DockThemeAnimation kind, int ms) = DockThemeMotion.Effective(mode, time, themeAnimationsEnabled, themePerformanceMode);
        presentationMotion.Play(kind, ms, visible, GetDockMotionAxis(), visible ? null : Hide);
    }

    private Vector GetDockMotionAxis() => revealAnchor?.Edge switch
    {
        HotZoneEdge.Top => new Vector(0, -9),
        HotZoneEdge.Left => new Vector(-9, 0),
        HotZoneEdge.Right => new Vector(9, 0),
        HotZoneEdge.Bottom => new Vector(0, 9),
        _ => session.Options.Placement switch
        {
            DockPlacement.Top => new Vector(0, -9),
            DockPlacement.Left => new Vector(-9, 0),
            DockPlacement.Right => new Vector(9, 0),
            _ => new Vector(0, 9)
        }
    };

    private void SetPointerInput(bool enabled)
    {
        allowPointerInput = enabled;
        DockChrome.IsHitTestVisible = enabled;
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0)
            return;

        int style = GetWindowLong(hwnd, ExtendedStyleIndex);
        int next = enabled ? style & ~TransparentWindowStyle : style | TransparentWindowStyle;
        if (style != next)
            _ = SetWindowLong(hwnd, ExtendedStyleIndex, next);
    }

    private void ApplyGripDesign()
    {
        GripDots.Children.Clear();
        DockThemeGrip grip = ThemeTokens?.Grip ?? DockThemeGrip.Dots;
        int count = grip == DockThemeGrip.Bar ? 1 : grip == DockThemeGrip.Lines ? 2 : 3;
        double width = grip == DockThemeGrip.Dots ? 3 : (vertical ? 13 : 2);
        double height = grip == DockThemeGrip.Dots ? 3 : (vertical ? 2 : 13);
        for (int i = 0; i < count; i++)
        {
            Shape shape = grip == DockThemeGrip.Dots ? new Ellipse() : new Rectangle { RadiusX = 1, RadiusY = 1 };
            shape.Width = width;
            shape.Height = height;
            shape.Margin = new Thickness(1.5);
            shape.Fill = themeVisuals!.Accent;
            GripDots.Children.Add(shape);
        }
        GripDots.Orientation = (grip == DockThemeGrip.Lines ? !vertical : vertical) ? Orientation.Horizontal : Orientation.Vertical;
    }

    private void ApplyEntryTooltip(Button button)
    {
        if (themeVisuals == null)
            return;
        if (button.ToolTip is string content)
            button.ToolTip = new ToolTip { Content = content };
        if (button.ToolTip is ToolTip tooltip)
            themeVisuals.ApplyTooltip(tooltip);
    }

    private void ApplyHoverMotion(Button button, Image icon)
    {
        var scale = new ScaleTransform(1, 1);
        var shift = new TranslateTransform();
        icon.RenderTransform = new TransformGroup { Children = [scale, shift] };
        icon.RenderTransformOrigin = new Point(.5, .5);
        button.MouseEnter += (_, _) => AnimateHover(true);
        button.MouseLeave += (_, _) => AnimateHover(false);

        void AnimateHover(bool enter)
        {
            DockThemeHover? treatment = ThemeTokens?.Hover;
            bool motion = themeAnimationsEnabled && !themePerformanceMode && SystemParameters.ClientAreaAnimation &&
                treatment is DockThemeHover.Scale or DockThemeHover.Lift or DockThemeHover.Bounce;
            if (!motion)
            {
                ClearHoverAnimation(scale, shift);
                return;
            }

            double targetScale = enter && treatment == DockThemeHover.Scale ? 1.11 : 1;
            double targetShift = enter && treatment is DockThemeHover.Lift or DockThemeHover.Bounce ? -4 : 0;
            DoubleAnimation scaleAnimation = DockThemeMotion.Animate(scale.ScaleX, targetScale, 150);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
            DoubleAnimation shiftAnimation = DockThemeMotion.Animate(shift.Y, targetShift, 170);
            if (enter && treatment == DockThemeHover.Bounce)
                shiftAnimation.EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = .8 };
            shift.BeginAnimation(TranslateTransform.YProperty, shiftAnimation);
        }
    }

    private static void ClearHoverAnimation(ScaleTransform scale, TranslateTransform shift)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        shift.BeginAnimation(TranslateTransform.YProperty, null);
    }

    internal object ReadThemeRendering()
    {
        UpdateLayout();
        return new
        {
            material = ThemeTokens?.Material.ToString(),
            background = (themeVisuals?.Background ?? Surface.Background)?.ToString(),
            treatmentBrush = ThemeTreatment.Background?.GetType().Name,
            textureBrush = ThemeTexture.Background?.GetType().Name,
            border = Surface.BorderBrush?.ToString(),
            cornerRadius = Surface.CornerRadius.TopLeft,
            borderThickness = Surface.BorderThickness.Left,
            shadow = ThemeShadow.Effect != null,
            blur = themeVisuals?.BlurRadius ?? 0,
            opacity = DockChrome.Opacity,
            animationActive = presentationMotion?.Active == true,
            animationMilliseconds = presentationMotion?.Milliseconds ?? 0,
            logicalVisible = IsPresentationRequested,
            nativeVisible = DockWindowPosition.IsNativeVisible(this),
            pointerInput = allowPointerInput,
            tooltip = ThemeTokens?.Tooltip.ToString(),
            hover = ThemeTokens?.Hover.ToString(),
            grip = ThemeTokens?.Grip.ToString(),
            gripElementCount = GripDots.Children.Count,
            iconSize = ThemedIconSize,
            iconSpacing = ThemedIconSpacing,
            padding = Surface.Padding.Left,
            layout = ThemedLayout().Mode.ToString(),
            animationsEnabled = themeAnimationsEnabled,
            performanceMode = themePerformanceMode,
            width = Surface.ActualWidth,
            height = Surface.ActualHeight
        };
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint window, int index, int style);
}
