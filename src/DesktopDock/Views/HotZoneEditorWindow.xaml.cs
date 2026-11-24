using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace DesktopDock;

public partial class HotZoneEditorWindow : Window
{
    private readonly Guid id;

    public HotZoneEditorWindow(DockHotZone zone)
    {
        id = zone.Id == Guid.Empty ? Guid.NewGuid() : zone.Id;
        InitializeComponent();
        EdgeInput.SelectedIndex = (int)zone.Edge;
        StartInput.Text = zone.StartPercent.ToString(CultureInfo.CurrentCulture);
        EndInput.Text = zone.EndPercent.ToString(CultureInfo.CurrentCulture);
        ThicknessInput.Text = zone.Thickness.ToString(CultureInfo.CurrentCulture);
        OffsetInput.Text = zone.Offset.ToString(CultureInfo.CurrentCulture);
        RevealDelayInput.Text = zone.RevealDelayMs.ToString(CultureInfo.CurrentCulture);
        HideDelayInput.Text = zone.HideDelayMs.ToString(CultureInfo.CurrentCulture);
    }

    internal DockHotZone? Result { get; private set; }

    private void Apply(object sender, RoutedEventArgs e)
    {
        try
        {
            var zone = new DockHotZone(id, (HotZoneEdge)EdgeInput.SelectedIndex,
                ReadNumber(StartInput, "起点"), ReadNumber(EndInput, "终点"), ReadNumber(ThicknessInput, "厚度"),
                ReadNumber(OffsetInput, "向内偏移"), ReadDelay(RevealDelayInput, "呼出延迟"), ReadDelay(HideDelayInput, "收起延迟"));
            zone.Validate();
            Result = zone;
            DialogResult = true;
        }
        catch (ArgumentException error)
        {
            ValidationMessage.Text = error.Message;
            ValidationMessage.Visibility = Visibility.Visible;
        }
    }

    private static double ReadNumber(TextBox input, string label)
    {
        if (double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) && double.IsFinite(value))
            return value;
        input.Focus();
        throw new ArgumentException($"请输入有效的{label}。");
    }

    private static int ReadDelay(TextBox input, string label)
    {
        if (int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int value) && value >= 0)
            return value;
        input.Focus();
        throw new ArgumentException($"{label}须为非负整数毫秒数。");
    }
}
