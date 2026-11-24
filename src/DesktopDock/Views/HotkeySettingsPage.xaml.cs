using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopDock;

public partial class HotkeySettingsPage : UserControl
{
    private bool dirty;

    public HotkeySettingsPage() => InitializeComponent();

    internal DockHotkeys Bindings { get; private set; } = new();

    internal void LoadOptions(DockHotkeys bindings)
    {
        if (!dirty)
            Bindings = bindings;
        ShowBindings();
    }

    internal void MarkSaved() => dirty = false;

    internal void ShowRegistrationStatus(string? status)
    {
        RegistrationStatus.Text = status ?? "";
        RegistrationStatus.Visibility = string.IsNullOrWhiteSpace(status) ? Visibility.Collapsed : Visibility.Visible;
    }

    internal bool Capture(HotkeyChord chord)
    {
        int index = SlotOf(Keyboard.FocusedElement);
        if (index == -1)
            return false;
        SetBinding(index, chord);
        return true;
    }

    private void RecordKey(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (key == Key.Tab && modifiers is ModifierKeys.None or ModifierKeys.Shift)
            return;

        e.Handled = true;
        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            ((TextBox)sender).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            return;
        }
        if (e.IsRepeat || key is Key.None or Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
            return;

        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
            return;
        int flags = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) flags |= 1;
        if (modifiers.HasFlag(ModifierKeys.Control)) flags |= 2;
        if (modifiers.HasFlag(ModifierKeys.Shift)) flags |= 4;
        if (modifiers.HasFlag(ModifierKeys.Windows)) flags |= 8;
        SetBinding(SlotOf(sender), new HotkeyChord(flags, virtualKey));
    }

    private int SlotOf(object? element)
    {
        if (ReferenceEquals(element, ToggleInput)) return 0;
        if (ReferenceEquals(element, PeekInput)) return 1;
        if (ReferenceEquals(element, LockInput)) return 2;
        if (ReferenceEquals(element, PauseInput)) return 3;
        return -1;
    }

    private void ClearBinding(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } && int.TryParse(value, out int slot))
            SetBinding(slot, null);
    }

    private void ResetBindings(object sender, RoutedEventArgs e)
    {
        Bindings = new DockHotkeys();
        dirty = true;
        ShowBindings();
    }

    private void SetBinding(int index, HotkeyChord? chord)
    {
        Bindings = index switch
        {
            0 => Bindings with { Toggle = chord },
            1 => Bindings with { Peek = chord },
            2 => Bindings with { Lock = chord },
            3 => Bindings with { Pause = chord },
            _ => Bindings
        };
        dirty = true;
        ShowBindings();
    }

    private void ShowBindings()
    {
        ToggleInput.Text = FormatChord(Bindings.Toggle);
        PeekInput.Text = FormatChord(Bindings.Peek);
        LockInput.Text = FormatChord(Bindings.Lock);
        PauseInput.Text = FormatChord(Bindings.Pause);
        try
        {
            Bindings.Validate();
            BindingValidationStatus.Visibility = Visibility.Collapsed;
        }
        catch (ArgumentException error)
        {
            BindingValidationStatus.Text = error.Message;
            BindingValidationStatus.Visibility = Visibility.Visible;
        }
    }

    private static string FormatChord(HotkeyChord? chord)
    {
        if (chord == null)
            return "未设置";

        var parts = new List<string>();
        if ((chord.Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((chord.Modifiers & 1) != 0) parts.Add("Alt");
        if ((chord.Modifiers & 4) != 0) parts.Add("Shift");
        if ((chord.Modifiers & 8) != 0) parts.Add("Win");
        Key key = KeyInterop.KeyFromVirtualKey(chord.VirtualKey);
        parts.Add(chord.VirtualKey is >= 0x30 and <= 0x39 ? ((char)chord.VirtualKey).ToString() : key.ToString());
        return string.Join(" + ", parts);
    }
}
