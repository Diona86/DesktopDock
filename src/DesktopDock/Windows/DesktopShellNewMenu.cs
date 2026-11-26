using System.Runtime.InteropServices;

namespace DesktopDock;

internal sealed class DesktopShellNewMenu : IDisposable
{
    // The Windows Shell's registered Directory\Background New handler.
    private static readonly Guid ShellNewClassId = new("D969A300-E7FF-11D0-A93B-00A0C90F2719");
    private const uint FirstCommand = 1;
    private const uint LastCommand = 0x7fff;
    private readonly object extension;
    private readonly IContextMenu context;
    private readonly nint rootMenu;
    private bool disposed;

    internal DesktopShellNewMenu(string folder, nint owner)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException("找不到 Windows 桌面文件夹。");

        try
        {
            extension = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellNewClassId, throwOnError: true)!)!;
        }
        catch (COMException error)
        {
            throw new InvalidOperationException("此 Windows 环境未提供原生新建菜单。", error);
        }
        try
        {
            Check(SHParseDisplayName(folder, 0, out nint folderId, 0, out _));
            try
            {
                Check(((IShellExtInit)extension).Initialize(folderId, 0, 0));
            }
            finally
            {
                Marshal.FreeCoTaskMem(folderId);
            }

            context = (IContextMenu)extension;
            rootMenu = CreatePopupMenu();
            if (rootMenu == 0)
                throw new InvalidOperationException("无法创建 Windows 新建菜单。");
            Check(context.QueryContextMenu(rootMenu, 0, FirstCommand, LastCommand, 0));
            Submenu = GetSubMenu(rootMenu, 0);
            if (Submenu == 0)
                throw new InvalidOperationException("Windows 未提供新建菜单。");
            Owner = owner;
        }
        catch (Exception error)
        {
            Dispose();
            if (error is COMException or InvalidCastException)
                throw new InvalidOperationException("Windows 无法初始化原生新建菜单。", error);
            throw;
        }
    }

    private nint Owner { get; }
    private nint Submenu { get; }

    internal void Show(int x, int y)
    {
        SetForegroundWindow(Owner);
        const uint returnCommand = 0x0100;
        const uint rightButton = 0x0002;
        uint command = TrackPopupMenuEx(Submenu, returnCommand | rightButton, x, y, Owner, 0);
        if (command is >= FirstCommand and <= LastCommand)
            Invoke(command);
    }

    private void Invoke(uint command)
    {
        const uint noAsync = 0x0100;
        var invoke = new CommandInfo
        {
            Size = (uint)Marshal.SizeOf<CommandInfo>(),
            Mask = noAsync,
            Owner = Owner,
            Verb = (nint)(command - FirstCommand),
            Show = 1
        };
        Check(context.InvokeCommand(ref invoke));
    }

    internal bool TryForwardMessage(int message, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (message is not (0x0117 or 0x002B or 0x002C or 0x0120)) // Init, draw, measure, or keyboard search.
            return false;

        if (extension is IContextMenu3 menu3)
            return menu3.HandleMenuMsg2((uint)message, wParam, lParam, out result) == 0;
        return extension is IContextMenu2 menu2 && menu2.HandleMenuMsg((uint)message, wParam, lParam) == 0;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (rootMenu != 0)
            DestroyMenu(rootMenu);
        Marshal.FinalReleaseComObject(extension);
    }

    private static void Check(int result)
    {
        if (result < 0)
            throw new InvalidOperationException("Windows 无法打开新建菜单或创建所选项目。", Marshal.GetExceptionForHR(result));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommandInfo
    {
        internal uint Size;
        internal uint Mask;
        internal nint Owner;
        internal nint Verb;
        internal nint Parameters;
        internal nint Directory;
        internal int Show;
        internal uint HotKey;
        internal nint Icon;
    }

    [ComImport, Guid("000214E8-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellExtInit
    {
        [PreserveSig] int Initialize(nint folderId, nint data, nint registryKey);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint insertIndex, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref CommandInfo command);
        [PreserveSig] int GetCommandString(nuint command, uint flags, nint reserved, nint buffer, uint capacity);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint insertIndex, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref CommandInfo command);
        [PreserveSig] int GetCommandString(nuint command, uint flags, nint reserved, nint buffer, uint capacity);
        [PreserveSig] int HandleMenuMsg(uint message, nint wParam, nint lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint insertIndex, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref CommandInfo command);
        [PreserveSig] int GetCommandString(nuint command, uint flags, nint reserved, nint buffer, uint capacity);
        [PreserveSig] int HandleMenuMsg(uint message, nint wParam, nint lParam);
        [PreserveSig] int HandleMenuMsg2(uint message, nint wParam, nint lParam, out nint result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, nint context, out nint idList, uint requested, out uint actual);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern nint GetSubMenu(nint menu, int position);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EndMenu();
}
