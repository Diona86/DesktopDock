using System.Windows;

namespace DesktopDock;

internal static class DockGroupDialogs
{
    internal static void Create(Window owner, DockSession session, Guid? parentId)
        => Show(owner, new DockGroupEditorWindow(session, null, parentId));

    internal static void Edit(Window owner, DockSession session, DockGroup group)
        => Show(owner, new DockGroupEditorWindow(session, group, null));

    internal static void Delete(Window owner, DockSession session, DockGroup group)
        => Show(owner, new DockGroupDeleteWindow(session, group));

    private static void Show(Window owner, Window dialog)
    {
        if (owner.IsVisible)
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
    }
}
