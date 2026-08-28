using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine.Tests;

/// <summary>A control that records what was written to it instead of touching hardware.</summary>
internal sealed class FakeControl(string name = "fan") : IControl
{
    /// <summary>Every duty written, in order, so a test can assert on a ramp rather than a value.</summary>
    public List<Duty> Writes { get; } = [];

    public SensorId Id { get; } = SensorId.New();

    public string Name { get; } = name;

    public SensorKind Kind => SensorKind.Control;

    public float? Value => CommandedDuty?.Percent;

    public HardwareFingerprint Fingerprint { get; } = new("fake", "board", 0, SensorKind.Control);

    public Duty? CommandedDuty { get; private set; }

    public bool SupportsAutomaticMode { get; set; }

    /// <summary>When set, every write throws it. Models a control that has stopped responding.</summary>
    public Exception? WriteFault { get; set; }

    /// <summary>What <see cref="TryRestoreAutomaticMode"/> should report.</summary>
    public bool AutomaticModeRestoreSucceeds { get; set; } = true;

    /// <summary>How many times a handoff to firmware was attempted.</summary>
    public int AutomaticModeRestoreAttempts { get; private set; }

    public void Write(Duty duty)
    {
        if (WriteFault is { } fault)
        {
            throw fault;
        }

        CommandedDuty = duty;
        Writes.Add(duty);
    }

    public bool TryRestoreAutomaticMode()
    {
        AutomaticModeRestoreAttempts++;
        return AutomaticModeRestoreSucceeds;
    }
}

/// <summary>A sensor whose value a test sets directly.</summary>
internal sealed class FakeSensor(SensorKind kind = SensorKind.Temperature, string name = "sensor") : ISensor
{
    public SensorId Id { get; } = SensorId.New();

    public string Name { get; } = name;

    public SensorKind Kind { get; } = kind;

    public float? Value { get; set; }

    public HardwareFingerprint Fingerprint { get; } = new("fake", "board", 0, kind);
}

/// <summary>An in-memory <see cref="ISensorRegistry"/> assembled by a test.</summary>
internal sealed class FakeSensorRegistry : ISensorRegistry
{
    private readonly Dictionary<SensorId, ISensor> _sensors = [];
    private readonly Dictionary<SensorId, IControl> _controls = [];

    public IReadOnlyList<IControl> Controls => [.. _controls.Values];

    public IReadOnlyList<ISensor> Sensors => [.. _sensors.Values];

    public float? GetValue(SensorId id) => _sensors.GetValueOrDefault(id)?.Value;

    public IControl? GetControl(SensorId id) => _controls.GetValueOrDefault(id);

    /// <summary>Registers a readable sensor.</summary>
    public FakeSensor Add(FakeSensor sensor)
    {
        _sensors[sensor.Id] = sensor;
        return sensor;
    }

    /// <summary>Registers a writable control, which is also readable.</summary>
    public FakeControl Add(FakeControl control)
    {
        _sensors[control.Id] = control;
        _controls[control.Id] = control;
        return control;
    }
}
