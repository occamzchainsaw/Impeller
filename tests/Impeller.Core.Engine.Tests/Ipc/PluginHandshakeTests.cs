using Impeller.Plugins.Abstractions;

namespace Impeller.Core.Engine.Tests.Ipc;

/// <summary>
/// Covers the part of the handshake that is decided before any policy: is this a well-formed
/// identity, and do the two ends speak the same protocol.
/// </summary>
/// <remarks>
/// Worth its own file because both ends run this code. The SDK runs it before connecting so an
/// author fails at their own desk; the engine runs it again on arrival because it has no reason to
/// believe the connection was built with the SDK at all.
/// </remarks>
public sealed class PluginHandshakeTests
{
    private const string Engine = "0.1.0";

    [Theory]
    [InlineData("com.example.myplugin")]
    [InlineData("com.occamzchainsaw.rigfan")]
    [InlineData("io.github.someone.fan-thing")]
    [InlineData("dev.impeller.plugins.sample")]
    public void A_reverse_dns_id_with_three_labels_is_accepted(string id) =>
        Assert.True(PluginId.IsValid(id));

    [Theory]
    [InlineData("rigfan")]                 // single label: collides with anything
    [InlineData("com.example")]            // two labels: still too close to a bare word
    [InlineData("Com.Example.Plugin")]     // uppercase: two ids differing only by case are one id
    [InlineData("com..example")]           // empty label
    [InlineData("com.example.my_plugin")]  // underscore is not in the alphabet
    [InlineData(" com.example.plugin")]
    [InlineData("")]
    [InlineData(null)]
    public void An_id_that_is_not_reverse_dns_is_refused(string? id) =>
        Assert.False(PluginId.IsValid(id));

    [Theory]
    [InlineData("shell")]
    [InlineData("SHELL")]
    [InlineData("lhm")]
    [InlineData("custom")]
    [InlineData("engine")]
    [InlineData("impeller")]
    public void An_id_the_engine_uses_for_itself_is_refused(string id)
    {
        // "shell" is the literal claimant id the user's own manual pins are held under. A plugin
        // admitted as "shell" could release them.
        Assert.False(PluginId.Validate(id, out var problem));
        Assert.Contains("reserved", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_id_under_a_reserved_prefix_is_still_fine()
    {
        // The blocklist is whole-id, not by prefix. Impeller's own sample plugin lives under
        // "dev.impeller.", and blocking that would be blocking ourselves for no benefit.
        Assert.True(PluginId.IsValid("dev.impeller.sample"));
        Assert.True(PluginId.IsValid("com.impeller.thirdparty"));
    }

    [Fact]
    public void An_absurdly_long_id_is_refused_before_it_reaches_the_state_file()
    {
        var id = "com.example." + new string('a', PluginId.MaxLength);

        Assert.False(PluginId.Validate(id, out var problem));
        Assert.Contains(PluginId.MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture), problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_this_engine_understands_gets_past_the_protocol_check()
    {
        Assert.True(PluginHandshake.TryAccept(Manifest(PluginProtocol.CurrentVersion), Engine, out var refusal));
        Assert.Null(refusal);
    }

    [Fact]
    public void A_plugin_built_against_an_older_protocol_is_told_what_to_do_about_it()
    {
        Assert.False(
            PluginHandshake.TryAccept(Manifest(PluginProtocol.MinimumVersion - 1), Engine, out var refusal));

        Assert.Equal(PluginRefusal.ProtocolTooOld, refusal.Refusal);
        Assert.Contains("Rebuild the plugin", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plugin_built_against_a_newer_protocol_is_refused_rather_than_tolerated()
    {
        // Never let a newer plugin guess at an older engine. A fan driven by two different sets of
        // assumptions about what a call means is worse than a fan nobody is driving.
        Assert.False(
            PluginHandshake.TryAccept(Manifest(PluginProtocol.CurrentVersion + 1), Engine, out var refusal));

        Assert.Equal(PluginRefusal.ProtocolTooNew, refusal.Refusal);
        Assert.Contains("Update Impeller", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_is_not_an_admission()
    {
        Assert.False(PluginHandshake.TryAccept(Manifest(99), Engine, out var refusal));

        Assert.False(refusal.IsAdmitted);
        Assert.Empty(refusal.Granted);
        Assert.Empty(refusal.Controls);
        Assert.False(refusal.MayControl(new SensorRef(Guid.NewGuid())));
    }

    [Fact]
    public void A_malformed_id_is_refused_before_the_protocol_is_even_considered()
    {
        // Order matters for the message: telling someone to rebuild against a newer SDK when the
        // real problem is their id sends them to fix the wrong thing.
        var manifest = new PluginManifest("rigfan", "RigFan", "1.0", 99, []);

        Assert.False(PluginHandshake.TryAccept(manifest, Engine, out var refusal));
        Assert.Equal(PluginRefusal.InvalidId, refusal.Refusal);
    }

    [Fact]
    public void A_connection_that_sends_no_manifest_at_all_is_refused_rather_than_crashing()
    {
        Assert.False(PluginHandshake.TryAccept(null, Engine, out var refusal));
        Assert.Equal(PluginRefusal.InvalidId, refusal.Refusal);
    }

    private static PluginManifest Manifest(int protocolVersion) =>
        new("com.example.plugin", "Example", "1.0.0", protocolVersion, [PluginCapability.ReadSensors]);
}
