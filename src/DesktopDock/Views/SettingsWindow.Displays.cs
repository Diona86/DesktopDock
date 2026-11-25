using System.Windows;
using System.Windows.Controls;

namespace DesktopDock;

public partial class SettingsWindow
{
    private sealed record DisplayChoice(string Id, string Label);

    private Action<string>? switchDisplay;
    private string? selectedDisplay;
    private Action? openZoneMap;
    private Action? openDesktopControls;
    private DockStartupRegistration? startup;
    private Func<bool>? readGlobalLock;
    private Action<bool>? saveGlobalLock;
    private Func<DockHotkeys>? readGlobalHotkeys;

    internal void SetDisplayChoices(IReadOnlyList<DockMonitor> monitors, string? selected, Action<string> switchDisplay)
    {
        this.switchDisplay = null;
        selectedDisplay = selected;
        var choices = monitors.Select(monitor => new DisplayChoice(monitor.Id,
            monitor.Primary ? $"{monitor.Name} · 主屏" : monitor.Name)).ToArray();
        DisplayInput.ItemsSource = choices;
        DisplayInput.SelectedItem = choices.FirstOrDefault(choice => choice.Id == selected);
        DisplaySelector.Visibility = choices.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DisplayNotice.Text = string.Empty;
        this.switchDisplay = switchDisplay;
    }

    internal void UpdateDisplayNotice(string message) => DisplayNotice.Text = message;

    private void SwitchDisplay(object sender, SelectionChangedEventArgs e)
    {
        if (DisplayInput.SelectedItem is DisplayChoice choice && choice.Id != selectedDisplay)
            switchDisplay?.Invoke(choice.Id);
    }

    internal void ConfigureSystemControls(Action openZoneMap, Action openDesktopControls,
        DockStartupRegistration? startup, Func<bool> readGlobalLock, Action<bool> saveGlobalLock, Func<DockHotkeys> readGlobalHotkeys)
    {
        this.openZoneMap = openZoneMap;
        this.openDesktopControls = openDesktopControls;
        this.startup = startup;
        this.readGlobalLock = readGlobalLock;
        this.saveGlobalLock = saveGlobalLock;
        this.readGlobalHotkeys = readGlobalHotkeys;
        ZoneMapButton.Visibility = Visibility.Visible;
        DesktopSystemControls.Visibility = Visibility.Visible;
        WindowsStartupInput.Visibility = startup == null ? Visibility.Collapsed : Visibility.Visible;
        WindowsStartupStatus.Visibility = startup == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenZoneMap(object sender, RoutedEventArgs e) => openZoneMap?.Invoke();
    private void OpenDesktopControls(object sender, RoutedEventArgs e) => openDesktopControls?.Invoke();

    private void RefreshStartupStatus()
    {
        if (startup == null)
            return;
        DockStartupStatus state = startup.Read();
        WindowsStartupInput.IsChecked = state.State is DockStartupState.Enabled or DockStartupState.OtherInstallation;
        WindowsStartupInput.IsEnabled = state.State is not (DockStartupState.Conflict or DockStartupState.Unavailable);
        WindowsStartupStatus.Text = state.Message;
    }

    private void ToggleWindowsStartup(object sender, RoutedEventArgs e)
    {
        try
        {
            startup?.SetEnabled(WindowsStartupInput.IsChecked == true);
            RefreshStartupStatus();
        }
        catch (InvalidOperationException error)
        {
            RefreshStartupStatus();
            WindowsStartupStatus.Text = error.Message;
        }
    }
}
