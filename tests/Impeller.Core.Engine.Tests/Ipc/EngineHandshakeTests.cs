using Impeller.Ipc.Contracts;

namespace Impeller.Core.Engine.Tests.Ipc;

/// <summary>
/// The version check between the window and the service.
/// </summary>
/// <remarks>
/// The failure this exists to prevent is quiet and confusing: two halves of Impeller updated at
/// different times, connecting happily, and then behaving inexplicably as individual calls fail.
/// Every case below therefore asserts the sentence as well as the enum, because the sentence is the
/// entire value — it names which half to update, and it is what a user reads.
/// </remarks>
public sealed class EngineHandshakeTests
{
    private const string Engine = "0.1.0";

    [Fact]
    public void A_shell_speaking_this_protocol_is_accepted()
    {
        Assert.True(EngineHandshakeCheck.TryAccept(Hello(EngineProtocol.CurrentVersion), Engine, out var refusal));
        Assert.Null(refusal);
    }

    [Fact]
    public void An_accepted_handshake_still_carries_a_sentence_and_the_engine_version()
    {
        var accepted = EngineHandshakeCheck.Accept(Engine);

        Assert.True(accepted.Accepted);
        Assert.Equal(EngineRefusal.None, accepted.Refusal);
        Assert.Equal(Engine, accepted.EngineVersion);
        Assert.Equal(EngineProtocol.CurrentVersion, accepted.ProtocolVersion);
        Assert.Contains(Engine, accepted.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The common case in practice: someone updated the window and the service is still the old one.
    /// </summary>
    /// <remarks>
    /// The message has to name the service specifically. "Versions do not match" sends somebody to
    /// re-download the thing they just downloaded.
    /// </remarks>
    [Fact]
    public void A_shell_newer_than_the_engine_is_told_to_update_the_engine()
    {
        Assert.False(
            EngineHandshakeCheck.TryAccept(Hello(EngineProtocol.CurrentVersion + 1), Engine, out var refusal));

        Assert.Equal(EngineRefusal.ProtocolTooNew, refusal.Refusal);
        Assert.Contains("engine service", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(Engine, refusal.EngineVersion);
    }

    [Fact]
    public void A_shell_older_than_the_engine_will_serve_is_told_to_update_itself()
    {
        Assert.False(
            EngineHandshakeCheck.TryAccept(Hello(EngineProtocol.MinimumVersion - 1), Engine, out var refusal));

        Assert.Equal(EngineRefusal.ProtocolTooOld, refusal.Refusal);
        Assert.Contains("Update Impeller", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refusal always carries which engine refused, because a bug report that does not name the
    /// versions involved cannot be answered.
    /// </summary>
    [Fact]
    public void Every_refusal_names_the_engine_and_the_protocol_it_speaks()
    {
        foreach (var version in new[] { EngineProtocol.MinimumVersion - 1, EngineProtocol.CurrentVersion + 1 })
        {
            Assert.False(EngineHandshakeCheck.TryAccept(Hello(version), Engine, out var refusal));

            Assert.False(refusal.Accepted);
            Assert.Equal(Engine, refusal.EngineVersion);
            Assert.Equal(EngineProtocol.CurrentVersion, refusal.ProtocolVersion);
            Assert.NotEqual(string.Empty, refusal.Message);
        }
    }

    /// <summary>
    /// Nothing sent at all is treated as an old shell rather than as a crash.
    /// </summary>
    /// <remarks>
    /// It reaches this only over a wire, where the far end is whatever it is. Throwing would take
    /// down a connection the engine is otherwise perfectly able to answer.
    /// </remarks>
    [Fact]
    public void A_hello_that_says_nothing_is_refused_rather_than_throwing()
    {
        Assert.False(EngineHandshakeCheck.TryAccept(null, Engine, out var refusal));

        Assert.Equal(EngineRefusal.ProtocolTooOld, refusal.Refusal);
        Assert.Contains("Update Impeller", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The minimum is only allowed to move deliberately.
    /// </summary>
    /// <remarks>
    /// This pins the policy rather than the number: additive change to the contracts must not raise
    /// the floor, because that refuses every build below it. A failure here means somebody moved
    /// <c>MinimumVersion</c>, and that is a decision, not a refactor.
    /// </remarks>
    [Fact]
    public void The_minimum_is_not_above_what_this_build_speaks()
    {
        Assert.True(EngineProtocol.MinimumVersion <= EngineProtocol.CurrentVersion);
        Assert.True(EngineProtocol.IsSupported(EngineProtocol.CurrentVersion));
        Assert.False(EngineProtocol.IsSupported(EngineProtocol.CurrentVersion + 1));
    }

    private static ShellHello Hello(int protocolVersion) => new("0.1.0", protocolVersion);
}
