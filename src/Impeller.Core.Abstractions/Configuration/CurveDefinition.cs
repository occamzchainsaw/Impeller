using System.Text.Json.Serialization;

namespace Impeller.Core.Abstractions.Configuration;

/// <summary>
/// A curve as it is stored and sent, independent of the class that evaluates it.
/// </summary>
/// <remarks>
/// <para>
/// The type is written explicitly, as a <c>type</c> property. The app this replaces stores no
/// discriminator at all and recovers the type by scoring each candidate class on how many of its
/// property names appear in the object — which works, and is also why it needs thirteen candidate
/// classes registered to stay unambiguous once legacy shapes are included. A curve that says what
/// it is costs one string and removes the entire question.
/// </para>
/// <para>
/// These are records with <c>init</c> setters: a loaded configuration is a value to be replaced
/// wholesale, not an object graph to be mutated in place. The engine builds live curve objects
/// from them; nothing edits one of these after it has been read.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FlatCurveDefinition), "flat")]
[JsonDerivedType(typeof(LinearCurveDefinition), "linear")]
[JsonDerivedType(typeof(GraphCurveDefinition), "graph")]
[JsonDerivedType(typeof(MixCurveDefinition), "mix")]
[JsonDerivedType(typeof(SyncCurveDefinition), "sync")]
[JsonDerivedType(typeof(TriggerCurveDefinition), "trigger")]
[JsonDerivedType(typeof(AutoCurveDefinition), "auto")]
public abstract record CurveDefinition
{
    /// <summary>Stable identity, referenced by controls and by composite curves.</summary>
    public CurveId Id { get; init; } = CurveId.None;

    /// <summary>What the user calls it.</summary>
    public string Name { get; init; } = string.Empty;
}

/// <summary>A fixed duty, regardless of any sensor.</summary>
public sealed record FlatCurveDefinition : CurveDefinition
{
    /// <summary>The duty to command.</summary>
    public Duty Duty { get; init; }
}

/// <summary>A straight ramp between two points, flat outside them.</summary>
public sealed record LinearCurveDefinition : CurveDefinition
{
    /// <summary>The sensor driving the ramp.</summary>
    public SensorId Source { get; init; } = SensorId.None;

    /// <summary>Input value at which the ramp starts.</summary>
    public float MinimumInput { get; init; }

    /// <summary>Input value at which the ramp reaches full.</summary>
    public float MaximumInput { get; init; }

    /// <summary>Output at or below <see cref="MinimumInput"/>.</summary>
    public Duty MinimumDuty { get; init; }

    /// <summary>Output at or above <see cref="MaximumInput"/>.</summary>
    public Duty MaximumDuty { get; init; } = Duty.Full;

    /// <summary>Change suppression applied to the input.</summary>
    public HysteresisDefinition Hysteresis { get; init; }
}

/// <summary>An arbitrary piecewise-linear curve through user-placed points.</summary>
public sealed record GraphCurveDefinition : CurveDefinition
{
    /// <summary>The sensor driving the curve.</summary>
    public SensorId Source { get; init; } = SensorId.None;

    /// <summary>The vertices, in ascending input order. Sorted on load, so order here is a courtesy.</summary>
    public EquatableArray<CurvePointDefinition> Points { get; init; } = [];

    /// <summary>Change suppression applied to the input.</summary>
    public HysteresisDefinition Hysteresis { get; init; }
}

/// <summary>Several curves combined into one output.</summary>
public sealed record MixCurveDefinition : CurveDefinition
{
    /// <summary>How the inputs are combined.</summary>
    public MixFunction Function { get; init; }

    /// <summary>The input curves. Order matters only for <see cref="MixFunction.Difference"/>.</summary>
    public EquatableArray<CurveId> Sources { get; init; } = [];
}

/// <summary>Another curve or control mirrored, optionally shifted.</summary>
public sealed record SyncCurveDefinition : CurveDefinition
{
    /// <summary>Which kind of thing is being mirrored.</summary>
    public SyncSourceKind SourceKind { get; init; }

    /// <summary>The curve being mirrored, when <see cref="SourceKind"/> is a curve.</summary>
    public CurveId SourceCurve { get; init; } = CurveId.None;

    /// <summary>The control being mirrored, when <see cref="SourceKind"/> is a control.</summary>
    public SensorId SourceControl { get; init; } = SensorId.None;

    /// <summary>The shift applied to the mirrored duty.</summary>
    public float Offset { get; init; }

    /// <summary>Whether the offset scales the source rather than shifting it.</summary>
    public bool Proportional { get; init; }
}

/// <summary>Two speeds with a band between them, latching at whichever it last reached.</summary>
public sealed record TriggerCurveDefinition : CurveDefinition
{
    /// <summary>The sensor driving the trigger.</summary>
    public SensorId Source { get; init; } = SensorId.None;

    /// <summary>At or below this input the curve returns to idle.</summary>
    public float IdleInput { get; init; }

    /// <summary>At or above this input the curve switches to load.</summary>
    public float LoadInput { get; init; }

    /// <summary>Output while idle.</summary>
    public Duty IdleDuty { get; init; }

    /// <summary>Output while under load.</summary>
    public Duty LoadDuty { get; init; } = Duty.Full;

    /// <summary>How long the load threshold must be held before switching up.</summary>
    public TimeSpan ResponseUp { get; init; }

    /// <summary>How long the idle threshold must be held before switching back down.</summary>
    public TimeSpan ResponseDown { get; init; }
}

/// <summary>A controller that seeks whatever duty holds a target temperature.</summary>
public sealed record AutoCurveDefinition : CurveDefinition
{
    /// <summary>The temperature this curve is trying to hold.</summary>
    public SensorId Source { get; init; } = SensorId.None;

    /// <summary>At or below this temperature the curve commands its floor.</summary>
    public float IdleTemperature { get; init; } = 35f;

    /// <summary>The temperature the curve seeks to hold.</summary>
    public float LoadTemperature { get; init; } = 70f;

    /// <summary>Floor of the duty range the curve works within.</summary>
    public Duty MinimumDuty { get; init; }

    /// <summary>Ceiling of the duty range the curve works within.</summary>
    public Duty MaximumDuty { get; init; } = Duty.Full;

    /// <summary>How far the duty moves per upward adjustment, in percentage points.</summary>
    public float Step { get; init; } = 2f;

    /// <summary>How far below the target still counts as on target, in degrees.</summary>
    public float Deadband { get; init; } = 3f;

    /// <summary>How long a trend must persist before the duty moves.</summary>
    public TimeSpan ResponseTime { get; init; } = TimeSpan.FromSeconds(2);
}
