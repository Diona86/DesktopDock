using System.Windows;

namespace DesktopDock;

public partial class App
{
    private DockThemeStore? themeStore;
    private DockThemeEditorWindow? themeEditor;
    private DockThemeState? previewThemeState;

    private void InitializeThemes(string directory)
    {
        themeStore = DockThemeStore.Open(directory);
        themeStore.Changed += ThemeSettingsChanged;
        ApplyThemeState(themeStore.Current);
    }

    private void DetachThemes()
    {
        if (themeStore != null)
            themeStore.Changed -= ThemeSettingsChanged;
    }

    private void ThemeSettingsChanged(object? sender, EventArgs e)
    {
        if (themeStore != null)
            ApplyThemeState(themeStore.Current);
    }

    private void ApplyThemeState(DockThemeState state)
    {
        previewThemeState = state;
        if (legacyDock != null)
            ApplyThemeTo(legacyDock.Window, legacyDock.ThemeId);
        foreach (DisplayDock instance in displayDocks.Values.Where(value => value != legacyDock))
            ApplyThemeTo(instance.Window, instance.ThemeId);
    }

    private void ApplyThemeTo(DockWindow window, string themeId)
    {
        DockThemeState state = previewThemeState ?? themeStore!.Current;
        window.ApplyTheme(state.Resolve(themeId), state.AnimationsEnabled, state.PerformanceMode);
    }

    private void ShowThemeEditor()
    {
        if (themeEditor == null)
        {
            var editor = new DockThemeEditorWindow(themeStore!, activeDock!.ThemeId, ApplyThemeState,
                () => session!.Options)
            {
                ShowActivated = !validationMode
            };
            if (settings?.IsVisible == true)
                editor.Owner = settings;
            editor.Closed += (_, _) =>
            {
                themeEditor = null;
                if (!exiting)
                    ApplyThemeState(themeStore!.Current);
            };
            themeEditor = editor;
            editor.Show();
        }
        if (themeEditor.WindowState == WindowState.Minimized)
            themeEditor.WindowState = WindowState.Normal;
        if (!validationMode)
            themeEditor.Activate();
    }
}
