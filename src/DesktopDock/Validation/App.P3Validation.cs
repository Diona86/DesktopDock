using System.Text.Json;

namespace DesktopDock;

public partial class App
{
    private static DockMcpTool[] P3Tools() =>
    [
        new("dock_p3_create", "Create a group in the test profile through the same hierarchy service as the group dialog.",
            """{"type":"object","properties":{"name":{"type":"string"},"parentId":{"type":["string","null"]}},"required":["name"],"additionalProperties":false}"""),
        new("dock_p3_edit", "Edit a group's name, optional icon, expansion mode and direction.",
            """{"type":"object","properties":{"id":{"type":"string"},"name":{"type":"string"},"expandMode":{"enum":["Grid","Fan","Horizontal","Vertical"]},"iconPath":{"type":["string","null"]},"direction":{"enum":["Auto","Up","Down","Left","Right"]}},"required":["id","name"],"additionalProperties":false}"""),
        new("dock_p3_delete", "Apply an explicit group deletion choice. Only test profile references can be removed.",
            """{"type":"object","properties":{"id":{"type":"string"},"choice":{"enum":["MoveContentsToParent","RemoveReferences"]}},"required":["id","choice"],"additionalProperties":false}"""),
        new("dock_p3_move", "Move or reorder an entry through the validated hierarchy service.",
            """{"type":"object","properties":{"id":{"type":"string"},"parentId":{"type":["string","null"]},"anchorId":{"type":["string","null"]},"after":{"type":"boolean"}},"required":["id"],"additionalProperties":false}"""),
        new("dock_p3_drop", "Exercise real visual drop routing and feedback at a group's or item's center or edge.",
            """{"type":"object","properties":{"id":{"type":"string"},"parentId":{"type":["string","null"]},"targetId":{"type":["string","null"]},"after":{"type":"boolean"},"center":{"type":"boolean"}},"required":["id"],"additionalProperties":false}"""),
        new("dock_p3_layout", "Set persisted Dock layout options in the test profile.",
            """{"type":"object","properties":{"layout":{"type":"object"}},"required":["layout"],"additionalProperties":false}"""),
        new("dock_p3_open", "Open the real WPF panel for a group.",
            """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":false}"""),
        new("dock_p3_back", "Close the current group panel, leaving its parent visible if present.",
            """{"type":"object","additionalProperties":false}"""),
        new("dock_p3_blank", "Close expanded groups using the Dock's blank-area dismissal path.",
            """{"type":"object","additionalProperties":false}""")
    ];

    private object InvokeP3Mcp(string name, JsonElement args)
    {
        DockSession profile = session!;
        DockWindow owner = dock!;
        switch (name)
        {
            case "dock_p3_create":
                return McpReply(profile.CreateGroup(Argument<string>(args, "name"), Optional<Guid?>(args, "parentId", null)));
            case "dock_p3_edit":
                Guid id = Argument<Guid>(args, "id");
                DockGroup group = profile.Groups.FirstOrDefault(value => value.Id == id) ?? throw new ArgumentException("Unknown group.");
                profile.UpdateGroup(id, Argument<string>(args, "name"), Optional(args, "expandMode", group.ExpandMode),
                    Optional(args, "iconPath", group.CustomIconPath), Optional(args, "direction", group.Direction));
                break;
            case "dock_p3_delete":
                profile.DeleteGroup(Argument<Guid>(args, "id"), Argument<DockGroupDeleteChoice>(args, "choice"));
                break;
            case "dock_p3_move":
                profile.MoveEntry(Argument<Guid>(args, "id"), Optional<Guid?>(args, "parentId", null),
                    Optional<Guid?>(args, "anchorId", null), Optional(args, "after", false));
                break;
            case "dock_p3_drop":
                Guid source = Argument<Guid>(args, "id");
                Guid? parent = Optional<Guid?>(args, "parentId", null);
                Guid? target = Optional<Guid?>(args, "targetId", null);
                bool after = Optional(args, "after", false);
                bool center = Optional(args, "center", false);
                return McpReply(parent is Guid panelId ? owner.TestGroupPanelDrop(source, panelId, target, after, center)
                    : owner.TestRootEntryDrop(source, target, after, center));
            case "dock_p3_layout":
                profile.UpdateOptions(profile.Options with { Layout = Argument<DockLayoutOptions>(args, "layout") });
                break;
            case "dock_p3_open":
                owner.OpenGroup(Argument<Guid>(args, "id"));
                break;
            case "dock_p3_back":
                owner.CloseTopGroup();
                break;
            case "dock_p3_blank":
                owner.CloseGroups();
                break;
            default:
                throw new ArgumentException("Unknown P3 tool.");
        }
        return McpReply(ReadTestState());
    }
}
