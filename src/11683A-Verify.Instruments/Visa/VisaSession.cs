using Ivi.Visa;

namespace Verify11683A.Instruments.Visa;

/// <summary>
/// NI-VISA session through the IVI VISA.NET shared components. GlobalResourceManager loads NI's
/// modern-.NET implementation from VISA\Microsoft.NET\VendorAssemblies, so this runs on .NET 10 x64.
/// </summary>
public sealed class VisaSession : IVisaSession
{
    private readonly IMessageBasedSession _session;

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
        var session = GlobalResourceManager.Open(resourceName, AccessModes.None, timeoutMilliseconds) as IMessageBasedSession
            ?? throw new InvalidOperationException($"{resourceName} is not a message-based VISA resource.");
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

    public void Write(string command) => _session.RawIO.Write(command + "\n");

    public string ReadLine() => _session.RawIO.ReadString().TrimEnd('\r', '\n');

    public void Clear() => _session.Clear();

    public void Dispose() => _session.Dispose();
}
