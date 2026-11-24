using System.Text.Json;

namespace DesktopDock;

public partial class App
{
    private static DockMcpTool[] P2Tools() =>
    [
        new("dock_p2_config", "Replace the test profile's hot zones, display mode, shortcut and application rules.",
            """{"type":"object","properties":{"behavior":{"type":"object"}},"required":["behavior"],"additionalProperties":false}"""),
        new("dock_p2_monitors", "Read native monitors and hot-zone rectangles, or override only the test geometry with synthetic monitors.",
            """{"type":"object","properties":{"monitors":{"type":["array","null"],"items":{"type":"object"}}},"additionalProperties":false}"""),
        new("dock_p2_step", "Feed a deterministic pointer and foreground observation into the real P2 controller without system input.",
            """{"type":"object","properties":{"now":{"type":"integer","minimum":0},"x":{"type":"integer"},"y":{"type":"integer"},"windowState":{"enum":["Desktop","Ordinary","Maximized","BorderlessFullScreen","ExclusiveFullScreen","Unknown"]},"executablePath":{"type":"string"},"shellState":{"type":"integer"},"inside":{"type":"boolean"}},"required":["now","x","y","windowState"],"additionalProperties":false}"""),
        new("dock_p2_event", "Invoke a real Dock shortcut action, dismissal or own context menu without system input.",
            """{"type":"object","properties":{"action":{"enum":["Toggle","Peek","Lock","Pause","OutsideClick","Escape","OpenMenu"]},"x":{"type":"integer"},"y":{"type":"integer"}},"required":["action"],"additionalProperties":false}"""),
        new("dock_p2_sampling", "Enable or disable actual passive cursor sampling in this test process.",
            """{"type":"object","properties":{"enabled":{"type":"boolean"}},"required":["enabled"],"additionalProperties":false}"""),
        new("dock_p2_hotkey_probe", "Briefly register a specified chord with Win32, try a second owner, then release the chord.",
            """{"type":"object","properties":{"chord":{"type":"object","properties":{"modifiers":{"type":"integer"},"virtualKey":{"type":"integer"}},"required":["modifiers","virtualKey"]}},"required":["chord"],"additionalProperties":false}""")
    ];

    private object InvokeP2Mcp(string name, JsonElement args)
    {
        DockInteractionRuntime scope = interaction!;
        switch (name)
        {
            case "dock_p2_config":
                string? error = SaveBehavior(Argument<DockBehaviorOptions>(args, "behavior"));
                if (error != null)
                    throw new ArgumentException(error);
                break;
            case "dock_p2_monitors":
                if (args.TryGetProperty("monitors", out JsonElement topology))
                {
                    DockMonitor[]? test = topology.Deserialize<DockMonitor[]>(McpJson);
                    if (test != null && (test.Length is < 1 or > 16 || test.Any(m => m == null || string.IsNullOrWhiteSpace(m.Id)
                        || m.Right <= m.Left || m.Bottom <= m.Top || !double.IsFinite(m.Scale) || m.Scale <= 0)))
                        throw new ArgumentException("Invalid test monitor geometry.");
                    scope.SetTestMonitors(test);
                }
                return McpReply(new { monitors = scope.ReadMonitors(), zones = scope.Zones });
            case "dock_p2_step":
                long now = Argument<long>(args, "now");
                if (now < 0)
                    throw new ArgumentException("Test time must be nonnegative.");
                var observation = new DockWindowObservation(Argument<DockWindowState>(args, "windowState"), "受控测试输入",
                    Optional<string?>(args, "executablePath", null), Optional<int?>(args, "shellState", null));
                scope.StepForTest(now, Argument<int>(args, "x"), Argument<int>(args, "y"), observation, Optional<bool?>(args, "inside", null));
                break;
            case "dock_p2_event":
                switch (Argument<string>(args, "action"))
                {
                    case "Toggle": scope.Shortcut(DockHotkeyAction.Toggle); break;
                    case "Peek": scope.Shortcut(DockHotkeyAction.Peek); break;
                    case "Lock": scope.Shortcut(DockHotkeyAction.Lock); break;
                    case "Pause": scope.Shortcut(DockHotkeyAction.Pause); break;
                    case "OutsideClick": scope.OutsideClick(Argument<int>(args, "x"), Argument<int>(args, "y")); break;
                    case "Escape": scope.HandleEscape(); break;
                    case "OpenMenu": dock!.OpenMenuForTest(); break;
                    default: throw new ArgumentException("Unknown P2 action.");
                }
                break;
            case "dock_p2_sampling":
                scope.SetNativeSamplingForTest(Argument<bool>(args, "enabled"));
                break;
            case "dock_p2_hotkey_probe":
                HotkeyChord chord = Argument<HotkeyChord>(args, "chord");
                (new DockHotkeys { Toggle = chord, Peek = null, Lock = null, Pause = null }).Validate();
                return McpReply(DockHotkeyService.ProbeConflict(chord));
            default:
                throw new ArgumentException("Unknown Dock MCP tool.");
        }
        return McpReply(ReadTestState());
    }
}
