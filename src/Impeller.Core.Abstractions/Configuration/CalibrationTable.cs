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
}
