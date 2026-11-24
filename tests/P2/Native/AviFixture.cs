using System.Text;

namespace DesktopDock;

internal sealed class AviFixture : IDisposable
{
    private const int Width = 64;
    private const int Height = 48;
    private const int FrameRate = 12;
    private const int Frames = 72;
    private const int FrameSize = Width * Height * 3;

    private AviFixture(string filePath) => FilePath = filePath;

    internal string FilePath { get; }

    internal static AviFixture Create()
    {
        string filePath = Path.Combine(AppContext.BaseDirectory, $"p2-video-{Guid.NewGuid():N}.avi");
        using var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Chunk("RIFF", () =>
        {
            writer.Tag("AVI ");
            WriteHeaders(writer);
            WriteFrames(writer);
        });
        Console.WriteLine($"Generated local AVI {Width}x{Height}, {FrameRate} fps, {Frames / FrameRate} seconds");
        return new AviFixture(filePath);
    }

    public void Dispose() => File.Delete(FilePath);

    private static void WriteHeaders(BinaryWriter writer)
    {
        writer.Chunk("LIST", () =>
        {
            writer.Tag("hdrl");
            writer.Chunk("avih", () => writer.Integers(1_000_000 / FrameRate, FrameSize * FrameRate, 0, 16,
                Frames, 0, 1, FrameSize, Width, Height, 0, 0, 0, 0));
            writer.Chunk("LIST", () =>
            {
                writer.Tag("strl");
                writer.Chunk("strh", () =>
                {
                    writer.Tag("vids");
                    writer.Tag("DIB ");
                    writer.Integers(0, 0, 0, 1, FrameRate, 0, Frames, FrameSize, -1, 0);
                    writer.Write((short)0);
                    writer.Write((short)0);
                    writer.Write((short)Width);
                    writer.Write((short)Height);
                });
                writer.Chunk("strf", () =>
                {
                    writer.Integers(40, Width, Height);
                    writer.Write((short)1);
                    writer.Write((short)24);
                    writer.Integers(0, FrameSize, 0, 0, 0, 0);
                });
            });
        });
    }

    private static void WriteFrames(BinaryWriter writer)
    {
        var offsets = new int[Frames];
        writer.Chunk("LIST", () =>
        {
            long start = writer.BaseStream.Position;
            writer.Tag("movi");
            var pixels = new byte[FrameSize];
            for (int frame = 0; frame < Frames; frame++)
            {
                offsets[frame] = checked((int)(writer.BaseStream.Position - start));
                int index = 0;
                for (int y = 0; y < Height; y++)
                    for (int x = 0; x < Width; x++)
                    {
                        pixels[index++] = (byte)(frame * 3 + x * 2);
                        pixels[index++] = (byte)(100 + frame + y * 2);
                        pixels[index++] = (byte)(80 + x * 2);
                    }
                writer.Chunk("00db", () => writer.Write(pixels));
            }
        });
        writer.Chunk("idx1", () =>
        {
            foreach (int offset in offsets)
            {
                writer.Tag("00db");
                writer.Integers(16, offset, FrameSize);
            }
        });
    }
}

file static class RiffWriter
{
    internal static void Tag(this BinaryWriter writer, string tag) => writer.Write(Encoding.ASCII.GetBytes(tag));

    internal static void Integers(this BinaryWriter writer, params int[] values)
    {
        foreach (int value in values)
            writer.Write(value);
    }

    internal static void Chunk(this BinaryWriter writer, string tag, Action contents)
    {
        Stream stream = writer.BaseStream;
        writer.Tag(tag);
        long sizePosition = stream.Position;
        writer.Write(0);
        contents();
        long end = stream.Position;
        int size = checked((int)(end - sizePosition - sizeof(int)));
        stream.Position = sizePosition;
        writer.Write(size);
        stream.Position = end;
        if ((size & 1) != 0)
            writer.Write((byte)0);
    }
}
