namespace DesktopDock;

public sealed record DockTile(Guid EntryId, int Column, int Row, int ColumnSpan = 1, int RowSpan = 1)
{
    internal int Right => Column + ColumnSpan;
    internal int Bottom => Row + RowSpan;

    internal bool Intersects(DockTile other) => Column < other.Right && Right > other.Column &&
        Row < other.Bottom && Bottom > other.Row;
}

public sealed record DockGrid(int Columns, int Rows, DockTile[] Tiles)
{
    public const int MaxDimension = 128;

    public DockGrid Resize(int columns, int rows)
    {
        int minColumns = Tiles.Length == 0 ? 1 : Tiles.Max(tile => tile.Right);
        int minRows = Tiles.Length == 0 ? 1 : Tiles.Max(tile => tile.Bottom);
        columns = Math.Clamp(columns, minColumns, MaxDimension);
        rows = Math.Clamp(rows, minRows, MaxDimension);
        return columns == Columns && rows == Rows ? this : this with { Columns = columns, Rows = rows };
    }

    public bool TryPlace(Guid id, int column, int row, int columnSpan, int rowSpan, out DockGrid result)
    {
        result = this;
        var tile = new DockTile(id, column, row, columnSpan, rowSpan);
        if (!Fits(tile) || Tiles.Any(other => other.EntryId != id && other.Intersects(tile)))
            return false;
        DockTile? existing = Tiles.FirstOrDefault(other => other.EntryId == id);
        if (existing != tile)
            result = this with { Tiles = [.. Tiles.Where(other => other.EntryId != id), tile] };
        return true;
    }

    public bool TrySwap(Guid id, Guid targetId, out DockGrid result)
    {
        result = this;
        DockTile? source = Tiles.FirstOrDefault(tile => tile.EntryId == id);
        DockTile? target = Tiles.FirstOrDefault(tile => tile.EntryId == targetId);
        if (source == null || target == null)
            return false;
        if (source == target)
            return true;

        DockGrid vacant = this with { Tiles = Tiles.Where(tile => tile.EntryId != id && tile.EntryId != targetId).ToArray() };
        if (!vacant.TryPlace(id, target.Column, target.Row, source.ColumnSpan, source.RowSpan, out DockGrid proposed))
            return false;
        if (!proposed.TryPlace(targetId, source.Column, source.Row, target.ColumnSpan, target.RowSpan, out proposed))
            return false;
        result = proposed;
        return true;
    }

    internal void Validate()
    {
        if (Columns is < 1 or > MaxDimension || Rows is < 1 or > MaxDimension || Tiles is null)
            throw new ArgumentException("Dock 网格尺寸或格位无效。");

        var occupied = new bool[Rows, Columns];
        var ids = new HashSet<Guid>();
        foreach (DockTile tile in Tiles)
        {
            if (tile is null || !Fits(tile) || !ids.Add(tile.EntryId))
                throw new ArgumentException("Dock 网格包含无效或重复的格位。");
            for (int row = tile.Row; row < tile.Bottom; row++)
                for (int column = tile.Column; column < tile.Right; column++)
                {
                    if (occupied[row, column])
                        throw new ArgumentException("Dock 图标的格位不能重叠。");
                    occupied[row, column] = true;
                }
        }
    }

    internal DockGrid Fill(IEnumerable<Guid> entryIds)
    {
        var tiles = Tiles.ToList();
        var ids = Tiles.Select(tile => tile.EntryId).ToHashSet();
        int columns = Columns;
        int rows = Rows;
        var occupied = new bool[MaxDimension, MaxDimension];
        foreach (DockTile tile in Tiles)
            for (int row = tile.Row; row < tile.Bottom; row++)
                for (int column = tile.Column; column < tile.Right; column++)
                    occupied[row, column] = true;

        int nextIndex = 0;
        foreach (Guid id in entryIds)
        {
            if (!ids.Add(id))
                continue;
            while (nextIndex < rows * columns && occupied[nextIndex / columns, nextIndex % columns])
                nextIndex++;
            if (nextIndex == rows * columns)
            {
                if (rows < MaxDimension)
                    rows++;
                else if (columns < MaxDimension)
                {
                    nextIndex = columns;
                    columns++;
                }
                else
                    throw new ArgumentException("Dock 网格已满，请移动或移除一个项目。");
            }
            int row = nextIndex / columns;
            int column = nextIndex % columns;
            tiles.Add(new DockTile(id, column, row));
            occupied[row, column] = true;
            nextIndex++;
        }
        return tiles.Count == Tiles.Length ? this : new DockGrid(columns, rows, [.. tiles]);
    }

    private bool Fits(DockTile tile) => tile.EntryId != Guid.Empty &&
        tile.Column >= 0 && tile.Column < Columns && tile.Row >= 0 && tile.Row < Rows &&
        tile.ColumnSpan > 0 && tile.ColumnSpan <= Columns - tile.Column &&
        tile.RowSpan > 0 && tile.RowSpan <= Rows - tile.Row;
}
