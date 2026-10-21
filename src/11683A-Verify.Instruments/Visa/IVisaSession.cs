namespace Verify11683A.Instruments.Visa;

/// <summary>
/// A message-based instrument session. Drivers talk only through this, so tests can script the
/// instrument's replies and the app can log every exchange.
/// </summary>
public interface IVisaSession : IDisposable
{
    string ResourceName { get; }

    int TimeoutMilliseconds { get; set; }

    /// <summary>Sends one command; the session adds the line terminator.</summary>
    void Write(string command);

    /// <summary>Reads one response line, without its terminator.</summary>
    string ReadLine();

    /// <summary>Device clear (SDC).</summary>
    void Clear();

    /// <summary>Serial poll: the status byte, read without sending the instrument a command (one would abort a 437B or 438A CAL).</summary>
    byte ReadStatusByte();

    string Query(string command)
    {
        Write(command);
        return ReadLine();
    }
}
