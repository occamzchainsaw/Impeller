namespace Impeller.Core.Abstractions;

/// <summary>How a mix curve combines its inputs.</summary>
/// <remarks>
/// Lives here rather than beside the curve that uses it because a saved configuration and the
/// engine-to-shell channel both have to name these values, and neither of those should have to
/// reference the engine to do it.
/// </remarks>
public enum MixFunction
{
    /// <summary>The highest input. The usual choice: one fan answering to whichever component is hottest.</summary>
    Maximum = 0,

    /// <summary>The lowest input.</summary>
    Minimum,

    /// <summary>The arithmetic mean of the inputs.</summary>
    Average,

    /// <summary>The sum of the inputs, saturating at full.</summary>
    Sum,

    /// <summary>The first input minus all the rest, saturating at zero.</summary>
    Difference,
}

/// <summary>What a sync curve is mirroring.</summary>
public enum SyncSourceKind
{
    /// <summary>Nothing selected yet. The curve produces no value.</summary>
    None = 0,

    /// <summary>Another curve's raw output, before any control's limits are applied to it.</summary>
    Curve,

    /// <summary>A control's commanded duty, after that control's limits and ramp rate.</summary>
    Control,
}

/// <summary>How a custom sensor derives its value from other sensors.</summary>
public enum CustomSensorKind
{
    /// <summary>Combines several sensors with a <see cref="MixFunction"/>.</summary>
    Mix = 0,

    /// <summary>Another sensor shifted by a constant or scaled by a percentage.</summary>
    Offset,

    /// <summary>Another sensor averaged over a window, to damp a noisy reading.</summary>
    TimeAverage,

    /// <summary>A value read from a text file, for bridging in something the engine cannot see.</summary>
    File,
}
