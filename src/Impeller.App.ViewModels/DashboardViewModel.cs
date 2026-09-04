using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Impeller.App.ViewModels.Controls;
using Impeller.App.ViewModels.Engine;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Ipc.Contracts;

namespace Impeller.App.ViewModels;

/// <summary>A curve, as an entry in the picker on a control's card.</summary>
/// <param name="Id">Which curve.</param>
/// <param name="Name">What the user calls it.</param>
public readonly record struct CurveChoice(CurveId Id, string Name)
{
    /// <summary>
    /// The entry meaning the engine does not drive this fan.
    /// </summary>
    /// <remarks>
    /// Named for the consequence rather than the mechanism. "No curve" describes the configuration;
    /// "Not driven" describes the fan, which is the thing the person is looking at.
    /// </remarks>
    public static CurveChoice None => new(CurveId.None, "Not driven");

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A fan the engine can see that is not in the configuration yet.</summary>
/// <param name="Id">Which control.</param>
/// <param name="Name">What the hardware calls it.</param>
/// <param name="HardwareName">What it hangs off, to tell two identically named fans apart.</param>
public readonly record struct AvailableFan(SensorId Id, string Name, string HardwareName)
{
    /// <inheritdoc />
    public override string ToString() =>
        string.IsNullOrWhiteSpace(HardwareName) ? Name : $"{Name} — {HardwareName}";
}

/// <summary>
/// The fan overview: the page the app opens on and the one people leave open.
/// </summary>
/// <remarks>
/// <para>
/// Shows the fans in the configuration, not every writable control on the machine. A motherboard
/// offers headers nobody has wired anything to, and a page listing all of them buries the four that
/// matter. Anything not listed is one click away behind <em>Add a fan</em>.
/// </para>
/// <para>
/// Reads from the engine and never touches hardware itself. The shell holds no engine types at
/// all — everything here arrives over the channel, which is what lets the engine keep running when
/// this window is closed, and what would let a different front end replace it.
/// </para>
/// </remarks>
public sealed partial class DashboardViewModel(EngineConnection connection)
    : EnginePageViewModel(connection)
{
    private readonly Dictionary<SensorId, ControlCardViewModel> _cards = [];
    private readonly Dictionary<SensorId, SensorId> _tachometers = [];
    private readonly Dictionary<string, string> _pluginNames = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public override string Title => "Dashboard";

    /// <summary>How many sensors the engine can see.</summary>
    [ObservableProperty]
    public partial int SensorCount { get; private set; }

    /// <summary>Whatever the last edit from this page turned up, or null.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <summary>Whether the configuration names no fans at all.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>The fans in the configuration, with what is driving each.</summary>
    public ObservableCollection<ControlCardViewModel> Controls { get; } = [];

    /// <summary>The curves a fan can be pointed at.</summary>
    public ObservableCollection<CurveChoice> Curves { get; } = [];

    /// <summary>Fans the engine can see that are not in the configuration.</summary>
    public ObservableCollection<AvailableFan> Available { get; } = [];

    /// <inheritdoc />
    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await base.LoadAsync(cancellationToken).ConfigureAwait(true);

        Connection.PluginsChanged += OnPluginsChanged;

        await RefreshPluginNamesAsync().ConfigureAwait(true);
    }

    /// <inheritdoc />
    protected override void OnDisposing()
    {
        Connection.PluginsChanged -= OnPluginsChanged;

        foreach (var card in _cards.Values)
        {
            // AsTask, because a discarded ValueTask is a bug waiting to happen and the analyser is
            // right to say so. There is nothing here to await against - the page is going away.
            _ = card.DisposeAsync().AsTask();
        }

        _cards.Clear();
    }

    /// <inheritdoc />
    protected override void OnSnapshot(EngineSnapshot snapshot)
    {
        SensorCount = snapshot.Sensors.Count;

        Curves.Clear();
        Curves.Add(CurveChoice.None);

        foreach (var curve in snapshot.Configuration.Curves)
        {
            Curves.Add(new CurveChoice(curve.Id, curve.Name));
        }

        _tachometers.Clear();

        foreach (var binding in snapshot.Configuration.Controls)
        {
            if (!binding.PairedFanSensorId.IsNone)
            {
                _tachometers[binding.ControlId] = binding.PairedFanSensorId;
            }
        }

        Rebuild(snapshot);
        _ = RefreshPluginNamesAsync();
    }

    /// <inheritdoc />
    protected override void OnTick(TickSnapshot tick)
    {
        // Matched by id and written into the existing cards rather than rebuilt, so the list does
        // not flicker once a second and whatever the user has open stays open.
        var speeds = tick.Sensors.ToDictionary(reading => reading.Id, reading => reading.Value);

        foreach (var reading in tick.Controls)
        {
            if (!_cards.TryGetValue(reading.Id, out var card))
            {
                continue;
            }

            var rpm = _tachometers.TryGetValue(reading.Id, out var tachometer)
                ? speeds.GetValueOrDefault(tachometer)
                : null;

            card.Apply(reading, rpm);
        }
    }

    /// <summary>
    /// Adds a fan to the configuration, not driven yet.
    /// </summary>
    /// <remarks>
    /// Deliberately without a curve, so adding a fan never starts writing to it. It appears as a
    /// card saying it is not driven, and picking a curve is the deliberate second step that starts
    /// it — which is the same step that makes it eligible for a plugin.
    /// </remarks>
    [RelayCommand]
    private async Task AddFanAsync(AvailableFan fan)
    {
        if (fan.Id.IsNone)
        {
            return;
        }

        await SaveAsync(new ControlBindingDefinition
        {
            ControlId = fan.Id,
            CurveId = CurveId.None,
            Enabled = false,
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Rebuilds the list of cards, keeping the ones that are still here.
    /// </summary>
    /// <remarks>
    /// Reused rather than recreated. A card holds the position of its manual slider and whatever
    /// went wrong the last time it was pressed, and throwing that away every time a configuration
    /// is saved would take the user's place from under them mid-adjustment.
    /// </remarks>
    private void Rebuild(EngineSnapshot snapshot)
    {
        var present = snapshot.Controls.ToDictionary(descriptor => descriptor.Id);
        var configured = new HashSet<SensorId>();

        Controls.Clear();

        foreach (var binding in snapshot.Configuration.Controls)
        {
            configured.Add(binding.ControlId);

            var descriptor = present.GetValueOrDefault(binding.ControlId);

            if (_cards.TryGetValue(binding.ControlId, out var existing))
            {
                existing.Rebind(binding, Curves);

                if (descriptor is not null)
                {
                    existing.Apply(
                        new ControlReading(
                            descriptor.Id,
                            descriptor.CommandedDuty,
                            descriptor.Owner,
                            descriptor.ClaimantId),
                        null);
                }

                Controls.Add(existing);
                continue;
            }

            var card = new ControlCardViewModel(
                descriptor,
                binding,
                Curves,
                Connection,
                SaveAsync,
                NameClaimant);

            _cards[binding.ControlId] = card;
            Controls.Add(card);
        }

        // A card for a binding that is no longer in the configuration is dropped, and its writer
        // with it — otherwise a removed fan keeps a rate limiter alive for the life of the window.
        foreach (var id in _cards.Keys.Where(id => !configured.Contains(id)).ToList())
        {
            if (_cards.Remove(id, out var stale))
            {
                _ = stale.DisposeAsync().AsTask();
            }
        }

        Available.Clear();

        foreach (var descriptor in snapshot.Controls.Where(control => !configured.Contains(control.Id)))
        {
            Available.Add(new AvailableFan(descriptor.Id, descriptor.Name, descriptor.HardwareName));
        }

        IsEmpty = Controls.Count == 0;
    }

    /// <summary>
    /// Sends one changed binding as a whole configuration.
    /// </summary>
    /// <remarks>
    /// All or nothing, because that is what the engine offers and what makes an edit safe: a
    /// configuration with errors never reaches the tick loop, so a bad change leaves the fans on
    /// the last good one rather than half-switching into a broken state.
    /// </remarks>
    private async Task SaveAsync(ControlBindingDefinition binding)
    {
        if (Connection.Engine is not { } engine || Snapshot is not { } snapshot)
        {
            Problem = "Not connected to the engine.";
            return;
        }

        var controls = snapshot.Configuration.Controls.ToArray();
        var index = Array.FindIndex(controls, control => control.ControlId == binding.ControlId);

        if (index < 0)
        {
            controls = [.. controls, binding];
        }
        else
        {
            controls[index] = binding;
        }

        var configuration = snapshot.Configuration with { Controls = [.. controls] };

        try
        {
            var result = await engine.ApplyConfigurationAsync(configuration).ConfigureAwait(true);

            Problem = result.Applied
                ? null
                : string.Join(" ", result.Validation.Issues.Select(issue => issue.Message));
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
        }
    }

    /// <summary>
    /// Turns a claimant id into something worth showing a person.
    /// </summary>
    /// <remarks>
    /// Resolved here rather than by the engine, so a tick carries an id and nothing more. The map
    /// is small, changes only when the user changes something, and the fallback is the id itself —
    /// which is at least searchable.
    /// </remarks>
    private string? NameClaimant(string? claimantId) =>
        claimantId is null ? null : _pluginNames.GetValueOrDefault(claimantId, claimantId);

    private async Task RefreshPluginNamesAsync()
    {
        if (Connection.Engine is not { } engine)
        {
            return;
        }

        try
        {
            var plugins = await engine.ListPluginsAsync().ConfigureAwait(true);

            _pluginNames.Clear();

            foreach (var plugin in plugins)
            {
                _pluginNames[plugin.Id] = plugin.DisplayName;
            }
        }
        catch (Exception)
        {
            // A card falling back to a manifest id is a cosmetic loss, and not one worth putting an
            // error on the page for.
        }
    }

    private void OnPluginsChanged(object? sender, EventArgs e) => _ = RefreshPluginNamesAsync();
}
