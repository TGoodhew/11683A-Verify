namespace Verify11683A.Instruments.Visa;

/// <summary>
/// What must happen even if the app is killed mid-run (Ctrl+C runs no finally blocks): a DC calibrator back to
/// Standby. Each action uses its own new session, because the run's session may be mid-exchange.
/// </summary>
public static class EmergencyStop
{
    private static readonly Lock Gate = new();
    private static readonly List<(string What, Action Action)> Actions = [];

    /// <summary>Registers <paramref name="action"/> until the returned handle is disposed.</summary>
    public static IDisposable Register(string what, Action action)
    {
        var entry = (what, action);
        lock (Gate)
            Actions.Add(entry);
        return new Registration(() =>
        {
            lock (Gate)
                Actions.Remove(entry);
        });
    }

    /// <summary>Runs every registered action; returns what could not be done.</summary>
    public static IReadOnlyList<string> RunAll()
    {
        List<(string What, Action Action)> actions;
        lock (Gate)
            actions = [.. Actions];
        var failed = new List<string>();
        foreach (var (what, action) in actions)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failed.Add($"{what}: {ex.Message}");
            }
        }
        return failed;
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;

        public void Dispose()
        {
            _remove?.Invoke();
            _remove = null;
        }
    }
}
