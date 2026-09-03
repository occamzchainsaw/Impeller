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
/// the three things a person wants to do about it — take it by hand, hand it back, and make it spin
/// up so they can work out which one it is behind the case panel.
/// </para>
/// <para>
/// Everything it changes goes through the engine, and it holds no authority of its own. Pinning a
/// fan is a claim the engine grants or refuses, and a refusal is displayed rather than worked
/// around: two things quietly fighting over one fan is the failure this whole ownership model
/// exists to make impossible.
/// </para>
/// </remarks>
public sealed partial class ControlCardViewModel : ObservableObject
{
    private readonly EngineConnection _connection;
    private readonly Func<ControlBindingDefinition, Task> _save;

    private ControlBindingDefinition _binding;

    /// <summary>How long an identify run spins the fan up for.</summary>
    private static readonly TimeSpan IdentifyDuration = TimeSpan.FromSeconds(5);

    /// <summary>What an identify run spins it up to.</summary>
    private static readonly Duty IdentifyDuty = new(100f);

    public ControlCardViewModel(
        ControlDescriptor descriptor,
        ControlBindingDefinition binding,
        string curveName,
        EngineConnection connection,
        Func<ControlBindingDefinition, Task> save)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(save);

        _connection = connection;
        _save = save;
        _binding = binding;

        Id = descriptor.Id;
        Name = descriptor.Name;
        HardwarePath = descriptor.HardwarePath;
        CurveName = curveName;
        IsDriven = binding.Enabled;

        // Where the slider starts. A stored pin is what the user last chose; otherwise the duty
        // standing now, so taking a fan by hand does not jolt it on the way.
        PinDuty = binding.ManualDuty?.Percent ?? descriptor.CommandedDuty?.Percent ?? 0f;

        Apply(new ControlReading(descriptor.Id, descriptor.CommandedDuty, descriptor.Owner), null);
    }

    /// <summary>Which control this is.</summary>
    public SensorId Id { get; }

    /// <summary>What the hardware calls it.</summary>
    public string Name { get; }

    /// <summary>Where it lives, for when two fans share a name.</summary>
    public string HardwarePath { get; }

    /// <summary>The tacho paired with it, or none.</summary>
    public SensorId PairedFanSensorId => _binding.PairedFanSensorId;

    /// <summary>The duty standing at it, ready to read.</summary>
    [ObservableProperty]
    public partial string DutyText { get; private set; } = "—";

    /// <summary>What its paired tacho reads, or a dash when there is none.</summary>
    [ObservableProperty]
    public partial string SpeedText { get; private set; } = "—";

    /// <summary>What is deciding this fan's speed, named for a person.</summary>
    [ObservableProperty]
    public partial string OwnerText { get; private set; } = "Curve";

    /// <summary>Whether the user is holding it by hand.</summary>
    [ObservableProperty]
    public partial bool IsPinned { get; private set; }

    /// <summary>Whether something other than the user or its curve holds it.</summary>
    /// <remarks>
    /// A plugin, or the failsafe. Pinning is refused while this is set, and saying so up front is
    /// better than a button that fails when pressed.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsHeldElsewhere { get; private set; }

    /// <summary>Whether the engine drives this control at all.</summary>
    [ObservableProperty]
    public partial bool IsDriven { get; private set; }

    /// <summary>The curve behind it, by name.</summary>
    [ObservableProperty]
    public partial string CurveName { get; private set; }

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

    /// <summary>Takes a fresh binding after the configuration changed.</summary>
    public void Rebind(ControlBindingDefinition binding, string curveName)
    {
        ArgumentNullException.ThrowIfNull(binding);

        _binding = binding;
        CurveName = curveName;
        IsDriven = binding.Enabled;

        if (binding.ManualDuty is { } pinned)
        {
            PinDuty = pinned.Percent;
        }

        OnPropertyChanged(nameof(CalibrationPoints));
        OnPropertyChanged(nameof(IsCalibrated));
        OnPropertyChanged(nameof(ThresholdText));
        OnPropertyChanged(nameof(PairedFanSensorId));
    }

    /// <summary>Takes one tick's reading.</summary>
    /// <param name="reading">The control's duty and owner.</param>
    /// <param name="rpm">Its paired tacho, or null when it has none or it is not reporting.</param>
    public void Apply(ControlReading reading, float? rpm)
    {
        DutyText = reading.CommandedDuty?.ToString() ?? "not driven";
        SpeedText = rpm is { } speed ? $"{speed:0} RPM" : "—";

        IsPinned = reading.Owner == ControlOwnerKind.ManualOverride;
        IsHeldElsewhere = reading.Owner is ControlOwnerKind.Plugin or ControlOwnerKind.Failsafe;

        OwnerText = reading.Owner switch
        {
            ControlOwnerKind.ManualOverride => "Held by you",
            ControlOwnerKind.Plugin => "Held by a plugin",
            ControlOwnerKind.Failsafe => "Failsafe",
            _ => IsDriven ? CurveName : "Not driven",
        };
    }

    /// <summary>Takes the fan by hand and holds it where the slider is.</summary>
    [RelayCommand]
    private async Task PinAsync()
    {
        await SendAsync(async engine =>
        {
            var outcome = await engine.SetManualDutyAsync(Id, new Duty(PinDuty)).ConfigureAwait(true);

            // Named rather than anonymous. "Something else has it" sends people looking; "a plugin
            // called iracing has it" tells them what to close.
            Problem = outcome.Granted
                ? null
                : $"Could not take this fan: {Describe(outcome)}";
        }).ConfigureAwait(true);
    }

    /// <summary>Hands it back to its curve.</summary>
    [RelayCommand]
    private async Task ReleaseAsync() =>
        await SendAsync(engine => engine.ReleaseControlAsync(Id)).ConfigureAwait(true);

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

    /// <summary>Turns engine control of this fan on or off, and saves.</summary>
    [RelayCommand]
    private async Task SetDrivenAsync(bool driven)
    {
        IsDriven = driven;
        await _save(_binding with { Enabled = driven }).ConfigureAwait(true);
    }

    /// <summary>Points it at a different curve, and saves.</summary>
    [RelayCommand]
    private async Task AssignCurveAsync(CurveId curveId) =>
        await _save(_binding with { CurveId = curveId, Enabled = !curveId.IsNone && IsDriven })
            .ConfigureAwait(true);

    /// <summary>
    /// Pushes the slider's new position to a fan that is already pinned.
    /// </summary>
    /// <remarks>
    /// Only while pinned. Dragging the slider on a fan its curve is driving should not silently
    /// take the fan over — that is what the pin button is for, and a slider that seizes control on
    /// touch is a slider people are afraid of.
    /// </remarks>
    partial void OnPinDutyChanged(float value)
    {
        if (!IsPinned || _connection.Engine is not { } engine)
        {
            return;
        }

        _ = engine.SetManualDutyAsync(Id, new Duty(value));
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
        ControlAcquireFailure.NotDriven => "Impeller is not driving it — switch the fan on first.",
        ControlAcquireFailure.NotPermitted => "it has not been granted to this program.",
        _ => "the engine refused.",
    };
}
