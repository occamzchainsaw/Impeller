namespace Impeller.Core.Abstractions;

/// <summary>
/// The engine's view of everything currently readable and writable, aggregated across providers.
/// </summary>
/// <remarks>
/// The tick loop talks only to this, never to providers directly. That keeps the loop testable
/// against a fake registry and means adding a provider changes nothing about how control works.
/// </remarks>
public interface ISensorRegistry
{
    /// <summary>The latest value for a sensor, or <see langword="null"/> if unknown or not reporting.</summary>
    float? GetValue(SensorId id);

    /// <summary>The control with this id, or <see langword="null"/> if it is not present.</summary>
    IControl? GetControl(SensorId id);

    /// <summary>Every control currently available for writing.</summary>
    IReadOnlyList<IControl> Controls { get; }

    /// <summary>Every sensor currently known, controls included.</summary>
    IReadOnlyList<ISensor> Sensors { get; }
}
