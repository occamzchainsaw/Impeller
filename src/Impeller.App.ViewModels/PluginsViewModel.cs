using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Impeller.App.ViewModels.Engine;
using Impeller.App.ViewModels.Notifications;
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
public sealed partial class PluginsViewModel(EngineConnection connection, NotificationCenter notifications)
    : EnginePageViewModel(connection, notifications)
{
    /// <inheritdoc />
    public override string Title => "Plugins";

    /// <summary>The plugins, newest first — the one that just turned up is the one being asked about.</summary>
    public ObservableCollection<PluginCardViewModel> Plugins { get; } = [];

    /// <summary>
    /// The refresh currently running, or the last one that did.
    /// </summary>
    /// <remarks>
    /// Everything here runs on the UI thread and only yields at an await, so a plain field is
    /// enough to serialise them — no lock, and no chance of two rebuilds interleaving.
    /// </remarks>
    private Task _refresh = Task.CompletedTask;

    /// <summary>Whether the engine has been asked and answered with nothing.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>How many are waiting on the user, for a heading that says so.</summary>
    [ObservableProperty]
    public partial int WaitingCount { get; private set; }

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

    /// <summary>
    /// Asks the engine what it knows and rebuilds the list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One at a time, and that is a fix rather than caution. Opening the page calls this while the
    /// first snapshot is arriving, which calls it again; both then wait on
    /// <c>ListPluginsAsync</c>, and both resume past the guard below to clear the list and add to
    /// it — leaving every plugin on screen twice.
    /// </para>
    /// <para>
    /// Each call queues behind the one before it rather than being dropped, so a trigger that
    /// arrives mid-refresh still gets a rebuild against what it saw — and so awaiting this still
    /// means the list is up to date, which the page's own <c>LoadAsync</c> relies on.
    /// </para>
    /// </remarks>
    [RelayCommand]
    private Task RefreshAsync()
    {
        // Chained rather than skipped, so that awaiting this still means the list is rebuilt. A
        // caller told "one is already running" would return before the answer it asked for existed,
        // and the page's own LoadAsync is one of those callers.
        _refresh = Chain(_refresh);
        return _refresh;

        async Task Chain(Task previous)
        {
            try
            {
                await previous.ConfigureAwait(true);
            }
            catch (Exception)
            {
                // A previous refresh that failed has already reported itself. It must not stop the
                // next one, or one bad answer would wedge the page for the session.
            }

            await RefreshOnceAsync().ConfigureAwait(true);
        }
    }

    private async Task RefreshOnceAsync()
    {
        if (Connection.Engine is not { } engine)
        {
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
        }
        catch (Exception ex)
        {
            Notify.Error("Could not read the plugin list.", ex);
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

                // The user's name for it, not the provider's. Deciding which program may drive
                // "Front Intake" is a question about the fan they named, and answering it with
                // "Fan #3" makes them translate their own labels back into the hardware's. The
                // engine already resolves this on the descriptor, and every other surface - the
                // dashboard, the sensors tree, what a plugin is told - uses the resolved one.
                control.DisplayName,
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
            Notify.Error("Not connected to the engine.", "Nothing was changed.");
            return;
        }

        card.IsBusy = true;

        try
        {
            switch (action)
            {
                case PluginAction.Approve:
                    // Said out loud, listing exactly what was granted. Deciding what another
                    // program may do to this machine's cooling is the one action in the whole
                    // shell that must never complete in silence - and it did, which is why this
                    // mechanism exists at all.
                    Notify.Success($"{card.DisplayName} approved.", await ApproveAsync(engine, card).ConfigureAwait(true));
                    break;

                case PluginAction.ToggleEnabled:
                    var enabling = !card.IsEnabled;
                    await engine.SetPluginEnabledAsync(card.Id, enabling).ConfigureAwait(true);

                    Notify.Success(
                        enabling ? $"{card.DisplayName} switched on." : $"{card.DisplayName} switched off.",
                        enabling
                            ? "It may connect again. Its permissions were kept."
                            : "Any fan it was driving has gone back to Impeller.");
                    break;

                case PluginAction.Forget:
                    await engine.ForgetPluginAsync(card.Id).ConfigureAwait(true);

                    Notify.Success(
                        $"{card.DisplayName} forgotten.",
                        "The next time it connects you will be asked about it as if it were new.");
                    break;

                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            Notify.Error("The plugin was not changed.", ex);
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
    /// Sends the whole set of permissions as one decision, and describes what was sent.
    /// </summary>
    /// <remarks>
    /// One call rather than a grant per fan, so a user ticking three boxes and pressing Approve gets
    /// three fans or none — never a plugin left holding two of them because the third failed.
    /// </remarks>
    /// <returns>What was granted, in the words the confirmation shows.</returns>
    private static async Task<string> ApproveAsync(IEngineControl engine, PluginCardViewModel card)
    {
        var capabilities = new List<PluginCapability>();

        if (card.GrantReadSensors)
        {
            capabilities.Add(PluginCapability.ReadSensors);
        }

        var granted = card.Fans
            .Where(fan => fan.Granted && fan.Claimable)
            .ToArray();

        if (granted.Length > 0)
        {
            capabilities.Add(PluginCapability.ControlFans);
        }

        await engine
            .ApprovePluginAsync(card.Id, [.. capabilities], [.. granted.Select(fan => fan.Id)])
            .ConfigureAwait(true);

        return Describe(card.GrantReadSensors, granted);
    }

    /// <summary>
    /// Names every fan rather than counting them.
    /// </summary>
    /// <remarks>
    /// "May drive 3 fans" is not a confirmation anybody can check. The whole value of saying it back
    /// is that a user who ticked the wrong box can see they did.
    /// </remarks>
    private static string Describe(bool reads, GrantedFanViewModel[] fans)
    {
        var parts = new List<string>(2);

        if (reads)
        {
            parts.Add("May read this machine's sensors");
        }

        parts.Add(fans.Length == 0
            ? "may drive no fans"
            : "may drive " + string.Join(", ", fans.Select(fan => fan.Name)));

        // Only capitalised when the reads clause did not already open the sentence.
        if (!reads)
        {
            parts[0] = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
        }

        return string.Join(", and ", parts) + ".";
    }

    private void OnPluginsChanged(object? sender, EventArgs e) => _ = RefreshAsync();
}
