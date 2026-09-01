namespace Impeller.Hardware.Lhm;

/// <summary>Which hardware LibreHardwareMonitor should open, and how often to re-read it.</summary>
public sealed class LhmOptions
{
    /// <summary>The configuration section these are bound from.</summary>
    public const string SectionName = "Hardware:Lhm";

    /// <summary>Motherboard and Super I/O. Off means no fan headers, so this is effectively required.</summary>
    public bool EnableMotherboard { get; set; } = true;

    /// <summary>CPU package and core temperatures — the most common curve input.</summary>
    public bool EnableCpu { get; set; } = true;

    /// <summary>GPU sensors and, on supported cards, GPU fan control.</summary>
    public bool EnableGpu { get; set; } = true;

    /// <summary>Memory. Rarely a useful curve input; cheap to read.</summary>
    public bool EnableMemory { get; set; }

    /// <summary>
    /// Drive temperatures, read over SMART.
    /// </summary>
    /// <remarks>
    /// Genuinely useful for NVMe cooling, but each read costs a device round-trip, which is why
    /// <see cref="SlowPollInterval"/> exists rather than polling these at the tick rate.
    /// </remarks>
    public bool EnableStorage { get; set; } = true;

    /// <summary>Network adapters. Off by default; almost never drives a fan.</summary>
    public bool EnableNetwork { get; set; }

    /// <summary>AIO pumps and USB fan controllers.</summary>
    public bool EnableCooler { get; set; } = true;

    /// <summary>Embedded controllers. The only source of fan control on many laptops.</summary>
    public bool EnableEmbeddedController { get; set; } = true;

    /// <summary>Digital power supplies.</summary>
    public bool EnablePsu { get; set; } = true;

    /// <summary>Battery sensors, on portable machines.</summary>
    public bool EnableBattery { get; set; }

    /// <summary>
    /// Cadence for hardware that is expensive to read — storage and network.
    /// </summary>
    /// <remarks>
    /// Sixty seconds matches what FanControl settled on. Drive temperature moves slowly enough
    /// that a faster poll buys nothing and costs a SMART query per drive per tick.
    /// </remarks>
    public TimeSpan SlowPollInterval { get; set; } = TimeSpan.FromSeconds(60);
}
