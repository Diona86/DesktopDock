namespace DesktopDock;

public sealed partial class DockSession
{
    public DockGroup CreateGroup(string name, Guid? parentId = null)
    {
        var group = new DockGroup(Guid.NewGuid(), GroupName(name));
        Change(state => OrderTree(state with
        {
            Groups = [.. state.Groups, group],
            Entries = [.. state.Entries, new DockEntry(group.Id, parentId)]
        }));
        return group;
    }

    public DockGroup CreateGroupFromItems(Guid sourceId, Guid targetId, string? name = null)
    {
        if (sourceId == targetId)
            throw new ArgumentException("创建分组需要两个不同的项目。");

        var group = new DockGroup(Guid.NewGuid(), GroupName(name ?? "新分组"));
        Change(state =>
        {
            FindItem(state.Items, sourceId);
            FindItem(state.Items, targetId);
            var entries = new List<DockEntry>(state.Entries.Length + 1);
            foreach (DockEntry entry in state.Entries)
            {
                if (entry.Id == targetId)
                {
                    entries.Add(new DockEntry(group.Id, entry.ParentId));
                    entries.Add(new DockEntry(targetId, group.Id));
                    entries.Add(new DockEntry(sourceId, group.Id));
                }
                else if (entry.Id != sourceId)
                {
                    entries.Add(entry);
                }
            }
            return OrderTree(state with { Groups = [.. state.Groups, group], Entries = [.. entries] });
        });
        return group;
    }

    public void UpdateGroup(Guid id, string name, DockGroupExpandMode mode, string? customIconPath,
        DockExpandDirection direction = DockExpandDirection.Auto)
    {
        string displayName = GroupName(name);
        if (!Enum.IsDefined(mode) || !Enum.IsDefined(direction))
            throw new ArgumentException("请选择有效的分组展开方式和方向。");

        Change(state =>
        {
            int index = FindGroup(state, id);
            DockGroup current = state.Groups[index];
            string? icon = string.Equals(customIconPath, current.CustomIconPath, StringComparison.Ordinal)
                ? current.CustomIconPath : DockTargets.CustomIconPath(customIconPath, requireFile: true);
            DockGroup updated = current with { Name = displayName, ExpandMode = mode, CustomIconPath = icon, Direction = direction };
            if (updated == current)
                return state;

            DockGroup[] groups = (DockGroup[])state.Groups.Clone();
            groups[index] = updated;
            return state with { Groups = groups };
        });
    }

    public void DeleteGroup(Guid id, DockGroupDeleteChoice choice)
    {
        if (!Enum.IsDefined(choice))
            throw new ArgumentException("请选择分组内容的处理方式。");

        Change(state =>
        {
            FindGroup(state, id);
            Guid? parent = FindEntry(state, id).ParentId;
            var children = state.Entries.ToLookup(entry => entry.ParentId);
            if (choice == DockGroupDeleteChoice.MoveContentsToParent)
            {
                var entries = new List<DockEntry>(state.Entries.Length - 1);
                foreach (DockEntry entry in state.Entries)
                {
                    if (entry.Id == id)
                        entries.AddRange(children[id].Select(child => child with { ParentId = parent }));
                    else if (entry.ParentId != id)
                        entries.Add(entry);
                }
                return OrderTree(state with { Groups = state.Groups.Where(group => group.Id != id).ToArray(), Entries = [.. entries] });
            }

            HashSet<Guid> removed = Traverse(children, id).Select(entry => entry.Id).ToHashSet();
            removed.Add(id);
            return state with
            {
                Items = state.Items.Where(item => !removed.Contains(item.Id)).ToArray(),
                Groups = state.Groups.Where(group => !removed.Contains(group.Id)).ToArray(),
                Entries = state.Entries.Where(entry => !removed.Contains(entry.Id)).ToArray()
            };
        });
    }

    public void MoveEntry(Guid entryId, Guid? parentId, Guid? anchorId = null, bool after = false) =>
        Change(state => MoveEntry(state, entryId, parentId, anchorId, after));

    private static Configuration MoveEntry(Configuration state, Guid entryId, Guid? parentId, Guid? anchorId, bool after)
    {
        DockEntry source = FindEntry(state, entryId);
        if (anchorId is Guid anchor)
        {
            DockEntry target = FindEntry(state, anchor);
            if (target.ParentId != parentId)
                throw new ArgumentException("请选择目标层级中的排序位置。");
            if (anchor == entryId)
                return state;
        }

        var entries = state.Entries.Where(entry => entry.Id != entryId).ToList();
        int index = anchorId is Guid sibling ? entries.FindIndex(entry => entry.Id == sibling) + (after ? 1 : 0) : entries.Count;
        entries.Insert(index, source with { ParentId = parentId });
        Configuration ordered = OrderTree(state with { Entries = [.. entries] });
        return state.Entries.SequenceEqual(ordered.Entries) ? state : ordered;
    }

    private static DockEntry FindEntry(Configuration state, Guid id) => state.Entries.FirstOrDefault(entry => entry.Id == id)
        ?? throw new InvalidOperationException("该 Dock 项目或分组已移除。");

    private static int FindGroup(Configuration state, Guid id)
    {
        int index = Array.FindIndex(state.Groups, group => group.Id == id);
        if (index < 0)
            throw new InvalidOperationException("该 Dock 分组已移除。");
        return index;
    }

    private static string GroupName(string? name) => !string.IsNullOrWhiteSpace(name) ? name.Trim()
        : throw new ArgumentException("请输入分组名称。");

    private static void ValidateGroup(DockGroup? group)
    {
        if (group is null || group.Id == Guid.Empty || !Enum.IsDefined(group.ExpandMode) || !Enum.IsDefined(group.Direction))
            throw new ArgumentException("Dock 配置包含无效的分组。");
        GroupName(group.Name);
        DockTargets.CustomIconPath(group.CustomIconPath, requireFile: false);
    }

    private static Configuration OrderTree(Configuration state)
    {
        var items = state.Items.ToDictionary(item => item.Id);
        var groups = state.Groups.ToDictionary(group => group.Id);
        var parents = new Dictionary<Guid, Guid?>();
        foreach (DockEntry entry in state.Entries)
        {
            if (entry is null || items.ContainsKey(entry.Id) == groups.ContainsKey(entry.Id) || !parents.TryAdd(entry.Id, entry.ParentId))
                throw new ArgumentException("Dock 层级包含重复、缺失或无效的引用。");
        }
        if (parents.Count != items.Count + groups.Count)
            throw new ArgumentException("Dock 项目或分组缺少排序位置。");

        foreach ((Guid id, Guid? parentId) in parents)
        {
            if (parentId is not Guid parent)
                continue;
            if (!groups.ContainsKey(parent))
                throw new ArgumentException("项目只能放在 Dock 或现有分组中。");
            if (groups.ContainsKey(id) && parents[parent] != null)
                throw new ArgumentException("Dock 最多支持两级分组，二级分组只能包含项目。");
        }

        DockEntry[] ordered = Traverse(state.Entries.ToLookup(entry => entry.ParentId), null).ToArray();
        return state with
        {
            Entries = ordered,
            Items = ordered.Where(entry => items.ContainsKey(entry.Id)).Select(entry => items[entry.Id]).ToArray(),
            Groups = ordered.Where(entry => groups.ContainsKey(entry.Id)).Select(entry => groups[entry.Id]).ToArray()
        };
    }

    private static IEnumerable<DockEntry> Traverse(ILookup<Guid?, DockEntry> children, Guid? parent)
    {
        foreach (DockEntry child in children[parent])
        {
            yield return child;
            foreach (DockEntry descendant in Traverse(children, child.Id))
                yield return descendant;
        }
    }
}
