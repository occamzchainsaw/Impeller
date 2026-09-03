using Impeller.Core.Abstractions;
using Impeller.Plugins.Abstractions;
using Impeller.Plugins.Host;

namespace Impeller.Core.Engine.Tests.Plugins;

/// <summary>
/// Covers the approval model: what a plugin is allowed to do, and what it takes to change that.
/// </summary>
/// <remarks>
/// The rule underneath every test here is that a pending plugin hands out nothing. Grants are kept
/// across a drop back to pending so that re-approving a plugin the user has already configured is
/// one click rather than a rebuild of their fan list, and that convenience is safe only for as long
/// as pending means empty.
/// </remarks>
public sealed class PluginRegistryTests : IDisposable
{
    private const string Engine = "0.1.0";
    private const string Id = "com.example.plugin";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "impeller-plugins-" + Guid.NewGuid().ToString("N"));

    private readonly SensorId _fanA = SensorId.New();
    private readonly SensorId _fanB = SensorId.New();

    private static readonly PluginIdentity Rig =
        new(@"C:\Programs\RigFan\RigFanControl.exe", "S-1-5-21-1-2-3-1001");

    private string StorePath => Path.Combine(_root, "plugins.json");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp folder that outlives the run is not worth failing a test over.
        }
    }

    /// <summary>A registry over this test's store. Building a second one is the restart.</summary>
    private PluginRegistry Registry() => new(new PluginStore(StorePath), TimeProvider.System);

    private static PluginManifest Manifest(
        params PluginCapability[] requests) =>
        new(Id, "Example", "1.0.0", PluginProtocol.CurrentVersion, requests);

    private static PluginManifest Both() =>
        Manifest(PluginCapability.ReadSensors, PluginCapability.ControlFans);

    [Fact]
    public void A_plugin_nobody_has_ever_seen_is_pending_and_granted_nothing()
    {
        var registry = Registry();

        var admission = registry.Admit(Both(), Rig, Engine);

        Assert.Equal(PluginAdmissionState.Pending, admission.State);
        Assert.True(admission.IsAdmitted);
        Assert.Empty(admission.Granted);
        Assert.Empty(admission.Controls);
    }

    [Fact]
    public void A_plugin_that_connects_again_before_anyone_answers_is_still_pending()
    {
        // Not an error, and not an escalation. A plugin restarting while the user is out does not
        // get further than it did the first time.
        var registry = Registry();
        registry.Admit(Both(), Rig, Engine);

        Assert.Equal(PluginAdmissionState.Pending, registry.Admit(Both(), Rig, Engine).State);
        Assert.Single(registry.All);
    }

    [Fact]
    public void Approving_grants_exactly_the_capabilities_and_fans_that_were_named()
    {
        var registry = Registry();
        registry.Admit(Both(), Rig, Engine);

        Assert.True(registry.Approve(Id, [PluginCapability.ReadSensors, PluginCapability.ControlFans], [_fanA]));

        var admission = registry.Describe(Id, Engine);

        Assert.Equal(PluginAdmissionState.Approved, admission.State);
        Assert.Equal([PluginCapability.ReadSensors, PluginCapability.ControlFans], admission.Granted);
        Assert.Equal([PluginTranslation.ToRef(_fanA)], admission.Controls);
        Assert.False(admission.MayControl(PluginTranslation.ToRef(_fanB)));
    }

    [Fact]
    public void A_capability_the_plugin_never_asked_for_cannot_be_granted()
    {
        // Otherwise it sits in the file forever and takes effect silently the day the plugin starts
        // asking for it - which is a permission the user gave to a different program than the one
        // that ends up using it.
        var registry = Registry();
        registry.Admit(Manifest(PluginCapability.ReadSensors), Rig, Engine);

        registry.Approve(Id, [PluginCapability.ReadSensors, PluginCapability.ControlFans], [_fanA]);

        var admission = registry.Describe(Id, Engine);

        Assert.Equal([PluginCapability.ReadSensors], admission.Granted);
        Assert.Empty(admission.Controls);
    }

    [Fact]
    public void A_plugin_that_stops_asking_for_a_capability_stops_having_it()
    {
        var registry = Registry();
        registry.Admit(Both(), Rig, Engine);
        registry.Approve(Id, [PluginCapability.ReadSensors, PluginCapability.ControlFans], [_fanA]);

        // A new version of the plugin that no longer controls fans.
        var admission = registry.Admit(Manifest(PluginCapability.ReadSensors), Rig, Engine);

        Assert.Equal([PluginCapability.ReadSensors], admission.Granted);
        Assert.Empty(admission.Controls);
    }

    [Fact]
    public void Granting_a_fan_adds_it_without_disturbing_the_others()
    {
        var registry = Registry();
        registry.Admit(Both(), Rig, Engine);
        registry.Approve(Id, [PluginCapability.ControlFans], [_fanA]);

        Assert.True(registry.Grant(Id, _fanB));

        Assert.Equal(
            [PluginTranslation.ToRef(_fanA), PluginTranslation.ToRef(_fanB)],
            registry.Describe(Id, Engine).Controls);
    }

    [Fact]
    public void Granting_the_same_fan_twice_does_not_list_it_twice()
    {
        var registry = Registry();
        registry.Admit(Both(), Rig, Engine);
        registry.Approve(Id, [PluginCapability.ControlFans], [_fanA]);

        registry.Grant(Id, _fanA);

        Assert.Single(registry.Describe(Id, Engine).Controls);
    }

    [Fact]
    public void Revoking_one_fan_leaves_the_plugin_connected_with_the_rest()
    {
        // The narrowest of the three verbs. Disabling a plugin because you want one fan back is how
        // a user ends up with a plugin they have forgotten they turned off.
        var registry = Registry();
        registry.Admit(Both(), Rig, Engine);
        registry.Approve(Id, [PluginCapability.ReadSensors, PluginCapability.ControlFans], [_fanA, _fanB]);

        Assert.True(registry.Revoke(Id, _fanA));

        var admission = registry.Describe(Id, Engine);

        Assert.Equal(PluginAdmissionState.Approved, admission.State);
        Assert.Contains(PluginCapability.ReadSensors, admission.Granted);
        Assert.Equal([PluginTranslation.ToRef(_fanB)], admission.Controls);
    }

    [Fact]
    public void A_plugin_without_the_fan_capability_is_told_about_no_fans_at_all()
    {
        // The control list is meaningless without the capability, and sending it anyway would let a
        // plugin display fans it cannot touch as though it could.
        var registry = Registry();
        registry.Admit(Both(), Rig, Engine);
        registry.Approve(Id, [PluginCapability.ReadSensors], [_fanA, _fanB]);

        Assert.Empty(registry.Describe(Id, Engine).Controls);
        Assert.False(registry.MayControl(Id, _fanA));
    }

    [Fact]
    public void A_disabled_plugin_is_refused_and_told_why()
    {
        var registry = Registry();
        registry.Admit(Both(), Rig, Engine);
        registry.Approve(Id, [PluginCapability.ControlFans], [_fanA]);

        Assert.True(registry.SetEnabled(Id, false));

        var admission = registry.Admit(Both(), Rig, Engine);

        Assert.Equal(PluginAdmissionState.Refused, admission.State);
        Assert.Equal(PluginRefusal.Disabled, admission.Refusal);
        Assert.False(admission.IsAdmitted);
        Assert.Empty(admission.Controls);
    }

    [Fact]
    public void Enabling_a_plugin_again_does_not_mean_configuring_it_again()
    {
        var registry = Registry();
        registry.Admit(Both(), Rig, Engine);
        registry.Approve(Id, [PluginCapability.ControlFans], [_fanA]);
        registry.SetEnabled(Id, false);

        registry.SetEnabled(Id, true);

        var admission = registry.Admit(Both(), Rig, Engine);

        Assert.Equal(PluginAdmissionState.Approved, admission.State);
        Assert.Equal([PluginTranslation.ToRef(_fanA)], admission.Controls);
    }

    [Fact]
    public void Forgetting_a_plugin_makes_its_next_connection_a_first_one()
    {
        // The destructive verb, and distinct from disabling: a forgotten plugin can come straight
        // back by connecting, where a disabled one cannot until the user says so.
        var registry = Registry();
        registry.Admit(Both(), Rig, Engine);
        registry.Approve(Id, [PluginCapability.ControlFans], [_fanA]);

        Assert.True(registry.Forget(Id));
        Assert.Empty(registry.All);

        var admission = registry.Admit(Both(), Rig, Engine);

        Assert.Equal(PluginAdmissionState.Pending, admission.State);
        Assert.Empty(admission.Controls);
    }

    [Fact]
    public void A_plugin_running_from_a_different_program_has_to_be_approved_again()
    {
        // The reason an approval is bound to a path and an account at all: without it, a standing
        // grant is one any program inherits by announcing the right manifest id.
        var registry = Registry();
        registry.Admit(Both(), Rig, Engine);
        registry.Approve(Id, [PluginCapability.ControlFans], [_fanA]);

        var admission = registry.Admit(
            Both(), Rig with { ImagePath = @"C:\Temp\NotRigFan.exe" }, Engine);

        Assert.Equal(PluginAdmissionState.Pending, admission.State);
        Assert.Empty(admission.Granted);
        Assert.Empty(admission.Controls);
    }

    [Fact]
    public void A_plugin_running_as_a_different_user_has_to_be_approved_again()
    {
        var registry = Registry();
        registry.Admit(Both(), Rig, Engine);
        registry.Approve(Id, [PluginCapability.ControlFans], [_fanA]);

        var admission = registry.Admit(Both(), Rig with { UserSid = "S-1-5-21-9-9-9-500" }, Engine);

        Assert.Equal(PluginAdmissionState.Pending, admission.State);
        Assert.Empty(admission.Controls);
    }

    [Fact]
    public void A_re_prompt_says_it_is_about_the_program_having_changed()
    {
        // Otherwise it reads as the engine having forgotten, and the user learns to click through
        // it - which is the failure mode that makes every later prompt worthless.
        var registry = Registry();
        registry.Admit(Both(), Rig, Engine);
        registry.Approve(Id, [PluginCapability.ControlFans], [_fanA]);

        var admission = registry.Admit(Both(), Rig with { ImagePath = @"C:\Temp\Other.exe" }, Engine);

        Assert.Contains("different program", admission.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Re_approving_after_a_program_change_restores_the_fans_in_one_step()
    {
        var registry = Registry();
        registry.Admit(Both(), Rig, Engine);
        registry.Approve(Id, [PluginCapability.ControlFans], [_fanA, _fanB]);

        var moved = Rig with { ImagePath = @"C:\Programs\RigFan\v2\RigFanControl.exe" };
        registry.Admit(Both(), moved, Engine);

        var record = registry.Find(Id)!;
        registry.Approve(Id, record.Capabilities, record.Controls);

        var admission = registry.Describe(Id, Engine);

        Assert.Equal(PluginAdmissionState.Approved, admission.State);
        Assert.Equal(2, admission.Controls.Count);
    }

    [Fact]
    public void The_same_program_spelled_differently_is_still_the_same_program()
    {
        // Windows paths are case-insensitive, and a plugin launched from a shortcut, a shell and a
        // scheduled task can legitimately arrive spelled three ways.
        var registry = Registry();
        registry.Admit(Both(), Rig, Engine);
        registry.Approve(Id, [PluginCapability.ControlFans], [_fanA]);

        var admission = registry.Admit(
            Both(), Rig with { ImagePath = Rig.ImagePath!.ToUpperInvariant() }, Engine);

        Assert.Equal(PluginAdmissionState.Approved, admission.State);
    }

    [Fact]
    public void An_approval_given_when_the_program_could_not_be_identified_covers_any_connection()
    {
        // A machine where client identification does not work must not re-prompt on every connect.
        // The cost of the looser rule is stated where it is taken: this is a boundary against
        // silent inheritance, not against a same-user adversary.
        var registry = Registry();
        registry.Admit(Both(), PluginIdentity.Unknown, Engine);
        registry.Approve(Id, [PluginCapability.ControlFans], [_fanA]);

        Assert.Equal(PluginAdmissionState.Approved, registry.Admit(Both(), Rig, Engine).State);
    }

    [Fact]
    public void A_connection_that_cannot_be_identified_does_not_inherit_a_bound_approval()
    {
        var registry = Registry();
        registry.Admit(Both(), Rig, Engine);
        registry.Approve(Id, [PluginCapability.ControlFans], [_fanA]);

        Assert.Equal(
            PluginAdmissionState.Pending,
            registry.Admit(Both(), PluginIdentity.Unknown, Engine).State);
    }

    [Fact]
    public void A_malformed_id_is_refused_without_being_written_down()
    {
        // A stranger must not be able to fill the state file with entries simply by connecting.
        var registry = Registry();

        var admission = registry.Admit(
            new PluginManifest("rigfan", "Rig", "1.0", PluginProtocol.CurrentVersion, []),
            Rig,
            Engine);

        Assert.Equal(PluginRefusal.InvalidId, admission.Refusal);
        Assert.Empty(registry.All);
    }

    [Fact]
    public void A_protocol_mismatch_is_refused_without_being_written_down()
    {
        var registry = Registry();

        var admission = registry.Admit(
            new PluginManifest(Id, "Example", "1.0", PluginProtocol.CurrentVersion + 1, []),
            Rig,
            Engine);

        Assert.Equal(PluginRefusal.ProtocolTooNew, admission.Refusal);
        Assert.Empty(registry.All);
    }

    [Fact]
    public void Grants_survive_a_restart()
    {
        // The whole point of writing anything down. A second registry over the same store is what
        // the service builds on its next start.
        var first = Registry();
        first.Admit(Both(), Rig, Engine);
        first.Approve(Id, [PluginCapability.ReadSensors, PluginCapability.ControlFans], [_fanA, _fanB]);

        var restarted = Registry();
        var admission = restarted.Admit(Both(), Rig, Engine);

        Assert.Equal(PluginAdmissionState.Approved, admission.State);
        Assert.Equal(2, admission.Controls.Count);
        Assert.True(restarted.MayControl(Id, _fanA));
    }

    [Fact]
    public void A_plugin_disabled_before_a_restart_is_still_disabled_after_one()
    {
        var first = Registry();
        first.Admit(Both(), Rig, Engine);
        first.Approve(Id, [PluginCapability.ControlFans], [_fanA]);
        first.SetEnabled(Id, false);

        Assert.Equal(PluginRefusal.Disabled, Registry().Admit(Both(), Rig, Engine).Refusal);
    }

    [Fact]
    public void What_a_plugin_asked_for_is_remembered_alongside_what_it_was_given()
    {
        // So the Plugins page can say that an updated plugin now wants something it does not have.
        // Without it, a plugin that starts asking to control fans is simply refused and neither the
        // user nor the author is told why.
        var registry = Registry();
        registry.Admit(Both(), Rig, Engine);
        registry.Approve(Id, [PluginCapability.ReadSensors], []);

        var record = registry.Find(Id)!;

        Assert.Equal([PluginCapability.ReadSensors, PluginCapability.ControlFans], record.Requested);
        Assert.Equal([PluginCapability.ControlFans], record.Outstanding);
    }

    [Fact]
    public void Every_change_is_announced_so_a_connected_plugin_can_be_told()
    {
        // A plugin that only reads its admission at the handshake believes it still holds
        // permissions it lost ten minutes ago.
        var registry = Registry();
        var changes = new List<PluginRecord>();
        registry.Changed += (_, record) => changes.Add(record);

        registry.Admit(Both(), Rig, Engine);
        registry.Approve(Id, [PluginCapability.ControlFans], [_fanA]);
        registry.Revoke(Id, _fanA);
        registry.SetEnabled(Id, false);
        registry.Forget(Id);

        Assert.Equal(5, changes.Count);
        Assert.Equal(PluginAdmissionState.Approved, changes[1].State);
        Assert.Empty(changes[2].Controls);
        Assert.False(changes[3].Enabled);
    }

    [Fact]
    public void Changing_nothing_announces_nothing()
    {
        // Revoking a fan the plugin never had should not push an admission at it saying its
        // permissions changed.
        var registry = Registry();
        registry.Admit(Both(), Rig, Engine);

        var changes = 0;
        registry.Changed += (_, _) => changes++;

        Assert.True(registry.Revoke(Id, _fanB));

        Assert.Equal(0, changes);
    }

    [Fact]
    public void Acting_on_a_plugin_that_does_not_exist_says_so_rather_than_inventing_one()
    {
        var registry = Registry();

        Assert.False(registry.Approve("com.example.ghost", [PluginCapability.ReadSensors], []));
        Assert.False(registry.Grant("com.example.ghost", _fanA));
        Assert.False(registry.Revoke("com.example.ghost", _fanA));
        Assert.False(registry.SetEnabled("com.example.ghost", false));
        Assert.False(registry.Forget("com.example.ghost"));
        Assert.Empty(registry.All);
    }

    [Fact]
    public void Asking_about_an_unknown_plugin_gets_a_refusal_rather_than_an_empty_approval()
    {
        var admission = Registry().Describe("com.example.ghost", Engine);

        Assert.False(admission.IsAdmitted);
    }
}
