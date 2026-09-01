using LibreHardwareMonitor.Hardware;

namespace Impeller.Hardware.Lhm;

/// <summary>
/// Refreshes a hardware subtree.
/// </summary>
/// <remarks>
/// LibreHardwareMonitor's <see cref="IHardware.Update"/> does not recurse into
/// <see cref="IHardware.SubHardware"/>, and on a desktop the fan headers live in exactly that
/// place — a SuperIO chip hanging off the motherboard. Updating only top-level hardware yields a
/// motherboard with no fans, which looks like unsupported hardware rather than a missed call.
/// </remarks>
internal sealed class UpdateVisitor : IVisitor
{
    /// <inheritdoc />
    public void VisitComputer(IComputer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        computer.Traverse(this);
    }

    /// <inheritdoc />
    public void VisitHardware(IHardware hardware)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        hardware.Update();

        foreach (var sub in hardware.SubHardware)
        {
            sub.Accept(this);
        }
    }

    /// <inheritdoc />
    public void VisitSensor(ISensor sensor)
    {
        // Values come from the hardware update; sensors need no separate visit.
    }

    /// <inheritdoc />
    public void VisitParameter(IParameter parameter)
    {
        // Parameters are configuration, not readings.
    }
}
