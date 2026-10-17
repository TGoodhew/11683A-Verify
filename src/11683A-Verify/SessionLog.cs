namespace Verify11683A.App;

/// <summary>A timestamped plain-text log of a session, including every instrument exchange.</summary>
public sealed class SessionLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();

    public SessionLog(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"{name}_{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _writer = new StreamWriter(Path, append: false) { AutoFlush = true };
    }

    public string Path { get; }

    public void Write(string message)
    {
        lock (_lock)
            _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}");
    }

    public void Dispose() => _writer.Dispose();
}
