#Requires -Version 7.4

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Text.UTF8Encoding]::new($false)

if (-not $IsWindows) {
    throw 'This probe requires an interactive Windows session.'
}

if (-not ('DesktopDock.P0.DesktopWindowProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopDock.P0;

public static class DesktopWindowProbe
{
    private const int QueryInterfaceSlot = 0; // IUnknown
    private const int QueryServiceSlot = 3; // IServiceProvider : IUnknown
    private const int QueryActiveShellViewSlot = 15; // IShellBrowser : IOleWindow
    private const int GetCurrentFolderFlagsSlot = 25; // IFolderView2 : IFolderView

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int QueryInterface(IntPtr instance, ref Guid iid, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int QueryService(IntPtr provider, ref Guid service, ref Guid iid, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int QueryActiveShellView(IntPtr browser, out IntPtr view);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetCurrentFolderFlags(IntPtr view, out uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    public static void Observe()
    {
        IntPtr shell = GetShellWindow();
        uint shellProcessId = Owner(shell);
        Console.WriteLine($"shell_hwnd=0x{shell:X} shell_pid={shellProcessId} shell_class={ClassName(shell)}");
        if (shell == IntPtr.Zero || shellProcessId == 0)
        {
            Console.WriteLine("icon_view=unavailable reason=no_current_shell_window");
            return;
        }

        int candidates = 0;
        bool enumerated = EnumWindows((host, _) =>
        {
            string hostClass = ClassName(host);
            if (hostClass is not ("Progman" or "WorkerW") || Owner(host) != shellProcessId)
            {
                return true;
            }

            EnumChildWindows(host, (view, _) =>
            {
                if (ClassName(view) != "SHELLDLL_DefView" || Owner(view) != shellProcessId)
                {
                    return true;
                }

                EnumChildWindows(view, (list, _) =>
                {
                    if (ClassName(list) == "SysListView32" && Owner(list) == shellProcessId)
                    {
                        candidates++;
                        Console.WriteLine($"host=0x{host:X} host_class={hostClass} " +
                            $"defview=0x{view:X} listview=0x{list:X} visible={IsWindowVisible(list)}");
                    }

                    return true;
                }, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);

        int enumerationError = Marshal.GetLastPInvokeError();
        Console.WriteLine($"enum_windows={enumerated} win32_error={(enumerated ? 0 : enumerationError)} " +
            $"candidate_count={candidates} shell_stable={GetShellWindow() == shell && Owner(shell) == shellProcessId}");
    }

    public static void ObserveFolderFlags(object desktopDispatch, int desktopWindow)
    {
        IntPtr shell = GetShellWindow();
        if (shell == IntPtr.Zero || unchecked((uint)shell.ToInt64()) != unchecked((uint)desktopWindow))
        {
            throw new InvalidOperationException("ShellWindows HWND does not match the current Shell window.");
        }

        var serviceProviderIid = new Guid("6D5140C1-7436-11CE-8034-00AA006009FA");
        var topLevelBrowserService = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        var shellBrowserIid = new Guid("000214E2-0000-0000-C000-000000000046");
        var folderView2Iid = new Guid("1AF3A467-214F-4298-908E-06B03E0B39F9");

        IntPtr desktop = Marshal.GetIUnknownForObject(desktopDispatch);
        IntPtr provider = IntPtr.Zero, browser = IntPtr.Zero, view = IntPtr.Zero, folderView = IntPtr.Zero;
        try
        {
            var queryDesktopInterface = Method<QueryInterface>(desktop, QueryInterfaceSlot);
            Check(queryDesktopInterface(desktop, ref serviceProviderIid, out provider),
                "QueryInterface(IServiceProvider)", provider);
            var queryService = Method<QueryService>(provider, QueryServiceSlot);
            Check(queryService(provider, ref topLevelBrowserService, ref shellBrowserIid, out browser),
                "QueryService(IShellBrowser)", browser);
            var queryActiveView = Method<QueryActiveShellView>(browser, QueryActiveShellViewSlot);
            Check(queryActiveView(browser, out view), "QueryActiveShellView", view);
            var queryViewInterface = Method<QueryInterface>(view, QueryInterfaceSlot);
            Check(queryViewInterface(view, ref folderView2Iid, out folderView), "QueryInterface(IFolderView2)", folderView);

            var getFlags = Method<GetCurrentFolderFlags>(folderView, GetCurrentFolderFlagsSlot);
            Check(getFlags(folderView, out uint flags), "GetCurrentFolderFlags", folderView);
            Console.WriteLine($"folder_flags=0x{flags:X8} desktop={(flags & 0x20) != 0} " +
                $"no_icons={(flags & 0x1000) != 0} shell_stable={GetShellWindow() == shell}");
        }
        finally
        {
            foreach (IntPtr reference in new[] { folderView, view, browser, provider, desktop })
            {
                if (reference != IntPtr.Zero)
                {
                    _ = Marshal.Release(reference);
                }
            }
        }
    }

    // The COM vtable order is defined by the Windows SDK IDL. Only the named read methods are called.
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(instance);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }

    private static void Check(int hresult, string operation, IntPtr result)
    {
        if (hresult != 0 || result == IntPtr.Zero)
        {
            throw new COMException(operation + " did not return a result.", hresult);
        }
    }

    private static string ClassName(IntPtr window)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    private static uint Owner(IntPtr window)
    {
        GetWindowThreadProcessId(window, out uint owner);
        return owner;
    }
}
'@
}

$timestamp = [DateTimeOffset]::UtcNow.ToString('o')
$sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
"utc=$timestamp windows=$([Environment]::OSVersion.Version) session=$sessionId pwsh=$($PSVersionTable.PSVersion)"
[DesktopDock.P0.DesktopWindowProbe]::Observe()

$shell = $null
try {
    $shell = New-Object -ComObject Shell.Application
    $hideIcons = $shell.GetSetting(0x4000)
    "shell_setting_hide_icons=$hideIcons"
} catch {
    $errorDetail = $_.Exception.GetBaseException()
    'shell_setting_error=0x{0:X8} type={1}' -f $errorDetail.HResult, $errorDetail.GetType().Name
} finally {
    if ($null -ne $shell) {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
}

$shellWindows = $null
$desktopDispatch = $null
$stage = 'activate_shell_windows'
try {
    $shellWindowsClass = [Guid]'9BA05972-F6A8-11CF-A442-00A0C90A8F39'
    $shellWindows = [Activator]::CreateInstance([type]::GetTypeFromCLSID($shellWindowsClass))
    $stage = 'find_desktop_dispatch'
    $desktopLocation = 0      # CSIDL_DESKTOP
    $emptyRoot = $null
    $desktopHandle = 0
    $desktopDispatch = $shellWindows.FindWindowSW([ref]$desktopLocation, [ref]$emptyRoot, 8, [ref]$desktopHandle, 1)
    'shell_windows_desktop_hwnd=0x{0:X} dispatch_found={1}' -f $desktopHandle, ($null -ne $desktopDispatch)
    if ($null -ne $desktopDispatch) {
        $stage = 'read_folder_flags'
        [DesktopDock.P0.DesktopWindowProbe]::ObserveFolderFlags($desktopDispatch, $desktopHandle)
    }
} catch {
    $errorDetail = $_.Exception.GetBaseException()
    'shell_windows_stage={0} hresult=0x{1:X8} type={2} detail={3}' -f $stage, $errorDetail.HResult, $errorDetail.GetType().Name, $errorDetail.Message
} finally {
    if ($null -ne $desktopDispatch) {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($desktopDispatch)
    }
    if ($null -ne $shellWindows) {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shellWindows)
    }
}
