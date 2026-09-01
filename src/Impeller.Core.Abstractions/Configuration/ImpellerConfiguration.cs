namespace Impeller.Core.Abstractions.Configuration;

/// <summary>
/// Everything a saved configuration says: which curves exist, which fan each one drives, and any
/// sensors the engine derives for itself.
/// </summary>
/// <remarks>
/// <para>
/// This is the one vocabulary shared by the file on disk, the engine that applies it, and the
/// channel the shell edits it over. Having a single shape for all three is the point — a wire
/// format that drifts from the file format is a bug waiting for a version mismatch to expose it.
/// </para>
/// <para>
/// Note what is <em>not</em> here: no hardware paths, no device names, no enumeration indices.
/// Everything points at a <see cref="SensorId"/> or a <see cref="CurveId"/>, so a configuration
/// describes intent and the identity map alone decides which physical fan that intent lands on.
/// </para>
/// </remarks>
public sealed record ImpellerConfiguration
{
    /// <summary>The name this configuration is saved and shown under.</summary>
    public string Name { get; init; } = "Default";

    /// <summary>Every curve defined, whether or not a control currently uses it.</summary>
    public EquatableArray<CurveDefinition> Curves { get; init; } = [];

    /// <summary>Every control the engine knows about, and how it should be driven.</summary>
    public EquatableArray<ControlBindingDefinition> Controls { get; init; } = [];

    /// <summary>Sensors the engine computes from other sensors.</summary>
    public EquatableArray<CustomSensorDefinition> CustomSensors { get; init; } = [];
}

/// <summary>Change suppression applied to a curve's input.</summary>
/// <param name="DeadbandUp">
/// How far the input must rise before a rise counts, in the sensor's own units.
/// </param>
/// <param name="DeadbandDown">How far it must fall before a fall counts.</param>
/// <param name="ResponseUp">How long a rise must persist before it is acted on.</param>
/// <param name="ResponseDown">
/// How long a fall must persist. Usually longer than <paramref name="ResponseUp"/>: reacting late
/// to heat is a thermal problem, reacting late to cooling is only a missed chance to be quieter.
/// </param>
public readonly record struct HysteresisDefinition(
    float DeadbandUp = 0f,
    float DeadbandDown = 0f,
    TimeSpan ResponseUp = default,
    TimeSpan ResponseDown = default);

/// <summary>One vertex of a graph curve.</summary>
/// <param name="Input">The sensor reading, in that sensor's units.</param>
/// <param name="Duty">The duty to command at that reading.</param>
public readonly record struct CurvePointDefinition(float Input, Duty Duty);

/// <summary>One measured point on a control's duty-to-speed curve.</summary>
/// <param name="Duty">The duty that was commanded.</param>
/// <param name="Rpm">The speed the fan settled at.</param>
/// <param name="Avoid">
/// Whether this duty should be skipped over — a resonance, or a speed where the fan is
/// unpleasant. The engine steps past an avoided band rather than settling inside it.
/// </param>
public readonly record struct CalibrationPointDefinition(Duty Duty, int Rpm, bool Avoid = false);

/// <summary>How one control is driven.</summary>
public sealed record ControlBindingDefinition
{
    /// <summary>The control this describes.</summary>
    public SensorId ControlId { get; init; } = SensorId.None;

    /// <summary>The curve that drives it while nothing has taken ownership.</summary>
    public CurveId CurveId { get; init; } = CurveId.None;

    /// <summary>
    /// Whether the engine drives this control at all. A disabled control is left entirely alone,
    /// which is what every control starts as on a fresh install.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>Lowest duty the engine will command.</summary>
    public Duty MinimumDuty { get; init; } = Duty.Off;

    /// <summary>Highest duty the engine will command.</summary>
    public Duty MaximumDuty { get; init; } = Duty.Full;

    /// <summary>What to command when the engine loses control.</summary>
    public Duty FailsafeDuty { get; init; } = Duty.Full;

    /// <summary>Fastest the duty may rise, in percentage points per second. Zero means no limit.</summary>
    public float MaximumStepUpPerSecond { get; init; } = 100f;

    /// <summary>Fastest the duty may fall, in percentage points per second. Zero means no limit.</summary>
    public float MaximumStepDownPerSecond { get; init; } = 100f;

    /// <summary>The duty this fan needs to break away from rest. Off disables start handling.</summary>
    public Duty StartDuty { get; init; } = Duty.Off;

    /// <summary>The duty below which this fan stalls rather than running slowly.</summary>
    public Duty StopDuty { get; init; } = Duty.Off;

    /// <summary>The tach sensor paired with this fan, if one has been identified.</summary>
    public SensorId PairedFanSensorId { get; init; } = SensorId.None;

    /// <summary>
    /// Measured duty-to-speed points for this fan.
    /// </summary>
    /// <remarks>
    /// Kept with the control rather than with a curve because it describes the fan itself, not
    /// anyone's intent for it — and because it is what makes an RPM-targeting curve or an
    /// avoid-band translatable into a duty at all.
    /// </remarks>
    public EquatableArray<CalibrationPointDefinition> Calibration { get; init; } = [];
}

/// <summary>A sensor the engine computes rather than reads.</summary>
/// <remarks>
/// Distinct from a mix <em>curve</em>, which combines duties. A custom sensor combines readings and
/// is itself a curve input — so "the hotter of my CPU and GPU, fed to one curve" is this, whereas
/// "the higher of what these two curves ask for" is a mix curve. The difference matters when
/// importing a configuration that has both.
/// </remarks>
public sealed record CustomSensorDefinition
{
    /// <summary>Stable identity, minted once and never derived from the name.</summary>
    public SensorId Id { get; init; } = SensorId.None;

    /// <summary>What the user calls it. Renaming is free; the id is what configurations reference.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>How the value is derived.</summary>
    public CustomSensorKind Kind { get; init; }

    /// <summary>What the derived value measures, so the UI can format it and curves can accept it.</summary>
    public SensorKind Measures { get; init; } = SensorKind.Temperature;

    /// <summary>The sensors read. A mix reads several; the others read one.</summary>
    public EquatableArray<SensorId> Sources { get; init; } = [];

    /// <summary>How a <see cref="CustomSensorKind.Mix"/> combines its sources.</summary>
    public MixFunction Function { get; init; }

    /// <summary>
    /// Whether a mix still reports when one of its sources is missing.
    /// </summary>
    /// <remarks>
    /// Off by default, deliberately. A "hottest of CPU and GPU" sensor that quietly keeps
    /// reporting after the GPU stops answering is reporting something other than what it claims.
    /// </remarks>
    public bool AllowMissingSource { get; init; }

    /// <summary>The shift applied by a <see cref="CustomSensorKind.Offset"/> sensor.</summary>
    public float Offset { get; init; }

    /// <summary>Whether that shift scales the source rather than adding to it.</summary>
    public bool Proportional { get; init; }

    /// <summary>The averaging window of a <see cref="CustomSensorKind.TimeAverage"/> sensor.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The file a <see cref="CustomSensorKind.File"/> sensor reads its value from.</summary>
    public string? Path { get; init; }
}
