using System.Text.Json;
using System.Windows.Threading;

namespace DesktopDock;

public partial class App
{
    private static DockMcpTool[] P5Tools() =>
    [
        new("dock_p5_displays", "Exercise real Dock windows using an isolated, optionally simulated display topology.",
            """{"type":"object","properties":{"action":{"enum":["state","topology","select","tray"]},"monitorId":{"type":"string"},"monitors":{"type":"array","items":{"type":"object"}},"command":{"enum":["show","hide","pause","resume","settings","restore","exit"]}},"required":["action"],"additionalProperties":false}"""),
        new("dock_p5_map", "Open the actual WPF hot-zone editor and use its map actions in an isolated profile.",
            """{"type":"object","properties":{"action":{"enum":["open","state","select","drag","edit","add","remove","reset","save","cancel"]},"monitorId":{"type":"string"},"index":{"type":"integer"},"handle":{"enum":["Start","End","Offset","Thickness"]},"deltaX":{"type":"number"},"deltaY":{"type":"number"},"zone":{"type":"object"}},"required":["action"],"additionalProperties":false}"""),
        new("dock_p5_system", "Read live desktop icon status without modifying Explorer, or exercise an isolated Windows registration.",
            """{"type":"object","properties":{"action":{"enum":["state","desktop","startupProbe","restoreHotkeyProbe"]}},"required":["action"],"additionalProperties":false}""")
    ];

    private object InvokeP5Mcp(string name, JsonElement args)
    {
        string action = Argument<string>(args, "action");
        return name switch
        {
            "dock_p5_displays" => McpReply(TestDisplays(action, args)),
            "dock_p5_map" => McpReply(TestZoneMap(action, args)),
            "dock_p5_system" => McpReply(TestDesktopSystem(action)),
            _ => throw new ArgumentException("Unknown P5 tool.")
        };
    }

    private object TestDisplays(string action, JsonElement args)
    {
        switch (action)
        {
            case "state":
                break;
            case "topology":
                DockMonitor[] monitors = Argument<DockMonitor[]>(args, "monitors");
                ValidateTopology(monitors);
                ApplyTopology(monitors);
                break;
            case "select":
                if (!displayDocks.TryGetValue(Argument<string>(args, "monitorId"), out DisplayDock? selected))
                    throw new ArgumentException("Display is not connected.");
                ActivateDock(selected);
                break;
            case "tray":
                string command = Argument<string>(args, "command");
                if (command == "exit")
                    Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => ApplyTrayCommand(command)));
                else
                    ApplyTrayCommand(command);
                break;
            default:
                throw new ArgumentException("Unknown P5 display action.");
        }
        return new
        {
            selectedDisplayId = activeDock?.Monitor?.Id,
            legacyDisplayId = displayProfiles?.LegacyMonitorId,
            settingsVisible = settings?.IsVisible == true,
            connected = ConnectedDocks.Select(instance => new
            {
                id = instance.Monitor?.Id,
                instance.Monitor,
                instance.ThemeId,
                options = instance.Profile.Options,
                entries = instance.Profile.Entries,
                visible = instance.RequestedVisible,
                paused = instance.Runtime.HotZonesPaused,
                view = instance.Window.ReadVisualState(),
                rendering = instance.Window.ReadThemeRendering()
            }).ToArray()
        };
    }

    private static void ValidateTopology(DockMonitor[] monitors)
    {
        if (monitors.Length is < 1 or > 16 || monitors.Any(monitor => monitor == null ||
            string.IsNullOrWhiteSpace(monitor.Id) || monitor.Id.Length > 128 || string.IsNullOrWhiteSpace(monitor.Name) ||
            monitor.Right <= monitor.Left || monitor.Bottom <= monitor.Top ||
            monitor.WorkRight <= monitor.WorkLeft || monitor.WorkBottom <= monitor.WorkTop ||
            monitor.WorkLeft < monitor.Left || monitor.WorkTop < monitor.Top || monitor.WorkRight > monitor.Right || monitor.WorkBottom > monitor.Bottom ||
            monitor.Left < -50000 || monitor.Top < -50000 || monitor.Right > 50000 || monitor.Bottom > 50000 ||
            !(monitor.Scale >= 0.5 && monitor.Scale <= 4)) ||
            monitors.Select(monitor => monitor.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != monitors.Length ||
            monitors.Count(monitor => monitor.Primary) != 1)
            throw new ArgumentException("Topology requires unique displays with valid bounds, DPI and one primary.");
    }

    private object TestZoneMap(string action, JsonElement args)
    {
        if (action == "open")
        {
            ShowZoneMap();
            return zoneMap!.RunValidationCommand("state", args);
        }
        return (zoneMap ?? throw new InvalidOperationException("请先打开热区地图。")).RunValidationCommand(action, args);
    }

    private object TestDesktopSystem(string action)
    {
        switch (action)
        {
            case "desktop":
                ShowDesktopManager();
                break;
            case "startupProbe":
                return DockStartupRegistration.ProbeForTest(Environment.ProcessPath!);
            case "restoreHotkeyProbe":
                return DockHotkeyService.ProbeConflict(DockHotkeyService.DesktopRestoreChord);
            case "state":
                break;
            default:
                throw new ArgumentException("Unknown P5 system action.");
        }
        return new { managerVisible = desktopManager?.IsVisible == true, desktop = desktopIcons!.Refresh() };
    }
}
