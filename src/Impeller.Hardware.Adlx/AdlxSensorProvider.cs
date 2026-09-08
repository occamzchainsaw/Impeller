using Impeller.Core.Abstractions;

namespace Impeller.Hardware.Adlx;

/// <summary>
/// Fan control for AMD GPUs, through AMD's own ADLX library.
/// </summary>
/// <remarks>
/// <para>
/// A deliberately narrow provider: it exposes fan controls and nothing else. Temperatures, clocks
/// and speeds all come from LibreHardwareMonitor perfectly well — it is only <em>writing</em> a
/// GPU fan that its legacy Overdrive path can no longer do. Duplicating the readable sensors here
/// would give every GPU reading two identities and make configurations ambiguous for no gain.
/// </para>
/// <para>
/// Absent hardware is not a failure. A machine with no AMD GPU, or with a driver too old to ship
/// ADLX, initialises to zero controls and says so — the engine carries on with its other
/// providers, exactly as it does when a Super I/O chip is unreadable.
/// </para>
/// </remarks>
public sealed unsafe class AdlxSensorProvider(ISensorIdentityMap identityMap) : ISensorProvider
{
    /// <summary>
    /// Serialises every ADLX call. The library is not documented as thread-safe, the tick loop
    /// and a tuning run can both reach a control, and a driver call that interleaves badly takes
    /// the display with it.
    /// </summary>
    private readonly Lock _gate = new();

    private readonly ISensorIdentityMap _identityMap =
        identityMap ?? throw new ArgumentNullException(nameof(identityMap));

    private readonly List<ISensor> _sensors = [];
    private readonly List<IControl> _controls = [];
    private readonly List<nint> _fanTunings = [];

    private void* _system;
    private void* _tuningServices;
    private bool _initialized;

    /// <inheritdoc />
    public string ProviderId => "adlx";

    /// <inheritdoc />
    public string DisplayName => "AMD ADLX";

    /// <summary>
    /// A GPU fan holds whatever it was last given, so there is nothing to re-read on a tick.
    /// </summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public IReadOnlyList<ISensor> Sensors => _sensors;

    /// <inheritdoc />
    public IReadOnlyList<IControl> Controls => _controls;

    /// <inheritdoc />
    public event EventHandler? TopologyChanged;

    /// <inheritdoc />
    public Task<ProviderInitializationResult> InitializeAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            try
            {
                return Task.FromResult(Initialize());
            }
            catch (DllNotFoundException)
            {
                // No AMD driver on this machine. Ordinary, and not worth an error.
                return Task.FromResult(ProviderInitializationResult.Success(0, 0));
            }
            catch (EntryPointNotFoundException ex)
            {
                // An AMD driver too old to carry ADLX. Named, because it is actionable.
                return Task.FromResult(new ProviderInitializationResult(
                    true, 0, 0, ["AMD ADLX (driver too old)"], ex));
            }
        }
    }

    /// <summary>
    /// Nothing to do. This provider owns no readable sensors, and a control's commanded duty is
    /// held in the control itself.
    /// </summary>
    public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            // Every card goes back to the curve it had. A service stopping must not leave a GPU
            // pinned to a flat curve with Zero RPM disabled.
            foreach (var control in _controls)
            {
                try
                {
                    control.TryRestoreAutomaticMode();
                }
                catch (Exception)
                {
                    // Best effort by definition: the driver may already be going away.
                }
            }

            foreach (var fanTuning in _fanTunings)
            {
                Adlx.Release((void*)fanTuning, Adlx.Fan.Release);
            }

            _fanTunings.Clear();
            _controls.Clear();
            _sensors.Clear();

            if (_tuningServices is not null)
            {
                Adlx.Release(_tuningServices, Adlx.Tuning.Release);
                _tuningServices = null;
            }

            if (_initialized)
            {
                Adlx.ADLXTerminate();
                _initialized = false;
                _system = null;
            }
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>Raises <see cref="TopologyChanged"/>, for a GPU appearing or leaving.</summary>
    private void OnTopologyChanged() => TopologyChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// ADLX is present but would not answer, reported as a named group rather than as a failure.
    /// </summary>
    /// <remarks>
    /// Succeeded stays true on purpose. A machine whose AMD driver will not talk to us still has
    /// working motherboard fan control, and failing the provider would be claiming otherwise. The
    /// group name carries the call and the code, so the reason reaches the engine log and the
    /// diagnostics bundle instead of being swallowed.
    /// </remarks>
    private static ProviderInitializationResult Unavailable(string call, AdlxResult result) =>
        new(true, 0, 0, [$"AMD ADLX ({call} returned {result})"]);

    private ProviderInitializationResult Initialize()
    {
        ulong version;

        // The runtime's own version rather than a compiled-in constant. ADLX refuses a version it
        // does not recognise, and asking it what it is cannot be wrong.
        var queried = Adlx.ADLXQueryFullVersion(&version);

        if (queried != AdlxResult.Ok)
        {
            return Unavailable("ADLXQueryFullVersion", queried);
        }

        void* system;
        var started = Adlx.ADLXInitialize(version, &system);

        if (started != AdlxResult.Ok)
        {
            // The one that matters in practice. The engine is a service running as LocalSystem
            // with no desktop, and if ADLX turns out to require a user session this is where it
            // says so — as a named group in the engine log rather than as a provider that quietly
            // found no hardware.
            return Unavailable("ADLXInitialize", started);
        }

        _initialized = true;
        _system = system;

        void* gpus;
        var listed = Adlx.Out(_system, Adlx.System.GetGPUs, &gpus);

        if (listed != AdlxResult.Ok)
        {
            return Unavailable("GetGPUs", listed);
        }

        void* tuningServices;
        var tuned = Adlx.Out(_system, Adlx.System.GetGPUTuningServices, &tuningServices);

        if (tuned != AdlxResult.Ok)
        {
            return Unavailable("GetGPUTuningServices", tuned);
        }

        _tuningServices = tuningServices;

        var count = Adlx.Count(gpus, Adlx.List.Size);
        var begin = Adlx.Count(gpus, Adlx.List.Begin);

        for (var i = begin; i < begin + count; i++)
        {
            void* gpu;

            if (Adlx.At(gpus, i, &gpu) != AdlxResult.Ok)
            {
                continue;
            }

            AddIfControllable(gpu, (int)(i - begin));
        }

        return ProviderInitializationResult.Success(_sensors.Count, _controls.Count);
    }

    /// <summary>
    /// Adds a fan control for one GPU, if this card supports manual fan tuning at all.
    /// </summary>
    /// <remarks>
    /// An integrated GPU normally does not, and neither does a card whose driver is in a state
    /// ADLX will not tune. Both are skipped silently: a machine with an APU and a discrete card
    /// should end up with exactly one controllable fan, not one control and one error.
    /// </remarks>
    private void AddIfControllable(void* gpu, int index)
    {
        var supported = 0;

        if (Adlx.ForGpu(_tuningServices, Adlx.Tuning.IsSupportedManualFanTuning, gpu, &supported) != AdlxResult.Ok
            || supported == 0)
        {
            return;
        }

        void* fanTuning;

        if (Adlx.ForGpu(_tuningServices, Adlx.Tuning.GetManualFanTuning, gpu, &fanTuning) != AdlxResult.Ok)
        {
            return;
        }

        _fanTunings.Add((nint)fanTuning);

        var speedRange = default(AdlxIntRange);
        var temperatureRange = default(AdlxIntRange);

        ((delegate* unmanaged[Stdcall]<void*, AdlxIntRange*, AdlxIntRange*, AdlxResult>)
            Adlx.Vtbl(fanTuning)[Adlx.Fan.GetFanTuningRanges])(fanTuning, &speedRange, &temperatureRange);

        int zeroSupported = 0, zeroState = 0, targetSupported = 0;
        Adlx.Out(fanTuning, Adlx.Fan.IsSupportedZeroRPM, &zeroSupported);
        Adlx.Out(fanTuning, Adlx.Fan.GetZeroRPMState, &zeroState);
        Adlx.Out(fanTuning, Adlx.Fan.IsSupportedTargetFanSpeed, &targetSupported);

        var fingerprint = new HardwareFingerprint(ProviderId, $"/gpu/{index}", 0, SensorKind.Control);

        // From the persistent map, not SensorId.New(). A random id here meant this control got a
        // different identity every time the service restarted — every reinstall, every reboot —
        // and any binding, pairing or calibration saved against the old one went stale on the very
        // next start. That is the actual reason pairing never held: the control the user paired
        // yesterday was not the control being driven today.
        var fan = new AdlxGpuFan(
            _identityMap.GetOrCreate(fingerprint),
            $"AMD GPU {index}",
            fingerprint,
            fanTuning,
            AdlxFanCurve.Capture(fanTuning),
            speedRange,
            zeroSupported != 0,
            zeroState != 0,
            targetSupported != 0,
            _gate);

        _sensors.Add(fan);
        _controls.Add(fan);
    }
}
