namespace Impeller.Core.Abstractions.Configuration;

/// <summary>
/// Reads a control's measured duty-to-speed table.
/// </summary>
/// <remarks>
/// <para>
/// Producing the table is a live procedure that belongs with the engine. Reading one back is pure
/// arithmetic on stored numbers, so it lives here beside the definition, where the importer can
/// reach it without depending on the engine.
/// </para>
/// <para>
/// The one thing worth knowing about the table is that it is not a function in both directions.
/// Duty to speed is monotonic; speed to duty is not, because every duty below the fan's start
/// threshold reports the same zero.
/// </para>
/// </remarks>
public static class CalibrationTable
{
    /// <summary>
    /// How far apart two avoided points can be and still count as one band.
    /// </summary>
    /// <remarks>
    /// Calibration steps in tens, so a mark within one step of its neighbour was part of the same
    /// stretch the user was drawing rather than a second band that happens to sit nearby.
    /// </remarks>
    private const float BandGap = 10f;

    /// <summary>How far outside a band a snapped duty lands, so it is not left sitting on the edge.</summary>
    private const float BandEdge = 1f;

    /// <summary>
    /// The duty that produced a given speed, interpolated between the two measurements either side
    /// of it.
    /// </summary>
    /// <returns>
    /// The duty, or <see langword="null"/> when the table holds nothing that could answer — no
    /// points at all, or none where the fan was actually turning.
    /// </returns>
    /// <remarks>
    /// Stalled points are dropped before interpolating. A row saying "10% produced 0 RPM" records
    /// that the fan was not spinning, not that 10% is the way to achieve 0 RPM, and treating it as
    /// the latter would map every low target onto a duty that cannot turn the fan.
    /// </remarks>
    public static Duty? DutyForRpm(EquatableArray<CalibrationPointDefinition> table, float rpm)
    {
        var turning = table
            .Where(point => point.Rpm > 0)
            .OrderBy(point => point.Rpm)
            .ToList();

        if (turning.Count == 0)
        {
            return null;
        }

        // Outside the measured range the answer is the nearest end. Extrapolating past the fastest
        // measurement would invent a duty above the one that was actually tested.
        if (rpm <= turning[0].Rpm)
        {
            return turning[0].Duty;
        }

        if (rpm >= turning[^1].Rpm)
        {
            return turning[^1].Duty;
        }

        for (var i = 1; i < turning.Count; i++)
        {
            var low = turning[i - 1];
            var high = turning[i];

            if (rpm > high.Rpm)
            {
                continue;
            }

            var span = high.Rpm - low.Rpm;
            if (span <= 0)
            {
                return high.Duty;
            }

            var position = (rpm - low.Rpm) / span;
            return new Duty(low.Duty.Percent + (position * (high.Duty.Percent - low.Duty.Percent)));
        }

        return turning[^1].Duty;
    }

    /// <summary>
    /// Moves a duty out of an avoided band, in whichever direction it was already heading.
    /// </summary>
    /// <param name="table">The control's measured points, some of which may be marked avoided.</param>
    /// <param name="duty">The duty a curve asked for.</param>
    /// <param name="previous">The duty currently standing, which is what says which way it is going.</param>
    /// <returns>The duty to command: unchanged, or the far edge of the band it fell inside.</returns>
    /// <remarks>
    /// <para>
    /// An avoided band is a run of adjacent points the user marked — usually a speed where the fan
    /// resonates with the case and produces a note audible across the room. The engine steps past
    /// such a band rather than settling inside it, so the fan spends no longer in it than one tick.
    /// </para>
    /// <para>
    /// The direction of travel decides which edge, rather than proximity. Snapping to the nearer
    /// edge sounds reasonable and behaves badly: a curve climbing slowly into a band gets pushed
    /// back below it, climbs again, and the fan oscillates on the lower edge for as long as the
    /// temperature sits there. Jumping the way it was already going crosses once.
    /// </para>
    /// </remarks>
    public static Duty SnapPastAvoided(
        EquatableArray<CalibrationPointDefinition> table,
        Duty duty,
        Duty previous)
    {
        if (!TryFindAvoidedBand(table, duty, out var below, out var above))
        {
            return duty;
        }

        // Against either end of the range there is only one way out, whichever way it was going.
        if (above.Percent >= Duty.MaxPercent)
        {
            return below;
        }

        if (below.Percent <= Duty.MinPercent)
        {
            return above;
        }

        return duty >= previous ? above : below;
    }

    /// <summary>
    /// Finds the avoided band a duty falls inside, and the duties just outside either end of it.
    /// </summary>
    /// <param name="table">The control's measured points.</param>
    /// <param name="duty">The duty to place.</param>
    /// <param name="below">The duty just under the band. Meaningful only when this returns true.</param>
    /// <param name="above">The duty just over it.</param>
    /// <returns>Whether the duty landed inside a band at all.</returns>
    /// <remarks>
    /// Both edges rather than a decision, so a caller with limits of its own can pick the one that
    /// is actually reachable instead of being handed a duty it then has to clamp back into the band.
    /// </remarks>
    public static bool TryFindAvoidedBand(
        EquatableArray<CalibrationPointDefinition> table,
        Duty duty,
        out Duty below,
        out Duty above)
    {
        below = default;
        above = default;

        var marked = table
            .Where(point => point.Avoid)
            .Select(point => point.Duty.Percent)
            .OrderBy(percent => percent)
            .ToList();

        if (marked.Count == 0)
        {
            return false;
        }

        // Adjacent marks form one band. Walking them in order and breaking wherever the gap widens
        // is what turns a list of marks back into the bands the user meant to draw.
        var start = 0;

        for (var i = 1; i <= marked.Count; i++)
        {
            if (i < marked.Count && marked[i] - marked[i - 1] <= BandGap)
            {
                continue;
            }

            var low = marked[start];
            var high = marked[i - 1];
            start = i;

            if (duty.Percent < low || duty.Percent > high)
            {
                continue;
            }

            below = new Duty(low - BandEdge);
            above = new Duty(high + BandEdge);
            return true;
        }

        return false;
    }
}
