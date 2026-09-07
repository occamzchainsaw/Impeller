namespace Impeller.Hardware.Adlx;

/// <summary>
/// A copy of a card's fan curve, taken so it can be put back.
/// </summary>
/// <remarks>
/// Held as plain numbers rather than as the ADLX state list it came from. The list is a live
/// driver object whose lifetime is tied to the tuning interface, and the whole point of this type
/// is to survive long enough to undo what Impeller did — including on a path where the driver
/// objects are being released.
/// </remarks>
internal sealed record AdlxFanCurve(int[] Temperatures, int[] Speeds)
{
    /// <summary>Nothing was captured, so there is nothing to put back.</summary>
    internal static AdlxFanCurve Empty { get; } = new([], []);

    /// <summary>Reads the curve a card is currently holding.</summary>
    internal static unsafe AdlxFanCurve Capture(void* fanTuning)
    {
        void* states;

        if (Adlx.Out(fanTuning, Adlx.Fan.GetFanTuningStates, &states) != AdlxResult.Ok)
        {
            return Empty;
        }

        try
        {
            var count = Adlx.Count(states, Adlx.List.Size);
            var begin = Adlx.Count(states, Adlx.List.Begin);

            var temperatures = new int[count];
            var speeds = new int[count];

            for (var i = 0u; i < count; i++)
            {
                void* state;

                if (Adlx.At(states, begin + i, &state) != AdlxResult.Ok)
                {
                    return Empty;
                }

                int speed = 0, temperature = 0;
                Adlx.Out(state, Adlx.State.GetFanSpeed, &speed);
                Adlx.Out(state, Adlx.State.GetTemperature, &temperature);

                temperatures[i] = temperature;
                speeds[i] = speed;
            }

            return new AdlxFanCurve(temperatures, speeds);
        }
        finally
        {
            Adlx.Release(states, 1);
        }
    }

    /// <summary>Puts this curve back on the card.</summary>
    /// <returns>Whether the card accepted it.</returns>
    internal unsafe bool TryApply(void* fanTuning)
    {
        if (Temperatures.Length == 0)
        {
            return false;
        }

        void* states;

        if (Adlx.Out(fanTuning, Adlx.Fan.GetFanTuningStates, &states) != AdlxResult.Ok)
        {
            return false;
        }

        try
        {
            var count = Math.Min(Adlx.Count(states, Adlx.List.Size), (uint)Temperatures.Length);
            var begin = Adlx.Count(states, Adlx.List.Begin);

            for (var i = 0u; i < count; i++)
            {
                void* state;

                if (Adlx.At(states, begin + i, &state) != AdlxResult.Ok)
                {
                    return false;
                }

                // Temperature first. ADLX validates the list as a whole and a speed written
                // against the wrong temperature can make an otherwise valid curve fail.
                Adlx.In(state, Adlx.State.SetTemperature, Temperatures[i]);
                Adlx.In(state, Adlx.State.SetFanSpeed, Speeds[i]);
            }

            var errorIndex = -1;

            return Adlx.ForGpu(fanTuning, Adlx.Fan.IsValidFanTuningStates, states, &errorIndex) == AdlxResult.Ok
                && ((delegate* unmanaged[Stdcall]<void*, void*, AdlxResult>)
                        Adlx.Vtbl(fanTuning)[Adlx.Fan.SetFanTuningStates])(fanTuning, states) == AdlxResult.Ok;
        }
        finally
        {
            Adlx.Release(states, 1);
        }
    }
}
