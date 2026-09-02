using System.Text.Json.Nodes;

namespace Impeller.Core.Persistence.Legacy;

/// <summary>The legacy shapes a curve object can turn out to be.</summary>
public enum LegacyCurveShape
{
    /// <summary>Nothing scored — the object is not a curve this importer recognises.</summary>
    Unknown = 0,

    /// <summary>A fixed percentage.</summary>
    Flat,

    /// <summary>A ramp, with hysteresis stored as an object.</summary>
    Linear,

    /// <summary>A ramp, with hysteresis stored as loose scalars.</summary>
    LinearLegacy,

    /// <summary>Points, with hysteresis stored as an object.</summary>
    Graph,

    /// <summary>Points, with hysteresis stored as loose scalars.</summary>
    GraphLegacy,

    /// <summary>Two speeds with response times stored as an object.</summary>
    Trigger,

    /// <summary>Two speeds with a single response time.</summary>
    TriggerLegacy,

    /// <summary>Several curves combined, held as a list.</summary>
    Mix,

    /// <summary>Two curves combined, held as a named pair.</summary>
    MixLegacy,

    /// <summary>A control mirrored.</summary>
    Sync,

    /// <summary>A seeking controller, with an explicit duty range.</summary>
    Auto,

    /// <summary>A seeking controller, with the range named after idle and load.</summary>
    AutoLegacy,

    /// <summary>Only a name — a reference to a curve, not a definition of one.</summary>
    NameOnly,
}

/// <summary>The legacy shapes a sensor object can turn out to be.</summary>
public enum LegacySensorShape
{
    /// <summary>Nothing scored.</summary>
    Unknown = 0,

    /// <summary>Settings attached to a hardware sensor, not a derived one.</summary>
    Plain,

    /// <summary>Several sensors combined.</summary>
    Mix,

    /// <summary>One sensor averaged over a window.</summary>
    TimeAverage,

    /// <summary>A value read from a file.</summary>
    File,

    /// <summary>One sensor shifted or scaled.</summary>
    Offset,
}

/// <summary>
/// Works out what a legacy object was, given that it never says.
/// </summary>
/// <remarks>
/// <para>
/// The original stores no discriminator. It recovers the type by scoring every candidate class on
/// how many of its property names appear in the object, ordering by that count descending and then
/// by how many of the class's properties are missing, ascending. Ties fall to whichever candidate
/// was registered first.
/// </para>
/// <para>
/// Reproducing that exactly, rather than sniffing for a distinctive property, is deliberate. The
/// legacy variants overlap almost completely with the current ones — a legacy linear curve differs
/// from a current one only in carrying <c>SelectedHysteresis</c> and friends where the current one
/// carries <c>HysteresisConfig</c> — and any simpler rule picks the wrong one on some real file.
/// The output of this importer names its types explicitly, so this is the last place the question
/// ever has to be asked.
/// </para>
/// </remarks>
public static class LegacyTypeResolver
{
    private const string Name = "Name";
    private const string IsHidden = "IsHidden";
    private const string CommandMode = "CommandMode";
    private const string TempSource = "SelectedTempSource";
    private const string NickName = "NickName";
    private const string Identifier = "Identifier";

    // Registration order is load-bearing: it is what breaks a tie, so these stay in the order the
    // original registers them and are not sorted into something tidier.
    private static readonly (LegacyCurveShape Shape, string[] Properties)[] CurveCandidates =
    [
        (LegacyCurveShape.LinearLegacy, [
            Name, IsHidden, CommandMode, TempSource,
            "MaximumFanSpeed", "MaximumTemperature", "MinimumFanSpeed", "MinimumTemperature",
            "SelectedHysteresis", "SelectedResponseTime", "OneWayHysteresis", "IgnoreHysteresisAtLimits"]),
        (LegacyCurveShape.Linear, [
            Name, IsHidden, CommandMode, TempSource,
            "MaximumFanSpeed", "MaximumTemperature", "MinimumFanSpeed", "MinimumTemperature",
            "HysteresisConfig"]),
        (LegacyCurveShape.GraphLegacy, [
            Name, IsHidden, CommandMode, TempSource,
            "Points", "MaximumTemperature", "MinimumTemperature", "MaximumCommand",
            "SelectedHysteresis", "SelectedResponseTime", "OneWayHysteresis", "IgnoreHysteresisAtLimits"]),
        (LegacyCurveShape.Graph, [
            Name, IsHidden, CommandMode, TempSource,
            "Points", "MaximumTemperature", "MinimumTemperature", "MaximumCommand",
            "HysteresisConfig"]),
        (LegacyCurveShape.Trigger, [
            Name, IsHidden, CommandMode, TempSource,
            "LoadFanSpeed", "LoadTemperature", "IdleFanSpeed", "IdleTemperature",
            "ResponseTimeConfig"]),
        (LegacyCurveShape.TriggerLegacy, [
            Name, IsHidden, CommandMode, TempSource,
            "LoadFanSpeed", "LoadTemperature", "IdleFanSpeed", "IdleTemperature",
            "SelectedResponseTime"]),
        (LegacyCurveShape.Flat, [Name, IsHidden, CommandMode, "Percent"]),
        (LegacyCurveShape.MixLegacy, [Name, "SelectedFanCurveA", "SelectedFanCurveB", "SelectedMixFunction"]),
        (LegacyCurveShape.Mix, [Name, IsHidden, CommandMode, "SelectedFanCurves", "SelectedMixFunction"]),
        (LegacyCurveShape.Sync, [Name, IsHidden, CommandMode, "SelectedControl", "SelectedOffset", "Proportional"]),
        (LegacyCurveShape.AutoLegacy, [
            Name, TempSource, "Step", "Deadband", "SelectedResponseTime",
            "IdleTemperature", "IdleFanSpeed", "LoadFanSpeed", "LoadTemperature"]),
        (LegacyCurveShape.Auto, [
            Name, IsHidden, CommandMode, TempSource, "Step", "Deadband", "SelectedResponseTime",
            "MinFanSpeed", "MaxFanSpeed", "LoadTemperature", "IdleTemperature"]),
        (LegacyCurveShape.NameOnly, [Name]),
    ];

    // "Type" is a read-only discriminator on the derived classes. It never appears in the file, but
    // it is one of the properties the original counts, so it is counted here too — leaving it out
    // would shift every derived candidate's score by one relative to the plain one.
    private static readonly (LegacySensorShape Shape, string[] Properties)[] SensorCandidates =
    [
        (LegacySensorShape.Plain, [NickName, Identifier, IsHidden]),
        (LegacySensorShape.Mix, [
            NickName, Identifier, IsHidden, "Type",
            "AllowMissingSensor", "SelectedMixFunction", "SelectedSensors"]),
        (LegacySensorShape.TimeAverage, [NickName, Identifier, IsHidden, "Type", TempSource, "SelectedTime"]),
        (LegacySensorShape.File, [NickName, Identifier, IsHidden, "Type", "FileFullName"]),
        (LegacySensorShape.Offset, [
            NickName, Identifier, IsHidden, "Type", "Offset", "Proportional", TempSource]),
    ];

    /// <summary>Scores a curve object against every known curve shape.</summary>
    public static LegacyCurveShape ResolveCurve(JsonObject curve) => Resolve(curve, CurveCandidates);

    /// <summary>Scores a sensor object against every known sensor shape.</summary>
    public static LegacySensorShape ResolveSensor(JsonObject sensor) => Resolve(sensor, SensorCandidates);

    private static TShape Resolve<TShape>(JsonObject item, (TShape Shape, string[] Properties)[] candidates)
        where TShape : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(item);

        var best = default(TShape);
        var bestPresent = -1;
        var bestMissing = int.MaxValue;

        foreach (var (shape, properties) in candidates)
        {
            var present = 0;
            foreach (var property in properties)
            {
                if (item.ContainsKey(property))
                {
                    present++;
                }
            }

            var missing = properties.Length - present;

            // Strictly greater, so an earlier registration keeps a tie — which is how the original
            // resolves one, and occasionally the only thing separating two identical shapes.
            if (present > bestPresent || (present == bestPresent && missing < bestMissing))
            {
                best = shape;
                bestPresent = present;
                bestMissing = missing;
            }
        }

        return bestPresent <= 0 ? default : best;
    }
}
