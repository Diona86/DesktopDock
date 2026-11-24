namespace DesktopDock;

public sealed partial class DockSession
{
    public DockGrid ResolveGrid(int columns, int rows)
    {
        Configuration state = _state;
        return state.Options.Layout.Grid ?? new DockGrid(1, 1, []).Resize(columns, rows)
            .Fill(state.Entries.Where(entry => entry.ParentId is null).Select(entry => entry.Id));
    }

    public void UpdateGrid(DockGrid grid, double? floatingLeft = null, double? floatingTop = null)
    {
        grid.Validate();
        Change(state =>
        {
            ValidateGridEntries(state, grid);
            Configuration next = WithGrid(state, grid);
            DockOptions options = next.Options with
            {
                FloatingLeft = floatingLeft ?? next.Options.FloatingLeft,
                FloatingTop = floatingTop ?? next.Options.FloatingTop
            };
            if (options == state.Options)
                return state;
            options.Validate();
            return next with { Options = options };
        });
    }

    public void MoveEntryToGrid(Guid id, int column, int row)
    {
        Change(state =>
        {
            DockGrid grid = state.Options.Layout.Grid ?? throw new InvalidOperationException("请先调整 Dock 窗口以启用自定义网格。");
            DockEntry entry = FindEntry(state, id);
            DockTile? tile = grid.Tiles.FirstOrDefault(value => value.EntryId == id);
            if (!grid.TryPlace(id, column, row, tile?.ColumnSpan ?? 1, tile?.RowSpan ?? 1, out DockGrid proposed))
                throw new ArgumentException("目标格位被占用或超出 Dock 网格范围。");
            return WithGrid(entry.ParentId is null ? state : MoveEntry(state, id, null, null, false), proposed);
        });
    }

    private static void ValidateGridEntries(Configuration state, DockGrid grid)
    {
        if (!state.Entries.Where(entry => entry.ParentId is null).Select(entry => entry.Id).ToHashSet()
            .SetEquals(grid.Tiles.Select(tile => tile.EntryId)))
            throw new ArgumentException("Dock 网格的图标和主界面项目不一致。");
    }

    private static Configuration SynchronizeGrid(Configuration state)
    {
        if (state.Options.Layout.Grid is not DockGrid grid)
            return state;

        DockEntry[] rootEntries = state.Entries.Where(entry => entry.ParentId is null).ToArray();
        Dictionary<Guid, DockEntry> roots = rootEntries.ToDictionary(entry => entry.Id);
        DockTile[] retained = grid.Tiles.Where(tile => roots.ContainsKey(tile.EntryId)).ToArray();
        if (retained.Length != grid.Tiles.Length)
            grid = grid with { Tiles = retained };
        if (roots.Count != retained.Length)
            grid = grid.Fill(rootEntries.Select(entry => entry.Id));
        state = WithGrid(state, grid);
        DockEntry[] orderedRoots = grid.Tiles.OrderBy(tile => tile.Row).ThenBy(tile => tile.Column)
            .Select(tile => roots[tile.EntryId]).ToArray();
        return rootEntries.SequenceEqual(orderedRoots) ? state : OrderTree(state with
        {
            Entries = [.. orderedRoots, .. state.Entries.Where(entry => entry.ParentId is not null)]
        });
    }

    private static Configuration ReplaceGridTile(Configuration state, Guid oldId, Guid newId, bool useExistingSpan)
    {
        if (state.Options.Layout.Grid is not DockGrid grid)
            return state;
        DockTile? oldTile = grid.Tiles.FirstOrDefault(tile => tile.EntryId == oldId);
        if (oldTile is null)
            return state;

        DockTile tile = useExistingSpan ? oldTile with { EntryId = newId } : new DockTile(newId, oldTile.Column, oldTile.Row);
        return WithGrid(state, grid with
        {
            Tiles = [.. grid.Tiles.Where(existing => existing.EntryId != oldId && existing.EntryId != newId), tile]
        });
    }

    private static Configuration WithGrid(Configuration state, DockGrid grid)
    {
        if (grid == state.Options.Layout.Grid)
            return state;
        return state with { Options = state.Options with { Layout = state.Options.Layout with { Grid = grid } } };
    }
}
