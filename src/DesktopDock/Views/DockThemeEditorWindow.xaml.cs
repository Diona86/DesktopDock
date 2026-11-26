using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DesktopDock;

public partial class DockThemeEditorWindow : Window
{
    private sealed record ThemeChoice(string? Id, string Name);
    private sealed record Field(string Key, string Label, double Min = 0, double Max = 0, double Step = 1, double? DockValue = null);
    private sealed record Section(string Key, string Name, Field[] Fields);
    private sealed record Draft(DockThemeState State, DockThemeTokens? GlobalTokens = null, string? CopyId = null);

    private static readonly Section[] Sections =
    [
        new("material", "背景材质",
        [
            new("Material", "材质"), new("BackgroundColor", "背景色"), new("SecondaryColor", "辅助色"),
            new("Opacity", "背景透明度", 0, 1, .01), new("Blur", "模糊", 0, 24),
            new("Pattern", "纹理"), new("PatternColor", "纹理颜色"), new("PatternSize", "纹理尺寸 · DIP", 4, 48),
            new("PatternOpacity", "纹理透明度", 0, 1, .01)
        ]),
        new("surface", "轮廓与阴影",
        [
            new("CornerRadius", "圆角 · DIP", 0, 32), new("BorderThickness", "边框宽度 · DIP", 0, 4, .1),
            new("BorderColor", "边框色"), new("ShadowBlur", "阴影柔化 · DIP", 0, 32),
            new("ShadowDepth", "阴影偏移 · DIP", 0, 16), new("ShadowOpacity", "阴影透明度", 0, 1, .01), new("ShadowColor", "阴影色")
        ]),
        new("layout", "图标与布局",
        [
            new("IconSize", "图标尺寸 · DIP", 16, 96, 1, 0), new("IconSpacing", "图标间距 · DIP", 0, 32, .5, -1),
            new("Padding", "内边距 · DIP", 0, 24, .5, -1), new("LayoutPreset", "布局预设")
        ]),
        new("interaction", "提示与交互",
        [
            new("ForegroundColor", "提示文字色"), new("AccentColor", "强调色"), new("Tooltip", "Tooltip 样式"),
            new("Grip", "移动手柄"), new("Hover", "图标悬停")
        ]),
        new("motion", "动画",
        [
            new("RevealAnimation", "呼出动画"), new("RevealMilliseconds", "呼出时间 · ms", 0, 500, 1),
            new("HideAnimation", "收起动画"), new("HideMilliseconds", "收起时间 · ms", 0, 500, 1),
            new("GroupAnimation", "分组展开动画"), new("GroupMilliseconds", "分组展开时间 · ms", 0, 500, 1)
        ]),
        new("performance", "性能选项", [])
    ];

    private static readonly Dictionary<string, PropertyInfo> TokenProperties =
        typeof(DockThemeTokens).GetProperties().Where(property => property.CanWrite)
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PropertyInfo> PatchProperties =
        typeof(DockThemePatch).GetProperties().Where(property => property.CanWrite)
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions InputJson = new()
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private readonly DockThemeStore store;
    private readonly string dockId;
    private readonly Action<DockThemeState> preview;
    private readonly Func<DockOptions> dockOptions;
    private readonly List<Draft> history = [];
    private Draft draft;
    private Draft baseline;
    private bool refreshInProgress;
    private bool isCurrent;
    private bool groupUndo;
    private bool hasGroupedUndo;
    private bool isGroupPreviewOpen;
    private DispatcherOperation? pendingPreview;

    internal DockThemeEditorWindow(DockThemeStore store, string dockId, Action<DockThemeState> preview, Func<DockOptions>? dockOptions = null)
    {
        this.store = store;
        this.dockId = dockId;
        this.preview = preview;
        this.dockOptions = dockOptions ?? (() => new DockOptions());
        draft = baseline = new Draft(store.Current);
        InitializeComponent();
        Closed += (_, _) => pendingPreview?.Abort();
        Width = Math.Min(Width, Math.Max(MinWidth, SystemParameters.WorkArea.Width - 30));
        Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 24));
        CategoryInput.ItemsSource = Sections.Select(section => section.Name);
        ScopeInput.SelectedIndex = 0;
        CategoryInput.SelectedIndex = 0;
        RefreshEditor();
        if (store.LoadError is { } error)
            SetFeedback(error, isError: true);
    }

    private string? ScopeId => isCurrent ? dockId : null;
    private DockThemeOverride LocalOverride => draft.State.Overrides.TryGetValue(dockId, out DockThemeOverride? local) ? local : new();
    private string SelectedId => draft.State.SelectedThemeId(ScopeId);
    private DockTheme SelectedTheme => draft.State.GetTheme(SelectedId);
    private DockThemeTokens EditingTokens => isCurrent ? PreviewState().Resolve(dockId) : draft.GlobalTokens ?? SelectedTheme.Tokens;
    private Section ActiveSection => Sections[Math.Max(0, CategoryInput.SelectedIndex)];

    private void ScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshInProgress || ScopeInput.SelectedIndex < 0)
            return;
        isCurrent = ScopeInput.SelectedIndex == 1;
        RefreshEditor();
    }

    private void CategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!refreshInProgress)
            RefreshInspector();
    }

    private void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!refreshInProgress && ThemeInput.SelectedItem is ThemeChoice choice)
            Guard(() => SelectTheme(choice.Id));
    }

    private void SelectTheme(string? id)
    {
        if (isCurrent)
        {
            if (id != null)
                _ = draft.State.GetTheme(id);
            ApplyDraft(draft with { State = draft.State.SetOverride(dockId, LocalOverride with { ThemeId = id }) });
        }
        else if (id != null && id != draft.State.GlobalThemeId)
        {
            ApplyDraft(new Draft(draft.State.SelectTheme(id)));
        }
    }

    private DockThemeState PreviewState()
    {
        if (draft.GlobalTokens == null)
            return draft.State;

        DockTheme original = draft.State.GetTheme(draft.State.GlobalThemeId);
        bool builtIn = IsBuiltIn(original.Id);
        DockTheme edited = builtIn ? new DockTheme(draft.CopyId!, original.Name, draft.GlobalTokens) : original with { Tokens = draft.GlobalTokens };
        // A temporary copy is also the base for Docks inheriting the global selection.
        DockThemeState state = builtIn ? draft.State with { CustomThemes = draft.State.CustomThemes.Add(edited) } : draft.State.SaveTheme(edited);
        return state.SelectTheme(edited.Id);
    }

    private static bool IsBuiltIn(string id) => id is "minimal" or "glass" or "tech";

    private void ApplyDraft(Draft next, bool rebuild = true)
    {
        if (next == draft)
            return;
        if (!groupUndo || !hasGroupedUndo)
        {
            if (history.Count == 48)
                history.RemoveAt(0);
            history.Add(draft);
            hasGroupedUndo = groupUndo;
        }
        draft = next;
        if (rebuild)
        {
            ApplyLivePreview();
            RefreshEditor();
        }
        else
        {
            if (pendingPreview?.Status != DispatcherOperationStatus.Pending)
                pendingPreview = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    pendingPreview = null;
                    Guard(() => { ApplyLivePreview(); RefreshPreview(); });
                }));
        }
        UpdateActions();
        SetFeedback("有未保存的修改");
    }

    private void Undo()
    {
        if (history.Count == 0)
            return;
        draft = history[^1];
        history.RemoveAt(history.Count - 1);
        ApplyLivePreview();
        RefreshEditor();
        SetFeedback(draft == baseline ? "已恢复到上次保存的状态" : "已撤销上一次修改");
    }

    private void Reset()
    {
        if (isCurrent)
        {
            ApplyDraft(draft with { State = draft.State.SetOverride(dockId, null) });
        }
        else
        {
            ApplyDraft(draft with { GlobalTokens = null, CopyId = null });
        }
        SetFeedback(isCurrent ? "当前 Dock 已恢复继承" : "已恢复所选主题的默认参数");
    }

    private void Save(string? newName = null)
    {
        DockThemeState committed = newName != null && !isCurrent ? draft.State : MaterializeGlobalTheme();
        if (newName != null)
        {
            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException("请输入主题名称。");
            string id = Guid.NewGuid().ToString("N");
            var copy = new DockTheme(id, newName.Trim(), EditingTokens);
            committed = committed.SaveTheme(copy);
            committed = isCurrent
                ? committed.SetOverride(dockId, new DockThemeOverride(id, new DockThemePatch()))
                : committed.SelectTheme(id);
        }
        store.Save(committed);
        draft = baseline = new Draft(store.Current);
        history.Clear();
        ApplyLivePreview();
        RefreshEditor();
        SetFeedback(newName == null ? "主题已保存" : "新主题已保存并应用");
    }

    private DockThemeState MaterializeGlobalTheme()
    {
        DockThemeState state = PreviewState();
        if (draft.CopyId == null)
            return state;
        DockTheme copy = state.GetTheme(draft.CopyId);
        return state.SaveTheme(copy with { Name = copy.Name + " · 自定义" });
    }

    private void ApplyLivePreview()
    {
        pendingPreview?.Abort();
        pendingPreview = null;
        preview(PreviewState());
    }

    private void Import(string path) => ApplyImportedTheme(DockThemePackage.Read(path));

    private void ImportText(string text) => ApplyImportedTheme(DockThemePackage.Parse(text));

    private void ApplyImportedTheme(DockTheme theme)
    {
        DockThemeState next = draft.State.AddTheme(theme);
        ApplyDraft(isCurrent
            ? draft with { State = next.SetOverride(dockId, LocalOverride with { ThemeId = theme.Id }) }
            : new Draft(next.SelectTheme(theme.Id)));
        SetFeedback("已载入主题，保存后加入主题库");
    }

    private void Export(string path)
    {
        DockThemePackage.Write(path, PrepareExportTheme());
        SetFeedback("已导出当前主题参数");
    }

    private string ExportText() => DockThemePackage.Serialize(PrepareExportTheme());

    private DockTheme PrepareExportTheme()
    {
        DockTheme theme = SelectedTheme;
        DockThemeTokens tokens = EditingTokens;
        if (IsBuiltIn(theme.Id) || tokens != theme.Tokens)
            theme = theme with { Id = Guid.NewGuid().ToString("N"), Tokens = tokens };
        return theme;
    }

    private void ChangeToken(string key, object value, bool rebuild = true)
    {
        PropertyInfo token = FindToken(key);
        if (isCurrent)
        {
            DockThemePatch patch = LocalOverride.Patch with { };
            PatchProperties[token.Name].SetValue(patch, value);
            DockThemeState next = draft.State.SetOverride(dockId, LocalOverride with { Patch = patch });
            next.Resolve(dockId).Validate();
            ApplyDraft(draft with { State = next }, rebuild);
        }
        else
        {
            DockThemeTokens tokens = EditingTokens with { };
            token.SetValue(tokens, value);
            tokens.Validate();
            bool changed = tokens != SelectedTheme.Tokens;
            string? copyId = changed && IsBuiltIn(SelectedId) ? draft.CopyId ?? Guid.NewGuid().ToString("N") : null;
            ApplyDraft(draft with { GlobalTokens = changed ? tokens : null, CopyId = copyId }, rebuild);
        }
    }

    private void SetFieldOverride(string key, bool enabled)
    {
        if (!isCurrent)
            throw new InvalidOperationException("请先切换到「当前 Dock」。");
        PropertyInfo token = FindToken(key);
        DockThemePatch patch = LocalOverride.Patch with { };
        PatchProperties[token.Name].SetValue(patch, enabled ? token.GetValue(EditingTokens) : null);
        ApplyDraft(draft with { State = draft.State.SetOverride(dockId, LocalOverride with { Patch = patch }) });
    }

    private static PropertyInfo FindToken(string key) => TokenProperties.TryGetValue(key, out PropertyInfo? property)
        && Sections.Any(section => section.Fields.Any(field => field.Key == property.Name))
        ? property : throw new ArgumentException("主题参数不存在。");

    private void SetPerformance(bool enabled) => ApplyDraft(draft with { State = draft.State with { PerformanceMode = enabled } });
    private void SetAnimations(bool enabled) => ApplyDraft(draft with { State = draft.State with { AnimationsEnabled = enabled } });

    private void RefreshEditor()
    {
        refreshInProgress = true;
        ThemeInput.ItemsSource = (isCurrent ? new[] { new ThemeChoice(null, "继承全局主题") } : Array.Empty<ThemeChoice>())
            .Concat(draft.State.Themes.Select(theme => new ThemeChoice(theme.Id, theme.Name))).ToArray();
        string? selection = isCurrent ? LocalOverride.ThemeId : draft.State.GlobalThemeId;
        ThemeInput.SelectedItem = ((IEnumerable<ThemeChoice>)ThemeInput.ItemsSource).FirstOrDefault(choice => choice.Id == selection);
        ScopeHint.Text = isCurrent ? "可覆盖单个参数，其他参数仍继承所选主题。" : "调整全局主题，独立覆盖的参数会保留。";
        refreshInProgress = false;
        RefreshInspector();
        RefreshPreview();
        UpdateActions();
    }

    private void UpdateActions()
    {
        UndoButton.IsEnabled = history.Count != 0;
        SaveButton.IsEnabled = draft != baseline;
    }

    private void RefreshInspector()
    {
        if (Inspector == null)
            return;
        InspectorTitle.Text = ActiveSection.Name;
        Inspector.Children.Clear();
        if (ActiveSection.Key == "performance")
        {
            Inspector.Children.Add(Option("启用动画", "应用于呼出、收起和分组展开。", draft.State.AnimationsEnabled, SetAnimations));
            Inspector.Children.Add(Option("性能模式", "降低模糊和阴影成本，简化动效。", draft.State.PerformanceMode, SetPerformance));
            return;
        }

        DockThemeTokens current = EditingTokens;
        foreach (Field field in ActiveSection.Fields)
        {
            PropertyInfo property = FindToken(field.Key);
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            var title = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
            bool enabled = !isCurrent || PatchProperties[property.Name].GetValue(LocalOverride.Patch) != null;
            if (isCurrent)
            {
                var toggle = new CheckBox { Content = "覆盖", IsChecked = enabled, VerticalAlignment = VerticalAlignment.Center };
                System.Windows.Automation.AutomationProperties.SetName(toggle, $"覆盖{field.Label}");
                toggle.Checked += (_, _) => Guard(() => SetFieldOverride(field.Key, true));
                toggle.Unchecked += (_, _) => Guard(() => SetFieldOverride(field.Key, false));
                DockPanel.SetDock(toggle, Dock.Right);
                title.Children.Add(toggle);
            }
            title.Children.Add(new TextBlock { Text = field.Label, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(title);
            object value = property.GetValue(current)!;
            FrameworkElement input = property.PropertyType.IsEnum ? EnumInput(field, value)
                : property.PropertyType == typeof(string) ? ColorInput(field, (string)value) : NumberInput(field, value);
            input.IsEnabled = enabled;
            System.Windows.Automation.AutomationProperties.SetName(input, field.Label);
            System.Windows.Automation.AutomationProperties.SetAutomationId(input, $"Theme_{field.Key}");
            row.Children.Add(input);
            Inspector.Children.Add(row);
        }
    }

    private FrameworkElement Option(string label, string detail, bool enabled, Action<bool> change)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 17) };
        var input = new CheckBox { Content = label, FontWeight = FontWeights.SemiBold, IsChecked = enabled };
        input.Checked += (_, _) => Guard(() => change(true));
        input.Unchecked += (_, _) => Guard(() => change(false));
        panel.Children.Add(input);
        panel.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0),
            Foreground = (Brush)FindResource("Muted") });
        return panel;
    }

    private FrameworkElement EnumInput(Field field, object value)
    {
        var input = new ComboBox { MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
        foreach (object option in Enum.GetValues(value.GetType()))
            input.Items.Add(new ComboBoxItem { Content = EnumLabel(option), Tag = option, IsSelected = option.Equals(value) });
        input.SelectionChanged += (_, _) =>
        {
            if (input.SelectedItem is ComboBoxItem { Tag: { } tag })
                Guard(() => ChangeToken(field.Key, tag, false));
        };
        return input;
    }

    private FrameworkElement ColorInput(Field field, string value)
    {
        var panel = new DockPanel();
        var swatch = new Button { Width = 38, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(0), ToolTip = "选择颜色" };
        var input = new TextBox { Text = value, MaxLength = 9, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
        var sample = new Border { Width = 20, Height = 17, CornerRadius = new CornerRadius(2) };
        swatch.Content = sample;
        sample.Background = new SolidColorBrush(ReadColor(value));
        DockPanel.SetDock(swatch, Dock.Left);
        panel.Children.Add(swatch);
        panel.Children.Add(input);
        input.GotKeyboardFocus += (_, _) => BeginUndoGroup();
        input.LostKeyboardFocus += (_, _) =>
        {
            EndUndoGroup();
            if (!ValidColor(input.Text))
                input.Text = (string)FindToken(field.Key).GetValue(EditingTokens)!;
        };
        input.TextChanged += (_, _) =>
        {
            if (!ValidColor(input.Text))
            {
                SetFeedback("颜色使用 #RRGGBB 或 #AARRGGBB。", isError: true);
                return;
            }
            sample.Background = new SolidColorBrush(ReadColor(input.Text));
            Guard(() => ChangeToken(field.Key, input.Text, false));
        };
        swatch.Click += (_, _) =>
        {
            Color original = ReadColor((string)FindToken(field.Key).GetValue(EditingTokens)!);
            using var picker = new Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(original.R, original.G, original.B) };
            if (picker.ShowDialog() == Forms.DialogResult.OK)
                input.Text = original.A == 255 ? $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}"
                    : $"#{original.A:X2}{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
        };
        return panel;
    }

    private static bool ValidColor(string value) => value.Length is 7 or 9 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
    private static Color ReadColor(string value) => (Color)ColorConverter.ConvertFromString(value);

    private FrameworkElement NumberInput(Field field, object value)
    {
        double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        var stack = new StackPanel();
        var panel = new DockPanel();
        var input = new TextBox { Width = 66, Margin = new Thickness(7, 0, 0, 0) };
        var slider = new Slider
        {
            Minimum = field.Min, Maximum = field.Max, TickFrequency = field.Step,
            IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center
        };
        CheckBox? inherit = field.DockValue == null ? null : new CheckBox { Content = "沿用 Dock 设置", Margin = new Thickness(0, 5, 0, 0) };
        bool manual = number != field.DockValue;
        bool syncing = false;
        bool integral = value is int;
        void Sync(double current)
        {
            syncing = true;
            bool active = current != field.DockValue;
            input.Text = current.ToString("0.##", CultureInfo.CurrentCulture);
            slider.Value = active ? current : field.Min;
            input.IsEnabled = slider.IsEnabled = active;
            if (inherit != null)
                inherit.IsChecked = !active;
            syncing = false;
        }
        void Update(double current)
        {
            object converted = integral ? (object)(int)current : current;
            Guard(() => ChangeToken(field.Key, converted, false));
            Sync(Convert.ToDouble(FindToken(field.Key).GetValue(EditingTokens), CultureInfo.InvariantCulture));
        }
        void CommitInput()
        {
            if (syncing || !input.IsEnabled)
                return;
            if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double entered) ||
                !double.IsFinite(entered) || entered < field.Min || entered > field.Max || (integral && entered != Math.Truncate(entered)))
            {
                Sync(Convert.ToDouble(FindToken(field.Key).GetValue(EditingTokens), CultureInfo.InvariantCulture));
                SetFeedback($"请输入 {field.Min:g} 至 {field.Max:g} 之间的数值。", isError: true);
                return;
            }
            Update(entered);
        }
        Sync(number);
        slider.ValueChanged += (_, _) => { if (!syncing) Update(slider.Value); };
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => BeginUndoGroup()));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => EndUndoGroup()));
        input.LostKeyboardFocus += (_, _) => CommitInput();
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitInput(); e.Handled = true; } };
        DockPanel.SetDock(input, Dock.Right);
        panel.Children.Add(input);
        panel.Children.Add(slider);
        stack.Children.Add(panel);
        if (inherit != null)
        {
            inherit.Checked += (_, _) => { if (!syncing) Update(field.DockValue!.Value); };
            inherit.Unchecked += (_, _) => { if (!syncing) Update(manual ? number : field.Min); };
            stack.Children.Add(inherit);
        }
        return stack;
    }

    private void BeginUndoGroup() { groupUndo = true; hasGroupedUndo = false; }
    private void EndUndoGroup() { groupUndo = false; hasGroupedUndo = false; }

    private void UndoClick(object sender, RoutedEventArgs e) => Guard(Undo);
    private void ResetClick(object sender, RoutedEventArgs e) => Guard(Reset);
    private void CancelClick(object sender, RoutedEventArgs e) => Close();
    private void SaveClick(object sender, RoutedEventArgs e) => Guard(() => Save());

    private void EditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Guard(Undo);
        }
    }

    private void SaveAsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "另存为主题", Width = 410, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = FontFamily
        };
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Views/SettingsStyles.xaml", UriKind.Absolute)
        });
        var content = new StackPanel { Margin = new Thickness(19) };
        var name = new TextBox { Text = SelectedTheme.Name.Length <= 59 ? SelectedTheme.Name + " · 副本" : SelectedTheme.Name,
            MaxLength = 64, Margin = new Thickness(0, 6, 0, 12) };
        content.Children.Add(new TextBlock { Text = "主题名称", FontWeight = FontWeights.SemiBold });
        content.Children.Add(name);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 7, 0) });
        var ok = new Button { Content = "保存主题", IsDefault = true };
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text)) dialog.DialogResult = true; };
        actions.Children.Add(ok);
        content.Children.Add(actions);
        dialog.Content = content;
        dialog.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        if (dialog.ShowDialog() == true)
            Guard(() => Save(name.Text));
    }

    private void ImportThemeClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "导入 Dock 主题", Filter = "Dock 主题 (*.docktheme)|*.docktheme", CheckFileExists = true };
        if (picker.ShowDialog(this) == true)
            Guard(() => ImportInteractive(() => DockThemePackage.Read(picker.FileName)));
    }

    private void ExportThemeClick(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Title = "导出 Dock 主题", Filter = "Dock 主题 (*.docktheme)|*.docktheme",
            DefaultExt = ".docktheme", AddExtension = true, FileName = SelectedTheme.Name };
        if (picker.ShowDialog(this) == true)
            Guard(() => Export(picker.FileName));
    }

    private void ImportClipboardClick(object sender, RoutedEventArgs e)
    {
        Guard(() =>
        {
            string text;
            try
            {
                if (!Clipboard.ContainsText())
                    throw new InvalidDataException("剪贴板中没有主题文本。");
                text = Clipboard.GetText();
            }
            catch (ExternalException)
            {
                SetFeedback("无法访问系统剪贴板，请稍后重试。", isError: true);
                return;
            }
            ImportInteractive(() => DockThemePackage.Parse(text));
        });
    }

    private void CopyThemeClick(object sender, RoutedEventArgs e)
    {
        Guard(() =>
        {
            string text = ExportText();
            try
            {
                Clipboard.SetText(text);
            }
            catch (ExternalException)
            {
                SetFeedback("无法访问系统剪贴板，请稍后重试。", isError: true);
                return;
            }
            SetFeedback("已复制当前主题分享文本到剪贴板");
        });
    }

    private void OpenDiscussionsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using Process? browser = Process.Start(new ProcessStartInfo(DockUpdateService.DiscussionsPage.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception error) when (DockDialogs.IsUserFacing(error))
        {
            SetFeedback("无法打开默认浏览器，请检查 Windows 的默认浏览器设置。", isError: true);
        }
    }

    private void ImportInteractive(Func<DockTheme> read)
    {
        DockTheme theme = read();
        if (draft.State.Themes.Any(existing => existing.Id == theme.Id))
        {
            MessageBoxResult choice = MessageBox.Show(
                this,
                $"主题列表中已存在标识为「{theme.Id}」的主题。\n是否作为新副本导入「{theme.Name}」？",
                "导入主题",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes)
            {
                SetFeedback("已取消导入");
                return;
            }
            theme = theme with { Id = Guid.NewGuid().ToString("N") };
        }
        ApplyImportedTheme(theme);
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception error) when (DockDialogs.IsUserFacing(error)) { SetFeedback(error.Message, isError: true); }
    }

    private void SetFeedback(string message, bool isError = false)
    {
        Feedback.Text = message;
        Feedback.Foreground = isError ? Brushes.Firebrick : (Brush)FindResource("Muted");
    }

    internal object RunValidationCommand(string action, JsonElement args)
    {
        T Arg<T>(string key) => args.TryGetProperty(key, out JsonElement element) && element.Deserialize<T>(InputJson) is T value
            ? value : throw new ArgumentException($"Missing or invalid '{key}'.");
        string? exportedText = null;
        switch (action)
        {
            case "state": break;
            case "scope":
                ScopeInput.SelectedIndex = Arg<string>("scope").ToLowerInvariant() switch
                {
                    "global" => 0, "current" => 1, _ => throw new ArgumentException("Unknown theme scope.")
                };
                break;
            case "category":
                int index = Array.FindIndex(Sections, section => section.Key.Equals(Arg<string>("name"), StringComparison.OrdinalIgnoreCase));
                CategoryInput.SelectedIndex = index == -1 ? throw new ArgumentException("Unknown theme category.") : index;
                break;
            case "theme":
                string? id = args.GetProperty("id").GetString();
                SelectTheme(id == "inherit" ? null : id);
                break;
            case "set":
                string key = Arg<string>("key");
                object value = args.GetProperty("value").Deserialize(FindToken(key).PropertyType, InputJson)
                    ?? throw new ArgumentException("Missing token value.");
                ChangeToken(key, value);
                break;
            case "override": SetFieldOverride(Arg<string>("key"), Arg<bool>("enabled")); break;
            case "undo": Undo(); break;
            case "reset": Reset(); break;
            case "save": Save(); break;
            case "saveAs": Save(Arg<string>("name")); break;
            case "export": Export(Arg<string>("path")); break;
            case "import": Import(Arg<string>("path")); break;
            case "exportText": exportedText = ExportText(); break;
            case "importText": ImportText(Arg<string>("text")); break;
            case "performance": SetPerformance(Arg<bool>("enabled")); break;
            case "animations": SetAnimations(Arg<bool>("enabled")); break;
            case "cancel": Close(); break;
            default: throw new ArgumentException("Unknown editor action.");
        }
        return new
        {
            scope = isCurrent ? "Current" : "Global",
            category = ActiveSection.Key,
            selectedId = SelectedId,
            selectedName = SelectedTheme.Name,
            dirty = draft != baseline,
            canUndo = history.Count != 0,
            visible = IsVisible,
            tokens = EditingTokens,
            effective = PreviewState().Resolve(dockId),
            globalThemeId = draft.State.GlobalThemeId,
            currentOverride = LocalOverride,
            draft.State.PerformanceMode,
            draft.State.AnimationsEnabled,
            feedback = Feedback.Text,
            exportedText,
            themes = draft.State.Themes.Select(theme => new { theme.Id, theme.Name })
        };
    }
}
