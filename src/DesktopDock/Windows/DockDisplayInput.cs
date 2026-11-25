namespace DesktopDock;

internal sealed class DockDisplayInput : IDisposable
{
    private readonly DockOutsideClickMonitor outsideClicks;
    private readonly HashSet<Action<int, int>> listeners = [];

    internal DockDisplayInput() => outsideClicks = new DockOutsideClickMonitor(DispatchOutsideClick);

    internal int AddOutsideClick(Action<int, int> listener)
    {
        if (listeners.Add(listener) && listeners.Count == 1)
        {
            bool hooked = outsideClicks.TryStart(out int error);
            OutsideHookError = !hooked && error == 0 ? -1 : error;
        }
        return OutsideHookError;
    }

    internal void RemoveOutsideClick(Action<int, int> listener)
    {
        if (!listeners.Remove(listener) || listeners.Count != 0)
            return;

        outsideClicks.Stop();
        OutsideHookError = 0;
    }

    private int OutsideHookError { get; set; }

    private void DispatchOutsideClick(int x, int y)
    {
        foreach (Action<int, int> listener in listeners.ToArray())
            listener(x, y);
    }

    public void Dispose()
    {
        listeners.Clear();
        outsideClicks.Dispose();
    }
}
