using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Ipc.Contracts;
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
            [new ControlReading(FakeEngine.FanControl, new Duty(55f), ControlOwnerKind.Curve)]);

        await _engine.Events!.OnTickAsync(tick);

        var received = await _client.NextTick();

        Assert.Equal(tick, received);
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

    /// <summary>A stand-in engine holding one of everything the contracts can carry.</summary>
    private sealed class FakeEngine : IEngineControl
    {
        public static readonly SensorId CpuSensor = SensorId.New();
        public static readonly SensorId FanControl = SensorId.New();
        public static readonly SensorId HeldControl = SensorId.New();

        private static readonly CurveId AutoCurve = CurveId.New();

        public IEngineEvents? Events { get; set; }

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

        public EngineSnapshot Snapshot { get; } = new(
            new EngineStatus("0.1.0", 1234, DateTimeOffset.UnixEpoch, false, @"C:\ProgramData\Impeller"),
            [
                new SensorDescriptor(
                    CpuSensor, "CPU Package", SensorKind.Temperature, "lhm", "lhm/amdcpu/0/temperature/2", 61.5f),
            ],
            [
                new ControlDescriptor(
                    FanControl, "Fan #4", "lhm", "lhm/lpc/nct6687d/0/control/4",
                    new Duty(55f), false, ControlOwnerKind.Curve, null),
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
    }
}
