using Ivi.Visa;

namespace Verify11683A.Instruments.Visa;

/// <summary>
/// NI-VISA session through the IVI VISA.NET shared components. GlobalResourceManager loads NI's
/// modern-.NET implementation from VISA\Microsoft.NET\VendorAssemblies, so this runs on .NET 10 x64.
/// </summary>
/// <remarks>
/// VISA's own exceptions (IOTimeoutException, NativeVisaException) come out as <see cref="InstrumentException"/>s that
/// say which instrument, what was sent and what to check, so the app can show them and offer a retry.
/// </remarks>
public sealed class VisaSession : IVisaSession
{
    private readonly IMessageBasedSession _session;
    private string _lastCommand = "";

    private VisaSession(IMessageBasedSession session, string resourceName)
    {
        _session = session;
        ResourceName = resourceName;
    }

    /// <summary>
    /// Opens a GPIB resource such as GPIB0::22::INSTR. The bus runs through an HP 37204A extender,
    /// so callers should pass a generous timeout.
    /// </summary>
    public static VisaSession Open(string resourceName, int timeoutMilliseconds)
    {
        IMessageBasedSession session;
        try
        {
            session = GlobalResourceManager.Open(resourceName, AccessModes.None, timeoutMilliseconds) as IMessageBasedSession
                ?? throw new InstrumentException($"{resourceName} is not a message-based VISA resource.");
        }
        catch (VisaException ex)
        {
            throw new InstrumentException($"{resourceName}: VISA could not open it ({ex.Message}). Check the address in "
                + "appsettings.local.json and that NI-VISA sees the GPIB interface (NI MAX).", ex);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        {
            throw new InstrumentException($"{resourceName}: NI-VISA is not installed with .NET support ({ex.Message}).", ex);
        }
        session.TimeoutMilliseconds = timeoutMilliseconds;
        session.TerminationCharacter = (byte)'\n';
        session.TerminationCharacterEnabled = true;
        session.SendEndEnabled = true;
        return new VisaSession(session, resourceName);
    }

    public string ResourceName { get; }

    /// <summary>"National Instruments 26.5" or similar, for the session log.</summary>
    public string Implementation => $"{_session.ResourceManufacturerName} {_session.ResourceImplementationVersion}";

    public int TimeoutMilliseconds
    {
        get => _session.TimeoutMilliseconds;
        set => _session.TimeoutMilliseconds = value;
    }

    public void Write(string command)
    {
        DiscardLateReply();
        _lastCommand = command;
        Io(() => _session.RawIO.Write(command + "\n"), $"sending '{command}'");
    }

    /// <summary>A read timed out, so its reply may still arrive and be taken for the next command's.</summary>
    private bool _replyMayBePending;

    /// <summary>
    /// After a timed-out read, reads (briefly) whatever reply came late and drops it, so the next query gets its own
    /// answer. A device clear would do it too, but it presets some meters (the 438A back to sensor A).
    /// </summary>
    private void DiscardLateReply()
    {
        if (!_replyMayBePending)
            return;
        _replyMayBePending = false;
        var timeout = _session.TimeoutMilliseconds;
        try
        {
            _session.TimeoutMilliseconds = 500;
            _session.RawIO.ReadString();
        }
        catch (VisaException)
        {
            // Nothing came: nothing to drop.
        }
        finally
        {
            _session.TimeoutMilliseconds = timeout;
        }
    }

    public string ReadLine()
    {
        try
        {
            return Io(() => _session.RawIO.ReadString().TrimEnd('\r', '\n'), $"waiting for the reply to '{_lastCommand}'");
        }
        catch (InstrumentTimeoutException)
        {
            _replyMayBePending = true;
            throw;
        }
    }

    public void Clear() => Io(_session.Clear, "sending a device clear");

    public byte ReadStatusByte() => Io(() => (byte)_session.ReadStatusByte(), "reading its status byte (serial poll)");

    public void Dispose() => _session.Dispose();

    private void Io(Action action, string doing) => Io(() =>
    {
        action();
        return 0;
    }, doing);

    private T Io<T>(Func<T> action, string doing)
    {
        try
        {
            return action();
        }
        catch (IOTimeoutException ex)
        {
            throw new InstrumentTimeoutException($"{ResourceName} did not respond within {TimeoutMilliseconds / 1000.0:0.#} s while {doing}. "
                + "Check that it is switched on, at this address, and not showing an error or a menu.", ex);
        }
        catch (VisaException ex)
        {
            throw new InstrumentException($"{ResourceName}: VISA error while {doing}: {ex.Message}", ex);
        }
    }
}
