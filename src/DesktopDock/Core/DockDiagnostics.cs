using System.Text;
using System.Text.Json;

namespace DesktopDock;

internal sealed record DockDiagnosticEvent(DateTimeOffset Time, string Event, string? ErrorType, int? ErrorCode);

internal sealed class DockDiagnostics(string directory)
{
    private const int MaximumLogBytes = 64 * 1024;
    private readonly string logPath = Path.Combine(directory, "runtime.log");
    private readonly object gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    internal static string Version => typeof(DockDiagnostics).Assembly.GetName().Version!.ToString(3);
    internal string ProfileDirectory => directory;

    internal void Record(string name, Exception? error = null)
    {
        lock (gate)
        {
            try
            {
                var file = new FileInfo(logPath);
                if (file.Exists && file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return;
                var entry = new DockDiagnosticEvent(DateTimeOffset.UtcNow, name, error?.GetType().Name, error?.HResult);
                string line = JsonSerializer.Serialize(entry, JsonSerializerOptions.Web) + Environment.NewLine;
                if (file.Exists && file.Length > MaximumLogBytes)
                {
                    using var stream = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                    stream.Seek(-MaximumLogBytes / 2, SeekOrigin.End);
                    byte[] tail = new byte[MaximumLogBytes / 2];
                    stream.ReadExactly(tail);
                    int start = Array.IndexOf(tail, (byte)'\n') + 1;
                    stream.Position = 0;
                    stream.Write(tail.AsSpan(start));
                    stream.SetLength(stream.Position);
                }
                File.AppendAllText(logPath, line, Encoding.UTF8);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Diagnostics never block Dock startup, shutdown, or recovery.
            }
        }
    }

    internal void Export(string destination, object displaySummary)
    {
        lock (gate)
        {
            var file = new FileInfo(logPath);
            string[] recent = file.Exists && file.Length <= 2 * MaximumLogBytes &&
                !file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                ? File.ReadLines(logPath, Encoding.UTF8).TakeLast(100).ToArray() : [];
            var report = new
            {
                schemaVersion = 1,
                product = "Desktop Dock",
                version = Version,
                time = DateTimeOffset.UtcNow,
                windows = Environment.OSVersion.Version.ToString(),
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                displays = displaySummary,
                recentEvents = recent
            };
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(output, report, JsonOptions);
        }
    }
}
