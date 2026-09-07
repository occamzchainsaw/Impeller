using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Impeller.App.ViewModels.Engine;
using Impeller.App.ViewModels.Notifications;
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

    /// <summary>
    /// How far a command may sit from its request before the card says so, in percentage points.
    /// </summary>
    /// <remarks>
    /// One point, which is below what any fan resolves anyway. The point of a tolerance at all is
    /// the slew limiter: it walks a duty toward its target across several ticks, and without this
    /// every ordinary ramp would flash an explanation for a disagreement that resolves itself in a
    /// second.
    /// </remarks>
    private const float RequestTolerance = 1f;

    private readonly EngineConnection _connection;
    private readonly NotificationCenter _notify;
    private readonly Func<ControlBindingDefinition, Task> _save;
    private readonly Func<SensorId, Task> _remove;
    private readonly Func<string?, string?> _nameClaimant;
    private readonly Func<SensorId, Task> _calibrate;
    private readonly Func<SensorId, Task> _pair;
    private readonly ThrottledWriter<Duty> _writer;

    private ControlBindingDefinition _binding;
    private bool _suppressWrite;
    private bool _suppressCurve;
    private bool _suppressLimits;

    public ControlCardViewModel(
        ControlDescriptor? descriptor,
        ControlBindingDefinition binding,
        IEnumerable<CurveChoice> curves,
        EngineConnection connection,
        NotificationCenter notify,
        Func<ControlBindingDefinition, Task> save,
        Func<SensorId, Task> remove,
        Func<string?, string?> nameClaimant,
        Func<SensorId, Task> calibrate,
        Func<SensorId, Task> pair)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(notify);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(remove);
        ArgumentNullException.ThrowIfNull(nameClaimant);
        ArgumentNullException.ThrowIfNull(calibrate);
        ArgumentNullException.ThrowIfNull(pair);

        _connection = connection;
        _notify = notify;
        _save = save;
        _remove = remove;
        _nameClaimant = nameClaimant;
        _calibrate = calibrate;
        _pair = pair;
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
                new ControlReading(
                    Id,
                    descriptor.CommandedDuty,
                    descriptor.RequestedDuty,
                    descriptor.Owner,
                    descriptor.ClaimantId),
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

    /// <summary>
    /// The curves this fan can be pointed at, including "not driven".
    /// </summary>
    /// <remarks>
    /// One collection for the life of the card, updated in place, and that is the whole of the fix
    /// for every picker on the page going blank at once. It used to be a fresh list built on every
    /// rebind — which is every time anything at all is saved, including the save that a picker's
    /// own selection causes — and replacing a <c>ComboBox</c>'s <c>ItemsSource</c> makes it clear
    /// its <c>SelectedItem</c>. That clear arrives back through the two-way binding as a null, so
    /// choosing a curve on one card blanked the picker on every card, while the lists behind them
    /// stayed perfectly correct: open one and the curves were all still there, with none of them
    /// marked as chosen.
    /// </remarks>
    public ObservableCollection<CurveChoice> Curves { get; } = [];

    /// <summary>
    /// Which one it is pointed at now.
    /// </summary>
    /// <remarks>
    /// Nullable, and that is load-bearing rather than tidiness. A <c>ComboBox</c> clears its
    /// <c>SelectedItem</c> to null whenever its <c>ItemsSource</c> is replaced — which happens on
    /// every rebind — and the two-way binding generated for a non-nullable value type unboxes that
    /// null straight into a <see cref="NullReferenceException"/>. The exception surfaces through
    /// the WinRT boundary as a stowed fault that no managed handler sees, so it took the whole
    /// shell down at startup, intermittently, leaving nothing behind but a Windows Error Reporting
    /// entry naming a system DLL.
    /// </remarks>
    [ObservableProperty]
    public partial CurveChoice? SelectedCurve { get; set; } = CurveChoice.None;

    /// <summary>The duty standing at it, ready to read.</summary>
    [ObservableProperty]
    public partial string DutyText { get; private set; } = "—";

    /// <summary>What its paired tacho reads, or a dash when there is none.</summary>
    [ObservableProperty]
    public partial string SpeedText { get; private set; } = "—";

    /// <summary>
    /// What the curve or claimant asked for, shown only when it is not what the fan got.
    /// </summary>
    /// <remarks>
    /// Empty in the ordinary case, where the request and the command are the same number and
    /// printing it twice would be noise. It earns its place in the case that reads as a fault: a
    /// fan sitting at 0% because its curve asked for less than the fan can physically sustain looks
    /// exactly like a broken curve, and the engine is the only thing that knows it is not.
    /// </remarks>
    [ObservableProperty]
    public partial string RequestedText { get; private set; } = string.Empty;

    /// <summary>The whole sentence, for hovering over <see cref="RequestedText"/>.</summary>
    [ObservableProperty]
    public partial string RequestedDetail { get; private set; } = string.Empty;

    /// <summary>
    /// This fan's own duty range, always shown, whether or not anything is currently disagreeing
    /// with it.
    /// </summary>
    /// <remarks>
    /// Its absence is what made a floor impossible to reason about: calibration measured the duty
    /// below which a fan stalls, the engine obeyed it, and the only place it ever appeared was a
    /// tooltip that showed up after the fan had already stopped. A limit that governs a fan every
    /// tick belongs on the fan, in view, before it surprises anybody.
    /// </remarks>
    [ObservableProperty]
    public partial string LimitsText { get; private set; } = string.Empty;

    /// <summary>Where <see cref="LimitsText"/> came from, for hovering over it.</summary>
    [ObservableProperty]
    public partial string LimitsDetail { get; private set; } = string.Empty;

    /// <summary>The floor, as an editable number.</summary>
    [ObservableProperty]
    public partial double FloorPercent { get; set; }

    /// <summary>The ceiling, as an editable number.</summary>
    [ObservableProperty]
    public partial double CeilingPercent { get; set; } = 100d;

    /// <summary>What calibration measured, or a line saying it has not run.</summary>
    [ObservableProperty]
    public partial string MeasuredText { get; private set; } = "Not calibrated.";

    /// <summary>Whether a tacho has been paired with this fan.</summary>
    [ObservableProperty]
    public partial bool IsPaired { get; private set; }

    /// <summary>What is deciding this fan's speed, named for a person.</summary>
    [ObservableProperty]
    public partial string HolderText { get; private set; } = "Not driven";

    /// <summary>Whether the user is holding it by hand.</summary>
    [ObservableProperty]
    public partial bool IsPinned { get; private set; }

    /// <summary>
    /// Where the mode switch is set, which is a request rather than a fact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The switch binds here two-way instead of raising an event. <c>Toggled</c> fires for
    /// programmatic changes as well as clicks, so with the switch also following
    /// <see cref="IsPinned"/> the two drove each other: one click produced eight events, taking the
    /// fan and letting go of it again several times over, and the fan ended up exactly where it
    /// started. The log of it reads True, False, True, False.
    /// </para>
    /// <para>
    /// Acting only when this differs from <see cref="IsPinned"/> is what breaks the loop, and it
    /// needs no suppression flag: an echo of the current state is, by definition, not a change.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    public partial bool WantsManual { get; set; }

    /// <summary>
    /// What letting go of the fan would leave it doing.
    /// </summary>
    /// <remarks>
    /// The other half of the mode switch, and it has to name the outcome rather than the mechanism.
    /// A fan with a curve goes back to it; one without is simply off, and labelling that "Curve"
    /// would promise a curve that does not exist.
    /// </remarks>
    [ObservableProperty]
    public partial string ModeOffLabel { get; private set; } = "Off";

    /// <summary>Whether something other than the user or its curve holds it.</summary>
    [ObservableProperty]
    public partial bool IsHeldElsewhere { get; private set; }

    /// <summary>Whether the engine drives this fan at all.</summary>
    [ObservableProperty]
    public partial bool IsDriven { get; private set; }

    /// <summary>
    /// Whether the user could take it by hand right now.
    /// </summary>
    /// <remarks>
    /// A fan that is switched off qualifies. Taking it by hand is itself the instruction to start
    /// driving it, so refusing until it had already been switched on made the obvious thing to
    /// click the one thing that did nothing.
    /// </remarks>
    [ObservableProperty]
    public partial bool CanTakeByHand { get; private set; }

    /// <summary>Where the manual slider sits, in percent.</summary>
    [ObservableProperty]
    public partial float PinDuty { get; set; }

    /// <summary>Whether a request to the engine is in flight.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

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
        SyncCurves(curves);
        DescribeLimits(binding);
        ModeOffLabel = binding.CurveId.IsNone ? "Off" : "Curve";

        // The engine's own rule, and nothing more. This used to require a curve as well, which is
        // where "you must give a fan a curve before you can drive it by hand" came from - a rule
        // the engine never had and one that made no sense: a pin is a complete instruction, and
        // needing to invent a curve you do not want in order to ignore it is absurd.
        IsDriven = binding.Enabled;

        _suppressCurve = true;
        SelectedCurve = Curves.FirstOrDefault(curve => curve.Id == binding.CurveId, CurveChoice.None);
        _suppressCurve = false;

        if (binding.ManualDuty is { } pinned)
        {
            _suppressWrite = true;
            PinDuty = pinned.Percent;
            _suppressWrite = false;
        }

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

        DescribeRequest(reading);

        IsPinned = reading.Owner == ControlOwnerKind.ManualOverride;
        IsHeldElsewhere = reading.Owner is ControlOwnerKind.Plugin or ControlOwnerKind.Failsafe;

        // Follows the fan, so the switch is right when something else takes it or hands it back.
        // Assigning the value it already holds raises nothing, so this cannot start a loop.
        WantsManual = IsPinned;
        CanTakeByHand = IsPresent && !IsHeldElsewhere;

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

            // Nothing is driving it, and that is a state worth naming rather than leaving blank.
            // The engine has handed it back, so the answer to "what is deciding this fan's speed"
            // is the board itself.
            _ when _binding.CurveId.IsNone => "Its own firmware",
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

    /// <summary>
    /// Works out whether the request and the command disagree, and says why if they do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things sit between what an owner asks for and what the fan gets: the binding's minimum
    /// and maximum, its slew limiter, and the start/stop gate. Only the last of them can turn a
    /// positive request into nothing at all, and that is the one people report as a bug — so it is
    /// named explicitly rather than left to a generic "adjusted".
    /// </para>
    /// <para>
    /// A tolerance rather than an equality test, because the slew limiter walks the duty toward the
    /// target over several ticks and a card that announced a disagreement during every ramp would
    /// be announcing normal operation.
    /// </para>
    /// </remarks>
    private void DescribeRequest(ControlReading reading)
    {
        if (reading.RequestedDuty is not { } requested)
        {
            RequestedText = string.Empty;
            RequestedDetail = string.Empty;
            return;
        }

        var commanded = reading.CommandedDuty;

        if (commanded is { } actual && Duty.Distance(actual, requested) < RequestTolerance)
        {
            RequestedText = string.Empty;
            RequestedDetail = string.Empty;
            return;
        }

        RequestedText = $"asks {requested}";

        var asker = reading.Owner switch
        {
            ControlOwnerKind.ManualOverride => "You are asking for",
            ControlOwnerKind.Plugin => $"{_nameClaimant(reading.ClaimantId) ?? "A plugin"} is asking for",
            ControlOwnerKind.Failsafe => "The failsafe is asking for",
            _ => "This fan's curve is asking for",
        };

        // The stall floor is the case worth explaining in full: it is the only one where a fan can
        // read 0% while something is actively asking it to run, and it is not a fault.
        var stalls = !_binding.StopDuty.IsOff
            && requested.Percent <= _binding.StopDuty.Percent
            && commanded is { IsOff: true };

        RequestedDetail = stalls
            ? $"{asker} {requested}, but this fan stalls below {_binding.StopDuty} — measured during "
                + "calibration. Impeller holds it off rather than command a speed that would leave "
                + "it drawing current, reporting no RPM and moving no air. Raise the curve's "
                + "minimum duty above that to keep it turning."
            : $"{asker} {requested}. The fan is at {commanded?.ToString() ?? "nothing"}, held there "
                + "by this fan's own limits or its ramp rate.";
    }

    /// <summary>
    /// Brings the limits shown on the card into line with the binding.
    /// </summary>
    /// <remarks>
    /// Writes the editable numbers under the suppression flag, because setting them raises the
    /// change handler that saves them - and a snapshot arriving while somebody is typing would
    /// otherwise save the value it just overwrote them with.
    /// </remarks>
    private void DescribeLimits(ControlBindingDefinition binding)
    {
        _suppressLimits = true;
        FloorPercent = binding.MinimumDuty.Percent;
        CeilingPercent = binding.MaximumDuty.Percent;
        _suppressLimits = false;

        IsPaired = !binding.PairedFanSensorId.IsNone;

        LimitsText = binding.MinimumDuty.IsOff && binding.MaximumDuty.Percent >= 100f
            ? string.Empty
            : $"{binding.MinimumDuty.Percent:0}–{binding.MaximumDuty.Percent:0}%";

        LimitsDetail = binding.MinimumDuty.IsOff
            ? "This fan will be driven anywhere from a standstill to full speed."
            : $"This fan is never commanded below {binding.MinimumDuty} — it stalls under that. "
                + "A curve asking for less gets the floor instead of stopping the fan.";

        MeasuredText = binding.Calibration.Count == 0
            ? "Not calibrated. Measure this fan to learn the speed each duty produces and the "
                + "duty below which it stalls."
            : $"Measured: {binding.Calibration.Count} points, starts at {binding.StartDuty}, "
                + $"stalls below {binding.StopDuty}.";
    }

    /// <summary>Saves a floor the user typed.</summary>
    partial void OnFloorPercentChanged(double value) => SaveLimits();

    /// <summary>Saves a ceiling the user typed.</summary>
    partial void OnCeilingPercentChanged(double value) => SaveLimits();

    /// <summary>
    /// Writes both limits back, with the floor never above the ceiling.
    /// </summary>
    /// <remarks>
    /// Clamped here rather than left to the engine, because an inverted pair is a state the user
    /// passes through while typing - dragging a floor up past a ceiling - and rejecting it would
    /// mean refusing a number mid-edit.
    /// </remarks>
    private void SaveLimits()
    {
        if (_suppressLimits)
        {
            return;
        }

        var floor = (float)Math.Clamp(FloorPercent, 0d, 100d);
        var ceiling = (float)Math.Clamp(CeilingPercent, 0d, 100d);

        if (floor > ceiling)
        {
            floor = ceiling;
        }

        if (Math.Abs(floor - _binding.MinimumDuty.Percent) < 0.01f
            && Math.Abs(ceiling - _binding.MaximumDuty.Percent) < 0.01f)
        {
            return;
        }

        _ = _save(_binding with
        {
            MinimumDuty = new Duty(floor),
            MaximumDuty = new Duty(ceiling),
        });
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

    /// <summary>
    /// Takes the fan by hand, or hands it back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One command for both directions, because they are one decision: what is driving this fan.
    /// The card used to carry two buttons that were each other's inverse, one of them called "Hold
    /// by hand", which named an implementation rather than an intent.
    /// </para>
    /// <para>
    /// Taking a switched-off fan by hand switches it on first. That is what the click means, and
    /// the alternative is a control that is correct about the engine's rules and useless to the
    /// person pressing it.
    /// </para>
    /// <para>
    /// Letting go hands it back to its curve, or switches it off when it has none — the same state
    /// it was in before, and the engine rests it on the way rather than leaving it stuck at the
    /// duty the user last chose.
    /// </para>
    /// </remarks>
    partial void OnWantsManualChanged(bool value)
    {
        if (value != IsPinned)
        {
            _ = SetModeAsync(value);
        }
    }

    private async Task SetModeAsync(bool byHand)
    {
        if (byHand == IsPinned)
        {
            return;
        }

        if (!byHand)
        {
            await SendAsync(engine => engine.ReleaseControlAsync(Id)).ConfigureAwait(true);

            // The stored pin goes with the claim. Keeping it would have the fan seize itself again
            // the next time this configuration was loaded, overriding whatever else it now says.
            if (_binding.ManualDuty is not null)
            {
                await _save(_binding with { ManualDuty = null }).ConfigureAwait(true);
            }

            return;
        }

        // Nothing to switch on first. Taking a fan by hand used to save the binding as enabled
        // before claiming it, because the engine refused a claim on a fan it was not driving; the
        // engine no longer does, so a click on the mode switch no longer edits the configuration
        // as a side effect and "Not driven" no longer flips to "Curve" on its own.
        await SendAsync(async engine =>
        {
            var outcome = await engine.SetManualDutyAsync(Id, new Duty(PinDuty)).ConfigureAwait(true);

            if (!outcome.Granted)
            {
                _notify.Error($"{Name} could not be taken by hand.", Describe(outcome));
            }
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

            if (!outcome.Granted)
            {
                _notify.Error($"{Name} could not be spun up.", Describe(outcome));
            }
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Points it at a different curve, and saves.
    /// </summary>
    /// <remarks>
    /// The curve picker is also the on switch, which is why there is no longer a separate one: a
    /// fan with nothing driving it is a fan that is off, and those were one fact wearing two
    /// controls. Holding it by hand is the other way of driving it, so choosing "Not driven" while
    /// the user has hold of it clears the curve and leaves the fan on.
    /// </remarks>
    partial void OnSelectedCurveChanged(CurveChoice? value)
    {
        if (_suppressCurve)
        {
            return;
        }

        // A null is never a choice: "Not driven" is an entry in the list rather than the absence of
        // one, so nothing a person can click produces this. What produces it is the picker clearing
        // itself when its items are touched, and the write-back arrives through a dependency
        // property callback that lands after the flag above has been put down again - so the flag
        // alone was never enough. Putting the fan's own curve straight back is what stops a card
        // sitting there showing nothing at all.
        if (value is not { } choice)
        {
            _suppressCurve = true;
            SelectedCurve = Curves.FirstOrDefault(curve => curve.Id == _binding.CurveId, CurveChoice.None);
            _suppressCurve = false;
            return;
        }

        if (choice.Id == _binding.CurveId)
        {
            return;
        }

        _ = _save(_binding with { CurveId = choice.Id, Enabled = !choice.Id.IsNone });
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

    /// <summary>
    /// Takes this fan off the page.
    /// </summary>
    /// <remarks>
    /// Adding a fan had no opposite, so a header brought onto the page to find out what it drove
    /// stayed there for good. The binding goes, and its curve, limits and calibration with it, so
    /// the page asks first — and the engine hands the fan back to the board on the way out rather
    /// than leaving it at whatever duty it was last given.
    /// </remarks>
    [RelayCommand]
    private Task RemoveAsync() => _remove(Id);

    /// <summary>
    /// Measures this fan alone: its duty-to-speed table, and the duties that start and stall it.
    /// </summary>
    /// <remarks>
    /// Minutes rather than the quarter of an hour a whole-machine run costs, and it is the only
    /// way to re-measure a fan that has been swapped without re-measuring seven that have not.
    /// </remarks>
    [RelayCommand]
    private Task CalibrateAsync() => _calibrate(Id);

    /// <summary>Works out which tacho belongs to this fan.</summary>
    [RelayCommand]
    private Task PairAsync() => _pair(Id);

    /// <summary>
    /// Brings the picker's list to match, touching only the entries that actually moved.
    /// </summary>
    /// <remarks>
    /// Not clear-and-refill: clearing is what takes the selection with it. Every save rebinds every
    /// card, and the list is identical almost every time, so the common case has to disturb the
    /// picker not at all.
    /// </remarks>
    private void SyncCurves(IEnumerable<CurveChoice> curves)
    {
        var wanted = curves as IList<CurveChoice> ?? [.. curves];

        for (var index = Curves.Count - 1; index >= wanted.Count; index--)
        {
            Curves.RemoveAt(index);
        }

        for (var index = 0; index < wanted.Count; index++)
        {
            if (index >= Curves.Count)
            {
                Curves.Add(wanted[index]);
            }
            else if (!Curves[index].Equals(wanted[index]))
            {
                Curves[index] = wanted[index];
            }
        }
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
            _notify.Error("Not connected to the engine.", $"{Name} was not changed.");
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
            _notify.Error($"{Name} did not change.", ex);
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
        ControlAcquireFailure.NotDriven => "Impeller is not driving it — switch it on first.",
        ControlAcquireFailure.NotPermitted => "it has not been granted to this program.",
        _ => "the engine refused.",
    };
}
