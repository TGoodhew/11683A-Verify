using Verify11683A.Instruments.Dvm;

namespace Verify11683A.Tests.Fakes;

/// <summary>Passes everything to another DVM and records each configuration, with whether the output was live at the time.</summary>
internal sealed class RecordingDvm(IDvm inner, Func<bool> outputLive) : IDvm
{
    public List<(DvmFunction Function, double Range, bool OutputLive)> Configured { get; } = [];

    public string Model => inner.Model;
    public string ResourceName => inner.ResourceName;
    public DvmConfiguration? Configuration => inner.Configuration;
    public DvmIdentity Identify() => inner.Identify();
    public void Reset() => inner.Reset();
    public IReadOnlyList<double> Ranges(DvmFunction function) => inner.Ranges(function);

    public DvmConfiguration Configure(DvmFunction function, double range, double nplc)
    {
        Configured.Add((function, range, outputLive()));
        return inner.Configure(function, range, nplc);
    }

    public DvmReading Read() => inner.Read();
    public IReadOnlyList<string> ReadErrors() => inner.ReadErrors();
    public IReadOnlyList<KeyValuePair<string, string>> ReadBackSettings() => inner.ReadBackSettings();
    public void Dispose() => inner.Dispose();
}
