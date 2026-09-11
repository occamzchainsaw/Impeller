using Impeller.Core.Abstractions;
using Impeller.Plugins.Abstractions;

namespace Impeller.Plugins.Host;

/// <summary>
/// Sensors and controls contributed by plugins, presented to the engine as one ordinary
/// <see cref="ISensorProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the engine side of <see cref="PluginCapability.ProvideHardware"/>. It exists because
/// some hardware simply cannot be driven from the engine's own process: an AMD GPU's fan tuning
/// interface, for one, only grants full functionality to a process in an interactive session, and
/// the engine runs as a Session-0 service. A plugin running where the user is logged in can reach
/// that hardware and hand it to the engine over the ordinary plugin pipe, at which point it is a
/// control like any other — bindable, calibratable, drawable on a curve.
/// </para>
/// <para>
/// One instance, shared by every plugin, registered with the engine's provider list exactly once
/// at startup with nothing in it. Hardware arrives and leaves as plugins connect and disconnect,
/// each change announced through <see cref="TopologyChanged"/> so the registry reindexes — the
/// same mechanism a GPU appearing or a drive being unplugged already uses.
/// </para>
/// </remarks>
public sealed class PluginHardwareProvider(ISensorIdentityMap identityMap) : ISensorProvider
{
    private readonly ISensorIdentityMap _identityMap =
        identityMap ?? throw new ArgumentNullException(nameof(identityMap));

    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<ProvidedItem>> _byPlugin = new(StringComparer.Ordinal);

    private IReadOnlyList<ISensor> _sensors = [];
    private IReadOnlyList<IControl> _controls = [];

    /// <inheritdoc />
    public string ProviderId => "plugin";

    /// <inheritdoc />
    public string DisplayName => "Plugin-provided hardware";

    /// <summary>
    /// Irrelevant. Values arrive through <see cref="ApplyReadings"/> on the plugin's own schedule,
    /// never polled.
    /// </summary>
    public TimeSpan PollInterval => TimeSpan.FromHours(1);

    /// <inheritdoc />
    public IReadOnlyList<ISensor> Sensors => _sensors;

    /// <inheritdoc />
    public IReadOnlyList<IControl> Controls => _controls;

    /// <inheritdoc />
    public event EventHandler? TopologyChanged;

    /// <inheritdoc />
    public Task<ProviderInitializationResult> InitializeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ProviderInitializationResult.Success(0, 0));

    /// <inheritdoc />
    public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _byPlugin.Clear();
            _sensors = [];
            _controls = [];
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Takes on one plugin's declared hardware, replacing anything it declared before.
    /// </summary>
    /// <remarks>
    /// A reconnect re-declares from scratch — the plugin process restarted, so nothing about its
    /// last declaration can be assumed still true. The fingerprint is what carries identity across
    /// that, not the declaration itself: the same <see cref="HardwareDeclaration.HardwareKey"/> and
    /// item key mint the same <see cref="SensorId"/> every time, so a binding made against last
    /// session's GPU fan still resolves against this session's.
    /// </remarks>
    public HardwareAdmission Admit(
        PluginSession session,
        string pluginId,
        IReadOnlyList<HardwareDeclaration> hardware)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(hardware);

        if (hardware.Count == 0)
        {
            return new HardwareAdmission(
                ProviderOutcome.InvalidDeclaration,
                null,
                new Dictionary<string, SensorRef>(),
                new Dictionary<string, SensorRef>(),
                "No hardware was declared.");
        }

        var sensorRefs = new Dictionary<string, SensorRef>(StringComparer.Ordinal);
        var controlRefs = new Dictionary<string, SensorRef>(StringComparer.Ordinal);
        var items = new List<ProvidedItem>();
        var providerId = $"plugin:{pluginId}";

        foreach (var device in hardware)
        {
            if (string.IsNullOrWhiteSpace(device.HardwareKey))
            {
                return Invalid("A declared device has an empty hardware key.");
            }

            foreach (var sensor in device.Sensors)
            {
                if (string.IsNullOrWhiteSpace(sensor.Key))
                {
                    return Invalid("A declared sensor has an empty key.");
                }

                var kind = PluginTranslation.ToSensorKind(sensor.Kind);
                var fingerprint = new HardwareFingerprint(
                    providerId, $"{device.HardwareKey}/{sensor.Key}", 0, kind);
                var id = _identityMap.GetOrCreate(fingerprint);

                items.Add(new ProvidedItem(sensor.Key, new PluginProvidedSensor(
                    id, sensor.Name, device.DisplayName, kind, fingerprint)));
                sensorRefs[sensor.Key] = PluginTranslation.ToRef(id);
            }

            foreach (var control in device.Controls)
            {
                if (string.IsNullOrWhiteSpace(control.Key))
                {
                    return Invalid("A declared control has an empty key.");
                }

                var fingerprint = new HardwareFingerprint(
                    providerId, $"{device.HardwareKey}/{control.Key}", 0, SensorKind.Control);
                var id = _identityMap.GetOrCreate(fingerprint);
                var reference = PluginTranslation.ToRef(id);

                var provided = new PluginProvidedControl(
                    id,
                    control.Name,
                    device.DisplayName,
                    fingerprint,
                    session,
                    reference,
                    control.SupportsAutomaticMode);

                items.Add(new ProvidedItem(control.Key, provided));
                controlRefs[control.Key] = reference;
            }
        }

        lock (_gate)
        {
            _byPlugin[pluginId] = items;
            Reindex();
        }

        TopologyChanged?.Invoke(this, EventArgs.Empty);

        return new HardwareAdmission(
            ProviderOutcome.Accepted,
            providerId,
            sensorRefs,
            controlRefs,
            $"{items.Count} item(s) accepted.");

        static HardwareAdmission Invalid(string message) => new(
            ProviderOutcome.InvalidDeclaration,
            null,
            new Dictionary<string, SensorRef>(),
            new Dictionary<string, SensorRef>(),
            message);
    }

    /// <summary>Drops everything a plugin contributed, because it disconnected.</summary>
    /// <remarks>
    /// The controls simply vanish from the registry rather than being failsafed here — the plugin
    /// SDK already drove them to their declared failsafe duty locally before the connection closed,
    /// which is the only place that can be relied on to still be reachable. See
    /// <c>IPluginHardware.ApplyFailsafe</c>.
    /// </remarks>
    public void Withdraw(string pluginId)
    {
        bool removed;

        lock (_gate)
        {
            removed = _byPlugin.Remove(pluginId);

            if (removed)
            {
                Reindex();
            }
        }

        if (removed)
        {
            TopologyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Applies fresh values pushed by a plugin for the sensors and controls it contributed.</summary>
    public void ApplyReadings(string pluginId, IReadOnlyList<ProvidedReading> readings)
    {
        List<ProvidedItem>? items;

        lock (_gate)
        {
            _byPlugin.TryGetValue(pluginId, out items);
        }

        if (items is null)
        {
            return;
        }

        foreach (var reading in readings)
        {
            foreach (var item in items)
            {
                if (string.Equals(item.Key, reading.Key, StringComparison.Ordinal))
                {
                    item.Sensor.SetValue(reading.Value);
                    break;
                }
            }
        }
    }

    private void Reindex()
    {
        // Called with _gate already held.
        var sensors = new List<ISensor>();
        var controls = new List<IControl>();

        foreach (var items in _byPlugin.Values)
        {
            foreach (var item in items)
            {
                sensors.Add(item.Sensor);

                if (item.Sensor is IControl control)
                {
                    controls.Add(control);
                }
            }
        }

        _sensors = sensors;
        _controls = controls;
    }

    private readonly record struct ProvidedItem(string Key, PluginProvidedSensor Sensor);
}
