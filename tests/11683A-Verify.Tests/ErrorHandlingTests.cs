using Verify11683A.App;
using Verify11683A.Instruments.DcSource;
using Verify11683A.Instruments.PowerMeter;
using Verify11683A.Instruments.Visa;
using Verify11683A.Meters;
using Verify11683A.Tests.Fakes;

namespace Verify11683A.Tests;

/// <summary>How errors are reported and what is left safe after one.</summary>
public class ErrorHandlingTests
{
    private static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "11683A-Verify-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void The_drivers_errors_are_instrument_errors()
    {
        Assert.IsAssignableFrom<InstrumentException>(new PowerMeterException("x"));
        Assert.IsAssignableFrom<InstrumentException>(new DcSourceException("x"));
        Assert.IsAssignableFrom<InstrumentException>(new Instruments.Dvm.DvmException("x"));
        Assert.IsAssignableFrom<InstrumentException>(new InstrumentTimeoutException("x"));
    }

    [Fact]
    public void Without_an_operator_a_failed_step_is_not_retried()
    {
        var dir = TempDirectory();
        using var log = new SessionLog(dir, "attempt");
        var calls = 0;
        Assert.Throws<InstrumentTimeoutException>(() =>
            ConsoleUi.Attempt<int>("Reading", () =>
            {
                calls++;
                throw new InstrumentTimeoutException("no reply");
            }, log));
        Assert.Equal(1, calls);
        Assert.Equal(42, ConsoleUi.Attempt("Reading", () => 42, log));
    }

    [Fact]
    public void A_bug_is_not_caught_as_an_instrument_error()
    {
        var dir = TempDirectory();
        using var log = new SessionLog(dir, "attempt");
        Assert.Throws<NullReferenceException>(() => ConsoleUi.Attempt<int>("Reading", () => throw new NullReferenceException(), log));
    }

    [Fact]
    public void Emergency_stop_runs_what_is_registered_until_it_is_released()
    {
        var ran = 0;
        var registration = EmergencyStop.Register("calibrator to Standby", () => ran++);
        Assert.Empty(EmergencyStop.RunAll());
        registration.Dispose();
        EmergencyStop.RunAll();
        Assert.Equal(1, ran);
    }

    [Fact]
    public void Emergency_stop_reports_what_failed()
    {
        using var registration = EmergencyStop.Register("5440B to Standby", () => throw new InstrumentException("no reply"));
        Assert.Contains(EmergencyStop.RunAll(), f => f.StartsWith("5440B to Standby", StringComparison.Ordinal));
    }

    [Fact]
    public void A_5440B_that_fails_Standby_on_closing_says_so_and_still_closes()
    {
        var session = new ScriptedVisaSession().Answer("GERR", "34");
        string? warned = null;
        var source = new Fluke5440B(session, sleep: _ => { }) { StandbyFailed = m => warned = m };
        source.Dispose();
        Assert.Contains("error 34", warned);
    }

    [Fact]
    public void A_meter_the_app_controlled_is_put_back_in_free_run_on_closing()
    {
        var session = new ScriptedVisaSession();
        var meter = new Hp437BPowerMeter(session, _ => { });
        meter.Preset();
        meter.Dispose();
        Assert.Equal("TR3", session.Sent[^1]);

        var untouched = new ScriptedVisaSession();
        new Hp437BPowerMeter(untouched).Dispose();
        Assert.Empty(untouched.Sent);
    }

    [Fact]
    public void A_settings_file_with_a_syntax_error_is_named()
    {
        var dir = TempDirectory();
        var path = Path.Combine(dir, "appsettings.json");
        File.WriteAllText(path, "{ \"dvm\": { \"model\": \"34401A\" ");
        var ex = Assert.Throws<InvalidDataException>(() => AppConfig.Load(path));
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void A_bad_appsettings_local_json_is_named()
    {
        var dir = TempDirectory();
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), "{}");
        var local = Path.Combine(dir, "appsettings.local.json");
        File.WriteAllText(local, "{ \"dvm\": { \"resource\": \"GPIB0::17::INSTR\" \"serial\": \"\" } }");
        var ex = Assert.Throws<InvalidDataException>(() => AppConfig.Load(Path.Combine(dir, "appsettings.json")));
        Assert.Contains("appsettings.local.json", ex.Message);
    }

    [Fact]
    public void A_bad_data_file_is_named()
    {
        var dir = TempDirectory();
        var path = Path.Combine(dir, "X.json");
        File.WriteAllText(path, "{ \"model\": ");
        var ex = Assert.Throws<InvalidDataException>(() => MeterDefinition.Load(path));
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void A_results_folder_that_cannot_be_written_falls_back_to_temp()
    {
        var dir = TempDirectory();
        using var log = new SessionLog(dir, "save");
        var tried = new List<string>();
        var saved = Saving.WithFallback(directory =>
        {
            tried.Add(directory);
            if (tried.Count == 1)
                throw new UnauthorizedAccessException("read-only");
            return Path.Combine(directory, "run.json");
        }, Path.Combine(dir, "locked"), log);
        Assert.Equal(Saving.FallbackDirectory, tried[1]);
        Assert.StartsWith(Saving.FallbackDirectory, saved);
    }

    [Fact]
    public void A_closed_session_log_drops_late_lines_instead_of_throwing()
    {
        var dir = TempDirectory();
        var log = new SessionLog(dir, "closed");
        log.Dispose();
        log.Write("after the end");
        Assert.Equal(log.Path, SessionLog.Latest);
    }
}
