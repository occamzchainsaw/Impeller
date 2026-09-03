namespace Impeller.Plugins.Abstractions;

/// <summary>
/// The engine, as a plugin sees it.
/// </summary>
/// <remarks>
/// <para>
/// This is a plugin's <em>only</em> route to hardware. It touches no registers, loads no driver,
/// and needs no elevation — which also means that severing the connection fully neuters it. There
/// is no cleanup a plugin can fail to do and no state it can leave behind on a fan, because it was
/// never the thing writing to the fan.
/// </para>
/// <para>
/// <see cref="HelloAsync"/> is the only call permitted before admission; every other one is refused
/// until the handshake has happened.
/// </para>
/// </remarks>
public interface IPluginHost
{
    /// <summary>
    /// Introduces the plugin and asks to be admitted.
    /// </summary>
    /// <remarks>
    /// Must be the first call on the connection, and must arrive within
    /// <see cref="PluginProtocol.HandshakeDeadline"/>. A plugin the engine has never seen is
    /// admitted as <see cref="PluginAdmissionState.Pending"/> with nothing granted: that is a
    /// success, not a failure, and the right response is to wait for
    /// <see cref="IPluginClient.OnAdmissionChangedAsync"/>.
    /// </remarks>
    Task<PluginAdmission> HelloAsync(PluginManifest manifest, CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything on the machine, as of now. Requires <see cref="PluginCapability.ReadSensors"/>
    /// for the sensor list; the control list is returned either way.
    /// </summary>
    Task<PluginSnapshot> GetSensorsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks to be sent these sensors' values every tick, replacing any previous subscription.
    /// </summary>
    /// <remarks>
    /// Explicit rather than implied by the grant, because the engine would otherwise be serialising
    /// a couple of hundred readings a second per connected plugin to say almost nothing new. An
    /// empty list is a valid subscription and means "stop sending me readings".
    /// </remarks>
    Task SubscribeAsync(IReadOnlyList<SensorRef> sensors, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims a control, so this plugin becomes the thing driving it.
    /// </summary>
    /// <param name="control">Which control. Must be one the user granted.</param>
    /// <param name="maxSilence">
    /// How long the engine should keep the claim without hearing a new duty, or null to hold it
    /// indefinitely.
    /// </param>
    /// <param name="cancellationToken">Cancels the call, not the claim.</param>
    /// <remarks>
    /// <para>
    /// <paramref name="maxSilence"/> is a lease, and only the plugin knows which kind it is. Null
    /// suits a slider the user sets and walks away from: the duty stands until something changes
    /// it. A value suits a plugin that computes a duty on a schedule — set it to a few times the
    /// update interval and a fan recovers to its curve when the plugin's own logic wedges, without
    /// the whole session being declared dead.
    /// </para>
    /// <para>
    /// A claim is never restored automatically after it is lost, by any route, including a
    /// reconnect. See <see cref="ControlLostReason"/>.
    /// </para>
    /// </remarks>
    Task<AcquireOutcome> AcquireAsync(
        SensorRef control,
        TimeSpan? maxSilence = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks for a duty on a control this plugin holds. Returns false if it does not hold it.
    /// </summary>
    /// <remarks>
    /// What is asked for and what is written are not the same number: the engine applies the
    /// binding's limits, its avoided speed bands and its slew limiter on the way through. Watch
    /// <see cref="ControlSample.CommandedDuty"/> for what actually happened.
    /// </remarks>
    Task<bool> SetDutyAsync(SensorRef control, float percent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives a control back. The fan returns to its curve on the next tick.
    /// </summary>
    /// <remarks>
    /// Worth calling, but never required: dying does the same thing. That equivalence is what makes
    /// the model safe — a killed process and a graceful exit are the same event as far as the fans
    /// are concerned.
    /// </remarks>
    Task<bool> ReleaseAsync(SensorRef control, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a line to the engine's log, prefixed with this plugin's id.
    /// </summary>
    /// <remarks>
    /// So that a plugin's account of what it was doing sits in the same file, on the same clock, as
    /// the engine's account of what the fans did.
    /// </remarks>
    Task LogAsync(PluginLogLevel level, string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Offers the plugin's own sensors and controls to the engine.
    /// </summary>
    /// <remarks>
    /// The contract is settled; the engine side is not built. Every declaration is answered with
    /// <see cref="ProviderOutcome.NotImplementedInThisBuild"/> in this build. See
    /// <see cref="DeclaredControl.FailsafeDuty"/> for why the shape had to be decided this early.
    /// </remarks>
    Task<HardwareAdmission> DeclareHardwareAsync(
        IReadOnlyList<HardwareDeclaration> hardware,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Supplies values for the sensors this plugin contributed.
    /// </summary>
    /// <remarks>
    /// Pushed by the plugin on its own schedule rather than pulled by the engine, because a tick
    /// that waited on a plugin would be a tick a plugin could hang. Inert in this build.
    /// </remarks>
    Task PushReadingsAsync(
        IReadOnlyList<ProvidedReading> readings,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A plugin, as the engine sees it.
/// </summary>
/// <remarks>
/// <para>
/// Everything here except <see cref="PingAsync"/> is fire-and-forget: the engine does not wait for
/// a plugin to finish handling a notification, and never calls into a plugin from the tick loop at
/// all. A plugin that stops reading its pipe, throws from every handler, or blocks forever cannot
/// slow fan control down — the worst it can do to the engine is stop answering, which is exactly
/// what the health check is looking for.
/// </para>
/// </remarks>
public interface IPluginClient
{
    /// <summary>New values for the sensors this plugin subscribed to, and the controls it holds.</summary>
    Task OnReadingsAsync(PluginReadings readings);

    /// <summary>
    /// A control this plugin held is no longer its.
    /// </summary>
    /// <remarks>
    /// The single most important notification in the contract. Without it, a plugin whose fan was
    /// taken by the failsafe goes on showing the duty it last asked for while the fan runs at full
    /// speed, and has no way to know the difference.
    /// </remarks>
    Task OnControlLostAsync(SensorRef control, ControlLostReason reason);

    /// <summary>
    /// What this plugin is now allowed to do.
    /// </summary>
    /// <remarks>
    /// Pushed whenever the user changes anything: approving a pending plugin, granting a fan,
    /// revoking one, disabling the plugin outright. The admission from the handshake goes stale the
    /// moment someone opens the Plugins page.
    /// </remarks>
    Task OnAdmissionChangedAsync(PluginAdmission admission);

    /// <summary>
    /// The engine is shutting down.
    /// </summary>
    /// <remarks>
    /// A courtesy, not a negotiation. The engine waits a brief bounded moment for every plugin
    /// together and then goes anyway: a service that can be kept alive by a plugin is a machine that
    /// cannot be rebooted.
    /// </remarks>
    Task OnEngineStoppingAsync();

    /// <summary>
    /// Are you still there?
    /// </summary>
    /// <remarks>
    /// The engine pings the plugin rather than the other way round. A plugin pushing heartbeats
    /// proves only that its transport thread is alive, which a completely wedged application still
    /// has; an answered ping proves its message loop is actually turning. For the ordinary failures
    /// — the process dying, the pipe closing — this is not involved at all, because the connection
    /// itself reports those immediately. It earns its keep against hung-but-alive.
    /// </remarks>
    Task<bool> PingAsync();

    /// <summary>
    /// The engine wants a duty written to hardware this plugin contributed.
    /// </summary>
    /// <remarks>
    /// Inert in this build; see <see cref="IPluginHost.DeclareHardwareAsync"/>. Note that this is
    /// the direction the engine cannot rely on in a failsafe, which is why a contributed control
    /// declares its own failsafe duty for the SDK to apply locally.
    /// </remarks>
    Task OnWriteRequestedAsync(SensorRef control, float percent);
}
