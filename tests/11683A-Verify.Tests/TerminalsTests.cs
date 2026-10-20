using Verify11683A.App;
using Verify11683A.Calibrator;
using Verify11683A.Instruments.Dvm;
using Verify11683A.Instruments.Simulation;
using Verify11683A.Tests.Fakes;

namespace Verify11683A.Tests;

public class TerminalsTests
{
    private static readonly CalibratorLimits Limits = CalibratorLimits.LoadDefault();

    private static SimulatedDvm Dvm(string scenario = "nominal")
    {
        var clock = new SimulatedClock();
        return new SimulatedDvm(new Simulated11683A(Limits, SimulationScenario.Get(scenario), clock), clock);
    }

    [Theory]
    [InlineData("FRON", DvmTerminals.Front)]
    [InlineData("\"FRON\"\n", DvmTerminals.Front)]
    [InlineData("REAR", DvmTerminals.Rear)]
    [InlineData("rubbish", DvmTerminals.Unknown)]
    public void The_34401A_reads_ROUT_TERM(string reply, DvmTerminals expected)
    {
        var session = new ScriptedVisaSession().Answer("ROUT:TERM?", reply);

        Assert.Equal(expected, new Hp34401A(session).ReadTerminals());
        Assert.Equal(["ROUT:TERM?"], session.Sent);
    }

    [Theory]
    [InlineData("1", DvmTerminals.Front)]
    [InlineData("2", DvmTerminals.Rear)]
    [InlineData("0", DvmTerminals.Open)]
    [InlineData("+1.0E+0", DvmTerminals.Front)]
    [InlineData("TERM FRONT", DvmTerminals.Front)]
    [InlineData("TERM REAR", DvmTerminals.Rear)]
    [InlineData("7", DvmTerminals.Unknown)]
    [InlineData("", DvmTerminals.Unknown)]
    public void The_3458A_reads_TERM_in_numeric_or_alpha_query_format(string reply, DvmTerminals expected)
    {
        var session = new ScriptedVisaSession().Answer("TERM?", reply);

        Assert.Equal(expected, new Hp3458A(session).ReadTerminals());
        Assert.Equal(["TERM?"], session.Sent);
    }

    [Fact]
    public void The_simulated_DVM_is_on_its_front_terminals_unless_the_scenario_says_rear()
    {
        Assert.Equal(DvmTerminals.Front, Dvm().ReadTerminals());
        Assert.Equal(DvmTerminals.Rear, Dvm("rear-terminals").ReadTerminals());
    }

    [Fact]
    public void On_the_rear_terminals_the_input_floats_and_the_ohms_safety_check_refuses()
    {
        var dvm = Dvm();
        dvm.Source.SetFunction(FunctionSwitch.Standby);
        dvm.Terminals = DvmTerminals.Rear;

        var check = OhmsSafety.Check(dvm);

        Assert.False(check.Safe);
        Assert.Equal(SimulatedDvm.FloatingVolts, check.Reading.Value, 1e-4);
        Assert.Contains(dvm.ReadBackSettings(), kv => kv.Key == "ROUT:TERM?" && kv.Value == "REAR");

        dvm.Terminals = DvmTerminals.Front;
        Assert.True(OhmsSafety.Check(dvm).Safe);
    }

    [Fact]
    public void Front_terminals_pass_without_asking_the_operator()
    {
        var log = new List<string>();
        var terminals = TerminalsCheck.Ensure(Dvm(), _ => throw new InvalidOperationException("should not ask"), log.Add);

        Assert.Equal(DvmTerminals.Front, terminals);
        Assert.Equal(["Terminals: FRONT"], log);
    }

    [Fact]
    public void Rear_terminals_ask_the_operator_and_check_again()
    {
        var dvm = Dvm("rear-terminals");
        var log = new List<string>();
        var asked = new List<string>();

        var terminals = TerminalsCheck.Ensure(dvm, problem =>
        {
            asked.Add(problem);
            if (asked.Count == 2)
                dvm.Terminals = DvmTerminals.Front;   // pressed the button on the second prompt
            return true;
        }, log.Add);

        Assert.Equal(DvmTerminals.Front, terminals);
        const string message = "The 34401A is reading its REAR terminals. Press the front-panel Front/Rear button so FRONT is selected.";
        Assert.Equal([message, message], asked);
        Assert.Equal([$"Terminals: REAR: {message}", $"Terminals: REAR: {message}", "Terminals: FRONT"], log);
    }

    [Fact]
    public void The_operator_can_give_up()
    {
        var terminals = TerminalsCheck.Ensure(Dvm("rear-terminals"), _ => false);
        Assert.Equal(DvmTerminals.Rear, terminals);
    }

    [Fact]
    public void An_unreadable_reply_is_reported_but_not_waited_on()
    {
        var session = new ScriptedVisaSession().Answer("ROUT:TERM?", "???");
        var log = new List<string>();

        var terminals = TerminalsCheck.Ensure(new Hp34401A(session), _ => throw new InvalidOperationException("should not wait"), log.Add);

        Assert.Equal(DvmTerminals.Unknown, terminals);
        Assert.Contains("did not say which terminals", Assert.Single(log));
    }

    [Fact]
    public void The_3458A_message_names_its_Terminals_button()
    {
        Assert.Equal("The 3458A is reading its REAR terminals. Press the front-panel Terminals button so FRONT is selected.",
            TerminalsCheck.Problem("3458A", DvmTerminals.Rear));
        Assert.Null(TerminalsCheck.Problem("3458A", DvmTerminals.Front));
    }

    [Fact]
    public void The_bench_check_logs_the_rear_terminals_and_presses_the_simulated_button()
    {
        var directory = Path.Combine(Path.GetTempPath(), "11683A-terminals-" + Guid.NewGuid().ToString("N"));
        try
        {
            string logPath;
            using (var bench = Bench.Open(new AppConfig { ResultsDirectory = directory }, CommandLine.Parse(["dvm-check", "--simulate=rear-terminals"]), "terminals"))
            {
                Assert.Equal(DvmTerminals.Rear, bench.Dvm.ReadTerminals());
                Assert.Equal(DvmTerminals.Front, bench.CheckTerminals());
                logPath = bench.Log.Path;
            }

            var log = File.ReadAllText(logPath);
            Assert.Contains("Terminals: REAR: The 34401A is reading its REAR terminals.", log);
            Assert.Contains("Terminals: FRONT", log);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
