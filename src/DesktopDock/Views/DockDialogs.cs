using Microsoft.Win32;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;

namespace DesktopDock;

internal static class DockDialogs
{
    private const string ImageFilter = "图标和图片|*.ico;*.png;*.jpg;*.jpeg;*.bmp";

    internal static bool IsUserFacing(Exception error) => error is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException
        or InvalidOperationException or NotSupportedException or Win32Exception or JsonException;

    internal static void Error(Window? owner, string caption, Exception error)
    {
        if (owner?.IsVisible == true)
            MessageBox.Show(owner, error.Message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(error.Message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    internal static void Edit(Window? owner, DockSession session, DockItem? item)
    {
        var dialog = new ItemEditorWindow(session, item);
        if (owner?.IsVisible == true)
            dialog.Owner = owner;
        dialog.ShowDialog();
    }

    internal static void ReplaceIcon(Window? owner, DockSession session, DockItem item)
    {
        string? selected = ChooseIcon(owner);
        if (selected == null)
            return;
        try
        {
            session.SetIcon(item.Id, selected);
        }
        catch (Exception error) when (IsUserFacing(error))
        {
            Error(owner, "无法替换图标", error);
        }
    }

    internal static string? ChooseIcon(Window? owner)
    {
        var dialog = new OpenFileDialog { Title = "选择 Dock 展示图标", Filter = ImageFilter, CheckFileExists = true };
        return (owner?.IsVisible == true ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true ? dialog.FileName : null;
    }
}
