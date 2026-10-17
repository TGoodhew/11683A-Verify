using Spectre.Console;
using Verify11683A.H01;
using Verify11683A.Instruments.PowerMeter;

namespace Verify11683A.App;

/// <summary>A power meter without a bus driver: the operator reads its display and types the value in.</summary>
public sealed class TypedPowerMeter(string model) : IPowerMeter
{
    public string Description => (model is "" or "manual" ? "Power meter" : model) + ", readings entered by the operator";

    public bool OverBus => false;

    public void Identify() { }

    public double ReadWatts()
    {
        if (Console.IsInputRedirected)
            throw new InvalidOperationException("A typed power meter reading needs an operator at the console.");
        var text = AnsiConsole.Prompt(new TextPrompt<string>("Power meter reading [grey](e.g. 1.0003 mW, 999.8 uW, 0.0 dBm)[/]:")
            .Validate(t => PowerText.ParseWatts(t) is > 0
                ? ValidationResult.Success()
                : ValidationResult.Error("Enter a power such as 1.0003 mW.")));
        return PowerText.ParseWatts(text)!.Value;
    }

    public void Dispose() { }
}
