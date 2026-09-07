using Impeller.Core.Abstractions;

namespace Impeller.Hardware.Adlx;

/// <summary>
/// One AMD GPU's fan, driven through ADLX's manual fan tuning.
/// </summary>
/// <remarks>
/// <para>
/// This exists because LibreHardwareMonitor cannot do it. Its AMD path is the legacy ADL
/// Overdrive interface, which recent drivers accept and then ignore: sweeping a duty from 1% to
/// 100% on an RDNA-era card moves the fan by a handful of RPM. The control looks healthy, reports
/// whatever duty was last written, and does nothing at all.
/// </para>
/// <para>
/// Two details of ADLX shape everything here. Zero RPM is a driver feature that stops the fan
/// below a temperature of its own choosing, and it overrides anything written while it is on, so
/// it has to be turned off before this control means anything. And many cards do not implement
/// <c>SetTargetFanSpeed</c>, so a constant duty is expressed as a flat curve: every state in the
/// card's tuning table set to the same speed.
/// </para>
/// <para>
/// Whatever the card had before Impeller touched it is captured at initialisation and put back on
/// disposal, Zero RPM included. Leaving somebody's GPU on a flat curve because a service stopped
/// is not an acceptable way to fail.
/// </para>
/// </remarks>
internal sealed unsafe class AdlxGpuFan : ISensor, IControl
{
    private readonly Lock _gate;
    private readonly void* _fanTuning;
    private readonly AdlxFanCurve _original;
    private readonly bool _zeroRpmSupported;
    private readonly bool _zeroRpmWasOn;
    private readonly bool _supportsTargetSpeed;
    private readonly AdlxIntRange _speedRange;

    internal AdlxGpuFan(
        SensorId id,
        string hardwareName,
        HardwareFingerprint fingerprint,
        void* fanTuning,
        AdlxFanCurve original,
        AdlxIntRange speedRange,
        bool zeroRpmSupported,
        bool zeroRpmWasOn,
        bool supportsTargetSpeed,
        Lock gate)
    {
        Id = id;
        HardwareName = hardwareName;
        Fingerprint = fingerprint;
        _fanTuning = fanTuning;
        _original = original;
        _speedRange = speedRange;
        _zeroRpmSupported = zeroRpmSupported;
        _zeroRpmWasOn = zeroRpmWasOn;
        _supportsTargetSpeed = supportsTargetSpeed;
        _gate = gate;
    }

    /// <inheritdoc />
    public SensorId Id { get; }

    /// <inheritdoc />
    public string Name => "GPU Fan";

    /// <inheritdoc />
    public string HardwareName { get; }

    /// <inheritdoc />
    public SensorKind Kind => SensorKind.Control;

    /// <inheritdoc />
    public HardwareFingerprint Fingerprint { get; }

    /// <inheritdoc />
    public float? Value => CommandedDuty?.Percent;

    /// <inheritdoc />
    public Duty? CommandedDuty { get; private set; }

    /// <summary>
    /// True: the card's own curve is a real automatic mode, and putting it back is a genuine
    /// handoff rather than a duty guess.
    /// </summary>
    public bool SupportsAutomaticMode => true;

    /// <summary>The lowest duty this card will accept, which is not usually zero.</summary>
    internal float MinimumPercent => _speedRange.Min;

    /// <inheritdoc />
    public void Write(Duty duty)
    {
        lock (_gate)
        {
            // Clamped, never rescaled, matching the Super I/O path: a card with a 15% floor
            // should turn a request for 5% into 15%, not restretch every curve on the machine.
            var wanted = (int)MathF.Round(Math.Clamp(duty.Percent, _speedRange.Min, _speedRange.Max));

            DisableZeroRpm();

            var applied = _supportsTargetSpeed
                ? Adlx.In(_fanTuning, Adlx.Fan.SetTargetFanSpeed, wanted) == AdlxResult.Ok
                : WriteFlatCurve(wanted);

            if (applied)
            {
                CommandedDuty = duty;
            }
        }
    }

    /// <inheritdoc />
    public bool TryRestoreAutomaticMode()
    {
        lock (_gate)
        {
            var restored = _original.TryApply(_fanTuning);

            if (_zeroRpmSupported && _zeroRpmWasOn)
            {
                Adlx.In(_fanTuning, Adlx.Fan.SetZeroRPMState, 1);
            }

            if (restored)
            {
                CommandedDuty = null;
            }

            return restored;
        }
    }

    /// <summary>
    /// Turns Zero RPM off, because it silently overrides every duty written while it is on.
    /// </summary>
    /// <remarks>
    /// Done on every write rather than once. The driver turns it back on by itself — a profile
    /// change, a driver reset, Adrenalin being opened — and a control that was correct at startup
    /// and quietly stopped applying an hour later is the failure this whole provider exists to
    /// remove, not one to reintroduce.
    /// </remarks>
    private void DisableZeroRpm()
    {
        if (!_zeroRpmSupported)
        {
            return;
        }

        var state = 0;

        if (Adlx.Out(_fanTuning, Adlx.Fan.GetZeroRPMState, &state) == AdlxResult.Ok && state != 0)
        {
            Adlx.In(_fanTuning, Adlx.Fan.SetZeroRPMState, 0);
        }
    }

    /// <summary>
    /// Expresses a constant duty as a curve that is flat at that duty.
    /// </summary>
    /// <remarks>
    /// The temperatures are left exactly as the card ordered them. ADLX validates that a state
    /// list rises monotonically in temperature, and rewriting them is both unnecessary and a way
    /// to get the whole list rejected.
    /// </remarks>
    private bool WriteFlatCurve(int percent)
    {
        void* states;

        if (Adlx.Out(_fanTuning, Adlx.Fan.GetFanTuningStates, &states) != AdlxResult.Ok)
        {
            return false;
        }

        try
        {
            var count = Adlx.Count(states, Adlx.List.Size);
            var begin = Adlx.Count(states, Adlx.List.Begin);

            for (var i = begin; i < begin + count; i++)
            {
                void* state;

                if (Adlx.At(states, i, &state) != AdlxResult.Ok)
                {
                    return false;
                }

                Adlx.In(state, Adlx.State.SetFanSpeed, percent);
            }

            var errorIndex = -1;

            return Adlx.ForGpu(_fanTuning, Adlx.Fan.IsValidFanTuningStates, states, &errorIndex) == AdlxResult.Ok
                && ((delegate* unmanaged[Stdcall]<void*, void*, AdlxResult>)
                        Adlx.Vtbl(_fanTuning)[Adlx.Fan.SetFanTuningStates])(_fanTuning, states) == AdlxResult.Ok;
        }
        finally
        {
            Adlx.Release(states, 1);
        }
    }
}
