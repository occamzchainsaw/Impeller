namespace Impeller.Core.Engine.Tests;

/// <summary>
/// Covers the signal that says the engine is working, rather than merely constructed.
/// </summary>
/// <remarks>
/// The plugin channel is gated on it. A plugin reconnects on a short backoff, so a service restart
/// has it knocking within a second or two of the process existing — well inside the window where
/// providers have not been enumerated and no sensor id means anything yet. What it would get is a
/// refusal for every fan it knows about, which is indistinguishable from having had its permissions
/// taken away.
/// </remarks>
public class EngineReadinessTests
{
    [Fact]
    public void An_engine_that_has_not_ticked_is_not_ready()
    {
        var readiness = new EngineReadiness();

        Assert.False(readiness.IsReady);
        Assert.False(readiness.WaitAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    public async Task A_wait_started_before_the_first_tick_completes_when_it_arrives()
    {
        var readiness = new EngineReadiness();
        var waiting = readiness.WaitAsync(CancellationToken.None);

        readiness.MarkReady();

        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(readiness.IsReady);
    }

    [Fact]
    public async Task A_wait_started_after_the_engine_is_ready_completes_immediately()
    {
        // So a caller never has to check first, and cannot race between checking and waiting.
        var readiness = new EngineReadiness();
        readiness.MarkReady();

        await readiness.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Marking_ready_on_every_tick_is_harmless()
    {
        // It is called from the tick path, which runs once a second for the life of the machine.
        var readiness = new EngineReadiness();

        readiness.MarkReady();
        readiness.MarkReady();
        readiness.MarkReady();

        Assert.True(readiness.IsReady);
    }

    [Fact]
    public async Task A_cancelled_wait_gives_up_without_making_the_engine_ready()
    {
        // An engine that never manages a tick is one that should never open the plugin channel.
        // The caller's shutdown token is what ends the wait, not a timeout that lets it through.
        var readiness = new EngineReadiness();
        using var cancelled = new CancellationTokenSource();

        var waiting = readiness.WaitAsync(cancelled.Token);
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(readiness.IsReady);
    }

    [Fact]
    public async Task One_caller_giving_up_does_not_end_anyone_elses_wait()
    {
        var readiness = new EngineReadiness();
        using var cancelled = new CancellationTokenSource();

        var abandoned = readiness.WaitAsync(cancelled.Token);
        var patient = readiness.WaitAsync(CancellationToken.None);

        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        readiness.MarkReady();
        await patient.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
