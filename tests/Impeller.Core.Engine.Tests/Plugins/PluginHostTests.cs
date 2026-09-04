using Impeller.Core.Abstractions;
using Impeller.Core.Engine.Curves;
using Impeller.Core.Persistence;
using Impeller.Plugins.Abstractions;
using Impeller.Plugins.Host;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Impeller.Core.Engine.Tests.Plugins;

/// <summary>
/// Drives a plugin through its whole life against a real engine, headlessly.
/// </summary>
/// <remarks>
/// <para>
/// A real <see cref="ControlLoop"/>, a real <see cref="ControlOwnershipRegistry"/>, a real
/// <see cref="PluginRegistry"/> over a real file, and a real JSON-RPC connection over an in-memory
/// duplex stream. Only the hardware and the named pipe are faked, and neither is where this can go
/// wrong.
/// </para>
/// <para>
/// The assertion that matters most is the same one in almost every test below: <em>where is the
/// fan</em>. A plugin refused, a plugin evicted, a plugin killed, a plugin that went quiet — every
/// one of them has to end with the fan back on its curve, because the alternative is a fan parked
/// at a duty nobody is maintaining.
/// </para>
/// </remarks>
public sealed class PluginHostTests
{
    private const string PluginId = "com.example.rig";
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private static readonly PluginManifest Manifest = new(
        PluginId,
        "Rig Fan Control",
        "2.0.0",
        PluginProtocol.CurrentVersion,
        [PluginCapability.ReadSensors, PluginCapability.ControlFans]);

    // ---- pending and permission ------------------------------------------------------------

    [Fact]
    public async Task A_plugin_the_engine_has_never_seen_is_admitted_pending_and_granted_nothing()
    {
        // Pending is a success, not a failure. A plugin that treats it as an error and exits leaves
        // nothing in the list for the user to approve, which is a deadlock by politeness.
        await using var rig = new Rig();
        await using var plug = rig.Connect();

        var admission = await plug.Engine.HelloAsync(Manifest);

        Assert.True(admission.IsAdmitted);
        Assert.Equal(PluginAdmissionState.Pending, admission.State);
        Assert.Empty(admission.Granted);
        Assert.Empty(admission.Controls);
    }

    [Fact]
    public async Task Nothing_but_hello_works_before_the_handshake()
    {
        await using var rig = new Rig();
        await using var plug = rig.Connect();

        var outcome = await plug.Engine.AcquireAsync(rig.FanRef);

        Assert.False(outcome.Granted);
        Assert.Equal(PluginAcquireFailure.NotAdmitted, outcome.Failure);
        Assert.False(await plug.Engine.SetDutyAsync(rig.FanRef, 80f));
        Assert.Empty((await plug.Engine.GetSensorsAsync()).Controls);
    }

    [Fact]
    public async Task A_connection_that_never_says_hello_is_dropped()
    {
        // Without this, any local process can open connections and never speak until the pipe's
        // instance limit is reached, and the only symptom is that plugins stop being able to
        // connect.
        await using var rig = new Rig(new PluginHostOptions
        {
            HandshakeDeadline = TimeSpan.FromMilliseconds(50),
        });

        await using var plug = rig.Connect();

        await Until(() => plug.Session.Completion.IsCompleted, "the silent connection to be dropped");
    }

    [Fact]
    public async Task A_connection_beyond_the_silent_limit_is_refused_rather_than_evicting_one()
    {
        // The newcomer loses, not the incumbent: evicting the oldest would let anyone flush out
        // connections that are merely slow to start, which is the same denial of service by
        // another door.
        await using var rig = new Rig(new PluginHostOptions
        {
            HandshakeDeadline = TimeSpan.FromMinutes(5),
            MaxUnannouncedConnections = 2,
        });

        await using var first = rig.Connect();
        await using var second = rig.Connect();

        var pair = FullDuplexStream.CreatePair();

        Assert.Null(rig.Host.Accept(pair.Item1, Identity));
        Assert.False(first.Session.Completion.IsCompleted);
    }

    [Fact]
    public async Task An_id_no_plugin_could_present_is_refused_with_a_message_that_says_what_to_do()
    {
        await using var rig = new Rig();
        await using var plug = rig.Connect();

        var admission = await plug.Engine.HelloAsync(Manifest with { Id = "rigfan" });

        Assert.False(admission.IsAdmitted);
        Assert.Equal(PluginRefusal.InvalidId, admission.Refusal);
        Assert.Contains("reverse DNS", admission.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Plugins.All);
    }

    [Fact]
    public async Task A_plugin_built_against_a_newer_protocol_is_refused_rather_than_tolerated()
    {
        // Letting it proceed means a fan driven by two different sets of assumptions about what
        // the calls mean.
        await using var rig = new Rig();
        await using var plug = rig.Connect();

        var admission = await plug.Engine.HelloAsync(
            Manifest with { ProtocolVersion = PluginProtocol.CurrentVersion + 1 });

        Assert.Equal(PluginRefusal.ProtocolTooNew, admission.Refusal);
    }

    [Fact]
    public async Task An_ungranted_control_is_refused_as_not_permitted()
    {
        // The value that has been sitting in the enum since Phase 0 waiting for something to mean
        // it. Requesting ControlFans in a manifest is asking; the user grants, one fan at a time.
        await using var rig = new Rig();
        await using var plug = rig.Connect();

        await plug.Engine.HelloAsync(Manifest);
        rig.Plugins.Approve(PluginId, [PluginCapability.ControlFans], []);

        var outcome = await plug.Engine.AcquireAsync(rig.FanRef);

        Assert.False(outcome.Granted);
        Assert.Equal(PluginAcquireFailure.NotPermitted, outcome.Failure);
        Assert.Equal(ControlOwnerKind.Curve, rig.Ownership.GetOwner(rig.Fan.Id).Kind);
    }

    [Fact]
    public async Task A_fan_the_engine_is_not_driving_is_refused_as_not_driven()
    {
        // Disabling a fan so two programs "do not fight" is exactly what a careful user does, and
        // before this refusal existed the claim was granted and every duty silently discarded.
        await using var rig = new Rig();
        rig.Configure(enabled: false);

        await using var plug = rig.Connect();
        await rig.AdmitAndGrantAsync(plug);

        var outcome = await plug.Engine.AcquireAsync(rig.FanRef);

        Assert.False(outcome.Granted);
        Assert.Equal(PluginAcquireFailure.NotDriven, outcome.Failure);
        Assert.Contains("switched on", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fan_with_no_curve_to_fall_back_to_is_refused_as_not_driven()
    {
        // The curve is what makes a plugin claim safe to lose. Without one the plugin would be the
        // only thing standing between the fan and a stopped fan.
        await using var rig = new Rig();
        rig.Configure(withCurve: false);

        await using var plug = rig.Connect();
        await rig.AdmitAndGrantAsync(plug);

        Assert.Equal(
            PluginAcquireFailure.NotDriven,
            (await plug.Engine.AcquireAsync(rig.FanRef)).Failure);
    }

    [Fact]
    public async Task A_fan_the_user_is_holding_is_refused_and_the_holder_is_named()
    {
        // "Something else has it" sends people looking; naming the holder tells them what to close.
        await using var rig = new Rig();
        await using var plug = rig.Connect();
        await rig.AdmitAndGrantAsync(plug);

        rig.Loop.TryAcquire(rig.Fan.Id, ControlOwnerKind.ManualOverride, ControlOwnershipRegistry.ManualClaimant);

        var outcome = await plug.Engine.AcquireAsync(rig.FanRef);

        Assert.Equal(PluginAcquireFailure.AlreadyOwned, outcome.Failure);
        Assert.Equal(ControlHolder.User, outcome.Holder);
        Assert.Equal(ControlOwnershipRegistry.ManualClaimant, outcome.HolderId);
    }

    // ---- driving a fan ---------------------------------------------------------------------

    [Fact]
    public async Task A_granted_fan_can_be_claimed_and_driven()
    {
        await using var rig = new Rig();
        await using var plug = rig.Connect();
        await rig.AdmitAndGrantAsync(plug);

        var outcome = await plug.Engine.AcquireAsync(rig.FanRef);

        Assert.True(outcome.Granted);
        Assert.Equal(PluginId, outcome.HolderId);
        Assert.True(await plug.Engine.SetDutyAsync(rig.FanRef, 80f));

        rig.Loop.Tick(Tick);

        Assert.Equal(80f, rig.Commanded, precision: 3);
    }

    [Fact]
    public async Task A_plugin_cannot_drive_a_fan_it_never_claimed()
    {
        await using var rig = new Rig();
        await using var plug = rig.Connect();
        await rig.AdmitAndGrantAsync(plug);

        Assert.False(await plug.Engine.SetDutyAsync(rig.FanRef, 80f));

        rig.Loop.Tick(Tick);

        Assert.Equal(50f, rig.Commanded, precision: 3);
    }

    [Fact]
    public async Task Releasing_hands_the_fan_straight_back_to_its_curve()
    {
        await using var rig = new Rig();
        await using var plug = rig.Connect();
        await rig.AdmitAndGrantAsync(plug);

        await plug.Engine.AcquireAsync(rig.FanRef);
        await plug.Engine.SetDutyAsync(rig.FanRef, 80f);
        rig.Loop.Tick(Tick);

        Assert.True(await plug.Engine.ReleaseAsync(rig.FanRef));

        rig.Loop.Tick(Tick);

        Assert.Equal(50f, rig.Commanded, precision: 3);
        await Until(() => plug.Lost.Count == 1, "the release to be reported");
        Assert.Equal(ControlLostReason.Released, plug.Lost[0].Reason);
    }

    [Fact]
    public async Task A_plugin_that_dies_mid_claim_leaves_its_fan_on_its_curve()
    {
        // The whole safety argument in one test. There is no cleanup a plugin can fail to do,
        // because it was never the thing writing to the fan.
        await using var rig = new Rig();
        var plug = rig.Connect();
        await rig.AdmitAndGrantAsync(plug);

        await plug.Engine.AcquireAsync(rig.FanRef);
        await plug.Engine.SetDutyAsync(rig.FanRef, 80f);
        rig.Loop.Tick(Tick);
        Assert.Equal(80f, rig.Commanded, precision: 3);

        await plug.DisposeAsync();

        await Until(
            () => rig.Ownership.GetOwner(rig.Fan.Id).IsCurve,
            "the dead plugin's fan to go back to its curve");

        rig.Loop.Tick(Tick);

        Assert.Equal(50f, rig.Commanded, precision: 3);
        Assert.Equal(0, rig.Host.SessionCount);
    }

    // ---- one session per id ----------------------------------------------------------------

    [Fact]
    public async Task A_second_connection_using_the_same_id_is_refused_while_the_first_answers()
    {
        // Two sessions sharing a claimant id could release each other's controls, set duties on
        // them, and free them all by dying.
        await using var rig = new Rig();
        await using var first = rig.Connect();
        await first.Engine.HelloAsync(Manifest);

        await using var second = rig.Connect();
        var admission = await second.Engine.HelloAsync(Manifest);

        Assert.False(admission.IsAdmitted);
        Assert.Equal(PluginRefusal.AlreadyConnected, admission.Refusal);
        Assert.Same(first.Session, rig.Host.Find(PluginId));
    }

    [Fact]
    public async Task A_connection_replaces_one_that_has_stopped_answering()
    {
        // A flat refusal would lock a user out of their own plugin after it hung, and restarting it
        // is exactly what someone does about that.
        await using var rig = new Rig(new PluginHostOptions
        {
            EvictionPingTimeout = TimeSpan.FromMilliseconds(100),
        });

        await using var hung = rig.Connect();
        await rig.AdmitAndGrantAsync(hung);
        await hung.Engine.AcquireAsync(rig.FanRef);
        await hung.Engine.SetDutyAsync(rig.FanRef, 80f);

        hung.Answers = false;

        await using var replacement = rig.Connect();
        var admission = await replacement.Engine.HelloAsync(Manifest);

        Assert.True(admission.IsAdmitted);
        Assert.Same(replacement.Session, rig.Host.Find(PluginId));

        // The evicted session's fan is not left where it parked it.
        Assert.True(rig.Ownership.GetOwner(rig.Fan.Id).IsCurve);
        rig.Loop.Tick(Tick);
        Assert.Equal(50f, rig.Commanded, precision: 3);
    }

    [Fact]
    public async Task A_claim_is_never_restored_by_reconnecting()
    {
        // A pin is a user instruction and survives a restart; a claim is a session fact and does
        // not. The asymmetry with RestoreManualPins is deliberate.
        await using var rig = new Rig();
        var first = rig.Connect();
        await rig.AdmitAndGrantAsync(first);
        await first.Engine.AcquireAsync(rig.FanRef);
        await first.DisposeAsync();

        await Until(() => rig.Host.SessionCount == 0, "the first session to be reaped");

        await using var second = rig.Connect();
        await second.Engine.HelloAsync(Manifest);

        Assert.True(rig.Ownership.GetOwner(rig.Fan.Id).IsCurve);
    }

    // ---- losing a fan ----------------------------------------------------------------------

    [Fact]
    public async Task A_plugin_holding_a_fan_when_the_failsafe_engages_is_told()
    {
        // Without this the plugin's window goes on showing 40% while the fan runs at full speed,
        // and it has no way to know the difference.
        await using var rig = new Rig();
        await using var plug = rig.Connect();
        await rig.AdmitAndGrantAsync(plug);
        await plug.Engine.AcquireAsync(rig.FanRef);

        rig.Loop.EngageFailsafe();

        await Until(() => plug.Lost.Count == 1, "the failsafe to be reported");
        Assert.Equal(ControlLostReason.Failsafe, plug.Lost[0].Reason);
        Assert.Equal(rig.FanRef, plug.Lost[0].Control);
    }

    [Fact]
    public async Task A_fan_the_user_revokes_is_taken_back_and_the_plugin_is_told()
    {
        // The narrowest of the three verbs: the plugin stays connected and keeps whatever else it
        // has.
        await using var rig = new Rig();
        await using var plug = rig.Connect();
        await rig.AdmitAndGrantAsync(plug);
        await plug.Engine.AcquireAsync(rig.FanRef);

        rig.Plugins.Revoke(PluginId, rig.Fan.Id);

        await Until(() => plug.Lost.Count == 1, "the revocation to be reported");
        Assert.Equal(ControlLostReason.Revoked, plug.Lost[0].Reason);
        Assert.True(rig.Ownership.GetOwner(rig.Fan.Id).IsCurve);
        Assert.False(plug.Session.Completion.IsCompleted);

        rig.Loop.Tick(Tick);
        Assert.Equal(50f, rig.Commanded, precision: 3);
    }

    [Fact]
    public async Task A_plugin_the_user_disables_loses_its_fan_and_its_connection()
    {
        // The middle verb. Distinct from revoking one fan, and distinct from forgetting it.
        await using var rig = new Rig();
        await using var plug = rig.Connect();
        await rig.AdmitAndGrantAsync(plug);
        await plug.Engine.AcquireAsync(rig.FanRef);

        rig.Plugins.SetEnabled(PluginId, enabled: false);

        await Until(() => plug.Session.Completion.IsCompleted, "the disabled plugin to be cut off");
        Assert.True(rig.Ownership.GetOwner(rig.Fan.Id).IsCurve);
        Assert.Equal(0, rig.Host.SessionCount);
    }

    [Fact]
    public async Task A_configuration_that_stops_driving_a_fan_takes_it_back_from_the_plugin()
    {
        // The grant is checked when the claim is taken, but a user can disable the fan at any point
        // afterwards. Leaving a plugin holding a control the engine has stopped writing is the
        // silent failure this whole rule exists to remove.
        await using var rig = new Rig();
        await using var plug = rig.Connect();
        await rig.AdmitAndGrantAsync(plug);
        await plug.Engine.AcquireAsync(rig.FanRef);

        rig.Configure(enabled: false);

        await Until(() => plug.Lost.Count == 1, "the configuration change to be reported");
        Assert.Equal(ControlLostReason.ConfigurationChanged, plug.Lost[0].Reason);
    }

    [Fact]
    public async Task A_lease_that_lapses_returns_the_fan_to_its_curve()
    {
        // The plugin's own safety net, and the failure a dead-process check cannot catch: the
        // process is alive, its transport is fine, and its update path has stopped producing values.
        await using var rig = new Rig();
        await using var plug = rig.Connect();
        await rig.AdmitAndGrantAsync(plug);

        await plug.Engine.AcquireAsync(rig.FanRef, TimeSpan.FromSeconds(5));
        await plug.Engine.SetDutyAsync(rig.FanRef, 80f);

        rig.Host.PublishTick(1, DateTimeOffset.UtcNow);
        Assert.Equal(ControlOwnerKind.Plugin, rig.Ownership.GetOwner(rig.Fan.Id).Kind);

        rig.Host.PublishTick(2, DateTimeOffset.UtcNow.AddSeconds(30));

        Assert.True(rig.Ownership.GetOwner(rig.Fan.Id).IsCurve);
        await Until(() => plug.Lost.Count == 1, "the lapsed lease to be reported");
        Assert.Equal(ControlLostReason.LeaseExpired, plug.Lost[0].Reason);

        rig.Loop.Tick(Tick);
        Assert.Equal(50f, rig.Commanded, precision: 3);
    }

    [Fact]
    public async Task A_claim_with_no_lease_is_held_however_long_the_plugin_stays_quiet()
    {
        // The default, and the right one for a slider a person set and walked away from.
        await using var rig = new Rig();
        await using var plug = rig.Connect();
        await rig.AdmitAndGrantAsync(plug);

        await plug.Engine.AcquireAsync(rig.FanRef);

        rig.Host.PublishTick(1, DateTimeOffset.UtcNow.AddHours(4));

        Assert.Equal(ControlOwnerKind.Plugin, rig.Ownership.GetOwner(rig.Fan.Id).Kind);
    }

    // ---- what a plugin is told -------------------------------------------------------------

    [Fact]
    public async Task Approving_a_plugin_reaches_it_without_a_reconnect()
    {
        // The admission from the handshake goes stale the moment someone opens the Plugins page.
        await using var rig = new Rig();
        await using var plug = rig.Connect();
        await plug.Engine.HelloAsync(Manifest);

        rig.Plugins.Approve(PluginId, [PluginCapability.ControlFans], [rig.Fan.Id]);

        await Until(() => plug.Admissions.Count >= 1, "the new admission to be pushed");

        var latest = plug.Admissions[^1];

        Assert.Equal(PluginAdmissionState.Approved, latest.State);
        Assert.True(latest.MayControl(rig.FanRef));
    }

    [Fact]
    public async Task Readings_reach_a_subscribed_plugin_and_carry_both_duties()
    {
        // A plugin asking for 30% may be commanded 45% because of a configured minimum. A window
        // shown only one of the two numbers will lie to its user.
        await using var rig = new Rig();
        await using var plug = rig.Connect();
        await rig.AdmitAndGrantAsync(plug);

        await plug.Engine.SubscribeAsync([rig.TempRef]);
        await plug.Engine.AcquireAsync(rig.FanRef);
        await plug.Engine.SetDutyAsync(rig.FanRef, 20f);
        rig.Loop.Tick(Tick);

        rig.Host.PublishTick(7, DateTimeOffset.UtcNow);

        await Until(() => plug.Readings.Count >= 1, "readings to arrive");

        var readings = plug.Readings[^1];
        var sensor = Assert.Single(readings.Sensors);
        var control = Assert.Single(readings.Controls);

        Assert.Equal(7, readings.Tick);
        Assert.Equal(rig.TempRef, sensor.Id);
        Assert.Equal(61.5f, sensor.Value!.Value, precision: 3);

        // Requested is what the plugin asked for; commanded is what the minimum duty made of it.
        Assert.Equal(20f, control.RequestedDuty!.Value, precision: 3);
        Assert.Equal(35f, control.CommandedDuty!.Value, precision: 3);
        Assert.Equal(ControlHolder.Plugin, control.Holder);
        Assert.Equal(PluginId, control.HolderId);
    }

    [Fact]
    public async Task A_plugin_without_the_read_grant_is_sent_no_sensors_at_all()
    {
        await using var rig = new Rig();
        await using var plug = rig.Connect();
        await plug.Engine.HelloAsync(Manifest);
        rig.Plugins.Approve(PluginId, [PluginCapability.ControlFans], [rig.Fan.Id]);
        await Until(() => plug.Admissions.Count >= 1, "the approval to arrive");

        await plug.Engine.SubscribeAsync([rig.TempRef]);
        var snapshot = await plug.Engine.GetSensorsAsync();

        Assert.Empty(snapshot.Sensors);

        // Controls are still listed, so a plugin's settings window can offer a fan the user has not
        // granted yet. Listing is not permission.
        Assert.NotEmpty(snapshot.Controls);

        rig.Host.PublishTick(1, DateTimeOffset.UtcNow);
        await Until(() => plug.Readings.Count >= 1, "readings to arrive");
        Assert.Empty(plug.Readings[^1].Sensors);
    }

    [Fact]
    public async Task A_snapshot_says_which_fans_are_granted_and_which_are_claimable()
    {
        await using var rig = new Rig();
        rig.Configure(enabled: false);

        await using var plug = rig.Connect();
        await rig.AdmitAndGrantAsync(plug);

        var control = Assert.Single((await plug.Engine.GetSensorsAsync()).Controls);

        Assert.True(control.Granted);
        Assert.False(control.Driven);
    }

    [Fact]
    public async Task A_plugin_that_is_not_keeping_up_gets_the_newest_readings_rather_than_a_backlog()
    {
        // The tick reads a requested duty once, so every reading between two ticks except the last
        // is redundant by construction. Latest-wins loses nothing and keeps the queue bounded.
        await using var rig = new Rig();
        await using var plug = rig.Connect();
        await rig.AdmitAndGrantAsync(plug);

        plug.HoldReadings = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The first one has to be in flight before the rest are published, or they all coalesce
        // before anything is sent - which is correct behaviour, but not the behaviour under test.
        rig.Host.PublishTick(1, DateTimeOffset.UtcNow);
        await Until(() => plug.Readings.Count == 1, "the first readings to be in flight");

        for (var tick = 2; tick <= 5; tick++)
        {
            rig.Host.PublishTick(tick, DateTimeOffset.UtcNow);
        }

        plug.HoldReadings.SetResult();
        plug.HoldReadings = null;

        await Until(() => plug.Readings.Count == 2, "the coalesced readings to arrive");

        // The one in flight, then the newest. The three in between were superseded, not queued.
        Assert.Equal([1, 5], plug.Readings.Select(readings => readings.Tick));
    }

    [Fact]
    public async Task A_hardware_declaration_is_answered_as_not_implemented_rather_than_refused()
    {
        // An author whose declaration comes back as a permissions failure goes looking for a grant
        // that does not exist.
        await using var rig = new Rig();
        await using var plug = rig.Connect();
        await rig.AdmitAndGrantAsync(plug);

        var answer = await plug.Engine.DeclareHardwareAsync(
            [new HardwareDeclaration("pump", "Pump", [], [])]);

        Assert.Equal(ProviderOutcome.NotImplementedInThisBuild, answer.Outcome);
        Assert.Contains("nothing is wrong with your plugin", answer.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shutting_down_tells_every_plugin_and_frees_every_fan()
    {
        await using var rig = new Rig();
        await using var plug = rig.Connect();
        await rig.AdmitAndGrantAsync(plug);
        await plug.Engine.AcquireAsync(rig.FanRef);

        await rig.Host.StopAsync();

        Assert.True(rig.Ownership.GetOwner(rig.Fan.Id).IsCurve);
        Assert.Equal(0, rig.Host.SessionCount);
    }

    // ---- scaffolding -----------------------------------------------------------------------

    private static readonly PluginIdentity Identity =
        new(@"C:\Programs\Rig\Rig.exe", "S-1-5-21-1-2-3-1001");

    /// <summary>Waits for something a notification will eventually make true.</summary>
    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    /// <summary>
    /// One fan on a flat 50% curve with a 35% floor, one temperature, and a plugin host over them.
    /// </summary>
    /// <remarks>
    /// The minimum duty is not decoration: it is what makes requested and commanded differ, which
    /// is the distinction the plugin contract goes out of its way to keep visible.
    /// </remarks>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "impeller-pluginhost-" + Guid.NewGuid().ToString("N"));

        private readonly List<Plug> _plugs = [];

        public Rig(PluginHostOptions? options = null)
        {
            Directory.CreateDirectory(_root);

            Fan = Registry.Add(new FakeControl("System Fan #3"));
            Temperature = Registry.Add(new FakeSensor(SensorKind.Temperature, "CPU Package") { Value = 61.5f });
            Curve = new FlatCurve(CurveId.New(), "rest", new Duty(50f));
            Loop = new ControlLoop(Registry, Ownership, TimeProvider.System);

            Plugins = new PluginRegistry(
                new PluginStore(Path.Combine(_root, StateLocation.PluginsName)),
                TimeProvider.System);

            Host = new PluginHost(
                Registry,
                Loop,
                Ownership,
                Plugins,
                "1.0.0-test",
                TimeProvider.System,
                options);

            Configure();
        }

        public FakeSensorRegistry Registry { get; } = new();

        public ControlOwnershipRegistry Ownership { get; } = new(TimeProvider.System);

        public FakeControl Fan { get; }

        public FakeSensor Temperature { get; }

        public FlatCurve Curve { get; }

        public ControlLoop Loop { get; }

        public PluginRegistry Plugins { get; }

        public PluginHost Host { get; }

        public SensorRef FanRef => PluginTranslation.ToRef(Fan.Id);

        public SensorRef TempRef => PluginTranslation.ToRef(Temperature.Id);

        public float Commanded => Loop.GetCommandedDuty(Fan.Id)!.Value.Percent;

        public void Configure(bool enabled = true, bool withCurve = true) =>
            Loop.Configure(
                [Curve],
                [
                    new ControlBinding(Fan.Id)
                    {
                        CurveId = withCurve ? Curve.Id : CurveId.None,
                        Enabled = enabled,
                        MinimumDuty = new Duty(35f),
                        MaximumStepUpPerSecond = 0f,
                        MaximumStepDownPerSecond = 0f,
                    },
                ]);

        /// <summary>Opens a connection and hands back the plugin end of it.</summary>
        public Plug Connect()
        {
            var pair = FullDuplexStream.CreatePair();
            var session = Host.Accept(pair.Item1, Identity);

            Assert.NotNull(session);

            var plug = new Plug(pair.Item2, session);
            _plugs.Add(plug);
            return plug;
        }

        /// <summary>Says hello, then approves the plugin for the one fan, and waits for it to hear.</summary>
        public async Task AdmitAndGrantAsync(Plug plug)
        {
            await plug.Engine.HelloAsync(Manifest);

            Plugins.Approve(
                PluginId,
                [PluginCapability.ReadSensors, PluginCapability.ControlFans],
                [Fan.Id]);

            await Until(
                () => plug.Session.Admission?.MayControl(FanRef) == true,
                "the grant to reach the session");
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var plug in _plugs)
            {
                await plug.DisposeAsync();
            }

            await Host.DisposeAsync();

            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Not worth failing a test over.
            }
        }
    }

    /// <summary>The plugin end of a connection: the proxy it calls, and what it was told.</summary>
    private sealed class Plug : IPluginClient, IAsyncDisposable
    {
        private readonly JsonRpc _rpc;
        private readonly Stream _stream;

        public Plug(Stream stream, PluginSession session)
        {
            _stream = stream;
            Session = session;

            _rpc = new JsonRpc(new HeaderDelimitedMessageHandler(
                stream,
                stream,
                new SystemTextJsonFormatter { JsonSerializerOptions = PluginJson.Options }));

            _rpc.AddLocalRpcTarget<IPluginClient>(this, null);
            Engine = _rpc.Attach<IPluginHost>();
            _rpc.StartListening();
        }

        /// <summary>The engine, as this plugin calls it.</summary>
        public IPluginHost Engine { get; }

        /// <summary>The engine's side of this same connection, for asserting on host state.</summary>
        public PluginSession Session { get; }

        /// <summary>Whether this plugin answers pings. False models hung-but-alive.</summary>
        public bool Answers { get; set; } = true;

        /// <summary>When set, readings block on it — for testing what happens to the ones behind.</summary>
        public TaskCompletionSource? HoldReadings { get; set; }

        public List<PluginReadings> Readings { get; } = [];

        public List<(SensorRef Control, ControlLostReason Reason)> Lost { get; } = [];

        public List<PluginAdmission> Admissions { get; } = [];

        public bool Stopping { get; private set; }

        public async Task OnReadingsAsync(PluginReadings readings)
        {
            lock (Readings)
            {
                Readings.Add(readings);
            }

            if (HoldReadings is { } gate)
            {
                await gate.Task;
            }
        }

        public Task OnControlLostAsync(SensorRef control, ControlLostReason reason)
        {
            lock (Lost)
            {
                Lost.Add((control, reason));
            }

            return Task.CompletedTask;
        }

        public Task OnAdmissionChangedAsync(PluginAdmission admission)
        {
            lock (Admissions)
            {
                Admissions.Add(admission);
            }

            return Task.CompletedTask;
        }

        public Task OnEngineStoppingAsync()
        {
            Stopping = true;
            return Task.CompletedTask;
        }

        public Task<bool> PingAsync() =>
            // Never completing rather than returning false: a hung plugin does not answer at all,
            // and a prompt "no" would be a different failure with a different remedy.
            Answers ? Task.FromResult(true) : new TaskCompletionSource<bool>().Task;

        public Task OnWriteRequestedAsync(SensorRef control, float percent) => Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            _rpc.Dispose();

            try
            {
                await _stream.DisposeAsync();
            }
            catch (IOException)
            {
                // Already gone.
            }
        }
    }
}
