using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DesktopDock;

public partial class DockThemeEditorWindow
{
    private static readonly (string Glyph, string Name)[] PreviewItems =
        [("\uE8B7", "文件夹"), ("\uE8A5", "文档"), ("\uE774", "网页"), ("\uE80A", "分组")];
    private static readonly (string Glyph, string Name)[] PreviewGroupItems =
        [("\uE8A5", "文档"), ("\uE91B", "图像"), ("\uE8B7", "文件夹")];

    private DockThemeMotion? previewMotion;
    private DockThemeMotion? previewGroupMotion;

    private void RefreshPreview()
    {
        if (PreviewSurface == null)
            return;
        DockThemeTokens tokens = EditingTokens;
        DockThemeState state = draft.State;
        DockOptions options = dockOptions();
        var visuals = new DockThemeVisuals(tokens, state.PerformanceMode);
        bool animations = state.AnimationsEnabled && SystemParameters.ClientAreaAnimation;
        visuals.ApplySurface(PreviewSurface, PreviewTreatment, PreviewTexture, PreviewShadow);
        visuals.ApplySurface(PreviewGroup, PreviewGroupTreatment, PreviewGroupTexture, PreviewGroupShadow);
        double padding = tokens.Padding < 0 ? 5 : tokens.Padding;
        double icon = tokens.IconSize == 0 ? options.IconSize : tokens.IconSize;
        double spacing = tokens.IconSpacing < 0 ? options.Layout.IconSpacing : tokens.IconSpacing;
        PreviewLayout.Margin = new Thickness(padding);
        PreviewGroupIcons.Margin = new Thickness(padding);
        PreviewIcons.Children.Clear();
        PreviewGroupIcons.Children.Clear();
        PreviewGrip.Children.Clear();
        PreviewGrip.Children.Add(PreviewGripMark(tokens.Grip, visuals.Accent));
        PreviewThemeName.Text = SelectedTheme.Name;

        DockLayoutMode layout = tokens.LayoutPreset == DockThemeLayout.Dock
            ? options.Layout.Mode : Enum.Parse<DockLayoutMode>(tokens.LayoutPreset.ToString());
        bool side = options.Placement is DockPlacement.Left or DockPlacement.Right || options.Placement == DockPlacement.Floating && options.FloatingVertical;
        bool vertical = layout is DockLayoutMode.SingleColumn or DockLayoutMode.MultiColumnGrid || layout is DockLayoutMode.Auto or DockLayoutMode.GroupExpand && side;
        int tracks = options.Layout.GridTracks;
        PreviewIcons.Rows = layout == DockLayoutMode.MultiRowGrid ? Math.Min(PreviewItems.Length, tracks) : vertical ? 0 : 1;
        PreviewIcons.Columns = layout == DockLayoutMode.MultiColumnGrid ? Math.Min(PreviewItems.Length, tracks) : vertical ? 1 : 0;
        if (tokens.LayoutPreset == DockThemeLayout.Dock && options.Layout.Grid is { } saved)
        {
            PreviewIcons.Rows = Math.Min(4, saved.Rows);
            PreviewIcons.Columns = Math.Min(4, saved.Columns);
            if (PreviewIcons.Rows * PreviewIcons.Columns < PreviewItems.Length)
                PreviewIcons.Rows = (int)Math.Ceiling(PreviewItems.Length / (double)PreviewIcons.Columns);
        }

        foreach ((string glyph, string label) in PreviewItems)
        {
            Button element = PreviewIcon(glyph, label, visuals, icon, spacing, animations && !state.PerformanceMode);
            if (label == "分组")
                element.Click += (_, _) => ToggleGroupPreview();
            PreviewIcons.Children.Add(element);
        }
        foreach ((string glyph, string label) in PreviewGroupItems)
            PreviewGroupIcons.Children.Add(PreviewIcon(glyph, label, visuals, icon, spacing, animations && !state.PerformanceMode));

        if (layout == DockLayoutMode.GroupExpand)
            isGroupPreviewOpen = true;
        PreviewGroup.Visibility = isGroupPreviewOpen ? Visibility.Visible : Visibility.Collapsed;
        if (!animations)
        {
            previewMotion?.Stop();
            previewGroupMotion?.Stop();
        }
    }

    private static UIElement PreviewGripMark(DockThemeGrip design, Brush accent)
    {
        var grip = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Opacity = .8 };
        for (int index = 0; index < (design == DockThemeGrip.Bar ? 1 : 3); index++)
        {
            if (design == DockThemeGrip.Dots)
            {
                grip.Children.Add(new Ellipse { Width = 3, Height = 3, Fill = accent, Margin = new Thickness(1.5) });
                continue;
            }
            grip.Children.Add(new Border
            {
                Width = design == DockThemeGrip.Bar ? 3 : 9, Height = design == DockThemeGrip.Bar ? 19 : 1.5,
                Background = accent, CornerRadius = new CornerRadius(design == DockThemeGrip.Bar ? 2 : 0), Margin = new Thickness(0, 1, 0, 1)
            });
        }
        return grip;
    }

    private static Button PreviewIcon(string glyph, string label, DockThemeVisuals visuals, double icon, double spacing, bool animations)
    {
        var transform = new TransformGroup { Children = [new ScaleTransform(), new TranslateTransform()] };
        var mark = new TextBlock
        {
            Text = glyph, Foreground = visuals.Foreground, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = icon * .57, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(.5, .5), RenderTransform = transform
        };
        var area = new Border
        {
            Width = icon + 8, Height = icon + 8, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Math.Min(9, visuals.Tokens.CornerRadius)), Background = Brushes.Transparent, Child = mark
        };
        var button = new Button
        {
            Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) },
            Content = area, Margin = new Thickness(spacing / 2), Cursor = Cursors.Hand, Focusable = true
        };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        var tooltip = new ToolTip { Content = label, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        visuals.ApplyTooltip(tooltip);
        button.ToolTip = tooltip;
        ToolTipService.SetInitialShowDelay(button, 350);

        void Hover(bool active)
        {
            area.Background = active ? visuals.HoverBackground : Brushes.Transparent;
            area.BorderBrush = active ? visuals.HoverBorder : Brushes.Transparent;
            if (!animations)
                return;
            var scale = (ScaleTransform)transform.Children[0];
            var offset = (TranslateTransform)transform.Children[1];
            DockThemeHover kind = visuals.Tokens.Hover;
            if (kind == DockThemeHover.Scale)
            {
                var zoom = DockThemeMotion.Animate(scale.ScaleX, active ? 1.10 : 1, 155);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
            }
            if (kind is DockThemeHover.Lift or DockThemeHover.Bounce)
                offset.BeginAnimation(TranslateTransform.YProperty, DockThemeMotion.Animate(offset.Y, active ? -4 : 0, 155));
        }
        button.MouseEnter += (_, _) => Hover(true);
        button.MouseLeave += (_, _) => Hover(false);
        return button;
    }

    private void ToggleGroupPreview()
    {
        if (isGroupPreviewOpen)
        {
            previewGroupMotion?.Stop();
            isGroupPreviewOpen = false;
            PreviewGroup.Visibility = Visibility.Collapsed;
        }
        else
        {
            PlayPreview(2);
        }
    }

    private void PlayPreviewClick(object sender, RoutedEventArgs e) => PlayPreview(DemoTypeInput.SelectedIndex);

    private void PlayPreview(int action)
    {
        DockThemeTokens tokens = EditingTokens;
        bool group = action == 2;
        bool enter = action != 1;
        (DockThemeAnimation kind, int duration) = action switch
        {
            1 => (tokens.HideAnimation, tokens.HideMilliseconds),
            2 => (tokens.GroupAnimation, tokens.GroupMilliseconds),
            _ => (tokens.RevealAnimation, tokens.RevealMilliseconds)
        };
        (kind, duration) = DockThemeMotion.Effective(kind, duration, draft.State.AnimationsEnabled, draft.State.PerformanceMode);
        if (group)
        {
            isGroupPreviewOpen = true;
            PreviewGroup.Visibility = Visibility.Visible;
            previewGroupMotion ??= new DockThemeMotion(PreviewGroup);
            previewGroupMotion.Play(kind, duration, true, new Vector(0, 12));
        }
        else
        {
            previewMotion ??= new DockThemeMotion(PreviewRoot);
            previewMotion.Play(kind, duration, enter, new Vector(0, 14));
        }
        if (duration == 0)
            SetFeedback("当前动画已关闭或为即时切换");
    }

    private static string EnumLabel(object value) => value switch
    {
        DockThemeMaterial.Solid => "纯色", DockThemeMaterial.Frosted => "磨砂玻璃",
        DockThemeMaterial.Technical => "科技材质", DockThemeMaterial.Transparent => "透明",
        DockThemeTooltip.Minimal => "简洁", DockThemeTooltip.Floating => "悬浮", DockThemeTooltip.Technical => "直角科技",
        DockThemeGrip.Dots => "点状", DockThemeGrip.Bar => "竖条", DockThemeGrip.Lines => "横线",
        DockThemeHover.None => "无", DockThemeHover.Scale => "放大", DockThemeHover.Lift => "上浮",
        DockThemeHover.Glow => "强调边框", DockThemeHover.Bounce => "弹跳",
        DockThemeAnimation.None => "无", DockThemeAnimation.Fade => "淡入淡出",
        DockThemeAnimation.Slide => "滑动", DockThemeAnimation.Scale => "缩放",
        DockThemeLayout.Dock => "沿用 Dock 设置", DockThemeLayout.Auto => "随停靠方向",
        DockThemeLayout.SingleRow => "单行", DockThemeLayout.SingleColumn => "单列",
        DockThemeLayout.MultiRowGrid => "多行网格", DockThemeLayout.MultiColumnGrid => "多列网格",
        DockThemeLayout.GroupExpand => "分组展开",
        DockThemePattern.None => "无", DockThemePattern.Dots => "点阵", DockThemePattern.Grid => "网格",
        DockThemePattern.Diagonal => "斜纹",
        _ => value.ToString() ?? string.Empty
    };
}
