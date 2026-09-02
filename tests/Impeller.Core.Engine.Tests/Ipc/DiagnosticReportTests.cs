using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Ipc.Contracts;

namespace Impeller.Core.Engine.Tests.Ipc;

/// <summary>
/// Covers the document someone attaches to a bug report.
/// </summary>
/// <remarks>
/// The test of this type is whether a reader who was not there can work out what happened, so the
/// assertions are about what reaches the page rather than how it is laid out.
/// </remarks>
public class DiagnosticReportTests
{
    private static DiagnosticReport Sample() => new()
    {
        Taken = new DateTimeOffset(2026, 9, 2, 14, 30, 0, TimeSpan.Zero),
        Status = new EngineStatus(
            "0.1.0-dev",
            4821,
            new DateTimeOffset(2026, 9, 2, 14, 29, 59, TimeSpan.Zero),
            FailsafeEngaged: true,
            @"C:\ProgramData\Impeller\Configurations"),
        OperatingSystem = "Microsoft Windows 11",
        Runtime = ".NET 10.0.0",
        Architecture = "X64",
        RunningAsService = true,
        Identity = @"NT AUTHORITY\SYSTEM",
        LogRoot = @"C:\ProgramData\Impeller\Logs",
        Providers =
        [
            new ProviderDiagnostics("lhm", "LibreHardwareMonitor", true, 193, 9, ["Graphics"], null),
            new ProviderDiagnostics("adlx", "AMD ADLX", false, 0, 0, [], "DllNotFoundException: ADLX"),
        ],
        Sensors =
        [
            new SensorDescriptor(SensorId.New(), "CPU Package", SensorKind.Temperature, "lhm", "/amdcpu/0/temperature/2", 62.5f),
            new SensorDescriptor(SensorId.New(), "Fan #1", SensorKind.FanSpeed, "lhm", "/lpc/nct6687d/0/fan/0", null),
        ],
        Controls =
        [
            new ControlDescriptor(
                SensorId.New(),
                "CPU Fan",
                "lhm",
                "/lpc/nct6687d/0/control/0",
                new Duty(55f),
                SupportsAutomaticMode: true,
                ControlOwnerKind.ManualOverride,
                "shell"),
        ],
        ConfigurationName = "Imported",
        Validation = new ConfigurationValidation(
        [
            new ConfigurationIssue(ConfigurationSeverity.Warning, "sensor.absent", "The GPU sensor is not present."),
        ]),
        RecentLog = ["14:29:58 [INF] Engine starting.", "14:29:59 [WRN] Provider adlx failed."],
    };

    [Fact]
    public void The_rendered_report_says_what_the_engine_is_and_where_it_keeps_things()
    {
        var text = Sample().Render();

        Assert.Contains("0.1.0-dev", text, StringComparison.Ordinal);
        Assert.Contains(".NET 10.0.0", text, StringComparison.Ordinal);
        Assert.Contains(@"C:\ProgramData\Impeller\Logs", text, StringComparison.Ordinal);

        // Who it runs as, because a backend that sees nothing under a user account and everything
        // under LocalSystem is a permissions problem dressed as a hardware one.
        Assert.Contains(@"NT AUTHORITY\SYSTEM", text, StringComparison.Ordinal);
        Assert.Contains("service", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_backend_that_failed_says_so_along_with_why()
    {
        var text = Sample().Render();

        Assert.Contains("AMD ADLX", text, StringComparison.Ordinal);
        Assert.Contains("FAILED", text, StringComparison.Ordinal);
        Assert.Contains("DllNotFoundException", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_backend_that_came_up_with_a_group_missing_names_the_group()
    {
        // The usual shape of "why is my GPU missing": the backend is fine, one group is not.
        var text = Sample().Render();

        Assert.Contains("LibreHardwareMonitor", text, StringComparison.Ordinal);
        Assert.Contains("unavailable groups: Graphics", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_failsafe_being_engaged_is_stated_outright()
    {
        // Buried, this is the single most important line in a report about fans running flat out.
        Assert.Contains("Failsafe engaged yes", Sample().Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_issues_are_carried_across()
    {
        var text = Sample().Render();

        Assert.Contains("sensor.absent", text, StringComparison.Ordinal);
        Assert.Contains("The GPU sensor is not present.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Sensors_and_controls_arrive_with_their_readings_and_their_owner()
    {
        var text = Sample().Render();

        Assert.Contains("CPU Package", text, StringComparison.Ordinal);
        Assert.Contains("62.5", text, StringComparison.Ordinal);

        // A sensor reporting nothing shows as nothing rather than as zero, which would read as a
        // fan that has stopped.
        Assert.Contains("Fan #1", text, StringComparison.Ordinal);

        Assert.Contains("ManualOverride", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_log_tail_is_in_the_document_rather_than_pointed_at()
    {
        // A path to a file the reader cannot open is not diagnostics.
        var text = Sample().Render();

        Assert.Contains("Provider adlx failed.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_from_an_engine_with_nothing_to_say_still_renders()
    {
        var text = new DiagnosticReport().Render();

        Assert.Contains("Impeller diagnostic report", text, StringComparison.Ordinal);
    }
}
