using Microsoft.Win32;
using System.Windows;

namespace DesktopDock;

public partial class ApplicationRuleEditorWindow : Window
{
    public ApplicationRuleEditorWindow(DockApplicationRule? rule)
    {
        InitializeComponent();
        ApplicationPathInput.Text = rule?.ExecutablePath ?? "";
        ActionInput.SelectedIndex = (int)(rule?.Action ?? DockRuleAction.BlockAutomatic);
    }

    internal DockApplicationRule? Result { get; private set; }

    private void Browse(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "选择应用程序",
            Filter = "应用程序 (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) == true)
            ApplicationPathInput.Text = picker.FileName;
    }

    private void Apply(object sender, RoutedEventArgs e)
    {
        try
        {
            var rule = new DockApplicationRule(ApplicationPathInput.Text.Trim().Trim('"'), (DockRuleAction)ActionInput.SelectedIndex);
            new DockBehaviorOptions { ApplicationRules = [rule] }.Validate();
            Result = rule with { ExecutablePath = Path.GetFullPath(rule.ExecutablePath) };
            DialogResult = true;
        }
        catch (Exception error) when (DockDialogs.IsUserFacing(error))
        {
            ValidationMessage.Text = error.Message;
            ValidationMessage.Visibility = Visibility.Visible;
        }
    }
}
