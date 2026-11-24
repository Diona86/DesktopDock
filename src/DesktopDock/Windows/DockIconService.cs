using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopDock;

public sealed class DockIconService
{
    private const int CacheCapacity = 192;
    private const int MaxIconSize = 256;
    private const int MaxSourceDimension = 4096;
    private const int MaxSourceBytes = 24 * 1024 * 1024;
    private readonly SemaphoreSlim extractionSlots = new(4);
    private readonly object cacheLock = new();
    private readonly Dictionary<IconKey, Lazy<ImageSource>> cache = [];
    private readonly Queue<IconKey> insertionOrder = [];

    public async Task<ImageSource> GetAsync(DockItem item, int pixelSize, CancellationToken cancellationToken = default)
    {
        int size = Math.Clamp(pixelSize, 16, MaxIconSize);
        await extractionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ImageSource image = await Task.Run(() => GetOrLoad(item, size), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return image;
        }
        finally
        {
            extractionSlots.Release();
        }
    }

    private ImageSource GetOrLoad(DockItem item, int size)
    {
        string? target = item.Kind == DockItemKind.Url ? null : item.Target;
        var key = new IconKey(item.Kind, target, ReadStamp(target), item.CustomIconPath, ReadStamp(item.CustomIconPath), size);
        Lazy<ImageSource>? entry;
        lock (cacheLock)
        {
            if (!cache.TryGetValue(key, out entry))
            {
                if (cache.Count == CacheCapacity)
                    cache.Remove(insertionOrder.Dequeue());

                entry = new Lazy<ImageSource>(() => Load(key));
                cache.Add(key, entry);
                insertionOrder.Enqueue(key);
            }
        }

        return entry.Value;
    }

    private static ImageSource Load(IconKey key) =>
        ReadCustomIcon(key.CustomPath, key.Size)
        ?? ReadShellIcon(key.Target, key.Size)
        ?? MakeFallback(key.Kind);

    private static FileStamp ReadStamp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return default;

        try
        {
            var file = new FileInfo(path);
            if (file.Exists)
                return new FileStamp(file.LastWriteTimeUtc.Ticks, file.Length);

            var directory = new DirectoryInfo(path);
            if (directory.Exists)
                return new FileStamp(directory.LastWriteTimeUtc.Ticks, -1);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The Shell or the fallback can still supply an image.
        }

        return default;
    }

    private static ImageSource? ReadCustomIcon(string? path, int size)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is 0 or > MaxSourceBytes)
                return null;

            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder is not (IconBitmapDecoder or PngBitmapDecoder or JpegBitmapDecoder or BmpBitmapDecoder))
                return null;

            BitmapFrame? frame = decoder.Frames
                .Where(frame => frame.PixelWidth <= MaxSourceDimension && frame.PixelHeight <= MaxSourceDimension)
                .OrderBy(frame => Math.Max(frame.PixelWidth, frame.PixelHeight) < size)
                .ThenBy(frame => Math.Abs(Math.Max(frame.PixelWidth, frame.PixelHeight) - size))
                .FirstOrDefault();
            return frame is null ? null : CopyScaledBitmap(frame, size);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException
            or ArgumentException or NotSupportedException or InvalidOperationException or COMException)
        {
            return null;
        }
    }

    private static ImageSource? ReadShellIcon(string? path, int size)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        int initializeResult = CoInitializeEx(0, 0);
        const int ChangedMode = unchecked((int)0x80010106);
        if (initializeResult < 0 && initializeResult != ChangedMode)
            return null;

        IShellItemImageFactory? factory = null;
        nint bitmap = 0;
        try
        {
            Guid interfaceId = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, 0, in interfaceId, out factory) < 0 || factory is null)
                return null;

            const uint IconOnly = 0x4;
            const uint BiggerSizeOk = 0x1;
            if (factory.GetImage(new NativeSize(size, size), IconOnly | BiggerSizeOk, out bitmap) < 0 || bitmap == 0)
                return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            return CopyScaledBitmap(source, size);
        }
        catch (Exception error) when (error is COMException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            if (bitmap != 0)
                DeleteObject(bitmap);
            if (factory is not null)
                Marshal.ReleaseComObject(factory);
            if (initializeResult >= 0)
                CoUninitialize();
        }
    }

    private static BitmapSource CopyScaledBitmap(BitmapSource source, int size)
    {
        double scale = Math.Min(1, (double)size / Math.Max(source.PixelWidth, source.PixelHeight));
        if (scale < 1)
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));

        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        var result = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, bitmap.Format, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static ImageSource MakeFallback(DockItemKind kind)
    {
        string glyph = kind switch
        {
            DockItemKind.Folder => "M5,9 L5,7 10,7 12,9 19,9 19,17 5,17 Z",
            DockItemKind.Url => "M12,5 A7,7 0 1 1 11.99,5 M5,12 L19,12 M12,5 C8,8 8,16 12,19 C16,16 16,8 12,5",
            DockItemKind.Application => "M5,6 L19,6 19,17 5,17 Z M5,9 L19,9",
            DockItemKind.Shortcut => "M13,7 L18,7 18,12 M18,7 L10,15 M10,7 L6,7 6,18 18,18 18,14",
            _ => "M7,5 L14,5 18,9 18,19 7,19 Z M14,5 L14,9 18,9 M10,12 L15,12 M10,15 L15,15",
        };
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(67, 87, 104)), null,
            new RectangleGeometry(new Rect(0, 0, 24, 24), 5, 5)));
        drawing.Children.Add(new GeometryDrawing(null, new Pen(Brushes.WhiteSmoke, 1.35), Geometry.Parse(glyph)));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private readonly record struct FileStamp(long ModifiedAt, long Length);

    private readonly record struct IconKey(
        DockItemKind Kind, string? Target, FileStamp TargetStamp, string? CustomPath, FileStamp CustomStamp, int Size);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int width, int height)
    {
        private readonly int width = width;
        private readonly int height = height;
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, uint flags, out nint bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, in Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint apartment);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint bitmap);
}
