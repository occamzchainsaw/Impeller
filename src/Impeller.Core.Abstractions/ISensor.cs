namespace Impeller.Core.Abstractions;

/// <summary>
/// A single readable measurement, refreshed by its owning <see cref="ISensorProvider"/>.
/// </summary>
public interface ISensor
{
    /// <summary>Stable identity. The only thing a saved configuration stores.</summary>
    SensorId Id { get; }

    /// <summary>Provider-supplied name, for example <c>CPU Package</c>. Not unique, not identity.</summary>
    /// <remarks>
    /// The sensor's own name and nothing else. A provider must not qualify it with its hardware -
    /// that is what <see cref="HardwareName"/> is for. Gluing the two together forces the
    /// combination on every surface downstream, including the ones that already say which piece of
    /// hardware they are showing, and no consumer can take it apart again without guessing at a
    /// separator.
    /// </remarks>
    string Name { get; }

    /// <summary>
    /// The hardware this sensor belongs to, for example <c>Nuvoton NCT6687D</c>, or empty when the
    /// provider cannot say.
    /// </summary>
    /// <remarks>
    /// A default implementation rather than a required member, so a provider that has nothing
    /// useful to add - and every existing test double - is unaffected. Names are not unique within
    /// a machine and this is what disambiguates them: four sensors called "Fan #2" are ordinary.
    /// </remarks>
    string HardwareName => string.Empty;

    /// <summary>What this sensor measures.</summary>
    SensorKind Kind { get; }

    /// <summary>
    /// The most recent reading, or <see langword="null"/> when the sensor has not reported yet
    /// or has stopped responding. Curves must treat null as "no data", never as zero — a missing
    /// temperature reading interpreted as 0 °C would idle a fan that should be ramping.
    /// </summary>
    float? Value { get; }

    /// <summary>How this sensor is recognised across restarts. Diagnostics and identity mapping only.</summary>
    HardwareFingerprint Fingerprint { get; }
}

/// <summary>
/// A writable output that drives a fan or pump.
/// </summary>
/// <remarks>
/// Implementations are responsible only for talking to hardware. Ownership arbitration,
/// slew limiting, calibration mapping and failsafe behaviour all live in the engine, so a
/// provider never needs to reason about who is allowed to write.
/// </remarks>
public interface IControl : ISensor
{
    /// <summary>The duty most recently written by the engine, or <see langword="null"/> if never written.</summary>
    Duty? CommandedDuty { get; }

    /// <summary>
    /// Whether the underlying device can be handed back to its own firmware curve.
    /// Rare: most Super I/O chips expose no such register, which is why the engine's
    /// failsafe is a duty write rather than a mode change.
    /// </summary>
    bool SupportsAutomaticMode { get; }

    /// <summary>Writes a duty to the hardware.</summary>
    void Write(Duty duty);

    /// <summary>
    /// Attempts to return the control to firmware/BIOS management.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the device accepted the handoff. A <see langword="false"/> result is
    /// expected on most hardware and is not an error; the caller falls back to a failsafe duty.
    /// </returns>
    bool TryRestoreAutomaticMode();
}
