namespace Verify11683A.App;

/// <summary>A timestamped plain-text log of a session, including every instrument exchange.</summary>
public sealed class SessionLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();
    private bool _closed;

    public SessionLog(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"{name}_{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _writer = new StreamWriter(Path, append: false) { AutoFlush = true };
        Latest = Path;
    }

    public string Path { get; }

    /// <summary>The most recent session's log, for an error message after the session has closed.</summary>
    public static string? Latest { get; private set; }

    /// <summary>Appends a line; after the log is closed (an error on the way out) the line is dropped rather than thrown.</summary>
    public void Write(string message)
    {
        lock (_lock)
        {
            if (!_closed)
                _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}");
        }
    }

    /// <summary>Copies the log so far to <paramref name="path"/>, e.g. next to the results file.</summary>
    public void SaveCopy(string path)
    {
        lock (_lock)
        {
            _writer.Flush();
            using var source = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var target = File.Create(path);
            source.CopyTo(target);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _closed = true;
            _writer.Dispose();
        }
    }
}
