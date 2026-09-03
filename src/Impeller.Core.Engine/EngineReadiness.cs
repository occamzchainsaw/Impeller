namespace Impeller.Core.Engine;

/// <summary>
/// Says when the engine has actually started working, as opposed to having merely been constructed.
/// </summary>
/// <remarks>
/// <para>
/// There is a real gap between the two. Providers are enumerated, the configuration is read, and
/// the first tick runs — and only after that does a sensor id mean anything, because the identity
/// map has not yet been asked about the hardware. Anything that answers questions about controls
/// before then answers them wrongly: every id is unknown, and every claim is refused.
/// </para>
/// <para>
/// The shell does not need this. It connects when a person opens a window, long after the service
/// started, and it is written to survive an engine that is not there at all. A plugin does: it
/// reconnects on a short backoff, so a service restart has it knocking within a second or two of
/// the process existing — well inside the gap — and what it would get is a refusal for every fan it
/// knows about, which is indistinguishable from having had its permissions taken away.
/// </para>
/// <para>
/// Marked from a successful tick rather than from the end of startup, because a tick completing is
/// the only evidence that the whole chain works. Startup finishing only proves nothing threw.
/// </para>
/// </remarks>
public sealed class EngineReadiness
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether the engine has completed a tick.</summary>
    public bool IsReady => _ready.Task.IsCompleted;

    /// <summary>
    /// Records that the engine is working. Safe to call on every tick; only the first counts.
    /// </summary>
    public void MarkReady() => _ready.TrySetResult();

    /// <summary>
    /// Waits until the engine is working.
    /// </summary>
    /// <remarks>
    /// Completes immediately once ready, so a caller need not check first. Never completes on its
    /// own if the engine never manages a tick — the caller's cancellation token is what ends the
    /// wait, and a plugin channel that never opens is the correct outcome for an engine that is not
    /// controlling anything.
    /// </remarks>
    public Task WaitAsync(CancellationToken cancellationToken = default) =>
        _ready.Task.WaitAsync(cancellationToken);
}
