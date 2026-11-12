namespace PhoneAudioConsole.Platform;

/// <summary>
/// Single-instance guard: the first process owns a global mutex and a named event; later
/// launches signal the owner to restore its tray window and then exit.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }

    public event Action? ShowRequested;

    public SingleInstance(string name)
    {
        _mutex = new Mutex(initiallyOwned: true, @"Global\" + name, out bool createdNew);
        IsFirst = createdNew;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\" + name + ".Show");

        if (IsFirst)
        {
            _ = Task.Run(WaitLoop);
        }
    }

    private void WaitLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (_showEvent.WaitOne(500))
            {
                ShowRequested?.Invoke();
            }
        }
    }

    public void SignalExisting() => _showEvent.Set();

    public void Dispose()
    {
        _cts.Cancel();
        _showEvent.Dispose();
        if (IsFirst)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owner (released elsewhere) - safe to ignore.
            }
        }

        _mutex.Dispose();
        _cts.Dispose();
    }
}
