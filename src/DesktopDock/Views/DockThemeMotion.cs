using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopDock;

internal sealed class DockThemeMotion
{
    private readonly FrameworkElement content;
    private readonly ScaleTransform scale = new(1, 1);
    private readonly TranslateTransform offset = new();
    private int sequence;

    internal DockThemeMotion(FrameworkElement content)
    {
        this.content = content;
        content.RenderTransform = new TransformGroup { Children = [scale, offset] };
        content.RenderTransformOrigin = new Point(.5, .5);
    }

    internal bool Active { get; private set; }
    internal int Milliseconds { get; private set; }

    internal static (DockThemeAnimation Kind, int Milliseconds) Effective(DockThemeAnimation kind, int milliseconds,
        bool enabled, bool performanceMode)
    {
        if (!enabled || !SystemParameters.ClientAreaAnimation || milliseconds <= 0 || kind == DockThemeAnimation.None)
            return (DockThemeAnimation.None, 0);
        return performanceMode ? (DockThemeAnimation.Fade, Math.Min(milliseconds, 100)) : (kind, milliseconds);
    }

    internal void Play(DockThemeAnimation kind, int milliseconds, bool enter, Vector direction, Action? completed = null)
    {
        double previous = Active ? content.Opacity : 1;
        Stop();
        Milliseconds = milliseconds;
        if (kind == DockThemeAnimation.None || milliseconds == 0)
        {
            completed?.Invoke();
            return;
        }

        int version = sequence;
        Active = true;
        var opacity = Animate(enter ? Math.Min(previous, .30) : previous, enter ? 1 : 0, milliseconds);
        opacity.Completed += (_, _) =>
        {
            if (version != sequence)
                return;
            Stop();
            completed?.Invoke();
        };
        if (kind == DockThemeAnimation.Scale)
        {
            var transform = Animate(enter ? .94 : 1, enter ? 1 : .94, milliseconds);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, transform);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, transform);
        }
        else if (kind == DockThemeAnimation.Slide)
        {
            offset.BeginAnimation(TranslateTransform.XProperty, Animate(enter ? direction.X : 0, enter ? 0 : direction.X, milliseconds));
            offset.BeginAnimation(TranslateTransform.YProperty, Animate(enter ? direction.Y : 0, enter ? 0 : direction.Y, milliseconds));
        }
        content.BeginAnimation(UIElement.OpacityProperty, opacity);
    }

    internal void Stop()
    {
        sequence++;
        Active = false;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        offset.BeginAnimation(TranslateTransform.XProperty, null);
        offset.BeginAnimation(TranslateTransform.YProperty, null);
        content.BeginAnimation(UIElement.OpacityProperty, null);
    }

    internal static DoubleAnimation Animate(double from, double to, int milliseconds) => new(from, to, TimeSpan.FromMilliseconds(milliseconds))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.HoldEnd
    };
}
