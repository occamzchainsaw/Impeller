using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;

namespace Impeller.Core.Engine.Sensors;

/// <summary>
/// Exposes the engine's own computed sensors through the same interface the hardware backends use.
/// </summary>
/// <remarks>
/// <para>
/// Making these a provider rather than a special case is what keeps them free: identity, the flat
/// registry, curve inputs and the settings UI all treat a mix of two temperatures exactly as they
/// treat a temperature. Nothing downstream needs to know the difference.
/// </para>
/// <para>
/// Evaluation runs in dependency order so a sensor derived from another derived sensor is correct
/// within one tick rather than lagging behind it. A definition that reads itself, directly or
/// through others, is dropped rather than evaluated — the loop is reported by the validator before
/// it ever gets here, and dropping is the safe answer if one arrives anyway.
/// </para>
/// </remarks>
public sealed class CustomSensorProvider(TimeProvider timeProvider) : IDerivedSensorProvider
{
    /// <summary>The provider id these sensors' fingerprints carry.</summary>
    public const string Id = "custom";

    private readonly TimeProvider _time = timeProvider;
    private readonly Lock _gate = new();

    private Dictionary<SensorId, CustomSensor> _byId = [];
    private CustomSensor[] _ordered = [];

    /// <summary>Where source readings are read from. Set once, before the first refresh.</summary>
    public ISensorRegistry? Registry { get; set; }

    /// <inheritdoc />
    public string ProviderId => Id;

    /// <inheritdoc />
    public string DisplayName => "Custom sensors";

    /// <summary>
    /// Computed every tick. There is nothing to wait for: the work is arithmetic over readings
    /// another provider has already paid for.
    /// </summary>
    public TimeSpan PollInterval => TimeSpan.Zero;

    /// <inheritdoc />
    public IReadOnlyList<ISensor> Sensors
    {
        get
        {
            lock (_gate)
            {
                return _ordered;
            }
        }
    }

    /// <summary>Never any. A computed value is not something that can be written to.</summary>
    public IReadOnlyList<IControl> Controls => [];

    /// <inheritdoc />
    public event EventHandler? TopologyChanged;

    /// <summary>
    /// Replaces the set of custom sensors.
    /// </summary>
    /// <remarks>
    /// Sensors that survive a configuration change keep their instance, and so keep any averaging
    /// window they had accumulated. Renaming a sensor or adjusting an unrelated one should not
    /// cost a time-average sensor its history.
    /// </remarks>
    public void SetDefinitions(IReadOnlyList<CustomSensorDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        lock (_gate)
        {
            var next = new Dictionary<SensorId, CustomSensor>(definitions.Count);

            foreach (var definition in definitions)
            {
                if (definition.Id.IsNone || next.ContainsKey(definition.Id))
                {
                    continue;
                }

                if (_byId.TryGetValue(definition.Id, out var existing))
                {
                    if (existing.Definition != definition)
                    {
                        existing.Update(definition);
                    }

                    next[definition.Id] = existing;
                }
                else
                {
                    next[definition.Id] = new CustomSensor(definition);
                }
            }

            _byId = next;
            _ordered = Order(next);
        }

        TopologyChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public Task<ProviderInitializationResult> InitializeAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(ProviderInitializationResult.Success(_ordered.Length, 0));
        }
    }

    /// <inheritdoc />
    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (Registry is not { } registry)
        {
            return Task.CompletedTask;
        }

        CustomSensor[] ordered;
        Dictionary<SensorId, CustomSensor> own;

        lock (_gate)
        {
            ordered = _ordered;
            own = _byId;
        }

        var now = _time.GetUtcNow();

        // Our own sensors first, then the registry. Evaluation runs in dependency order, so a
        // sibling read this way has already been recomputed this pass.
        float? Read(SensorId id) =>
            own.TryGetValue(id, out var sibling) ? sibling.Value : registry.GetValue(id);

        foreach (var sensor in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sensor.Refresh(Read, now);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _byId = [];
            _ordered = [];
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Orders sensors so each is computed after everything it reads. Anything caught in a cycle is
    /// left out entirely rather than evaluated in an arbitrary order.
    /// </summary>
    private static CustomSensor[] Order(Dictionary<SensorId, CustomSensor> byId)
    {
        var ordered = new List<CustomSensor>(byId.Count);
        var walk = new OrderWalk(byId, ordered);

        foreach (var id in byId.Keys)
        {
            walk.Visit(id);
        }

        // Everything caught in a loop is dropped, not merely ordered arbitrarily. A sensor that
        // reads itself has no defined value, and evaluating it anyway would give a number that
        // looked plausible and meant nothing.
        return [.. ordered.Where(sensor => !walk.Cyclic.Contains(sensor.Id))];
    }

    /// <summary>Depth-first ordering that records which sensors are caught in a cycle.</summary>
    private sealed class OrderWalk(Dictionary<SensorId, CustomSensor> byId, List<CustomSensor> ordered)
    {
        private readonly Dictionary<SensorId, bool> _finished = [];
        private readonly List<SensorId> _path = [];

        public HashSet<SensorId> Cyclic { get; } = [];

        public void Visit(SensorId id)
        {
            if (_finished.TryGetValue(id, out var done))
            {
                if (!done)
                {
                    // Still on the stack: everything from its first appearance onward is in the loop.
                    var from = _path.IndexOf(id);

                    for (var i = from < 0 ? 0 : from; i < _path.Count; i++)
                    {
                        Cyclic.Add(_path[i]);
                    }
                }

                return;
            }

            if (!byId.TryGetValue(id, out var sensor))
            {
                // A hardware sensor, or one that has gone away. Either way, not ours to order.
                return;
            }

            _finished[id] = false;
            _path.Add(id);

            foreach (var source in sensor.Sources)
            {
                Visit(source);
            }

            _path.RemoveAt(_path.Count - 1);
            _finished[id] = true;
            ordered.Add(sensor);
        }
    }
}
