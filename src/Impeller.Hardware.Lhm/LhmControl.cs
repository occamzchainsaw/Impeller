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

            _control.SetSoftware(value);
            CommandedDuty = duty;
        }
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
