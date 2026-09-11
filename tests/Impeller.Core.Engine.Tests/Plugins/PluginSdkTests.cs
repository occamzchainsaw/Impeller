using Impeller.Core.Abstractions;
using Impeller.Core.Engine.Curves;
using Impeller.Core.Persistence;
using Impeller.Plugins.Abstractions;
using Impeller.Plugins.Host;
using Impeller.Plugins.Sdk;
using Nerdbank.Streams;

namespace Impeller.Core.Engine.Tests.Plugins;

/// <summary>
/// Drives the SDK against a real engine, over an in-memory stream.
/// </summary>
/// <remarks>
/// <para>
/// The SDK is the only part of Impeller whose users are other people, and the only part where being
/// merely correct is not enough — a contract that is awkward produces plugins that work around it.
/// These tests exercise it the way an author would: construct a client, start it, wait to be
/// approved, take a fan, lose it, and be told why.
/// </para>
/// <para>
/// Against the real <see cref="PluginHost"/>, not a stand-in. A mock engine would agree with
/// whatever the SDK did, which is the one thing worth not assuming.
/// </para>
/// </remarks>
public sealed class PluginSdkTests
{
    private const string PluginId = "com.example.sdk";

    private static PluginManifest Manifest => new(
        PluginId,
        "SDK Test Plugin",
        "1.0.0",
        PluginProtocol.CurrentVersion,
        [PluginCapability.ReadSensors, PluginCapability.ControlFans]);

    // ---- what an author gets wrong at their own desk ---------------------------------------

    [Theory]
    [InlineData("rigfan")]
    [InlineData("com.example")]
    [InlineData("Com.Example.Plugin")]
    [InlineData("shell")]
    [InlineData("")]
    public void An_id_no_engine_would_accept_is_refused_at_construction(string id)
    {
        // At their own desk rather than in a user's bug report. The engine checks again on arrival
        // and has no reason to trust that this ever ran.
        var problem = Assert.Throws<ArgumentException>(
            () => new ImpellerClient(Manifest with { Id = id }));

        Assert.Contains("plugin id", problem.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_protocol_version_the_sdk_does_not_speak_is_refused_at_construction()
    {
        // Naming a number does not make this SDK speak it, and finding that out at the handshake
        // would waste an author's afternoon.
        Assert.Throws<ArgumentException>(
            () => new ImpellerClient(Manifest with { ProtocolVersion = 99 }));
    }

    [Fact]
    public async Task A_well_formed_manifest_is_accepted_without_connecting_to_anything()
    {
        // Constructing a client must not require a running engine: an author writing their first
        // plugin has not started one yet.
        await using var client = new ImpellerClient(Manifest);

        Assert.Equal(ImpellerConnectionState.Disconnected, client.State);
    }

    // ---- the lifecycle an author actually writes --------------------------------------------

    [Fact]
    public async Task A_plugin_that_has_never_been_approved_waits_rather_than_failing()
    {
        await using var rig = new SdkRig();

        rig.Client.Start();

        await Until(
            () => rig.Client.State == ImpellerConnectionState.AwaitingApproval,
            "the client to settle on awaiting approval");

        Assert.False(rig.Client.IsReady);
        Assert.Contains("waiting to be approved", rig.Client.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approving_reaches_a_running_plugin_without_it_reconnecting()
    {
        await using var rig = new SdkRig();

        rig.Client.Start();
        await Until(() => rig.Host.SessionCount == 1, "the plugin to connect");

        rig.Approve();

        await Until(() => rig.Client.IsReady, "the approval to reach the plugin");
        Assert.True(rig.Client.MayControl(rig.FanRef));
    }

    [Fact]
    public async Task A_granted_fan_can_be_taken_and_driven_through_the_sdk()
    {
        await using var rig = new SdkRig();

        rig.Client.Start();
        await Until(() => rig.Host.SessionCount == 1, "the plugin to connect");
        await rig.ApproveAndSettleAsync();

        var outcome = await rig.Client.AcquireAsync(rig.FanRef);

        Assert.True(outcome.Granted, outcome.Message);
        Assert.True(await rig.Client.SetDutyAsync(rig.FanRef, 80f));

        rig.Loop.Tick(TimeSpan.FromSeconds(1));

        Assert.Equal(80f, rig.Loop.GetCommandedDuty(rig.Fan.Id)!.Value.Percent, precision: 3);
    }

    [Fact]
    public async Task An_ungranted_fan_comes_back_with_a_sentence_worth_showing_a_user()
    {
        await using var rig = new SdkRig();

        rig.Client.Start();
        await Until(() => rig.Client.State == ImpellerConnectionState.AwaitingApproval, "connection");

        var outcome = await rig.Client.AcquireAsync(rig.FanRef);

        Assert.False(outcome.Granted);
        Assert.Equal(PluginAcquireFailure.NotPermitted, outcome.Failure);
        Assert.EndsWith(".", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Losing_a_fan_says_why_and_says_whether_taking_it_again_is_sensible()
    {
        // The distinction the whole reason enum exists for. Re-acquiring after a failsafe is the
        // wrong thing to do and the SDK should not leave an author to work that out.
        await using var rig = new SdkRig();

        rig.Client.Start();
        await Until(() => rig.Host.SessionCount == 1, "the plugin to connect");
        await rig.ApproveAndSettleAsync();
        await rig.Client.AcquireAsync(rig.FanRef);

        rig.Loop.EngageFailsafe();

        await Until(() => rig.Lost.Count == 1, "the failsafe to be reported");

        Assert.Equal(ControlLostReason.Failsafe, rig.Lost[0].Reason);
        Assert.False(rig.Lost[0].WorthRetrying);
    }

    [Fact]
    public async Task A_lapsed_lease_is_the_one_loss_worth_retrying()
    {
        await using var rig = new SdkRig();

        rig.Client.Start();
        await Until(() => rig.Host.SessionCount == 1, "the plugin to connect");
        await rig.ApproveAndSettleAsync();

        var outcome = await rig.Client.AcquireAsync(rig.FanRef, TimeSpan.FromSeconds(5));

        // Asserted rather than assumed: a refused claim here would otherwise surface as a mystery
        // timeout waiting for a notification that was never going to arrive.
        Assert.True(outcome.Granted, outcome.Message);

        rig.Host.PublishTick(1, DateTimeOffset.UtcNow.AddMinutes(1));

        await Until(() => rig.Lost.Count == 1, "the lapsed lease to be reported");

        Assert.Equal(ControlLostReason.LeaseExpired, rig.Lost[0].Reason);
        Assert.True(rig.Lost[0].WorthRetrying);
    }

    [Fact]
    public async Task Readings_reach_a_subscribed_plugin()
    {
        await using var rig = new SdkRig();

        rig.Client.Start();
        await Until(() => rig.Host.SessionCount == 1, "the plugin to connect");
        await rig.ApproveAndSettleAsync();

        await rig.Client.SubscribeAsync([rig.TempRef]);
        rig.Host.PublishTick(3, DateTimeOffset.UtcNow);

        await Until(() => rig.Readings.Count >= 1, "readings");

        var sample = Assert.Single(rig.Readings[^1].Sensors);
        Assert.Equal(61.5f, sample.Value!.Value, precision: 3);
    }

    [Fact]
    public async Task Nothing_throws_when_the_engine_is_not_there()
    {
        // An author should be able to call the verbs without guarding every one of them. A plugin
        // cannot usefully tell "not running" from "refused" and should behave the same either way.
        var client = new ImpellerClient(
            Manifest,
            new ImpellerClientOptions
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(50),
                Transport = _ => throw new IOException("nothing is listening"),
            });

        await using (client)
        {
            client.Start();

            var fan = new SensorRef(Guid.NewGuid());

            Assert.False((await client.AcquireAsync(fan)).Granted);
            Assert.False(await client.SetDutyAsync(fan, 50f));
            Assert.False(await client.ReleaseAsync(fan));
            Assert.Null(await client.GetMachineAsync());
            await client.SubscribeAsync([fan]);
            await client.LogAsync(PluginLogLevel.Information, "still fine");
        }
    }

    [Fact]
    public async Task A_hardware_declaration_from_an_unapproved_plugin_is_answered_rather_than_refused()
    {
        // Pending, not yet approved for anything — DeclareHardwareAsync still answers with a real
        // outcome rather than throwing or hanging, the same courtesy AcquireAsync and the rest get.
        await using var rig = new SdkRig();

        rig.Client.Start();
        await Until(() => rig.Client.State == ImpellerConnectionState.AwaitingApproval, "connection");

        var answer = await rig.Client.DeclareHardwareAsync(
            [new HardwareDeclaration("pump", "Pump", [], [])]);

        Assert.False(answer.Accepted);
        Assert.Equal(ProviderOutcome.NotRequested, answer.Outcome);
    }

    [Fact]
    public async Task A_dropped_connection_puts_contributed_hardware_into_its_safe_state()
    {
        // The engine cannot do this: writing to a plugin's hardware means posting to a process that
        // may be gone. If the SDK does not do it here, nothing does, and the fan is stuck.
        var hardware = new RecordingHardware();

        await using var rig = new SdkRig(hardware);

        rig.Client.Start();
        await Until(() => rig.Host.SessionCount == 1, "the plugin to connect");

        await rig.Host.StopAsync();

        await Until(() => hardware.FailsafeApplied > 0, "the local failsafe to run");
    }

    // ---- scaffolding -----------------------------------------------------------------------

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);

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

    private sealed class RecordingHardware : IPluginHardware
    {
        public int FailsafeApplied { get; private set; }

        public void Write(SensorRef control, float percent)
        {
        }

        public void ApplyFailsafe() => FailsafeApplied++;
    }

    /// <summary>A real engine, a real plugin host, and an SDK client wired to it over a stream.</summary>
    private sealed class SdkRig : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "impeller-sdk-" + Guid.NewGuid().ToString("N"));

        public SdkRig(IPluginHardware? hardware = null)
        {
            Directory.CreateDirectory(_root);

            Fan = Registry.Add(new FakeControl("System Fan #1"));
            Temperature = Registry.Add(new FakeSensor(SensorKind.Temperature, "CPU") { Value = 61.5f });
            Curve = new FlatCurve(CurveId.New(), "rest", new Duty(50f));
            Loop = new ControlLoop(Registry, Ownership, TimeProvider.System);

            Loop.Configure(
                [Curve],
                [
                    new ControlBinding(Fan.Id)
                    {
                        CurveId = Curve.Id,
                        Enabled = true,
                        MaximumStepUpPerSecond = 0f,
                        MaximumStepDownPerSecond = 0f,
                    },
                ]);

            Plugins = new PluginRegistry(
                new PluginStore(Path.Combine(_root, StateLocation.PluginsName)),
                TimeProvider.System);

            Host = new PluginHost(
                Registry,
                Loop,
                Ownership,
                Plugins,
                new JsonSensorNames(Path.Combine(_root, StateLocation.NamesName)),
                new PluginHardwareProvider(new InMemorySensorIdentityMap()),
                "1.0-test",
                TimeProvider.System);

            Client = new ImpellerClient(Manifest, new ImpellerClientOptions
            {
                Hardware = hardware,

                MinimumRetryDelay = TimeSpan.FromMilliseconds(20),
                HandshakeTimeout = TimeSpan.FromSeconds(5),

                // Each attempt gets a fresh pair, exactly as a fresh pipe connection would. A
                // host that has stopped accepting refuses outright rather than handing back a
                // stream nothing is reading, which is what a stopped engine's pipe does.
                Transport = _ =>
                {
                    var pair = FullDuplexStream.CreatePair();
                    var identity = new PluginIdentity(@"C:\sdk\test.exe", "S-1-5-21-9");

                    if (Host.Accept(pair.Item1, identity) is null)
                    {
                        pair.Item1.Dispose();
                        pair.Item2.Dispose();
                        throw new IOException("Impeller is not accepting plugins.");
                    }

                    return Task.FromResult<Stream>(pair.Item2);
                },
            });

            Client.ControlLost += (_, lost) =>
            {
                lock (Lost)
                {
                    Lost.Add(lost);
                }
            };

            Client.Readings += (_, readings) =>
            {
                lock (Readings)
                {
                    Readings.Add(readings);
                }
            };
        }

        public FakeSensorRegistry Registry { get; } = new();

        public ControlOwnershipRegistry Ownership { get; } = new(TimeProvider.System);

        public FakeControl Fan { get; }

        public FakeSensor Temperature { get; }

        public FlatCurve Curve { get; }

        public ControlLoop Loop { get; }

        public PluginRegistry Plugins { get; }

        public PluginHost Host { get; }

        public ImpellerClient Client { get; }

        public List<ControlLostEventArgs> Lost { get; } = [];

        public List<PluginReadings> Readings { get; } = [];

        public SensorRef FanRef => PluginTranslation.ToRef(Fan.Id);

        public SensorRef TempRef => PluginTranslation.ToRef(Temperature.Id);

        /// <summary>Approves the plugin the way the Plugins page does.</summary>
        public void Approve() => Plugins.Approve(
            PluginId,
            [PluginCapability.ReadSensors, PluginCapability.ControlFans],
            [Fan.Id]);

        /// <summary>
        /// Approves, and waits for the engine to have applied it.
        /// </summary>
        /// <remarks>
        /// Waits on the engine's own view rather than on the notification reaching the client,
        /// because a test about leases or readings should not fail when a push is merely slow. The
        /// client does not need to have heard: it checks nothing locally before asking, and the
        /// engine is the thing that decides.
        /// </remarks>
        public async Task ApproveAndSettleAsync()
        {
            Approve();

            await Until(
                () => Host.Find(PluginId)?.Admission?.MayControl(FanRef) == true,
                "the engine to apply the approval");
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
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
}
