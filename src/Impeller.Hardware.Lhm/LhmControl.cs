using Impeller.Core.Abstractions;
using LhmHw = LibreHardwareMonitor.Hardware;

namespace Impeller.Hardware.Lhm;

/// <summary>
/// A writable LibreHardwareMonitor control — a PWM or DC fan header.
/// </summary>
/// <remarks>
/// <para>
/// Writes are serialised through the provider's lock. LibreHardwareMonitor talks to Super I/O
/// chips over a shared ISA/LPC bus with no internal synchronisation, so two threads writing at
/// once can interleave register accesses and land a duty on the wrong header.
/// </para>
/// </remarks>
internal sealed class LhmControl(
    SensorId id,
    LhmHw.ISensor sensor,
    HardwareFingerprint fingerprint,
    Lock gate) : LhmSensor(id, sensor, fingerprint), IControl
{
    /// <summary>
    /// How LibreHardwareMonitor turns a percentage into the byte it writes to the PWM register:
    /// <c>(byte)(percent * 2.55f)</c>, in <c>SuperIOHardware.GetSoftwareValueAsByte</c>.
    /// </summary>
    private const float PercentToRegister = 2.55f;

    /// <summary>Close enough that two percentages are the same request.</summary>
    private const float Epsilon = 0.0001f;

    private readonly LhmHw.IControl _control = sensor.Control;

    /// <inheritdoc />
    public Duty? CommandedDuty { get; private set; }

    /// <summary>
    /// LibreHardwareMonitor restores the register values it captured when it opened the chip, so
    /// handing a header back to firmware is genuinely supported here — unlike the general case,
    /// where a control has no automatic mode to return to.
    /// </summary>
    public bool SupportsAutomaticMode => true;

    /// <inheritdoc />
    public void Write(Duty duty)
    {
        lock (gate)
        {
            // Clamped, never rescaled. If a header refuses to go below 20%, a curve asking for 10%
            // should get 20% — not have its whole range stretched so that "50%" silently means
            // something else. Distorting the curve to fit the hardware would make every saved
            // configuration mean something different on different machines.
            var value = Math.Clamp(duty.Percent, _control.MinSoftwareValue, _control.MaxSoftwareValue);

            // LibreHardwareMonitor drops a write whose value equals the one it is already holding,
            // and that swallows precisely the write this engine most needs to land: the periodic
            // restatement of a duty that has not changed. Restating it is the only thing that keeps
            // a header claimed, because writing one is what re-asserts the chip's manual-mode bit —
            // board firmware takes an unattended header back and drives it from its own curve.
            //
            // So an unchanged duty is nudged to a percentage that converts to the same PWM byte.
            // The register value written is identical; only the equality check is defeated.
            _control.SetSoftware(Math.Abs(_control.SoftwareValue - value) < Epsilon
                ? SameRegisterValueAs(value)
                : value);

            CommandedDuty = duty;
        }
    }

    /// <summary>
    /// A different percentage that lands on the same PWM register value as the one given.
    /// </summary>
    /// <remarks>
    /// Half a register step above the requested value, which by construction truncates back to the
    /// same byte, so the fan sees no change at all. Should the conversion above ever stop matching
    /// LibreHardwareMonitor's, the worst this can be wrong by is one step in 255 — a fifth of a
    /// percent of fan speed, and only on the ticks that restate an unchanged duty.
    /// </remarks>
    internal static float SameRegisterValueAs(float percent)
    {
        var register = (float)(byte)Math.Clamp(percent * PercentToRegister, 0f, 255f);
        var nudged = (register + 0.5f) / PercentToRegister;

        // A value already sitting exactly on the half step needs somewhere else to go, or the
        // write is dropped by the very check it exists to get past.
        return Math.Abs(nudged - percent) < Epsilon
            ? (register + 0.25f) / PercentToRegister
            : nudged;
    }

    /// <inheritdoc />
    public bool TryRestoreAutomaticMode()
    {
        lock (gate)
        {
            try
            {
                _control.SetDefault();
                CommandedDuty = null;
                return _control.ControlMode == LhmHw.ControlMode.Default;
            }
            catch (Exception)
            {
                // Failing to hand back is not fatal and not exceptional: the caller falls back to
                // writing a failsafe duty, which is the outcome this method exists to try to avoid
                // but is perfectly safe.
                return false;
            }
        }
    }
}
