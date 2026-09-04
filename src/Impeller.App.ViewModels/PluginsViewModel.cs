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
    /// A configuration change can make a fan claimable or stop it being claimable, so the rows are
    /// rebuilt against the new one rather than left describing the old.
    /// </remarks>
    protected override void OnSnapshot(EngineSnapshot snapshot) => _ = RefreshAsync();

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

            var ordered = summaries.OrderByDescending(entry => entry.FirstSeenAt).ToArray();

            // Updated in place where the plugin is already on screen, rather than cleared and
            // rebuilt. A plugin reconnecting announces a change, and rebuilding would wipe the
            // boxes the user was in the middle of ticking - which, on the one page whose whole
            // purpose is ticking boxes, is not a small annoyance.
            if (ordered.Length == Plugins.Count
                && ordered.Zip(Plugins).All(pair => pair.First.Id == pair.Second.Id))
            {
                foreach (var (summary, card) in ordered.Zip(Plugins))
                {
                    card.Update(summary, FansFor(summary));
                }
            }
            else
            {
                Plugins.Clear();

                foreach (var summary in ordered)
                {
                    Plugins.Add(new PluginCardViewModel(summary, FansFor(summary), ActAsync));
                }
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
    /// <remarks>
    /// Read from the connection at the moment of use rather than cached from a snapshot event, so a
    /// page opened before the first snapshot arrives is not left with an empty list it never fills.
    /// </remarks>
    private IEnumerable<GrantedFanViewModel> FansFor(PluginSummary summary) =>
        (Connection.Snapshot?.Controls ?? [])
            .Select(control => new GrantedFanViewModel(
                control.Id,
                control.Name,
                summary.Controls.Contains(control.Id),

                // The engine's own answer, carried on the descriptor. Deriving it here from a
                // commanded duty was wrong in both directions: it enabled a fan that is pinned by
                // hand but has no curve, and it would have disabled one the engine had not yet
                // written to.
                control.Claimable));

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
