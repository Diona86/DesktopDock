using Microsoft.Win32;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopDock;

public partial class SettingsWindow : Window
{
    private readonly DockSession session;
    private readonly Action<bool> setDockVisible;
    private readonly Action<DockItem> editItem;
    private readonly Action<DockItem> replaceIcon;
    private readonly Func<IReadOnlyList<DockMonitor>> monitors;
    private readonly Func<DockBehaviorOptions, string?> saveBehavior;
    private readonly Action? openThemeEditor;
    private bool dockVisible;

    public SettingsWindow(DockSession session, bool dockVisible, Action<bool> setDockVisible,
        Action<DockItem> editItem, Action<DockItem> replaceIcon,
        Func<IReadOnlyList<DockMonitor>>? monitors = null, Func<DockBehaviorOptions, string?>? saveBehavior = null,
        Action? openThemeEditor = null)
    {
        this.session = session;
        this.setDockVisible = setDockVisible;
        this.editItem = editItem;
        this.replaceIcon = replaceIcon;
        this.monitors = monitors ?? (() => Array.Empty<DockMonitor>());
        this.saveBehavior = saveBehavior ?? SaveBehaviorToSession;
        this.openThemeEditor = openThemeEditor;
        InitializeComponent();
        ThemeEditorButton.Visibility = openThemeEditor == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateDockVisibility(dockVisible);
        RefreshItems();
        session.Changed += SessionChanged;
        Closed += (_, _) => session.Changed -= SessionChanged;
        Navigation.SelectedIndex = 1;
    }

    public void UpdateDockVisibility(bool visible)
    {
        dockVisible = visible;
        VisibilityButton.Content = visible ? "隐藏 Dock" : "显示 Dock";
    }

    internal void SelectPage(int index) => Navigation.SelectedIndex = index;
    internal int SelectedPage => Navigation.SelectedIndex;

    private void OpenThemeEditor(object sender, RoutedEventArgs e) => openThemeEditor?.Invoke();

    internal bool IsEditingShortcuts => Navigation.SelectedIndex == 5;

    internal bool CaptureRegisteredHotkey(HotkeyChord chord) => IsActive && Navigation.SelectedIndex == 5 && HotkeysPage.Capture(chord);

    internal void UpdateHotkeyRegistrationStatus(string? status) => HotkeysPage.ShowRegistrationStatus(status);

    private DockItem? SelectedItem => (ItemList.SelectedItem as ItemRow)?.Item;

    private void Navigate(object sender, SelectionChangedEventArgs e)
    {
        if (Navigation.SelectedItem is not ListBoxItem item)
            return;

        int page = Navigation.SelectedIndex;
        PageTitle.Text = item.Content.ToString();
        AppearancePage.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        ItemsPage.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        PositionPage.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        GeneralPage.Visibility = page == 3 ? Visibility.Visible : Visibility.Collapsed;
        HotZonesPage.Visibility = page == 4 ? Visibility.Visible : Visibility.Collapsed;
        HotkeysPage.Visibility = page == 5 ? Visibility.Visible : Visibility.Collapsed;
        LayoutPage.Visibility = page == 6 ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.Visibility = page == 1 ? Visibility.Collapsed : Visibility.Visible;
        Status.Text = string.Empty;
        LoadPageOptions();
    }

    private void LoadPageOptions()
    {
        DockOptions options = session.Options;
        switch (Navigation.SelectedIndex)
        {
            case 0:
                IconSizeInput.Text = options.IconSize.ToString(CultureInfo.CurrentCulture);
                DockThicknessInput.Text = options.DockThickness.ToString(CultureInfo.CurrentCulture);
                DockLengthInput.Text = options.DockLength.ToString(CultureInfo.CurrentCulture);
                break;
            case 2:
                PlacementInput.SelectedIndex = (int)options.Placement;
                FloatingVerticalInput.IsChecked = options.FloatingVertical;
                DockLockInput.IsChecked = options.DockPositionLocked;
                break;
            case 3:
                StartupVisibilityInput.IsChecked = options.ShowDockOnStartup;
                GlobalLockInput.IsChecked = readGlobalLock?.Invoke() ?? options.GlobalPositionLocked;
                RefreshStartupStatus();
                break;
            case 4:
                BehaviorPage.LoadOptions(options.Behavior, options.Placement, monitors());
                break;
            case 5:
                HotkeysPage.LoadOptions(readGlobalHotkeys?.Invoke() ?? options.Behavior.Hotkeys);
                break;
            case 6:
                LayoutPage.LoadOptions(session);
                break;
        }
    }

    private void SaveOptions(object sender, RoutedEventArgs e)
    {
        if (Navigation.SelectedIndex is 4 or 5)
        {
            ApplyBehaviorSettings();
            return;
        }

        TryChange("无法保存 Dock 设置", () =>
        {
            DockOptions options = session.Options;
            options = Navigation.SelectedIndex switch
            {
                0 => options with
                {
                    IconSize = ReadSize(IconSizeInput, "图标尺寸"),
                    DockThickness = ReadSize(DockThicknessInput, "Dock 厚度"),
                    DockLength = ReadSize(DockLengthInput, "Dock 长度")
                },
                2 => options with
                {
                    Placement = (DockPlacement)PlacementInput.SelectedIndex,
                    FloatingVertical = FloatingVerticalInput.IsChecked == true,
                    DockPositionLocked = DockLockInput.IsChecked == true
                },
                3 => options with
                {
                    ShowDockOnStartup = StartupVisibilityInput.IsChecked == true,
                    GlobalPositionLocked = saveGlobalLock == null ? GlobalLockInput.IsChecked == true : options.GlobalPositionLocked
                },
                6 => options with { Layout = LayoutPage.ReadOptions(options.Layout) },
                _ => options
            };
            session.UpdateOptions(options);
            if (Navigation.SelectedIndex == 3)
                saveGlobalLock?.Invoke(GlobalLockInput.IsChecked == true);
            Status.Text = "设置已保存。";
        });
    }

    private void ApplyBehaviorSettings()
    {
        bool editingBehavior = Navigation.SelectedIndex == 4;
        try
        {
            DockBehaviorOptions current = session.Options.Behavior;
            DockBehaviorOptions updated = editingBehavior ? BehaviorPage.ApplyTo(current) : current with { Hotkeys = HotkeysPage.Bindings };
            updated.Validate();
            string? error = saveBehavior(updated);
            if (error != null)
            {
                Status.Text = error;
                return;
            }

            if (editingBehavior)
                BehaviorPage.MarkSaved();
            else
                HotkeysPage.MarkSaved();
            Status.Text = "设置已保存。";
        }
        catch (Exception error) when (DockDialogs.IsUserFacing(error))
        {
            Status.Text = error.Message;
        }
    }

    private string? SaveBehaviorToSession(DockBehaviorOptions options)
    {
        session.UpdateOptions(session.Options with { Behavior = options });
        return null;
    }

    private static double ReadSize(TextBox field, string label)
    {
        if (double.TryParse(field.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value))
            return value;
        field.Focus();
        throw new ArgumentException($"请输入有效的{label}。");
    }

    private void PlacementChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FloatingVerticalInput != null)
            FloatingVerticalInput.IsEnabled = PlacementInput.SelectedIndex == (int)DockPlacement.Floating;
    }

    private void ToggleDock(object sender, RoutedEventArgs e) => setDockVisible(!dockVisible);

    private void AddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加文件或应用",
            Filter = "所有文件|*.*|应用和快捷方式|*.exe;*.lnk",
            CheckFileExists = true,
            DereferenceLinks = false,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true)
            AddTargets(dialog.FileNames);
    }

    private void AddFolders(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "添加文件夹", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            AddTargets(dialog.FolderNames);
    }

    private void AddUrl(object sender, RoutedEventArgs e) => DockDialogs.Edit(this, session, null);

    private void AddTargets(IEnumerable<string> paths)
    {
        int count = 0;
        var errors = new List<string>();
        foreach (string path in paths)
        {
            try
            {
                DockItem item = session.AddItem(path);
                count++;
                ItemList.SelectedItem = ItemList.Items.Cast<ItemRow>().FirstOrDefault(row => row.Item.Id == item.Id);
            }
            catch (Exception error) when (DockDialogs.IsUserFacing(error))
            {
                errors.Add(error.Message);
            }
        }
        Status.Text = errors.Count > 0 ? $"已添加 {count} 项，{errors.Count} 项未添加。" : $"已添加 {count} 项。";
        if (errors.Count > 0)
            DockDialogs.Error(this, "部分项目无法添加", new ArgumentException(string.Join(Environment.NewLine, errors.Take(4))));
    }

    private void SessionChanged(object? sender, EventArgs e)
    {
        RefreshItems();
        if (Navigation.SelectedIndex == 6)
        {
            LayoutPage.RefreshGridMode();
            LayoutPage.RefreshGroups();
        }
    }

    private void RefreshItems()
    {
        Guid? selected = SelectedItem?.Id;
        ItemRow[] items = session.Items.Select(item => new ItemRow(item)).ToArray();
        ItemList.ItemsSource = items;
        ItemList.SelectedItem = items.FirstOrDefault(row => row.Item.Id == selected);
        EmptyItems.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateItemButtons();
    }

    private void SelectItem(object sender, SelectionChangedEventArgs e) => UpdateItemButtons();

    private void UpdateItemButtons()
    {
        if (EditButton == null)
            return;
        bool enabled = SelectedItem != null;
        EditButton.IsEnabled = enabled;
        ReplaceIconButton.IsEnabled = enabled;
        RemoveButton.IsEnabled = enabled;
    }

    private void EditSelected(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is DockItem item)
            editItem(item);
    }

    private void EditSelectedOnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ItemList, e.OriginalSource as DependencyObject) is ListViewItem)
            EditSelected(sender, e);
    }

    private void ReplaceSelectedIcon(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is DockItem item)
            replaceIcon(item);
    }

    private void RemoveSelected(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is not DockItem item)
            return;
        TryChange("无法移除引用", () =>
        {
            session.RemoveItem(item.Id);
            Status.Text = "已移除 Dock 引用。";
        });
    }

    private void TryChange(string caption, Action change)
    {
        try
        {
            change();
        }
        catch (Exception error) when (DockDialogs.IsUserFacing(error))
        {
            DockDialogs.Error(this, caption, error);
        }
    }

    private sealed class ItemRow(DockItem item)
    {
        public DockItem Item { get; } = item;
        public string Name => Item.Name;
        public string Target => Item.Target;
        public string KindLabel => Item.Kind switch
        {
            DockItemKind.Application => "应用",
            DockItemKind.Shortcut => "快捷方式",
            DockItemKind.Folder => "文件夹",
            DockItemKind.Url => "网址",
            _ => "文件"
        };
    }
}
