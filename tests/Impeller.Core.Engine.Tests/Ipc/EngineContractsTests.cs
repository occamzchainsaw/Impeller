using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Ipc.Contracts;
using Impeller.Plugins.Abstractions;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Impeller.Core.Engine.Tests.Ipc;

/// <summary>
/// Drives the real contracts over a real JSON-RPC connection, on an in-memory duplex stream rather
/// than a pipe.
/// </summary>
/// <remarks>
/// The pipe is the least interesting part of the channel and the most annoying to test. What can
/// genuinely break is the wire format: a polymorphic curve losing its type, an id arriving as an
/// object, a duty coming back as a string, an empty collection deserialising as null. All of that
/// is exercised here at full speed with nothing to clean up.
/// </remarks>
public sealed class EngineContractsTests : IAsyncDisposable
{
    private readonly (Stream Client, Stream Server) _streams = FullDuplexStream.CreatePair();
    private readonly FakeEngine _engine = new();
    private readonly RecordingClient _client = new();
    private readonly JsonRpc _serverRpc;
    private readonly JsonRpc _clientRpc;

    public EngineContractsTests()
    {
        _serverRpc = new JsonRpc(Handler(_streams.Server));
        _serverRpc.AddLocalRpcTarget<IEngineControl>(_engine, null);
        _engine.Events = _serverRpc.Attach<IEngineEvents>();
        _serverRpc.StartListening();

        _clientRpc = new JsonRpc(Handler(_streams.Client));
        _clientRpc.AddLocalRpcTarget<IEngineEvents>(_client, null);
        Engine = _clientRpc.Attach<IEngineControl>();
        _clientRpc.StartListening();
    }

    /// <summary>The engine as the shell sees it: a proxy, over a real connection.</summary>
    private IEngineControl Engine { get; }

    public async ValueTask DisposeAsync()
    {
        _clientRpc.Dispose();
        _serverRpc.Dispose();
        await _streams.Client.DisposeAsync();
        await _streams.Server.DisposeAsync();
    }

    /// <summary>The same serializer contract the engine and the configuration file use.</summary>
    private static HeaderDelimitedMessageHandler Handler(Stream stream) =>
        new(stream, stream, new SystemTextJsonFormatter { JsonSerializerOptions = ImpellerJson.CompactOptions });

    [Fact]
    public async Task A_snapshot_survives_the_wire_intact()
    {
        var snapshot = await Engine.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(_engine.Snapshot, snapshot);
    }

    [Fact]
    public async Task A_sensor_id_arrives_as_the_same_id()
    {
        // The whole identity model rests on this. An id that round-trips into a different value,
        // or into an object, would repoint every curve in a configuration sent over this channel.
        var snapshot = await Engine.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(FakeEngine.CpuSensor, snapshot.Sensors[0].Id);
        Assert.Equal(FakeEngine.FanControl, snapshot.Controls[0].Id);
    }

    [Fact]
    public async Task A_polymorphic_curve_keeps_its_type_across_the_wire()
    {
        var snapshot = await Engine.GetSnapshotAsync(CancellationToken.None);

        var auto = Assert.IsType<AutoCurveDefinition>(snapshot.Configuration.Curves[0]);
        Assert.Equal(75f, auto.LoadTemperature, precision: 3);
        Assert.Equal(TimeSpan.FromSeconds(2), auto.ResponseTime);
    }

    [Fact]
    public async Task A_configuration_sent_to_the_engine_arrives_unchanged()
    {
        var sent = _engine.Snapshot.Configuration with { Name = "round trip" };

        var result = await Engine.ApplyConfigurationAsync(sent, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal(sent, _engine.LastApplied);
    }

    [Fact]
    public async Task Validation_issues_come_back_with_their_severity_and_code()
    {
        var result = await Engine.LoadConfigurationAsync("broken", CancellationToken.None);

        Assert.False(result.Applied);
        Assert.True(result.Validation.HasErrors);

        var error = Assert.Single(result.Validation.Errors);
        Assert.Equal("curve-cycle", error.Code);
    }

    [Fact]
    public async Task A_refused_claim_names_what_holds_the_control_instead()
    {
        // A conflict that fails anonymously is one the user cannot act on.
        var outcome = await Engine.SetManualDutyAsync(FakeEngine.HeldControl, new Duty(50f), CancellationToken.None);

        Assert.False(outcome.Granted);
        Assert.Equal(ControlAcquireFailure.AlreadyOwned, outcome.Failure);
        Assert.Equal(ControlOwnerKind.Plugin, outcome.CurrentOwner);
        Assert.Equal("com.example.plugin", outcome.CurrentClaimantId);
    }

    [Fact]
    public async Task A_granted_claim_carries_the_duty_through()
    {
        var outcome = await Engine.SetManualDutyAsync(FakeEngine.FanControl, new Duty(42.5f), CancellationToken.None);

        Assert.True(outcome.Granted);
        Assert.Equal(42.5f, _engine.LastDuty!.Value.Percent, precision: 3);
    }

    [Fact]
    public async Task An_empty_collection_arrives_empty_rather_than_null()
    {
        var names = await Engine.ListConfigurationsAsync(CancellationToken.None);

        Assert.Equal(2, names.Count);
        Assert.Contains("Default", names);
    }

    [Fact]
    public async Task The_engine_can_push_a_tick_without_being_asked()
    {
        // The push direction is what makes a one-second tick rate affordable to display; if it
        // did not work the shell would have to poll and we would be back where we started.
        var tick = new TickSnapshot(
            42,
            DateTimeOffset.UnixEpoch,
            [new SensorReading(FakeEngine.CpuSensor, 61.5f)],
            [new ControlReading(FakeEngine.FanControl, new Duty(55f), ControlOwnerKind.Plugin, "com.example.rigfan")],
            [new CurveReading(CurveId.New(), new Duty(72f))]);

        await _engine.Events!.OnTickAsync(tick);

        var received = await _client.NextTick();

        Assert.Equal(tick, received);
    }

    [Fact]
    public async Task A_curve_that_cannot_answer_arrives_as_no_output_rather_than_zero()
    {
        // Same reasoning as a sensor that is not reporting: the editor's read-out has to be able to
        // say "no output", and a null read back as 0 would show a curve asking for a stopped fan.
        var curve = CurveId.New();

        await _engine.Events!.OnTickAsync(new TickSnapshot(
            1,
            DateTimeOffset.UnixEpoch,
            [],
            [],
            [new CurveReading(curve, null)]));

        var received = await _client.NextTick();

        Assert.Equal(curve, received.Curves[0].Id);
        Assert.Null(received.Curves[0].Output);
    }

    [Fact]
    public async Task A_tick_sent_without_curve_outputs_still_arrives()
    {
        // The field was added after the shape had shipped, and the whole point of defaulting it is
        // that neither end has to be rebuilt at the same moment as the other.
        await _engine.Events!.OnTickAsync(new TickSnapshot(1, DateTimeOffset.UnixEpoch, [], []));

        Assert.Empty((await _client.NextTick()).Curves);
    }

    [Fact]
    public async Task A_sensor_that_is_not_reporting_arrives_as_no_value_rather_than_zero()
    {
        // A missing temperature read back as 0 would idle a fan that should be ramping.
        var tick = new TickSnapshot(
            1,
            DateTimeOffset.UnixEpoch,
            [new SensorReading(FakeEngine.CpuSensor, null)],
            []);

        await _engine.Events!.OnTickAsync(tick);

        Assert.Null((await _client.NextTick()).Sensors[0].Value);
    }

    [Fact]
    public async Task A_cancelled_call_does_not_bring_the_connection_down()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Engine.GetSnapshotAsync(cancelled.Token));

        // Still usable afterwards.
        Assert.NotNull(await Engine.GetSnapshotAsync(CancellationToken.None));
    }


    [Fact]
    public async Task An_import_summary_keeps_every_note_and_its_outcome()
    {
        // The notes are the reason the import is worth doing rather than guessing, and an outcome
        // that arrives as the wrong enum value turns "this could not be resolved" into "imported".
        var summary = await Engine.ImportConfigurationAsync(@"C:\FanControl\userConfig.json", null, CancellationToken.None);

        Assert.Equal(_engine.Import, summary);
        Assert.Equal(3, summary.Notes.Count);
        Assert.Equal(ImportOutcome.Unresolved, summary.Notes[1].Outcome);
        Assert.Equal("Rig Cooling", summary.Notes[1].Subject);
    }

    [Fact]
    public async Task A_failed_import_carries_its_reason_rather_than_faulting_the_call()
    {
        // A file that is not a configuration is an ordinary thing for a user to pick, and an
        // exception across the wire would reach them as a stack trace instead of a sentence.
        var summary = await Engine.ImportConfigurationAsync(@"C:\nope.json", "Broken", CancellationToken.None);

        Assert.Equal(_engine.Import.Succeeded, summary.Succeeded);
    }

    [Fact]
    public async Task A_tuning_report_survives_the_wire_with_its_outcomes()
    {
        var report = await Engine.CalibrateAsync(
            [FakeEngine.FanControl, FakeEngine.HeldControl],
            CancellationToken.None);

        Assert.Equal(_engine.Tuning, report);

        // Cancelled and saved are independent: a run stopped after six of eight fans keeps those six.
        Assert.True(report.Cancelled);
        Assert.True(report.Saved);
    }

    [Fact]
    public async Task The_controls_named_for_a_tuning_run_arrive_as_the_same_ids()
    {
        await Engine.PairFansAsync([FakeEngine.FanControl, FakeEngine.HeldControl], CancellationToken.None);

        Assert.Equal(2, _engine.LastTuned.Count);
        Assert.Equal(FakeEngine.FanControl, _engine.LastTuned[0]);
        Assert.Equal(FakeEngine.HeldControl, _engine.LastTuned[1]);
    }

    [Fact]
    public async Task Tuning_progress_is_pushed_to_the_shell()
    {
        var progress = new TuningProgress(TuningKind.Calibration, "SteppingDown", "Fan #4 at 40% — 780 RPM", 2, 8);

        await _engine.Events!.OnTuningProgressAsync(progress);

        Assert.Equal(progress, await _client.NextTuningProgress());
    }
    [Fact]
    public async Task A_plugin_summary_survives_the_trip_to_the_shell()
    {
        // Enums by name, arrays by value, nullable identity fields present. The identity is the
        // half most worth checking: a grant bound to a path and a SID that arrived as nulls would
        // look like a whole binding on the page.
        var listed = Assert.Single(await Engine.ListPluginsAsync());

        Assert.Equal(_engine.Plugin, listed);
        Assert.Equal(PluginAdmissionState.Approved, listed.State);
        Assert.Equal([PluginCapability.ControlFans], listed.Granted);
        Assert.Equal(@"C:\Programs\Rig\Rig.exe", listed.ImagePath);
    }

    [Fact]
    public async Task An_approval_arrives_with_exactly_what_the_page_ticked()
    {
        Assert.True(await Engine.ApprovePluginAsync(
            "com.example.rigfan",
            [PluginCapability.ReadSensors, PluginCapability.ControlFans],
            [FakeEngine.FanControl, FakeEngine.HeldControl]));

        var approval = Assert.NotNull(_engine.LastApproval);

        Assert.Equal("com.example.rigfan", approval.Id);
        Assert.Equal([PluginCapability.ReadSensors, PluginCapability.ControlFans], approval.Capabilities);
        Assert.Equal([FakeEngine.FanControl, FakeEngine.HeldControl], approval.Controls);
    }

    [Fact]
    public async Task A_rename_arrives_with_the_name_the_user_typed()
    {
        Assert.True(await Engine.RenameAsync(FakeEngine.FanControl, "Seat blower"));

        var rename = Assert.NotNull(_engine.LastRename);

        Assert.Equal(FakeEngine.FanControl, rename.Id);
        Assert.Equal("Seat blower", rename.Name);
    }

    [Fact]
    public async Task Clearing_a_name_crosses_the_wire_as_null_rather_than_as_an_empty_string()
    {
        // Null is the removal. An empty string arriving as "" would be a name of no characters,
        // and the difference decides whether the fan goes back to being called "Fan #4".
        await Engine.RenameAsync(FakeEngine.FanControl, null);

        Assert.Null(Assert.NotNull(_engine.LastRename).Name);
    }

    [Fact]
    public async Task A_display_name_survives_the_trip_to_the_shell()
    {
        var snapshot = await Engine.GetSnapshotAsync();

        // Both halves travel: what the user calls it, and what the hardware does - the second being
        // the only route back once the first has been set.
        Assert.Equal("Seat blower", snapshot.Controls[0].DisplayName);
        Assert.Equal("Fan #4", snapshot.Controls[0].Name);
        Assert.Equal("CPU Package", snapshot.Sensors[0].DisplayName);
    }

    [Fact]
    public async Task A_change_to_the_plugins_reaches_the_shell()
    {
        await _engine.Events!.OnPluginsChangedAsync();

        await _client.NextPluginsChanged();
    }

    /// <summary>A stand-in engine holding one of everything the contracts can carry.</summary>
    private sealed class FakeEngine : IEngineControl
    {
        public static readonly SensorId CpuSensor = SensorId.New();
        public static readonly SensorId FanControl = SensorId.New();
        public static readonly SensorId HeldControl = SensorId.New();

        private static readonly CurveId AutoCurve = CurveId.New();

        public const string Version = "0.1.0";

        public IEngineEvents? Events { get; set; }

        /// <summary>The last hello this engine was sent, so a test can assert what crossed the wire.</summary>
        public ShellHello? Greeted { get; private set; }

        /// <inheritdoc />
        public Task<EngineHandshake> HelloAsync(
            ShellHello hello,
            CancellationToken cancellationToken = default)
        {
            Greeted = hello;

            return Task.FromResult(
                EngineHandshakeCheck.TryAccept(hello, Version, out var refusal)
                    ? EngineHandshakeCheck.Accept(Version)
                    : refusal);
        }

        /// <summary>A report with one of everything, so the round-trip has something to lose.</summary>
        public DiagnosticReport Report { get; } = new()
        {
            Taken = DateTimeOffset.UnixEpoch,
            Status = new EngineStatus("0.1.0", 99, DateTimeOffset.UnixEpoch, true, @"C:\ProgramData\Impeller"),
            OperatingSystem = "Windows 11",
            Runtime = ".NET 10.0",
            Architecture = "X64",
            RunningAsService = true,
            Identity = @"NT AUTHORITY\SYSTEM",
            LogRoot = @"C:\ProgramData\Impeller\Logs",
            Providers =
            [
                new ProviderDiagnostics("lhm", "LibreHardwareMonitor", true, 193, 9, ["GPU"], null),
                new ProviderDiagnostics("adlx", "AMD", false, 0, 0, [], "not installed"),
            ],
            ConfigurationName = "Default",
            RecentLog = ["line one", "line two"],
        };

        public ImpellerConfiguration? LastApplied { get; private set; }

        public Duty? LastDuty { get; private set; }

        /// <summary>An import report with one note of each outcome, so nothing survives by accident.</summary>
        public ImportSummary Import { get; } = new(
            true,
            "userConfig (2)",
            null,
            [
                new ImportNote(ImportOutcome.Adjusted, "Auto CPU", "Hysteresis was widened to match."),
                new ImportNote(ImportOutcome.Unresolved, "Rig Cooling", "No control here matches."),
                new ImportNote(ImportOutcome.Skipped, "Graph 1", "Its sensor could not be read."),
            ],
            4,
            9,
            1,
            ConfigurationValidation.Clean);

        /// <summary>A finished calibration, with one fan that worked and one that did not.</summary>
        public TuningReport Tuning { get; } = new(
            TuningKind.Calibration,
            [
                new TuningOutcome(FanControl, "Fan #4", true, "11 points measured."),
                new TuningOutcome(HeldControl, "Fan #5", false, "No tacho is paired with this control."),
            ],
            Cancelled: true,
            Saved: true);

        /// <summary>Which controls the last tuning call named.</summary>
        public EquatableArray<SensorId> LastTuned { get; private set; }

        public EngineSnapshot Snapshot { get; } = new(
            new EngineStatus("0.1.0", 1234, DateTimeOffset.UnixEpoch, false, @"C:\ProgramData\Impeller"),
            [
                new SensorDescriptor(
                    CpuSensor, "CPU Package", "CPU Package", "AMD Ryzen 7 9800X3D",
                    SensorKind.Temperature, "lhm", "lhm/amdcpu/0/temperature/2", 61.5f),
            ],
            [
                new ControlDescriptor(
                    FanControl, "Fan #4", "Seat blower", "Nuvoton NCT6687D", "lhm",
                    "lhm/lpc/nct6687d/0/control/4",
                    new Duty(55f), false, ControlOwnerKind.Curve, null, Claimable: true),
            ],
            "Default",
            new ImpellerConfiguration
            {
                Name = "Default",
                Curves =
                [
                    new AutoCurveDefinition
                    {
                        Id = AutoCurve,
                        Name = "Auto CPU",
                        Source = CpuSensor,
                        IdleTemperature = 35f,
                        LoadTemperature = 75f,
                        MinimumDuty = new Duty(30f),
                        Step = 2f,
                        Deadband = 3f,
                        ResponseTime = TimeSpan.FromSeconds(2),
                    },
                ],
                Controls =
                [
                    new ControlBindingDefinition
                    {
                        ControlId = FanControl,
                        CurveId = AutoCurve,
                        Enabled = true,
                        StartDuty = new Duty(13f),
                        StopDuty = new Duty(7f),
                        Calibration = [new CalibrationPointDefinition(new Duty(50f), 1208)],
                    },
                ],
                CustomSensors =
                [
                    new CustomSensorDefinition
                    {
                        Id = SensorId.New(),
                        Name = "CPU GPU Mix",
                        Kind = CustomSensorKind.Mix,
                        Function = MixFunction.Maximum,
                        Sources = [CpuSensor],
                    },
                ],
            },
            ConfigurationValidation.Clean,
            ["Default", "Quiet"]);

        public Task<EngineSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Snapshot);
        }

        public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot.Status);

        public Task<ConfigurationResult> ApplyConfigurationAsync(
            ImpellerConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            LastApplied = configuration;
            return Task.FromResult(new ConfigurationResult(true, configuration.Name, ConfigurationValidation.Clean));
        }

        public Task<ConfigurationValidation> ValidateConfigurationAsync(
            ImpellerConfiguration configuration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ConfigurationValidation.Clean);

        public Task<EquatableArray<string>> ListConfigurationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot.AvailableConfigurations);

        public Task<ConfigurationResult> LoadConfigurationAsync(
            string name,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConfigurationResult(
                false,
                "Default",
                new ConfigurationValidation(
                [
                    new ConfigurationIssue(
                        ConfigurationSeverity.Error,
                        "curve-cycle",
                        "These curves depend on each other in a loop."),
                ])));

        public Task<bool> DeleteConfigurationAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<ControlAcquireOutcome> SetManualDutyAsync(
            SensorId controlId,
            Duty duty,
            CancellationToken cancellationToken = default)
        {
            if (controlId == HeldControl)
            {
                return Task.FromResult(new ControlAcquireOutcome(
                    false,
                    ControlAcquireFailure.AlreadyOwned,
                    ControlOwnerKind.Plugin,
                    "com.example.plugin"));
            }

            LastDuty = duty;
            return Task.FromResult(new ControlAcquireOutcome(
                true, default, ControlOwnerKind.ManualOverride, "shell"));
        }

        public Task<bool> ReleaseControlAsync(SensorId controlId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<DiagnosticReport> GetDiagnosticReportAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Report);
        }

        public Task<ControlAcquireOutcome> IdentifyControlAsync(
            SensorId controlId,
            Duty duty,
            TimeSpan duration,
            CancellationToken cancellationToken = default) =>
            SetManualDutyAsync(controlId, duty, cancellationToken);

        public Task<ImportSummary> ImportConfigurationAsync(
            string path,
            string? name = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Import);

        public Task<TuningReport> CalibrateAsync(
            EquatableArray<SensorId> controlIds,
            CancellationToken cancellationToken = default)
        {
            LastTuned = controlIds;
            return Task.FromResult(Tuning);
        }

        public Task<TuningReport> PairFansAsync(
            EquatableArray<SensorId> controlIds,
            CancellationToken cancellationToken = default)
        {
            LastTuned = controlIds;
            return Task.FromResult(Tuning);
        }

        public Task<bool> CancelTuningAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        /// <summary>One plugin with a bit of everything, so the round trip has something to lose.</summary>
        public PluginSummary Plugin { get; } = new(
            "com.example.rigfan",
            "Rig Fan Control",
            "2.1.0",
            PluginAdmissionState.Approved,
            Enabled: true,
            Connected: true,
            Requested: [PluginCapability.ReadSensors, PluginCapability.ControlFans],
            Granted: [PluginCapability.ControlFans],
            Controls: [FanControl],
            ImagePath: @"C:\Programs\Rig\Rig.exe",
            UserSid: "S-1-5-21-1-2-3-1001",
            IdentityChanged: false,
            FirstSeenAt: DateTimeOffset.UnixEpoch,
            LastSeenAt: DateTimeOffset.UnixEpoch.AddDays(2));

        /// <summary>What the last approve call carried, so the arguments can be checked too.</summary>
        public (string Id, EquatableArray<PluginCapability> Capabilities, EquatableArray<SensorId> Controls)?
            LastApproval { get; private set; }

        public Task<EquatableArray<PluginSummary>> ListPluginsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EquatableArray<PluginSummary>>([Plugin]);

        public Task<bool> ApprovePluginAsync(
            string pluginId,
            EquatableArray<PluginCapability> capabilities,
            EquatableArray<SensorId> controls,
            CancellationToken cancellationToken = default)
        {
            LastApproval = (pluginId, capabilities, controls);
            return Task.FromResult(true);
        }

        public Task<bool> GrantPluginControlAsync(
            string pluginId,
            SensorId controlId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> RevokePluginControlAsync(
            string pluginId,
            SensorId controlId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> SetPluginEnabledAsync(
            string pluginId,
            bool enabled,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> ForgetPluginAsync(
            string pluginId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        /// <summary>What the last rename carried, so the arguments can be checked too.</summary>
        public (SensorId Id, string? Name)? LastRename { get; private set; }

        public Task<bool> RenameAsync(
            SensorId sensorId,
            string? name,
            CancellationToken cancellationToken = default)
        {
            LastRename = (sensorId, name);
            return Task.FromResult(true);
        }
    }

    /// <summary>A shell that records what the engine pushed at it.</summary>
    private sealed class RecordingClient : IEngineEvents
    {
        private readonly TaskCompletionSource<TickSnapshot> _tick =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TickSnapshot> NextTick() => _tick.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public Task OnTickAsync(TickSnapshot snapshot)
        {
            _tick.TrySetResult(snapshot);
            return Task.CompletedTask;
        }

        public Task OnConfigurationChangedAsync(ConfigurationResult result) => Task.CompletedTask;

        public Task OnHardwareChangedAsync() => Task.CompletedTask;

        private readonly TaskCompletionSource<TuningProgress> _tuning =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TuningProgress> NextTuningProgress() => _tuning.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public Task OnTuningProgressAsync(TuningProgress progress)
        {
            _tuning.TrySetResult(progress);
            return Task.CompletedTask;
        }

        private readonly TaskCompletionSource _plugins =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task NextPluginsChanged() => _plugins.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public Task OnPluginsChangedAsync()
        {
            _plugins.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
