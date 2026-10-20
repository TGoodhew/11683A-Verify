namespace Verify11683A.Instruments.Visa;

/// <summary>Records every exchange on a session (for the session log), and every failure, and passes it through.</summary>
public sealed class LoggingVisaSession(IVisaSession inner, Action<string> log) : IVisaSession
{
    public string ResourceName => inner.ResourceName;

    public int TimeoutMilliseconds
    {
        get => inner.TimeoutMilliseconds;
        set => inner.TimeoutMilliseconds = value;
    }

    public void Write(string command)
    {
        log($"{ResourceName} << {command}");
        Logged(() => inner.Write(command));
    }

    public string ReadLine()
    {
        var line = Logged(inner.ReadLine);
        log($"{ResourceName} >> {line}");
        return line;
    }

    public void Clear()
    {
        log($"{ResourceName} -- device clear");
        Logged(inner.Clear);
    }

    public byte ReadStatusByte()
    {
        var status = Logged(inner.ReadStatusByte);
        log($"{ResourceName} -- serial poll: {status}");
        return status;
    }

    public void Dispose() => inner.Dispose();

    private void Logged(Action action) => Logged(() =>
    {
        action();
        return 0;
    });

    private T Logged<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            log($"{ResourceName} !! {ex.Message}");
            throw;
        }
    }
}
