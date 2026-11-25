using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopDock;

internal sealed record DesktopShellIdentity(uint ProcessId, long ProcessStartTicks, long ShellWindow, long IconView);
internal sealed record DesktopIconObservation(DesktopShellIdentity Shell, bool Visible, bool WindowsHidden);

internal interface IDesktopIconBackend
{
    DesktopIconObservation? Observe();
    bool SetVisible(DesktopIconObservation expected, bool visible);
}

internal sealed record DesktopIconStatus(bool? Visible, bool? WindowsPreferenceHidden, uint? ExplorerProcessId,
    string State, bool CanHide, bool CanRestore, bool RecoveryNeeded, string Message);

internal sealed partial class DesktopIconService
{
    internal const string ManualRecovery = "在桌面空白处右击，选择「查看 → 显示桌面图标」。如果已经勾选，先取消再重新勾选。";
    private const int Version = 1;
    private const int JournalLimit = 8192;
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(4);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private enum Phase { HidePending, Hidden, RestorePending, Restored, UserChanged, ExplorerChanged, Uncertain }
    private sealed record Journal(int Version, Guid Operation, Guid Owner, DesktopIconObservation Before, Phase Phase,
        DateTimeOffset Started, DateTimeOffset Updated);

    private readonly IDesktopIconBackend backend;
    private readonly string path;
    private readonly bool allowChanges;
    private readonly Guid owner = Guid.NewGuid();
    private Journal? record;
    private DesktopIconObservation? observation;
    private string? journalError;

    internal DesktopIconService(string configurationDirectory, IDesktopIconBackend backend, bool allowChanges = true)
    {
        this.backend = backend;
        this.allowChanges = allowChanges;
        path = Path.Combine(configurationDirectory, "desktop-icons.json");
        try
        {
            record = ReadJournal(path);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            record = null;
            journalError = $"无法读取桌面图标恢复记录，原文件已保留。{error.Message}";
        }
        Status = MakeStatus();
    }

    internal static bool HasUnresolvedRecovery(string configurationDirectory)
    {
        try
        {
            Journal journal = ReadJournal(Path.Combine(configurationDirectory, "desktop-icons.json"));
            return journal.Phase is not (Phase.Restored or Phase.UserChanged);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static Journal ReadJournal(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > JournalLimit)
            throw new InvalidDataException("记录文件超出大小限制。");
        Journal? journal = JsonSerializer.Deserialize<Journal>(stream, JsonOptions);
        if (journal?.Version != Version || journal.Owner == Guid.Empty || journal.Operation == Guid.Empty ||
            journal.Before is not { Visible: true, WindowsHidden: false } before || before.Shell == null ||
            before.Shell.ProcessId == 0 || before.Shell.ProcessStartTicks <= 0 ||
            before.Shell.ShellWindow == 0 || before.Shell.IconView == 0 || journal.Updated < journal.Started)
            throw new InvalidDataException("记录格式无效。");
        return journal;
    }

    internal DesktopIconStatus Status { get; private set; }
    internal event EventHandler? Changed;

    internal DesktopIconStatus Refresh()
    {
        observation = backend.Observe();
        if (allowChanges && journalError == null && observation is { } current && record is { } previous &&
            previous.Phase is Phase.HidePending or Phase.Hidden or Phase.RestorePending)
        {
            Phase next = NextPhase(previous, current);
            if (next != previous.Phase)
                Persist(previous with { Phase = next, Updated = DateTimeOffset.UtcNow });
        }

        DesktopIconStatus status = MakeStatus();
        if (status != Status)
        {
            Status = status;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return status;
    }

    // The desktop management dialog calls this only after a separate affirmative confirmation.
    internal DesktopIconStatus HideAfterConfirmation()
    {
        if (!Refresh().CanHide || observation is not { } current)
            return Status;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var pending = new Journal(Version, Guid.NewGuid(), owner, current, Phase.HidePending, now, now);
        if (Persist(pending) && !backend.SetVisible(current, false))
            Persist(pending with { Phase = Phase.Uncertain });
        return Refresh();
    }

    internal DesktopIconStatus Restore()
    {
        if (!Refresh().CanRestore || observation is not { } current || record is not { } previous)
            return Status;

        Journal pending = previous with { Phase = Phase.RestorePending, Updated = DateTimeOffset.UtcNow };
        if (Persist(pending) && !backend.SetVisible(current, previous.Before.Visible))
            Persist(pending with { Phase = Phase.Uncertain });
        return Refresh();
    }

    internal void RestoreOnExit()
    {
        if (!allowChanges || record?.Owner != owner)
            return;

        // Explorer runs the asynchronous window request on a different thread.
        long deadline = Environment.TickCount64 + (long)ObservationTimeout.TotalMilliseconds;
        AwaitPending(Phase.HidePending, deadline);
        Restore();
        AwaitPending(Phase.RestorePending, deadline);
    }

    private void AwaitPending(Phase pending, long deadline)
    {
        while (record?.Phase == pending && journalError == null)
        {
            Refresh();
            long remaining = deadline - Environment.TickCount64;
            if (record?.Phase != pending || journalError != null || remaining <= 0)
                break;
            Thread.Sleep((int)Math.Min(50, remaining));
        }
    }

    private static Phase NextPhase(Journal previous, DesktopIconObservation current)
    {
        if (current.Shell != previous.Before.Shell)
            return Phase.ExplorerChanged;
        if (current.WindowsHidden != previous.Before.WindowsHidden)
            return Phase.UserChanged;

        bool expired = DateTimeOffset.UtcNow - previous.Updated >= ObservationTimeout;
        return previous.Phase switch
        {
            Phase.HidePending when !current.Visible => Phase.Hidden,
            Phase.Hidden when current.Visible => Phase.UserChanged,
            Phase.RestorePending when current.Visible => Phase.Restored,
            Phase.HidePending or Phase.RestorePending when expired => Phase.Uncertain,
            _ => previous.Phase
        };
    }

    private bool Persist(Journal next)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                JsonSerializer.Serialize(stream, next, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
            record = next;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            journalError = $"无法安全写入桌面图标恢复记录。{error.Message}";
            return false;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private DesktopIconStatus MakeStatus()
    {
        Phase? phase = record?.Phase;
        bool active = phase is Phase.HidePending or Phase.Hidden or Phase.RestorePending;
        bool trusted = allowChanges && journalError == null;
        bool canHide = trusted && !active && observation is { Visible: true, WindowsHidden: false };
        bool canRestore = trusted && phase == Phase.Hidden && observation is { Visible: false, WindowsHidden: false } current &&
            record is { } previous && current.Shell == previous.Before.Shell;
        bool recovery = journalError != null || active && record?.Owner != owner ||
            phase is Phase.ExplorerChanged or Phase.Uncertain && observation?.Visible != true ||
            active && observation == null;
        string message = journalError ?? (observation == null
            ? "暂时无法确认 Explorer 图标视图。"
            : phase switch
            {
                Phase.HidePending => "等待 Explorer 确认隐藏。",
                Phase.Hidden when recovery => "检测到上次运行留下的图标隐藏记录。可恢复显示。",
                Phase.Hidden => "桌面图标已由 Dock 暂时隐藏。",
                Phase.RestorePending => "等待 Explorer 确认恢复。",
                Phase.Restored => "已恢复桌面图标。",
                Phase.UserChanged => "检测到桌面图标状态已手动变更，保留现有设置。",
                Phase.ExplorerChanged => "检测到 Explorer 视图已更换。请按当前桌面状态选择手动恢复。",
                Phase.Uncertain => "未能确认 Explorer 已完成操作。请检查桌面并按需手动恢复。",
                _ when observation.WindowsHidden => "Windows 已隐藏桌面图标，可在桌面右键菜单恢复。",
                _ when observation.Visible => "桌面图标当前可见。",
                _ => "桌面图标当前隐藏，可在桌面右键菜单恢复。"
            });
        return new(observation?.Visible, observation?.WindowsHidden, observation?.Shell.ProcessId,
            phase?.ToString() ?? "Idle", canHide, canRestore, recovery, message);
    }
}
