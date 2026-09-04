using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Impeller.App.ViewModels.Engine;
using Impeller.App.ViewModels.Plugins;
using Impeller.Ipc.Contracts;
using Impeller.Plugins.Abstractions;

namespace Impeller.App.ViewModels;

/// <summary>
/// Every program that has asked to drive this machine's fans, and what the user let it do.
/// </summary>
/// <remarks>
/// <para>
/// This page is the only place a plugin's permissions are decided, and the engine grants nothing
/// without it. A plugin that has never been through here is <em>pending</em>: it may connect, say
/// hello, read nothing, drive nothing, and wait. That is the default because a program that has
/// just turned up asking to control the cooling should not get it by turning up.
/// </para>
/// <para>
/// The page names the program's path and the account it ran as, because a manifest id is a string
/// any program can announce, and a standing grant keyed only to a string is one any program can
/// inherit. It is careful not to imply more than it delivers: this is a defence against a different
/// program quietly picking up someone else's permissions, not against an attacker already running
/// as the user.
/// </para>
/// </remarks>
public sealed partial class PluginsViewModel(EngineConnection connection) : EnginePageViewModel(connection)
{
    private IReadOnlyList<ControlDescriptor> _controls = [];

    /// <inheritdoc />
    public override string Title => "Plugins";

    /// <summary>The plugins, newest first — the one that just turned up is the one being asked about.</summary>
    public ObservableCollection<PluginCardViewModel> Plugins { get; } = [];

    /// <summary>Whether the engine has been asked and answered with nothing.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>How many are waiting on the user, for a heading that says so.</summary>
    [ObservableProperty]
    public partial int WaitingCount { get; private set; }

    /// <summary>Whatever went wrong, or null.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <inheritdoc />
    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await base.LoadAsync(cancellationToken).ConfigureAwait(true);

        Connection.PluginsChanged += OnPluginsChanged;

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <inheritdoc />
    protected override void OnDisposing() => Connection.PluginsChanged -= OnPluginsChanged;

    /// <inheritdoc />
    /// <remarks>
    /// A configuration change can make a fan claimable or stop it being claimable, so the tick
    /// boxes are rebuilt against the new one rather than left describing the old.
    /// </remarks>
    protected override void OnSnapshot(EngineSnapshot snapshot)
    {
        _controls = snapshot.Controls;
        _ = RefreshAsync();
    }

    /// <summary>Asks the engine what it knows and rebuilds the list.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Connection.Engine is not { } engine)
        {
            Problem = "Not connected to the engine.";
            return;
        }

        try
        {
            var summaries = await engine.ListPluginsAsync().ConfigureAwait(true);

            Plugins.Clear();

            foreach (var summary in summaries.OrderByDescending(entry => entry.FirstSeenAt))
            {
                Plugins.Add(new PluginCardViewModel(summary, FansFor(summary), ActAsync));
            }

            IsEmpty = Plugins.Count == 0;
            WaitingCount = Plugins.Count(plugin => plugin.NeedsAnswer);
            Problem = null;
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
        }
    }

    /// <summary>The fans this plugin could have, with the ones it does have ticked.</summary>
    private IEnumerable<GrantedFanViewModel> FansFor(PluginSummary summary) =>
        _controls.Select(control => new GrantedFanViewModel(
            control.Id,
            control.Name,
            summary.Controls.Contains(control.Id),

            // Mirrored here only to grey the box out and say why. The engine applies the same rule
            // and refuses regardless, so a stale view costs a refusal rather than a bad grant.
            IsClaimable(control)));

    /// <summary>
    /// Whether a fan is one a plugin could be given.
    /// </summary>
    /// <remarks>
    /// The shell cannot see the binding directly, but a control the engine is driving reports a
    /// commanded duty and one it is not reports none. That is enough to grey the box, which is all
    /// this has to do — the engine holds the real rule.
    /// </remarks>
    private static bool IsClaimable(ControlDescriptor control) => control.CommandedDuty is not null;

    private async Task ActAsync(PluginCardViewModel card, PluginAction action)
    {
        if (Connection.Engine is not { } engine)
        {
            Problem = "Not connected to the engine.";
            return;
        }

        card.IsBusy = true;

        try
        {
            switch (action)
            {
                case PluginAction.Approve:
                    await ApproveAsync(engine, card).ConfigureAwait(true);
                    break;

                case PluginAction.ToggleEnabled:
                    await engine.SetPluginEnabledAsync(card.Id, !card.IsEnabled).ConfigureAwait(true);
                    break;

                case PluginAction.Forget:
                    await engine.ForgetPluginAsync(card.Id).ConfigureAwait(true);
                    break;

                default:
                    break;
            }

            Problem = null;
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
        }
        finally
        {
            card.IsBusy = false;
        }

        // Reloaded from the engine rather than assumed, so what the page shows is what was actually
        // stored rather than what it asked for.
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Sends the whole set of permissions as one decision.
    /// </summary>
    /// <remarks>
    /// One call rather than a grant per fan, so a user ticking three boxes and pressing Approve gets
    /// three fans or none — never a plugin left holding two of them because the third failed.
    /// </remarks>
    private static async Task ApproveAsync(IEngineControl engine, PluginCardViewModel card)
    {
        var capabilities = new List<PluginCapability>();

        if (card.GrantReadSensors)
        {
            capabilities.Add(PluginCapability.ReadSensors);
        }

        var fans = card.Fans
            .Where(fan => fan.Granted && fan.Claimable)
            .Select(fan => fan.Id)
            .ToArray();

        if (fans.Length > 0)
        {
            capabilities.Add(PluginCapability.ControlFans);
        }

        await engine.ApprovePluginAsync(card.Id, [.. capabilities], [.. fans]).ConfigureAwait(true);
    }

    private void OnPluginsChanged(object? sender, EventArgs e) => _ = RefreshAsync();
}
