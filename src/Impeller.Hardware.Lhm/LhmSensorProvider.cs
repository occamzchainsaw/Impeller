using System.Runtime.Versioning;
using System.Security.Principal;
using Impeller.Core.Abstractions;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Options;

namespace Impeller.Hardware.Lhm;

/// <summary>
/// Exposes LibreHardwareMonitor as an <see cref="ISensorProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place in Impeller that knows LibreHardwareMonitor exists. Everything above it
/// sees sensors, controls and fingerprints, which is what keeps a second backend (NvAPI, ADLX, a
/// plugin) from needing engine changes.
/// </para>
/// <para>
/// <b>Elevation.</b> Reading MSRs and Super I/O registers goes through a kernel driver, so an
/// unelevated process enumerates a much thinner set of hardware — typically no motherboard fan
/// headers at all. Rather than fail, the provider reports what it found and flags the groups that
/// came back empty, so the UI can say "run elevated" instead of "your motherboard is unsupported".
/// </para>
/// <para>
/// <b>Threading.</b> LibreHardwareMonitor performs no internal synchronisation and its Super I/O
/// access is a shared bus. Every refresh and every control write is serialised through one lock,
/// shared with the controls this provider hands out.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class LhmSensorProvider : ISensorProvider
{
    private readonly ISensorIdentityMap _identityMap;
    private readonly LhmOptions _options;
    private readonly Lock _gate = new();
    private readonly UpdateVisitor _visitor = new();

    private Computer? _computer;
    private List<Core.Abstractions.ISensor> _sensors = [];
    private List<Core.Abstractions.IControl> _controls = [];
    private DateTimeOffset _slowLastRefreshed = DateTimeOffset.MinValue;
    private bool _disposed;

    /// <summary>Creates the provider.</summary>
    /// <param name="identityMap">Supplies stable ids for the fingerprints this provider mints.</param>
    /// <param name="options">Which hardware groups to open.</param>
    public LhmSensorProvider(ISensorIdentityMap identityMap, IOptions<LhmOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _identityMap = identityMap ?? throw new ArgumentNullException(nameof(identityMap));
        _options = options.Value;
    }

    /// <inheritdoc />
    public string ProviderId => "lhm";

    /// <inheritdoc />
    public string DisplayName => "LibreHardwareMonitor";

    /// <summary>
    /// The tick rate. Slow hardware is throttled internally rather than by asking the engine for a
    /// long interval, because one provider covers both fan headers and drive temperatures.
    /// </summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public IReadOnlyList<Core.Abstractions.ISensor> Sensors
    {
        get
        {
            lock (_gate)
            {
                return _sensors;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Core.Abstractions.IControl> Controls
    {
        get
        {
            lock (_gate)
            {
                return _controls;
            }
        }
    }

    /// <summary>
    /// Whether this process has the privileges the kernel driver needs. False means the sensor set
    /// will be thin, and is worth surfacing rather than diagnosing as broken hardware.
    /// </summary>
    public static bool IsElevated =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <inheritdoc />
    public event EventHandler? TopologyChanged;

    /// <inheritdoc />
    public Task<ProviderInitializationResult> InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var computer = new Computer
            {
                IsMotherboardEnabled = _options.EnableMotherboard,
                IsCpuEnabled = _options.EnableCpu,
                IsGpuEnabled = _options.EnableGpu,
                IsMemoryEnabled = _options.EnableMemory,
                IsStorageEnabled = _options.EnableStorage,
                IsNetworkEnabled = _options.EnableNetwork,
                IsControllerEnabled = _options.EnableCooler,
                IsPsuEnabled = _options.EnablePsu,
                IsBatteryEnabled = _options.EnableBattery,
            };

            computer.Open();

            // One update before enumerating, so sensors that only materialise after a first read
            // are present when ids are minted. Without it, controls can be missed on first run and
            // then appear on the next launch carrying freshly minted ids.
            computer.Accept(_visitor);

            computer.HardwareAdded += OnHardwareChanged;
            computer.HardwareRemoved += OnHardwareChanged;

            int sensorCount;
            int controlCount;

            lock (_gate)
            {
                _computer = computer;
                Reindex(computer);
                sensorCount = _sensors.Count;
                controlCount = _controls.Count;
            }

            var failed = FindEmptyGroups(computer);

            return Task.FromResult(failed.Count == 0
                ? ProviderInitializationResult.Success(sensorCount, controlCount)
                : ProviderInitializationResult.Partial(sensorCount, controlCount, failed));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ProviderInitializationResult.Failed(ex));
        }
    }

    /// <inheritdoc />
    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_computer is null)
            {
                return Task.CompletedTask;
            }

            var now = DateTimeOffset.UtcNow;
            var includeSlow = now - _slowLastRefreshed >= _options.SlowPollInterval;

            foreach (var hardware in _computer.Hardware)
            {
                if (!includeSlow && IsSlow(hardware))
                {
                    continue;
                }

                hardware.Accept(_visitor);
            }

            if (includeSlow)
            {
                _slowLastRefreshed = now;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Builds the flat sensor and control lists, resolving a stable id per fingerprint.</summary>
    private void Reindex(Computer computer)
    {
        var sensors = new List<Core.Abstractions.ISensor>();
        var controls = new List<Core.Abstractions.IControl>();

        foreach (var hardware in computer.Hardware)
        {
            Walk(hardware, sensors, controls);
        }

        _sensors = sensors;
        _controls = controls;
    }

    private void Walk(
        IHardware hardware,
        List<Core.Abstractions.ISensor> sensors,
        List<Core.Abstractions.IControl> controls)
    {
        var hardwareKey = LhmMapping.HardwareKey(hardware);

        foreach (var sensor in hardware.Sensors)
        {
            var kind = LhmMapping.ToSensorKind(sensor.SensorType);
            var fingerprint = new HardwareFingerprint(ProviderId, hardwareKey, sensor.Index, kind);
            var id = _identityMap.GetOrCreate(fingerprint);

            if (sensor.Control is not null && kind == SensorKind.Control)
            {
                var control = new LhmControl(id, sensor, fingerprint, _gate);
                controls.Add(control);
                sensors.Add(control);
            }
            else
            {
                sensors.Add(new LhmSensor(id, sensor, fingerprint));
            }
        }

        foreach (var sub in hardware.SubHardware)
        {
            Walk(sub, sensors, controls);
        }
    }

    /// <summary>
    /// Groups that were asked for but produced no hardware.
    /// </summary>
    /// <remarks>
    /// An empty group is the signature of missing privileges or a missing vendor driver rather
    /// than of absent hardware — every machine has a motherboard and a CPU. Reporting it is what
    /// lets the UI say something more useful than "no sensors found".
    /// </remarks>
    private List<string> FindEmptyGroups(Computer computer)
    {
        var present = computer.Hardware
            .Select(hardware => LhmMapping.ToGroup(hardware.HardwareType))
            .ToHashSet();

        (bool Enabled, HardwareGroup Group)[] expected =
        [
            (_options.EnableMotherboard, HardwareGroup.Motherboard),
            (_options.EnableCpu, HardwareGroup.Cpu),
            (_options.EnableGpu, HardwareGroup.Gpu),
            (_options.EnableStorage, HardwareGroup.Storage),
        ];

        return [.. expected
            .Where(entry => entry.Enabled && !present.Contains(entry.Group))
            .Select(entry => entry.Group.ToString())];
    }

    private static bool IsSlow(IHardware hardware) =>
        LhmMapping.ToGroup(hardware.HardwareType) is HardwareGroup.Storage or HardwareGroup.Network;

    private void OnHardwareChanged(IHardware hardware) =>
        TopologyChanged?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        lock (_gate)
        {
            if (_computer is not null)
            {
                _computer.HardwareAdded -= OnHardwareChanged;
                _computer.HardwareRemoved -= OnHardwareChanged;

                try
                {
                    // Closing restores the register values LibreHardwareMonitor captured when it
                    // opened the chip, handing the headers back to firmware. The engine's failsafe
                    // has already run by this point; this is the belt to its braces.
                    _computer.Close();
                }
                catch (Exception)
                {
                    // A driver that will not close cleanly must not stop the service exiting.
                }

                _computer = null;
            }

            _sensors = [];
            _controls = [];
        }

        return ValueTask.CompletedTask;
    }
}
