namespace Impeller.Core.Abstractions;

/// <summary>
/// A control output level, as a percentage of full speed.
/// </summary>
/// <remarks>
/// Always in <c>[0, 100]</c>. The constructor clamps instead of throwing: curve math
/// legitimately produces out-of-range intermediates (an offset pushing past 100, a
/// mix subtracting below 0), and the sane response is saturation, not an exception
/// on the tick loop.
/// </remarks>
public readonly record struct Duty : IComparable<Duty>, IFormattable
{
    /// <summary>Lowest representable duty.</summary>
    public const float MinPercent = 0f;

    /// <summary>Highest representable duty.</summary>
    public const float MaxPercent = 100f;

    /// <summary>Fully off.</summary>
    public static Duty Off => new(MinPercent);

    /// <summary>Full speed. The default failsafe when the engine loses control of a fan.</summary>
    public static Duty Full => new(MaxPercent);

    /// <summary>The duty as a percentage in <c>[0, 100]</c>.</summary>
    public float Percent { get; }

    /// <summary>Creates a duty, clamping <paramref name="percent"/> into range. NaN becomes 0.</summary>
    public Duty(float percent) =>
        Percent = float.IsNaN(percent) ? MinPercent : Math.Clamp(percent, MinPercent, MaxPercent);

    /// <summary>Creates a duty from a 0..1 fraction.</summary>
    public static Duty FromFraction(float fraction) => new(fraction * 100f);

    /// <summary>The duty as a 0..1 fraction.</summary>
    public float Fraction => Percent / 100f;

    /// <summary>True when the control is commanded fully off.</summary>
    public bool IsOff => Percent <= MinPercent;

    /// <summary>
    /// Absolute difference between two duties, in percentage points. Used by the tick loop's
    /// rate limiter and by change-detection that avoids redundant hardware writes.
    /// </summary>
    public static float Distance(Duty a, Duty b) => MathF.Abs(a.Percent - b.Percent);

    /// <summary>
    /// Moves <paramref name="from"/> toward <paramref name="to"/> by at most
    /// <paramref name="maxStep"/> percentage points. This is the engine's slew-rate limiter:
    /// it keeps a curve's step change from becoming an audible jump.
    /// </summary>
    public static Duty StepToward(Duty from, Duty to, float maxStep)
    {
        if (maxStep <= 0f || Distance(from, to) <= maxStep)
        {
            return to;
        }

        var delta = to.Percent > from.Percent ? maxStep : -maxStep;
        return new Duty(from.Percent + delta);
    }

    public int CompareTo(Duty other) => Percent.CompareTo(other.Percent);

    public static bool operator <(Duty left, Duty right) => left.Percent < right.Percent;

    public static bool operator >(Duty left, Duty right) => left.Percent > right.Percent;

    public static bool operator <=(Duty left, Duty right) => left.Percent <= right.Percent;

    public static bool operator >=(Duty left, Duty right) => left.Percent >= right.Percent;

    public static explicit operator float(Duty duty) => duty.Percent;

    public static explicit operator Duty(float percent) => new(percent);

    /// <summary>Named alternative to the explicit <see cref="float"/> conversion.</summary>
    public static Duty FromPercent(float percent) => new(percent);

    /// <summary>Named alternative to the explicit conversion to <see cref="float"/>.</summary>
    public float ToSingle() => Percent;

    public override string ToString() => ToString(null, null);

    public string ToString(string? format, IFormatProvider? formatProvider) =>
        Percent.ToString(format ?? "0.#", formatProvider ?? System.Globalization.CultureInfo.InvariantCulture) + "%";
}
