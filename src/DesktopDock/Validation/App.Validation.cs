using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopDock;

public partial class App
{
    private static readonly JsonSerializerOptions McpJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private static readonly DockMcpTool[] McpTools =
    [
        new("dock_state", "Inspect the test profile, visible Dock controls, layout, tooltip configuration and loaded icons.",
            """{"type":"object","additionalProperties":false}"""),
        new("dock_add", "Add explicit filesystem paths or HTTP(S) URLs to the test Dock. Input drop uses the file-drop handler.",
            """{"type":"object","properties":{"targets":{"type":"array","items":{"type":"string"},"minItems":1},"input":{"enum":["picker","drop"]}},"required":["targets"],"additionalProperties":false}"""),
        new("dock_edit", "Edit a Dock reference and optionally replace or clear its display icon.",
            """{"type":"object","properties":{"id":{"type":"string","format":"uuid"},"target":{"type":"string"},"name":{"type":"string"},"iconPath":{"type":["string","null"]}},"required":["id","target","name"],"additionalProperties":false}"""),
        new("dock_remove", "Remove one Dock reference from the test profile.",
            """{"type":"object","properties":{"id":{"type":"string","format":"uuid"}},"required":["id"],"additionalProperties":false}"""),
        new("dock_open", "Invoke the real Dock item open action. Windows opens the referenced target.",
            """{"type":"object","properties":{"id":{"type":"string","format":"uuid"}},"required":["id"],"additionalProperties":false}"""),
        new("dock_show_in_folder", "Open Explorer at a filesystem-backed Dock reference.",
            """{"type":"object","properties":{"id":{"type":"string","format":"uuid"}},"required":["id"],"additionalProperties":false}"""),
        new("dock_options", "Apply selected Dock settings through the same validated profile used by the settings window.",
            """{"type":"object","properties":{"placement":{"enum":["Bottom","Top","Left","Right","Floating"]},"floatingLeft":{"type":["number","null"]},"floatingTop":{"type":["number","null"]},"floatingVertical":{"type":"boolean"},"iconSize":{"type":"number"},"dockThickness":{"type":"number"},"dockLength":{"type":"number"},"globalPositionLocked":{"type":"boolean"},"dockPositionLocked":{"type":"boolean"},"showDockOnStartup":{"type":"boolean"}},"additionalProperties":false}"""),
        new("dock_visibility", "Show or hide the Dock without closing the process.",
            """{"type":"object","properties":{"visible":{"type":"boolean"}},"required":["visible"],"additionalProperties":false}"""),
        new("dock_settings", "Open, close or navigate the real settings window.",
            """{"type":"object","properties":{"visible":{"type":"boolean"},"page":{"enum":["appearance","items","position","general","hotZones","shortcuts"]}},"required":["visible"],"additionalProperties":false}"""),
        new("dock_drag_item", "Exercise in-app drop placement, feedback and order persistence without injecting global pointer events.",
            """{"type":"object","properties":{"id":{"type":"string","format":"uuid"},"targetId":{"type":"string","format":"uuid"},"after":{"type":"boolean"}},"required":["id","targetId","after"],"additionalProperties":false}"""),
        new("dock_drag_handle", "Exercise the movement handle's native placement and locks without injecting global pointer events.",
            """{"type":"object","properties":{"dx":{"type":"number"},"dy":{"type":"number"}},"required":["dx","dy"],"additionalProperties":false}"""),
        new("dock_tooltip", "Open or close the real WPF tooltip for a test Dock item.",
            """{"type":"object","properties":{"id":{"type":"string","format":"uuid"},"open":{"type":"boolean"}},"required":["id","open"],"additionalProperties":false}"""),
        new("dock_capture", "Render the selected app window's own visual tree as a PNG.",
            """{"type":"object","properties":{"window":{"enum":["dock","settings"]}},"required":["window"],"additionalProperties":false}"""),
        .. P2Tools()
    ];

    private async Task RunMcp()
    {
        int exitCode = 0;
        try
        {
            await DockMcpServer.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), McpTools,
                (name, args) => Dispatcher.InvokeAsync(() => InvokeMcp(name, args), DispatcherPriority.Background).Task);
        }
        catch (Exception error) when (DockDialogs.IsUserFacing(error))
        {
            WriteMcpError(error);
            exitCode = 1;
        }
        finally
        {
            await Dispatcher.InvokeAsync(() => ExitApp(exitCode));
        }
    }

    private static void WriteMcpError(Exception error)
    {
        using Stream stderr = Console.OpenStandardError();
        stderr.Write(Encoding.UTF8.GetBytes(error.Message + Environment.NewLine));
    }

    private object InvokeMcp(string name, JsonElement args)
    {
        DockSession testSession = session!;
        DockWindow testDock = dock!;
        switch (name)
        {
            case "dock_state":
                return McpReply(ReadTestState());
            case "dock_add":
                string[] targets = Argument<string[]>(args, "targets");
                if (targets.Length == 0)
                    throw new ArgumentException("Provide at least one explicit target.");
                string input = Optional(args, "input", "picker");
                if (input == "drop")
                {
                    if (targets.Any(target => !Path.IsPathFullyQualified(target)))
                        throw new ArgumentException("File drops require fully qualified filesystem paths.");
                    var result = testDock.AcceptFiles(targets);
                    if (result.Errors.Count > 0)
                        throw new ArgumentException(string.Join(Environment.NewLine, result.Errors));
                }
                else if (input == "picker")
                {
                    foreach (string target in targets)
                        testSession.AddItem(target);
                }
                else
                {
                    throw new ArgumentException("Input must be picker or drop.");
                }
                return McpReply(ReadTestState());
            case "dock_edit":
                DockItem selected = ReadTestItem(args);
                testSession.UpdateItem(selected.Id, Argument<string>(args, "target"), Argument<string>(args, "name"),
                    Optional(args, "iconPath", selected.CustomIconPath));
                break;
            case "dock_remove":
                testSession.RemoveItem(Argument<Guid>(args, "id"));
                break;
            case "dock_open":
                testDock.OpenFromDock(Argument<Guid>(args, "id"));
                break;
            case "dock_show_in_folder":
                DockTargetLauncher.ShowInFolder(ReadTestItem(args));
                break;
            case "dock_options":
                testSession.UpdateOptions(UpdateTestOptions(testSession.Options, args));
                break;
            case "dock_visibility":
                SetDockVisible(Argument<bool>(args, "visible"));
                break;
            case "dock_settings":
                if (Argument<bool>(args, "visible"))
                {
                    int page = Optional(args, "page", "items") switch
                    {
                        "appearance" => 0,
                        "items" => 1,
                        "position" => 2,
                        "general" => 3,
                        "hotZones" => 4,
                        "shortcuts" => 5,
                        _ => throw new ArgumentException("Unknown settings page.")
                    };
                    ShowSettings();
                    settings!.SelectPage(page);
                }
                else
                {
                    settings?.Close();
                }
                break;
            case "dock_drag_item":
                return McpReply(testDock.TestDragItem(Argument<Guid>(args, "id"), Argument<Guid>(args, "targetId"), Argument<bool>(args, "after")));
            case "dock_drag_handle":
                return McpReply(testDock.TestDragHandle(Argument<double>(args, "dx"), Argument<double>(args, "dy")));
            case "dock_tooltip":
                return McpReply(testDock.TestTooltip(Argument<Guid>(args, "id"), Argument<bool>(args, "open")));
            case "dock_capture":
                string targetWindow = Argument<string>(args, "window");
                return CaptureTestWindow(targetWindow, targetWindow switch
                {
                    "dock" => testDock,
                    "settings" => settings,
                    _ => throw new ArgumentException("Unknown window.")
                });
            default:
                return InvokeP2Mcp(name, args);
        }
        return McpReply(ReadTestState());
    }

    private object ReadTestState() => new
    {
        items = session!.Items,
        options = session.Options,
        view = dock!.ReadVisualState(),
        settingsVisible = settings?.IsVisible == true,
        interaction = interaction!.Diagnostics()
    };

    private DockItem ReadTestItem(JsonElement args)
    {
        Guid id = Argument<Guid>(args, "id");
        return session!.Items.FirstOrDefault(item => item.Id == id) ?? throw new ArgumentException("Dock item does not exist.");
    }

    private static DockOptions UpdateTestOptions(DockOptions current, JsonElement args) => current with
    {
        Placement = Optional(args, "placement", current.Placement),
        FloatingLeft = Optional(args, "floatingLeft", current.FloatingLeft),
        FloatingTop = Optional(args, "floatingTop", current.FloatingTop),
        FloatingVertical = Optional(args, "floatingVertical", current.FloatingVertical),
        IconSize = Optional(args, "iconSize", current.IconSize),
        DockThickness = Optional(args, "dockThickness", current.DockThickness),
        DockLength = Optional(args, "dockLength", current.DockLength),
        GlobalPositionLocked = Optional(args, "globalPositionLocked", current.GlobalPositionLocked),
        DockPositionLocked = Optional(args, "dockPositionLocked", current.DockPositionLocked),
        ShowDockOnStartup = Optional(args, "showDockOnStartup", current.ShowDockOnStartup)
    };

    private static T Argument<T>(JsonElement args, string name) =>
        args.TryGetProperty(name, out JsonElement value) && value.Deserialize<T>(McpJson) is T converted
            ? converted : throw new ArgumentException($"Missing or invalid '{name}'.");

    private static T Optional<T>(JsonElement args, string name, T fallback) =>
        args.TryGetProperty(name, out JsonElement value) ? value.Deserialize<T>(McpJson)! : fallback;

    private static object McpReply(object data)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(data, McpJson);
        return new { content = new[] { new { type = "text", text = structured.GetRawText() } }, structuredContent = structured };
    }

    private static object CaptureTestWindow(string name, Window? window)
    {
        if (window?.IsVisible != true || window.Content is not FrameworkElement visual)
            throw new InvalidOperationException("Show the window before capturing it.");
        window.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
        if (width > 4096 || height > 4096)
            throw new InvalidOperationException("The window is too large for a validation capture.");

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var bytes = new MemoryStream();
        png.Save(bytes);
        return new
        {
            content = new[] { new { type = "image", data = Convert.ToBase64String(bytes.ToArray()), mimeType = "image/png" } },
            structuredContent = new { window = name, width, height }
        };
    }
}
