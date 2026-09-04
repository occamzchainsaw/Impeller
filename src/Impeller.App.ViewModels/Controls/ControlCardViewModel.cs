using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Impeller.App.ViewModels.Engine;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Ipc.Contracts;

namespace Impeller.App.ViewModels.Controls;

/// <summary>
/// One fan, as the card that shows it and the controls that change it.
/// </summary>
/// <remarks>
/// <para>
/// The card is the app's smallest complete unit: what this fan is doing, what is deciding that, and
/// the things a person wants to do about it — point it at a curve, take it by hand, hand it back,
/// and make it spin up so they can work out which one it is behind the case panel.
/// </para>
/// <para>
/// Everything it changes goes through the engine, and it holds no authority of its own. Taking a
/// fan by hand is a claim the engine grants or refuses, and a refusal is displayed rather than
/// worked around: two things quietly fighting over one fan is the failure this whole ownership
/// model exists to make impossible.
/// </para>
/// <para>
/// When something else holds the fan the card names it and the slider goes dead. A live slider on a
/// fan somebody else is driving is a control that silently does nothing, which is worse than no
/// control at all.
/// </para>
/// </remarks>
public sealed partial class ControlCardViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>The shortest gap between two duty writes while the slider is moving.</summary>
    private static readonly TimeSpan WriteInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How long an identify run spins the fan up for.</summary>
    private static readonly TimeSpan IdentifyDuration = TimeSpan.FromSeconds(5);

    /// <summary>What an identify run spins it up to.</summary>
    private static readonly Duty IdentifyDuty = new(100f);

    private readonly EngineConnection _connection;
    private readonly Func<ControlBindingDefinition, Task> _save;
    private readonly Func<string?, string?> _nameClaimant;
    private readonly ThrottledWriter<Duty> _writer;

    private ControlBindingDefinition _binding;
    private bool _suppressWrite;
    private bool _suppressCurve;

    public ControlCardViewModel(
        ControlDescriptor? descriptor,
        ControlBindingDefinition binding,
        IEnumerable<CurveChoice> curves,
        EngineConnection connection,
        Func<ControlBindingDefinition, Task> save,
        Func<string?, string?> nameClaimant)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(nameClaimant);

        _connection = connection;
        _save = save;
        _nameClaimant = nameClaimant;
        _binding = binding;
        _writer = new ThrottledWriter<Duty>(TimeProvider.System, WriteInterval, SendDutyAsync);

        Id = binding.ControlId;
        IsPresent = descriptor is not null;
        HardwarePath = descriptor?.HardwarePath ?? string.Empty;

        Rebind(descriptor, binding, curves);

        // Where the slider starts. A stored pin is what the user last chose; otherwise the duty
        // standing now, so taking a fan by hand does not jolt it on the way.
        PinDuty = binding.ManualDuty?.Percent ?? descriptor?.CommandedDuty?.Percent ?? 0f;

        if (descriptor is not null)
        {
            Apply(
                new ControlReading(Id, descriptor.CommandedDuty, descriptor.Owner, descriptor.ClaimantId),
                null);
        }
    }

    /// <summary>Which control this is.</summary>
    public SensorId Id { get; }

    /// <summary>What to call it: the user's name, or the hardware's when they have not given one.</summary>
    [ObservableProperty]
    public partial string Name { get; private set; } = "No longer present";

    /// <summary>
    /// The name as the user is editing it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Name"/> so a half-typed name is never what the card claims the fan
    /// is called, and so an edit that the engine refuses leaves the card showing the truth.
    /// </remarks>
    [ObservableProperty]
    public partial string EditableName { get; set; } = string.Empty;

    /// <summary>What the hardware calls it, whatever the user has renamed it to.</summary>
    /// <remarks>
    /// Kept so that clearing a name can restore it, and so the tooltip can still say what the
    /// hardware thinks this is when the label on the card is something personal.
    /// </remarks>
    [ObservableProperty]
    public partial string ProviderName { get; private set; } = string.Empty;

    /// <summary>What it hangs off, for a tooltip rather than the card's face.</summary>
    [ObservableProperty]
    public partial string HardwareName { get; private set; } = string.Empty;

    /// <summary>Where it lives, for when two fans share a name.</summary>
    public string HardwarePath { get; }

    /// <summary>
    /// Whether the engine can still see this fan.
    /// </summary>
    /// <remarks>
    /// A binding whose control has gone still gets a card, marked absent. Dropping it silently
    /// would look exactly like Impeller having lost the user's settings.
    /// </remarks>
    public bool IsPresent { get; }

    /// <summary>The tacho paired with it, or none.</summary>
    public SensorId PairedFanSensorId => _binding.PairedFanSensorId;

    /// <summary>The curves this fan can be pointed at, including "not driven".</summary>
    public IReadOnlyList<CurveChoice> Curves { get; private set; } = [];

    /// <summary>Which one it is pointed at now.</summary>
    [ObservableProperty]
    public partial CurveChoice SelectedCurve { get; set; } = CurveChoice.None;

    /// <summary>The duty standing at it, ready to read.</summary>
    [ObservableProperty]
    public partial string DutyText { get; private set; } = "—";

    /// <summary>What its paired tacho reads, or a dash when there is none.</summary>
    [ObservableProperty]
    public partial string SpeedText { get; private set; } = "—";

    /// <summary>What is deciding this fan's speed, named for a person.</summary>
    [ObservableProperty]
    public partial string HolderText { get; private set; } = "Not driven";

    /// <summary>Whether the user is holding it by hand.</summary>
    [ObservableProperty]
    public partial bool IsPinned { get; private set; }

    /// <summary>Whether something other than the user or its curve holds it.</summary>
    [ObservableProperty]
    public partial bool IsHeldElsewhere { get; private set; }

    /// <summary>Whether the engine drives this fan at all.</summary>
    [ObservableProperty]
    public partial bool IsDriven { get; private set; }

    /// <summary>Whether the user could take it by hand right now.</summary>
    [ObservableProperty]
    public partial bool CanTakeByHand { get; private set; }

    /// <summary>Where the manual slider sits, in percent.</summary>
    [ObservableProperty]
    public partial float PinDuty { get; set; }

    /// <summary>Whether a request to the engine is in flight.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>The last thing that went wrong, or null.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <summary>How many calibration points this fan has, so the card can offer to measure it.</summary>
    public int CalibrationPoints => _binding.Calibration.Count;

    /// <summary>Whether it has been measured.</summary>
    public bool IsCalibrated => CalibrationPoints > 0;

    /// <summary>The measured thresholds, or a note that there are none.</summary>
    public string ThresholdText => _binding.StartDuty.IsOff
        ? "Not measured"
        : $"Starts at {_binding.StartDuty}, stalls below {_binding.StopDuty}";

    /// <summary>Takes a fresh descriptor, binding and curve list after anything changed.</summary>
    public void Rebind(
        ControlDescriptor? descriptor,
        ControlBindingDefinition binding,
        IEnumerable<CurveChoice> curves)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (descriptor is not null)
        {
            Name = descriptor.DisplayName;
            ProviderName = descriptor.Name;
            HardwareName = descriptor.HardwareName;
        }

        // Only when the user is not part-way through typing one. A snapshot arriving mid-edit -
        // and one arrives whenever anything at all changes - must not overwrite what they are
        // typing.
        if (!IsRenaming)
        {
            EditableName = Name;
        }

        _binding = binding;
        Curves = [.. curves];
        IsDriven = binding.Enabled && !binding.CurveId.IsNone;

        _suppressCurve = true;
        SelectedCurve = Curves.FirstOrDefault(curve => curve.Id == binding.CurveId, CurveChoice.None);
        _suppressCurve = false;

        if (binding.ManualDuty is { } pinned)
        {
            _suppressWrite = true;
            PinDuty = pinned.Percent;
            _suppressWrite = false;
        }

        OnPropertyChanged(nameof(Curves));
        OnPropertyChanged(nameof(CalibrationPoints));
        OnPropertyChanged(nameof(IsCalibrated));
        OnPropertyChanged(nameof(ThresholdText));
        OnPropertyChanged(nameof(PairedFanSensorId));
    }

    /// <summary>Takes one tick's reading.</summary>
    /// <param name="reading">The control's duty, owner and claimant.</param>
    /// <param name="rpm">Its paired tacho, or null when it has none or it is not reporting.</param>
    public void Apply(ControlReading reading, float? rpm)
    {
        DutyText = reading.CommandedDuty?.ToString() ?? "—";
        SpeedText = rpm is { } speed ? $"{speed:0} RPM" : "—";

        IsPinned = reading.Owner == ControlOwnerKind.ManualOverride;
        IsHeldElsewhere = reading.Owner is ControlOwnerKind.Plugin or ControlOwnerKind.Failsafe;
        CanTakeByHand = IsPresent && IsDriven && !IsHeldElsewhere;

        // Only the exceptional holders. When the fan's own curve is driving it the picker directly
        // below already says which, and repeating it there is a card telling the user the same
        // thing twice - which also buries the cards where something unusual is going on.
        HolderText = reading.Owner switch
        {
            ControlOwnerKind.ManualOverride => "Held by you",

            // Named, not just "a plugin". A user told that Rig Fan Control has their fan knows what
            // to close; one told that something does, does not.
            ControlOwnerKind.Plugin => $"Held by {_nameClaimant(reading.ClaimantId) ?? "a plugin"}",

            ControlOwnerKind.Failsafe => "Failsafe",
            _ when !IsPresent => "Not connected",
            _ => string.Empty,
        };

        // Follows the fan while something else drives it, so taking it by hand starts where it
        // already is. Left alone once pinned: from that point the slider is the instruction.
        if (!IsPinned && reading.CommandedDuty is { } commanded)
        {
            _suppressWrite = true;
            PinDuty = commanded.Percent;
            _suppressWrite = false;
        }
    }

    /// <summary>Whether the user currently has the name field open.</summary>
    public bool IsRenaming { get; set; }

    /// <summary>
    /// Gives the fan the user's own name, or restores the hardware's.
    /// </summary>
    /// <remarks>
    /// A name typed back to what the hardware already calls it is a removal rather than a stored
    /// duplicate, so clearing the field and typing the original are the same act — which is what
    /// somebody undoing a rename will try.
    /// </remarks>
    [RelayCommand]
    private async Task RenameAsync()
    {
        IsRenaming = false;

        var wanted = EditableName?.Trim() ?? string.Empty;

        var value = wanted.Length == 0 || string.Equals(wanted, ProviderName, StringComparison.Ordinal)
            ? null
            : wanted;

        if (string.Equals(wanted, Name, StringComparison.Ordinal))
        {
            return;
        }

        await SendAsync(engine => engine.RenameAsync(Id, value)).ConfigureAwait(true);
    }

    /// <summary>Takes the fan by hand, or hands it back to its curve.</summary>
    /// <remarks>
    /// One command for both directions, because they are one decision: what is driving this fan.
    /// The card used to carry two buttons that were each other's inverse, one of them called "Hold
    /// by hand", which named an implementation rather than an intent.
    /// </remarks>
    [RelayCommand]
    private async Task SetModeAsync(bool byHand)
    {
        if (byHand == IsPinned)
        {
            return;
        }

        if (!byHand)
        {
            await SendAsync(engine => engine.ReleaseControlAsync(Id)).ConfigureAwait(true);
            return;
        }

        await SendAsync(async engine =>
        {
            var outcome = await engine.SetManualDutyAsync(Id, new Duty(PinDuty)).ConfigureAwait(true);

            Problem = outcome.Granted ? null : $"Could not take this fan: {Describe(outcome)}";
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Spins it up briefly so the user can hear which one it is.
    /// </summary>
    /// <remarks>
    /// The engine hands it back on its own timer, so nothing here has to remember to. A window
    /// closed halfway through identifying a fan must not leave it at full speed indefinitely.
    /// </remarks>
    [RelayCommand]
    private async Task IdentifyAsync()
    {
        await SendAsync(async engine =>
        {
            var outcome = await engine
                .IdentifyControlAsync(Id, IdentifyDuty, IdentifyDuration)
                .ConfigureAwait(true);

            Problem = outcome.Granted ? null : $"Could not spin this fan up: {Describe(outcome)}";
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Points it at a different curve, and saves.
    /// </summary>
    /// <remarks>
    /// The curve picker is also the on switch, which is why there is no longer a separate one. A
    /// fan with no curve is a fan the engine is not driving; those were always one fact wearing two
    /// controls, and keeping both let a user set one and be surprised by the other.
    /// </remarks>
    partial void OnSelectedCurveChanged(CurveChoice value)
    {
        if (_suppressCurve)
        {
            return;
        }

        _ = _save(_binding with { CurveId = value.Id, Enabled = !value.Id.IsNone });
    }

    /// <summary>
    /// Pushes the slider's new position to a fan that is already pinned.
    /// </summary>
    /// <remarks>
    /// Only while pinned. Dragging the slider on a fan its curve is driving should not silently
    /// take the fan over — that is what the mode control is for, and a slider that seizes control
    /// on touch is a slider people are afraid of.
    /// </remarks>
    partial void OnPinDutyChanged(float value)
    {
        if (_suppressWrite || !IsPinned)
        {
            return;
        }

        _writer.Write(new Duty(value));
    }

    private async Task SendDutyAsync(Duty duty, CancellationToken cancellationToken)
    {
        if (_connection.Engine is { } engine)
        {
            await engine.SetManualDutyAsync(Id, duty, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(Func<IEngineControl, Task> send)
    {
        if (_connection.Engine is not { } engine)
        {
            Problem = "Not connected to the engine.";
            return;
        }

        IsBusy = true;

        try
        {
            await send(engine).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The connection dropping mid-click is ordinary, and the reconnect loop is already on
            // it. What must not happen is the click disappearing without a word.
            Problem = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _writer.DisposeAsync();

    private static string Describe(ControlAcquireOutcome outcome) => outcome.Failure switch
    {
        ControlAcquireFailure.AlreadyOwned when outcome.CurrentClaimantId is { } who =>
            $"{who} is holding it.",
        ControlAcquireFailure.AlreadyOwned => "something else is holding it.",
        ControlAcquireFailure.EngineUnavailable => "the engine is in a failsafe state.",
        ControlAcquireFailure.UnknownControl => "the engine no longer sees this control.",

        // Says what to do about it. This refusal exists precisely so that switching a fan off and
        // then wondering why the slider does nothing stops being a silent failure, and answering it
        // with "the engine refused" would give back the silence in a different font.
        ControlAcquireFailure.NotDriven => "Impeller is not driving it — give it a curve first.",
        ControlAcquireFailure.NotPermitted => "it has not been granted to this program.",
        _ => "the engine refused.",
    };
}
