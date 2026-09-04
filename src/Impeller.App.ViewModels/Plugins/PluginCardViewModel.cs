using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Impeller.Core.Abstractions;
using Impeller.Ipc.Contracts;
using Impeller.Plugins.Abstractions;

namespace Impeller.App.ViewModels.Plugins;

/// <summary>One fan, as a tick box on a plugin's card.</summary>
public sealed partial class GrantedFanViewModel(SensorId id, string name, bool granted, bool claimable)
    : ObservableObject
{
    /// <summary>Which fan.</summary>
    public SensorId Id { get; } = id;

    /// <summary>What the hardware calls it.</summary>
    public string Name { get; } = name;

    /// <summary>Whether this plugin currently has it.</summary>
    [ObservableProperty]
    public partial bool Granted { get; set; } = granted;

    /// <summary>
    /// Whether it could be granted at all: the engine drives it and it has a curve to fall back to.
    /// </summary>
    /// <remarks>
    /// A fan that fails this cannot be given to a plugin, because there would be nothing for the
    /// fan to return to when the plugin let go or died. Shown greyed with a reason rather than
    /// hidden, so switching a fan on is a discoverable fix rather than a mystery.
    /// </remarks>
    public bool Claimable { get; } = claimable;

    /// <summary>Why it cannot be granted, or null when it can.</summary>
    public string? Obstacle { get; } = claimable
        ? null
        : "Impeller is not driving this fan — switch it on and give it a curve first.";
}

/// <summary>
/// One plugin, as the card that shows it and the four things a person can do about it.
/// </summary>
/// <remarks>
/// <para>
/// The four verbs are deliberately distinct and the card must never conflate them. <em>Approve</em>
/// grants what was asked for. <em>Revoke</em> takes one fan back and leaves everything else alone.
/// <em>Disable</em> switches the whole plugin off but keeps its grants, so switching it on again is
/// one click rather than configuring it from scratch. <em>Forget</em> erases it, so the next time it
/// connects the user is asked afresh.
/// </para>
/// <para>
/// Reaching for the wrong one has real consequences: a user who disables a plugin because they
/// wanted one fan back ends up with a plugin they have forgotten they switched off, wondering why
/// it never works.
/// </para>
/// </remarks>
public sealed partial class PluginCardViewModel : ObservableObject
{
    private readonly Func<PluginCardViewModel, PluginAction, Task> _act;

    public PluginCardViewModel(
        PluginSummary summary,
        IEnumerable<GrantedFanViewModel> fans,
        Func<PluginCardViewModel, PluginAction, Task> act)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(act);

        _act = act;
        Summary = summary;
        Id = summary.Id;

        foreach (var fan in fans)
        {
            Fans.Add(fan);
        }

        WantsReadSensors = summary.Granted.Contains(PluginCapability.ReadSensors)
            || summary.Requested.Contains(PluginCapability.ReadSensors);

        GrantReadSensors = summary.Granted.Contains(PluginCapability.ReadSensors);
    }

    /// <summary>The engine's word on this plugin.</summary>
    public PluginSummary Summary { get; }

    /// <summary>The manifest id. Shown small, because it is a key rather than a name.</summary>
    public string Id { get; }

    /// <summary>What to call it.</summary>
    public string DisplayName => Summary.DisplayName;

    /// <summary>Its own version, as last seen.</summary>
    public string Version => Summary.Version;

    /// <summary>Whether it is connected right now.</summary>
    public bool IsConnected => Summary.Connected;

    /// <summary>Whether the user has switched it off.</summary>
    public bool IsEnabled => Summary.Enabled;

    /// <summary>Whether anything is still waiting on the user.</summary>
    public bool NeedsAnswer => Summary.NeedsAnswer;

    /// <summary>Whether it asked for reads at all, so the tick box is worth showing.</summary>
    public bool WantsReadSensors { get; }

    /// <summary>Whether reads are currently granted.</summary>
    [ObservableProperty]
    public partial bool GrantReadSensors { get; set; }

    /// <summary>The fans, with a tick against the ones this plugin has.</summary>
    public ObservableCollection<GrantedFanViewModel> Fans { get; } = [];

    /// <summary>Whether a request to the engine is in flight.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>A plain statement of where this plugin stands.</summary>
    public string StateText => Summary switch
    {
        { Enabled: false } => "Switched off",
        { IdentityChanged: true } => "Running from a different program — needs approving again",
        { State: PluginAdmissionState.Pending } => "Waiting to be approved",
        { State: PluginAdmissionState.Refused } => "Refused",
        { Connected: true } => "Approved and connected",
        _ => "Approved, not running",
    };

    /// <summary>What it may do, in words rather than enum names.</summary>
    public string GrantsText
    {
        get
        {
            if (Summary.State != PluginAdmissionState.Approved)
            {
                return "Nothing granted yet.";
            }

            var reads = Summary.Granted.Contains(PluginCapability.ReadSensors)
                ? "may read sensors"
                : "may not read sensors";

            var fans = Summary.Controls.Count switch
            {
                0 => "no fans",
                1 => "one fan",
                var count => $"{count} fans",
            };

            return $"Can drive {fans}, and {reads}.";
        }
    }

    /// <summary>Where it last ran from, for the user to recognise or not.</summary>
    public string ProgramText => Summary.ImagePath ?? "Impeller could not identify the program.";

    /// <summary>
    /// Whether this plugin asked for something the engine does not implement.
    /// </summary>
    /// <remarks>
    /// Said out loud rather than shown as an ungranted request, so nobody spends an evening trying
    /// to work out which tick box turns it on.
    /// </remarks>
    public bool WantsHardwareProvision =>
        Summary.Requested.Contains(PluginCapability.ProvideHardware);

    /// <summary>Approves the plugin with whatever is currently ticked.</summary>
    [RelayCommand]
    private Task ApproveAsync() => _act(this, PluginAction.Approve);

    /// <summary>Switches it off, or back on.</summary>
    [RelayCommand]
    private Task ToggleEnabledAsync() => _act(this, PluginAction.ToggleEnabled);

    /// <summary>Erases it, so the next connection asks again.</summary>
    [RelayCommand]
    private Task ForgetAsync() => _act(this, PluginAction.Forget);
}

/// <summary>What a card asked the page to do.</summary>
public enum PluginAction
{
    /// <summary>Apply whatever is ticked as the plugin's permissions.</summary>
    Approve = 0,

    /// <summary>Switch the plugin off, or back on.</summary>
    ToggleEnabled,

    /// <summary>Drop it from the record entirely.</summary>
    Forget,
}
