using System.Reflection;
using System.Text.Json;
using Impeller.Plugins.Abstractions;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Impeller.Core.Engine.Tests.Ipc;

/// <summary>
/// Drives the plugin contracts over a real JSON-RPC connection, on an in-memory duplex stream.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="EngineContractsTests"/>, and for the same reason: the pipe is the
/// least interesting part of the channel and the most annoying to test, while the wire format is
/// what actually breaks. Here it matters more than it does for the shell, because both ends of this
/// channel are eventually built by different people against different versions.
/// </para>
/// </remarks>
public sealed class PluginContractsTests : IAsyncDisposable
{
    private readonly (Stream Client, Stream Server) _streams = FullDuplexStream.CreatePair();
    private readonly FakeHost _host = new();
    private readonly RecordingPlugin _plugin = new();
    private readonly JsonRpc _serverRpc;
    private readonly JsonRpc _clientRpc;

    public PluginContractsTests()
    {
        _serverRpc = new JsonRpc(Handler(_streams.Server));
        _serverRpc.AddLocalRpcTarget<IPluginHost>(_host, null);
        _host.Client = _serverRpc.Attach<IPluginClient>();
        _serverRpc.StartListening();

        _clientRpc = new JsonRpc(Handler(_streams.Client));
        _clientRpc.AddLocalRpcTarget<IPluginClient>(_plugin, null);
        Engine = _clientRpc.Attach<IPluginHost>();
        _clientRpc.StartListening();
    }

    /// <summary>The engine as a plugin sees it: a proxy, over a real connection.</summary>
    private IPluginHost Engine { get; }

    public async ValueTask DisposeAsync()
    {
        _clientRpc.Dispose();
        _serverRpc.Dispose();
        await _streams.Client.DisposeAsync();
        await _streams.Server.DisposeAsync();
    }

    private static HeaderDelimitedMessageHandler Handler(Stream stream) =>
        new(stream, stream, new SystemTextJsonFormatter { JsonSerializerOptions = PluginJson.Options });

    [Fact]
    public void The_plugin_surface_does_not_drag_the_engine_along_with_it()
    {
        // The one decision in this milestone that is expensive to undo. If Plugins.Abstractions
        // references Core.Abstractions, then ImpellerConfiguration, IFanCurve and CalibrationTable
        // are in the permanent dependency surface of every third-party plugin — and by the time
        // anyone notices, plugins exist that would break when it is removed.
        var referenced = typeof(SensorRef).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToList();

        Assert.DoesNotContain("Impeller.Core.Abstractions", referenced);
        Assert.DoesNotContain(referenced, name => name?.StartsWith("Impeller.", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Nothing_in_the_plugin_surface_mentions_a_type_from_another_Impeller_assembly()
    {
        // The reference check above passes vacuously if a reference exists but no type from it is
        // used. This is the check that actually holds the line: whatever a plugin author can see,
        // they can see without referencing anything else of ours.
        var assembly = typeof(SensorRef).Assembly;

        var leaked = assembly.GetExportedTypes()
            .SelectMany(SignatureTypes)
            .Where(type => type.Assembly != assembly)
            .Where(type => type.Assembly.GetName().Name?.StartsWith("Impeller.", StringComparison.Ordinal) == true)
            .Select(type => type.FullName)
            .Distinct()
            .ToList();

        Assert.Empty(leaked);
    }

    [Fact]
    public async Task A_manifest_survives_the_wire_with_everything_it_asked_for()
    {
        var manifest = new PluginManifest(
            "com.occamzchainsaw.rigfan",
            "RigFanControl",
            "2.0.0",
            PluginProtocol.CurrentVersion,
            [PluginCapability.ReadSensors, PluginCapability.ControlFans]);

        await Engine.HelloAsync(manifest, CancellationToken.None);

        Assert.NotNull(_host.LastManifest);
        Assert.Equal(manifest.Id, _host.LastManifest.Id);
        Assert.Equal(manifest.DisplayName, _host.LastManifest.DisplayName);
        Assert.Equal(manifest.ProtocolVersion, _host.LastManifest.ProtocolVersion);
        Assert.Equal(manifest.Requests, _host.LastManifest.Requests);
    }

    [Fact]
    public async Task A_plugin_the_engine_has_never_seen_is_pending_rather_than_refused()
    {
        // Pending is a success. A plugin that treats it as a failure and exits never gives the user
        // the chance to approve it, because there is nothing left in the list to approve.
        var admission = await Engine.HelloAsync(Manifest("com.example.newcomer"), CancellationToken.None);

        Assert.Equal(PluginAdmissionState.Pending, admission.State);
        Assert.True(admission.IsAdmitted);
        Assert.Equal(PluginRefusal.None, admission.Refusal);
        Assert.Empty(admission.Granted);
        Assert.Empty(admission.Controls);
    }

    [Fact]
    public async Task An_admission_carries_the_specific_fans_that_were_granted()
    {
        // Per-fan is the whole point of the grant list. A capability that arrived as "may control
        // fans" with no list would be a different, much larger permission than the user gave.
        var admission = await Engine.HelloAsync(Manifest(FakeHost.ApprovedId), CancellationToken.None);

        Assert.Equal(PluginAdmissionState.Approved, admission.State);
        Assert.Equal([PluginCapability.ReadSensors, PluginCapability.ControlFans], admission.Granted);
        Assert.Equal([FakeHost.GrantedFan], admission.Controls);
        Assert.True(admission.MayControl(FakeHost.GrantedFan));
        Assert.False(admission.MayControl(FakeHost.OtherFan));
    }

    [Fact]
    public async Task A_sensor_reference_arrives_as_the_same_reference()
    {
        // Everything a plugin saves rests on this. A reference that round-trips into a different
        // value, or into an object, would point a plugin at a different fan after a restart.
        var snapshot = await Engine.GetSensorsAsync(CancellationToken.None);

        Assert.Equal(FakeHost.CpuSensor, snapshot.Sensors[0].Id);
        Assert.Equal(FakeHost.GrantedFan, snapshot.Controls[0].Id);
    }

    [Fact]
    public void A_sensor_reference_serialises_as_a_bare_string()
    {
        // Wire-identical to the engine's own SensorId is what keeps the host's translation layer
        // down to a cast over a Guid. The moment this becomes an object, it stops being that.
        var reference = new SensorRef(Guid.Parse("2fb3a1d0-9c47-4f2a-8a1e-0d5b6c7e8f90"));

        var json = JsonSerializer.Serialize(reference, PluginJson.Options);

        Assert.Equal("\"2fb3a1d0-9c47-4f2a-8a1e-0d5b6c7e8f90\"", json);
        Assert.Equal(reference, JsonSerializer.Deserialize<SensorRef>(json, PluginJson.Options));
    }

    [Fact]
    public void Enums_travel_by_name_rather_than_by_number()
    {
        // Both ends of this channel are eventually built by different people against different
        // versions. By-number means a value inserted into the middle of an enum turns one plugin's
        // Warning into another's Error, silently, forever.
        var json = JsonSerializer.Serialize(ControlLostReason.LeaseExpired, PluginJson.Options);

        Assert.Equal("\"LeaseExpired\"", json);
    }

    [Fact]
    public async Task A_sensor_that_is_not_reporting_arrives_as_no_value_rather_than_zero()
    {
        var snapshot = await Engine.GetSensorsAsync(CancellationToken.None);

        Assert.Null(snapshot.Sensors[1].Value);
    }

    [Fact]
    public async Task A_subscription_arrives_as_the_ids_that_were_asked_for()
    {
        await Engine.SubscribeAsync([FakeHost.CpuSensor, FakeHost.QuietSensor], CancellationToken.None);

        Assert.Equal([FakeHost.CpuSensor, FakeHost.QuietSensor], _host.Subscribed);
    }

    [Fact]
    public async Task An_empty_subscription_arrives_empty_rather_than_null()
    {
        // "Stop sending me readings" is a legitimate thing to say, and a null here would be an
        // unhandled exception on the engine's side of a call a plugin is entitled to make.
        await Engine.SubscribeAsync([], CancellationToken.None);

        Assert.NotNull(_host.Subscribed);
        Assert.Empty(_host.Subscribed);
    }

    [Fact]
    public async Task A_lease_survives_the_wire_as_the_duration_that_was_asked_for()
    {
        await Engine.AcquireAsync(FakeHost.GrantedFan, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(5), _host.LastLease);
    }

    [Fact]
    public async Task No_lease_means_no_lease_rather_than_an_instant_one()
    {
        // A null that arrived as TimeSpan.Zero would expire the claim on the very next tick, and
        // the symptom would be a slider that snaps back for no visible reason.
        await Engine.AcquireAsync(FakeHost.GrantedFan, null, CancellationToken.None);

        Assert.Null(_host.LastLease);
    }

    [Fact]
    public async Task A_refused_claim_names_what_holds_the_control_instead()
    {
        var outcome = await Engine.AcquireAsync(FakeHost.OtherFan, null, CancellationToken.None);

        Assert.False(outcome.Granted);
        Assert.Equal(PluginAcquireFailure.AlreadyOwned, outcome.Failure);
        Assert.Equal(ControlHolder.User, outcome.Holder);
        Assert.Equal("shell", outcome.HolderId);
    }

    [Fact]
    public async Task A_claim_on_a_control_the_engine_is_not_driving_is_refused_rather_than_ignored()
    {
        // The defect this value exists for: the tick loop skips a disabled binding before it looks
        // at ownership, so without a refusal the claim succeeds, the duties are accepted, and every
        // one of them is silently discarded.
        var outcome = await Engine.AcquireAsync(FakeHost.UndrivenFan, null, CancellationToken.None);

        Assert.False(outcome.Granted);
        Assert.Equal(PluginAcquireFailure.NotDriven, outcome.Failure);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Message));
    }

    [Fact]
    public async Task A_duty_arrives_as_the_number_that_was_asked_for()
    {
        Assert.True(await Engine.SetDutyAsync(FakeHost.GrantedFan, 42.5f, CancellationToken.None));

        Assert.Equal(42.5f, _host.LastDuty, precision: 3);
    }

    [Fact]
    public async Task A_tick_of_readings_reaches_a_plugin_without_it_asking()
    {
        // The push direction is what lets a plugin stop polling hardware of its own — which is the
        // entire reason a plugin should route through the engine rather than around it.
        var readings = new PluginReadings(
            42,
            DateTimeOffset.UnixEpoch,
            [new SensorSample(FakeHost.CpuSensor, 61.5f), new SensorSample(FakeHost.QuietSensor, null)],
            [new ControlSample(FakeHost.GrantedFan, 30f, 45f, ControlHolder.Plugin, "com.example.approved")]);

        await _host.Client!.OnReadingsAsync(readings);

        var received = await _plugin.NextReadings();

        Assert.Equal(42, received.Tick);
        Assert.Equal(readings.Sensors, received.Sensors);
        Assert.Equal(readings.Controls, received.Controls);
    }

    [Fact]
    public async Task Requested_and_commanded_duties_arrive_as_two_different_numbers()
    {
        // They genuinely differ — limits, avoided bands and the slew limiter all sit between them —
        // and a plugin UI that showed only one of them would lie to its user.
        var readings = new PluginReadings(
            1,
            DateTimeOffset.UnixEpoch,
            [],
            [new ControlSample(FakeHost.GrantedFan, 30f, 45f, ControlHolder.Plugin, "p")]);

        await _host.Client!.OnReadingsAsync(readings);

        var control = (await _plugin.NextReadings()).Controls[0];

        Assert.Equal(30f, control.RequestedDuty!.Value, precision: 3);
        Assert.Equal(45f, control.CommandedDuty!.Value, precision: 3);
    }

    [Fact]
    public async Task Losing_a_control_tells_the_plugin_which_one_and_why()
    {
        // Without the reason, a plugin cannot tell "the user took this fan back" from "the engine
        // is in trouble and took everything", and those call for opposite behaviour.
        await _host.Client!.OnControlLostAsync(FakeHost.GrantedFan, ControlLostReason.Failsafe);

        var (control, reason) = await _plugin.NextLoss();

        Assert.Equal(FakeHost.GrantedFan, control);
        Assert.Equal(ControlLostReason.Failsafe, reason);
    }

    [Fact]
    public async Task A_change_of_permissions_reaches_a_plugin_that_is_already_connected()
    {
        // The admission from the handshake goes stale the moment someone opens the Plugins page.
        var admission = new PluginAdmission(
            PluginAdmissionState.Approved,
            [PluginCapability.ReadSensors],
            [],
            PluginRefusal.None,
            "The fan was revoked.",
            "0.1.0",
            PluginProtocol.CurrentVersion);

        await _host.Client!.OnAdmissionChangedAsync(admission);

        var received = await _plugin.NextAdmission();

        Assert.Equal(PluginAdmissionState.Approved, received.State);
        Assert.Empty(received.Controls);
        Assert.False(received.MayControl(FakeHost.GrantedFan));
    }

    [Fact]
    public async Task The_engine_can_ask_a_plugin_whether_it_is_still_there()
    {
        Assert.True(await _host.Client!.PingAsync());
    }

    [Fact]
    public async Task A_hardware_declaration_is_answered_as_unbuilt_rather_than_as_refused()
    {
        // An author who is told "refused" goes looking for a permission to ask the user for, and
        // there is no such permission to find. This is what makes a shipped-but-inert contract
        // honest rather than a trap.
        var admission = await Engine.DeclareHardwareAsync(
            [
                new HardwareDeclaration(
                    "pump-0",
                    "Custom Loop",
                    [new DeclaredSensor("temp", "Coolant", PluginSensorKind.Temperature)],
                    [new DeclaredControl("pwm", "Pump", false, 100f)]),
            ],
            CancellationToken.None);

        Assert.False(admission.Accepted);
        Assert.Equal(ProviderOutcome.NotImplementedInThisBuild, admission.Outcome);
        Assert.Null(admission.ProviderId);
        Assert.Contains("does not implement", admission.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_declared_control_carries_the_duty_the_plugin_will_failsafe_to_itself()
    {
        // The one field that had to be settled now: the engine cannot failsafe hardware it reaches
        // only by messaging the process that may be why it is failsafing.
        await Engine.DeclareHardwareAsync(
            [new HardwareDeclaration("pump-0", "Custom Loop", [], [new DeclaredControl("pwm", "Pump", false, 80f)])],
            CancellationToken.None);

        Assert.Equal(80f, _host.LastDeclared!.Controls[0].FailsafeDuty, precision: 3);
    }

    [Fact]
    public async Task A_cancelled_call_does_not_bring_the_connection_down()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Engine.GetSensorsAsync(cancelled.Token));

        Assert.NotNull(await Engine.GetSensorsAsync(CancellationToken.None));
    }

    private static PluginManifest Manifest(string id) => new(
        id,
        "Test Plugin",
        "1.0.0",
        PluginProtocol.CurrentVersion,
        [PluginCapability.ReadSensors, PluginCapability.ControlFans]);

    /// <summary>Every type named in a type's public surface, for the leakage check.</summary>
    private static IEnumerable<Type> SignatureTypes(Type type)
    {
        const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.DeclaredOnly;

        foreach (var method in type.GetMethods(Public))
        {
            foreach (var part in Unwrap(method.ReturnType))
            {
                yield return part;
            }

            foreach (var part in method.GetParameters().SelectMany(parameter => Unwrap(parameter.ParameterType)))
            {
                yield return part;
            }
        }

        foreach (var part in type.GetProperties(Public).SelectMany(property => Unwrap(property.PropertyType)))
        {
            yield return part;
        }

        foreach (var part in type.GetFields(Public).SelectMany(field => Unwrap(field.FieldType)))
        {
            yield return part;
        }
    }

    /// <summary>A type and, for a generic one, everything inside it.</summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;

        if (!type.IsGenericType)
        {
            yield break;
        }

        foreach (var argument in type.GetGenericArguments().SelectMany(Unwrap))
        {
            yield return argument;
        }
    }

    /// <summary>An engine holding one of everything the plugin contracts can carry.</summary>
    private sealed class FakeHost : IPluginHost
    {
        public const string ApprovedId = "com.example.approved";

        public static readonly SensorRef CpuSensor = new(Guid.NewGuid());
        public static readonly SensorRef QuietSensor = new(Guid.NewGuid());
        public static readonly SensorRef GrantedFan = new(Guid.NewGuid());
        public static readonly SensorRef OtherFan = new(Guid.NewGuid());
        public static readonly SensorRef UndrivenFan = new(Guid.NewGuid());

        public IPluginClient? Client { get; set; }

        public PluginManifest? LastManifest { get; private set; }

        public IReadOnlyList<SensorRef>? Subscribed { get; private set; }

        public TimeSpan? LastLease { get; private set; }

        public float LastDuty { get; private set; }

        public HardwareDeclaration? LastDeclared { get; private set; }

        public Task<PluginAdmission> HelloAsync(
            PluginManifest manifest,
            CancellationToken cancellationToken = default)
        {
            LastManifest = manifest;

            return Task.FromResult(manifest.Id == ApprovedId
                ? new PluginAdmission(
                    PluginAdmissionState.Approved,
                    [PluginCapability.ReadSensors, PluginCapability.ControlFans],
                    [GrantedFan],
                    PluginRefusal.None,
                    "Approved.",
                    "0.1.0",
                    PluginProtocol.CurrentVersion)
                : new PluginAdmission(
                    PluginAdmissionState.Pending,
                    [],
                    [],
                    PluginRefusal.None,
                    "Waiting for approval in the Impeller window.",
                    "0.1.0",
                    PluginProtocol.CurrentVersion));
        }

        public Task<PluginSnapshot> GetSensorsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(new PluginSnapshot(
                "0.1.0",
                false,
                [
                    new SensorInfo(
                        CpuSensor, "CPU Package", "AMD Ryzen 7 9800X3D", PluginSensorKind.Temperature,
                        "lhm", "lhm/amdcpu/0/temperature/2", 61.5f),
                    new SensorInfo(
                        QuietSensor, "Fan #7", "Nuvoton NCT6687D", PluginSensorKind.FanSpeed,
                        "lhm", "lhm/lpc/nct6687d/0/fan/7", null),
                ],
                [
                    // One with a tachometer paired to it and one without, because the pairing is a
                    // value a plugin cannot work out for itself and an absent one has to be
                    // distinguishable from a present one.
                    new ControlInfo(
                        GrantedFan, "Fan #4", "Nuvoton NCT6687D", "lhm",
                        "lhm/lpc/nct6687d/0/control/4",
                        QuietSensor, 30f, 45f, ControlHolder.Curve, null, true, true),
                    new ControlInfo(
                        UndrivenFan, "Fan #5", "Nuvoton NCT6687D", "lhm",
                        "lhm/lpc/nct6687d/0/control/5",
                        SensorRef.None, null, null, ControlHolder.Curve, null, true, false),
                ]));
        }

        public Task SubscribeAsync(
            IReadOnlyList<SensorRef> sensors,
            CancellationToken cancellationToken = default)
        {
            Subscribed = sensors;
            return Task.CompletedTask;
        }

        public Task<AcquireOutcome> AcquireAsync(
            SensorRef control,
            TimeSpan? maxSilence = null,
            CancellationToken cancellationToken = default)
        {
            LastLease = maxSilence;

            if (control == OtherFan)
            {
                return Task.FromResult(AcquireOutcome.No(
                    PluginAcquireFailure.AlreadyOwned,
                    "Held by the user.",
                    ControlHolder.User,
                    "shell"));
            }

            if (control == UndrivenFan)
            {
                return Task.FromResult(AcquireOutcome.No(
                    PluginAcquireFailure.NotDriven,
                    "Impeller is not driving this fan. Enable it and give it a curve first."));
            }

            return Task.FromResult(AcquireOutcome.Ok(ApprovedId));
        }

        public Task<bool> SetDutyAsync(
            SensorRef control,
            float percent,
            CancellationToken cancellationToken = default)
        {
            LastDuty = percent;
            return Task.FromResult(true);
        }

        public Task<bool> ReleaseAsync(SensorRef control, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task LogAsync(
            PluginLogLevel level,
            string message,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<HardwareAdmission> DeclareHardwareAsync(
            IReadOnlyList<HardwareDeclaration> hardware,
            CancellationToken cancellationToken = default)
        {
            LastDeclared = hardware[0];
            return Task.FromResult(HardwareAdmission.NotImplemented());
        }

        public Task PushReadingsAsync(
            IReadOnlyList<ProvidedReading> readings,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>A plugin that records what the engine pushed at it.</summary>
    private sealed class RecordingPlugin : IPluginClient
    {
        private readonly TaskCompletionSource<PluginReadings> _readings =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<(SensorRef Control, ControlLostReason Reason)> _loss =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<PluginAdmission> _admission =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PluginReadings> NextReadings() => _readings.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public Task<(SensorRef Control, ControlLostReason Reason)> NextLoss() =>
            _loss.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public Task<PluginAdmission> NextAdmission() => _admission.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public Task OnReadingsAsync(PluginReadings readings)
        {
            _readings.TrySetResult(readings);
            return Task.CompletedTask;
        }

        public Task OnControlLostAsync(SensorRef control, ControlLostReason reason)
        {
            _loss.TrySetResult((control, reason));
            return Task.CompletedTask;
        }

        public Task OnAdmissionChangedAsync(PluginAdmission admission)
        {
            _admission.TrySetResult(admission);
            return Task.CompletedTask;
        }

        public Task OnEngineStoppingAsync() => Task.CompletedTask;

        public Task<bool> PingAsync() => Task.FromResult(true);

        public Task OnWriteRequestedAsync(SensorRef control, float percent) => Task.CompletedTask;
    }
}
