using Impeller.App.ViewModels.Engine;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Ipc.Contracts;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Impeller.Core.Engine.Tests.Ipc;

/// <summary>
/// What the shell's end of the channel does when the engine will not have it.
/// </summary>
/// <remarks>
/// <para>
/// The connection loop had no tests at all before this, because it built its own named pipe and so
/// could only be exercised with an engine actually running. It now takes its transport from
/// outside, which is enough to pin down the two behaviours that are quiet when they go wrong.
/// </para>
/// <para>
/// Both are about a refusal. A refusal must reach the user as a state and a sentence rather than as
/// a stream of failed calls, and it must <em>not</em> be treated as a connection that dropped —
/// because that resets the retry backoff to one second, and a version mismatch does not heal by
/// being asked again a second later.
/// </para>
/// </remarks>
public sealed class EngineConnectionTests
{
    private const string EngineVersion = "9.9.9";

    /// <summary>Long enough for the loop to reach a conclusion, short enough not to stall a suite.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_shell_the_engine_refuses_ends_up_incompatible_carrying_the_engines_own_words()
    {
        await using var rig = new Rig(new RefusingEngine());

        var state = await rig.WaitFor(EngineConnectionState.Incompatible);

        Assert.Equal(EngineConnectionState.Incompatible, state);
        Assert.Contains("engine service", rig.Connection.StatusMessage, StringComparison.Ordinal);

        // The proxy is never published on a refused connection: anything holding it would go on
        // making calls against an engine that has just said it cannot serve them.
        Assert.Null(rig.Connection.Engine);
    }

    /// <summary>
    /// An engine built before the handshake existed is older than this shell, and saying so is more
    /// use than reporting a missing method.
    /// </summary>
    [Fact]
    public async Task An_engine_that_has_never_heard_of_the_handshake_reads_as_an_old_engine()
    {
        await using var rig = new Rig(new AncientEngine());

        await rig.WaitFor(EngineConnectionState.Incompatible);

        Assert.Contains("older than this copy", rig.Connection.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("installer", rig.Connection.StatusMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sentence an observer reads when the state changes is the sentence for that state.
    /// </summary>
    /// <remarks>
    /// A watcher reacts inside the state's own property-changed handler and reads the message in
    /// the same breath, so assigning the state before the message hands it the previous one. That
    /// is not theoretical: the mismatch notification's first outing read "Looking for the engine
    /// service…", which is the message from the attempt that had just been refused.
    /// </remarks>
    [Fact]
    public async Task The_message_is_in_place_before_the_state_that_explains_it()
    {
        await using var rig = new Rig(new RefusingEngine());

        string? seen = null;

        rig.Connection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EngineConnection.State)
                && rig.Connection.State == EngineConnectionState.Incompatible)
            {
                seen ??= rig.Connection.StatusMessage;
            }
        };

        await rig.WaitFor(EngineConnectionState.Incompatible);

        Assert.NotNull(seen);
        Assert.Contains("engine service", seen, StringComparison.Ordinal);
    }

    /// <summary>
    /// The backoff must keep growing through a refusal.
    /// </summary>
    /// <remarks>
    /// A refused connection looks like a successful one from the loop's point of view — it opened,
    /// it talked, it ended — and being counted as one would reset the delay to a second. The
    /// observable consequence is the count: a mismatch that reset the backoff would open connections
    /// once a second for the life of the window, and this asserts it does not.
    /// </remarks>
    [Fact]
    public async Task A_refusal_does_not_reset_the_retry_backoff()
    {
        await using var rig = new Rig(new RefusingEngine());

        await rig.WaitFor(EngineConnectionState.Incompatible);

        var afterFirst = rig.Attempts;
        await Task.Delay(TimeSpan.FromSeconds(3));

        // 1s + 2s + 4s: three attempts in three seconds if the backoff is growing, and roughly one
        // a second if it is not. Asserted loosely because it is timing, and the failure it guards
        // against is an order of magnitude away rather than one attempt.
        Assert.True(
            rig.Attempts - afterFirst <= 3,
            $"the backoff reset: {rig.Attempts - afterFirst} further attempts in three seconds");
    }

    /// <summary>A connection wired to a stand-in engine over an in-memory stream pair.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<IDisposable> _open = [];
        private readonly object _engine;

        private int _attempts;

        public Rig(object engine)
        {
            _engine = engine;

            Connection = new EngineConnection
            {
                // Installed, so a failure to connect is never reported as "not installed" and the
                // state under test is the only one that can appear.
                IsEngineInstalled = () => true,
                Connect = OpenAsync,
            };

            Connection.Start();
        }

        public EngineConnection Connection { get; }

        /// <summary>How many times the loop has asked for a transport.</summary>
        public int Attempts => Volatile.Read(ref _attempts);

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();

            foreach (var open in _open)
            {
                open.Dispose();
            }
        }

        /// <summary>Waits for the connection to settle into a state, or gives up.</summary>
        public async Task<EngineConnectionState> WaitFor(EngineConnectionState state)
        {
            var deadline = DateTime.UtcNow + Settle;

            while (DateTime.UtcNow < deadline)
            {
                if (Connection.State == state)
                {
                    return state;
                }

                await Task.Delay(25);
            }

            return Connection.State;
        }

        private Task<Stream> OpenAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);

            var (client, server) = FullDuplexStream.CreatePair();

            var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(
                server,
                server,
                new SystemTextJsonFormatter { JsonSerializerOptions = ImpellerJson.CompactOptions }));

            rpc.AddLocalRpcTarget(_engine, null);
            rpc.StartListening();

            _open.Add(rpc);
            _open.Add(server);

            return Task.FromResult<Stream>(client);
        }
    }

    /// <summary>An engine that speaks the handshake and refuses this shell.</summary>
    private sealed class RefusingEngine
    {
        public Task<EngineHandshake> HelloAsync(ShellHello hello, CancellationToken cancellationToken = default)
        {
            // The shell always sends CurrentVersion, so the refusal is provoked by an engine that
            // claims to speak less than the shell does - which is exactly the real case: a service
            // left behind by an update.
            _ = hello;

            return Task.FromResult(new EngineHandshake(
                false,
                EngineRefusal.ProtocolTooNew,
                "This copy of Impeller speaks protocol version 2; the engine speaks 1. Update the "
                + "Impeller engine service — the installer does both halves together.",
                EngineVersion,
                1));
        }
    }

    /// <summary>An engine from before the handshake: it has every other verb and not this one.</summary>
    private sealed class AncientEngine
    {
        public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new EngineStatus(EngineVersion, 1, DateTimeOffset.UnixEpoch, false, "."));
    }
}
