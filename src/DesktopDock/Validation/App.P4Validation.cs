using System.Text.Json;

namespace DesktopDock;

public partial class App
{
    private static DockMcpTool[] P4Tools() =>
    [
        new("dock_p4_theme", "Open the real theme editor, exercise its commands, and inspect the applied Dock visuals.",
            """{"type":"object","properties":{"action":{"enum":["open","state","category","scope","theme","set","override","undo","reset","save","saveAs","import","export","cancel","performance","animations"]},"id":{"type":["string","null"]},"name":{"type":"string"},"scope":{"enum":["Global","Current"]},"key":{"type":"string"},"value":{},"path":{"type":"string"},"enabled":{"type":"boolean"}},"required":["action"],"additionalProperties":false}""")
    ];

    private object InvokeP4Mcp(string name, JsonElement args)
    {
        if (name != "dock_p4_theme")
            throw new ArgumentException("Unknown P4 tool.");

        string action = Argument<string>(args, "action");
        object? editorResult = null;
        switch (action)
        {
            case "open":
                ShowThemeEditor();
                break;
            case "state":
                break;
            default:
                editorResult = (themeEditor ?? throw new InvalidOperationException("请先打开主题编辑器。"))
                    .RunValidationCommand(action, args);
                break;
        }
        DockThemeState saved = themeStore!.Current;
        return McpReply(new
        {
            editorVisible = themeEditor?.IsVisible == true,
            editor = themeEditor == null ? null : editorResult ?? themeEditor.RunValidationCommand("state", args),
            saved = new
            {
                globalThemeId = saved.GlobalThemeId,
                saved.AnimationsEnabled,
                saved.PerformanceMode,
                themes = saved.Themes,
                saved.Overrides,
                tokens = saved.Resolve(DockThemeState.PrimaryDockId),
                loadError = themeStore.LoadError
            },
            dockRequestedVisible = dockVisibilityRequested,
            rendering = dock!.ReadThemeRendering()
        });
    }
}
