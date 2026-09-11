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

    /// <summary>What to call it: the user's name for it, or the provider's when they gave none.</summary>
    public string Name { get; } = name;

    /// <summary>Whether this plugin currently has it.</summary>
    [ObservableProperty]
    public partial bool Granted { get; set; } = granted;

    /// <summary>
    /// Whether it could be granted at all, which now means only that the engine is driving it.
    /// </summary>
    /// <remarks>
    /// It also required a curve, once, on the reasoning that the curve was what the fan returned to
    /// when the plugin let go. Every enabled fan has a resting state of its own now, so the engine
    /// answers this from whether the fan is switched on and nothing else. Shown greyed with a
    /// reason rather than hidden, so switching a fan on is a discoverable fix rather than a mystery.
    /// </remarks>
    public bool Claimable { get; } = claimable;

    /// <summary>Why it cannot be granted, or null when it can.</summary>
    public string? Obstacle { get; } = claimable
        ? null
        : "Impeller is not driving this fan — switch it on in the Dashboard first.";
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

    private PluginSummary _summary;

    public PluginCardViewModel(
        PluginSummary summary,
        IEnumerable<GrantedFanViewModel> fans,
        Func<PluginCardViewModel, PluginAction, Task> act)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(act);

        _act = act;
        _summary = summary;
        Id = summary.Id;

        foreach (var fan in fans)
        {
            Watch(fan);
            Fans.Add(fan);
        }

        WantsReadSensors = summary.Granted.Contains(PluginCapability.ReadSensors)
            || summary.Requested.Contains(PluginCapability.ReadSensors);

        WantsControlFans = summary.Granted.Contains(PluginCapability.ControlFans)
            || summary.Requested.Contains(PluginCapability.ControlFans);

        HasFans = Fans.Count > 0;

        GrantReadSensors = summary.Granted.Contains(PluginCapability.ReadSensors);
        GrantProvideHardware = summary.Granted.Contains(PluginCapability.ProvideHardware);
    }

    /// <summary>The engine's word on this plugin.</summary>
    public PluginSummary Summary => _summary;

    /// <summary>
    /// Takes a fresh answer from the engine without disturbing what the user is doing.
    /// </summary>
    /// <remarks>
    /// The tick boxes are re-seeded only when the engine's own grants have changed. Otherwise they
    /// are left exactly as the user set them, because a plugin reconnecting in the background is
    /// not a reason to undo half a decision.
    /// </remarks>
    public void Update(PluginSummary summary, IEnumerable<GrantedFanViewModel> fans)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var grantsChanged = !summary.Granted.SequenceEqual(_summary.Granted)
            || !summary.Controls.SequenceEqual(_summary.Controls);

        _summary = summary;

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(NeedsAnswer));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(GrantsText));
        OnPropertyChanged(nameof(ProgramText));
        OnPropertyChanged(nameof(CanApply));

        var replacements = fans.ToArray();

        // Rebuilt when the shape of the machine changed - a fan appearing, or becoming claimable -
        // or when the engine's grants moved under us. A plain reconnect changes neither.
        if (grantsChanged
            || replacements.Length != Fans.Count
            || !replacements.Zip(Fans).All(pair =>
                pair.First.Id == pair.Second.Id && pair.First.Claimable == pair.Second.Claimable))
        {
            foreach (var fan in Fans)
            {
                fan.PropertyChanged -= OnFanChanged;
            }

            Fans.Clear();

            foreach (var fan in replacements)
            {
                Watch(fan);
                Fans.Add(fan);
            }

            HasFans = Fans.Count > 0;
            OnPropertyChanged(nameof(HasFans));
            GrantReadSensors = summary.Granted.Contains(PluginCapability.ReadSensors);
            GrantProvideHardware = summary.Granted.Contains(PluginCapability.ProvideHardware);
            OnPropertyChanged(nameof(CanApply));
        }
    }

    /// <summary>Recomputes <see cref="CanApply"/> whenever a fan's tick box moves.</summary>
    private void Watch(GrantedFanViewModel fan) => fan.PropertyChanged += OnFanChanged;

    private void OnFanChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GrantedFanViewModel.Granted))
        {
            OnPropertyChanged(nameof(CanApply));
        }
    }

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

    /// <summary>Whether it asked to drive fans at all, so the section is worth showing.</summary>
    public bool WantsControlFans { get; }

    /// <summary>Whether reads are currently granted.</summary>
    [ObservableProperty]
    public partial bool GrantReadSensors { get; set; }

    partial void OnGrantReadSensorsChanged(bool value) => OnPropertyChanged(nameof(CanApply));

    /// <summary>Whether this plugin may hand the engine hardware of its own.</summary>
    [ObservableProperty]
    public partial bool GrantProvideHardware { get; set; }

    partial void OnGrantProvideHardwareChanged(bool value) => OnPropertyChanged(nameof(CanApply));

    /// <summary>The fans, with a tick against the ones this plugin has.</summary>
    public ObservableCollection<GrantedFanViewModel> Fans { get; } = [];

    /// <summary>Whether there are any fans to show at all.</summary>
    /// <remarks>
    /// An empty list needs a sentence rather than an empty box. It means the shell has not received
    /// the machine's controls yet, and saying so beats leaving someone staring at nothing. Bound
    /// OneWay, like everything else computed on this card, because it becomes true later.
    /// </remarks>
    public bool HasFans { get; private set; }

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
    /// <remarks>
    /// Built only from what this plugin actually asked for. A plugin that never requested
    /// <see cref="PluginCapability.ControlFans"/> has no fans to speak of, and saying "can drive no
    /// fans" about it is not a fact worth stating — it reads as a problem where there is none.
    /// </remarks>
    public string GrantsText
    {
        get
        {
            if (Summary.State != PluginAdmissionState.Approved)
            {
                return "Nothing granted yet.";
            }

            var parts = new List<string>(3);

            if (WantsReadSensors)
            {
                parts.Add(Summary.Granted.Contains(PluginCapability.ReadSensors)
                    ? "may read sensors"
                    : "may not read sensors");
            }

            if (WantsControlFans)
            {
                parts.Add(Summary.Controls.Count switch
                {
                    0 => "can drive no fans",
                    1 => "can drive one fan",
                    var count => $"can drive {count} fans",
                });
            }

            if (WantsHardwareProvision)
            {
                parts.Add(Summary.Granted.Contains(PluginCapability.ProvideHardware)
                    ? "may offer the engine its own hardware"
                    : "may not offer the engine its own hardware");
            }

            if (parts.Count == 0)
            {
                return "Approved, but asked for nothing.";
            }

            var sentence = string.Join(", and ", parts);
            return char.ToUpperInvariant(sentence[0]) + sentence[1..] + ".";
        }
    }

    /// <summary>Where it last ran from, for the user to recognise or not.</summary>
    public string ProgramText => Summary.ImagePath ?? "Impeller could not identify the program.";

    /// <summary>Whether it asked to offer the engine hardware of its own, so the tick box is worth showing.</summary>
    public bool WantsHardwareProvision =>
        Summary.Granted.Contains(PluginCapability.ProvideHardware)
        || Summary.Requested.Contains(PluginCapability.ProvideHardware);

    /// <summary>
    /// Whether pressing Apply would change anything.
    /// </summary>
    /// <remarks>
    /// A plugin still <see cref="PluginAdmissionState.Pending"/> can always be applied — approving
    /// it for the first time is a real action even when every box is left unticked, since Pending
    /// and Approved-with-nothing-granted are different states. Past that first approval, the button
    /// does nothing useful once the ticks already match what the engine has, and a button that can
    /// always be pressed teaches nobody that pressing it does anything.
    /// </remarks>
    public bool CanApply =>
        Summary.State == PluginAdmissionState.Pending
        || GrantReadSensors != Summary.Granted.Contains(PluginCapability.ReadSensors)
        || GrantProvideHardware != Summary.Granted.Contains(PluginCapability.ProvideHardware)
        || !new HashSet<SensorId>(Fans.Where(fan => fan.Granted && fan.Claimable).Select(fan => fan.Id))
            .SetEquals(Summary.Controls);

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
