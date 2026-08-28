using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine.Tests;

/// <summary>
/// A hand-driven <see cref="ICurveEvaluationContext"/>. Lets a test advance time and feed
/// readings explicitly, so curve behaviour is verified without a clock or any hardware.
/// </summary>
internal sealed class TestCurveContext : ICurveEvaluationContext
{
    private readonly Dictionary<SensorId, float?> _sensors = [];
    private readonly Dictionary<CurveId, Duty?> _curves = [];

    /// <summary>Time attributed to the next evaluation. Defaults to the engine's nominal tick.</summary>
    public TimeSpan Elapsed { get; set; } = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public DateTimeOffset Timestamp { get; private set; } = DateTimeOffset.UnixEpoch;

    /// <inheritdoc />
    public float? GetSensorValue(SensorId id) => _sensors.GetValueOrDefault(id);

    /// <inheritdoc />
    public Duty? GetCurveOutput(CurveId id) => _curves.GetValueOrDefault(id);

    /// <summary>Sets a sensor reading. A null value models a sensor that has stopped reporting.</summary>
    public TestCurveContext WithSensor(SensorId id, float? value)
    {
        _sensors[id] = value;
        Timestamp += Elapsed;
        return this;
    }

    /// <summary>Sets the output another curve produced this tick.</summary>
    public TestCurveContext WithCurve(CurveId id, Duty? value)
    {
        _curves[id] = value;
        return this;
    }

    /// <summary>Sets how much time the next evaluation should account for.</summary>
    public TestCurveContext Advancing(TimeSpan elapsed)
    {
        Elapsed = elapsed;
        return this;
    }
}
