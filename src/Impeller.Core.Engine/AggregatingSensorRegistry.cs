using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine;

/// <summary>
/// Presents every provider's sensors as one flat set.
/// </summary>
/// <remarks>
/// <para>
/// Providers are polled on their own cadence — motherboard sensors at the tick rate, storage
/// SMART data far more slowly — so a slow provider degrades only its own freshness rather than
/// stalling the tick loop. <see cref="RefreshDueAsync"/> refreshes only those actually due.
/// </para>
/// <para>
/// Identity collisions across providers are impossible by construction: a
/// <see cref="HardwareFingerprint"/> begins with its provider's id, and ids are minted per
/// fingerprint.
/// </para>
/// </remarks>
public sealed class AggregatingSensorRegistry : ISensorRegistry, IAsyncDisposable
{
    private readonly List<ProviderState> _providers = [];
    private readonly Lock _gate = new();

    private Dictionary<SensorId, ISensor> _sensors = [];
    private Dictionary<SensorId, IControl> _controls = [];

    /// <summary>Raised when a provider reports that its hardware set has changed.</summary>
    public event EventHandler<ISensorProvider>? ProviderTopologyChanged;

    /// <inheritdoc />
    public IReadOnlyList<IControl> Controls
    {
        get
        {
            lock (_gate)
            {
                return [.. _controls.Values];
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ISensor> Sensors
    {
        get
        {
            lock (_gate)
            {
                return [.. _sensors.Values];
            }
        }
    }

    /// <summary>
    /// How many providers are registered. Cheaper than materialising <see cref="Providers"/>
    /// just to count them.
    /// </summary>
    public int ProviderCount
    {
        get
        {
            lock (_gate)
            {
                return _providers.Count;
            }
        }
    }

    /// <summary>The providers currently registered, with their most recent initialisation result.</summary>
    public IReadOnlyList<(ISensorProvider Provider, ProviderInitializationResult? Result)> Providers
    {
        get
        {
            lock (_gate)
            {
                return [.. _providers.Select(state => (state.Provider, state.LastResult))];
            }
        }
    }

    /// <inheritdoc />
    public float? GetValue(SensorId id)
    {
        lock (_gate)
        {
            return _sensors.GetValueOrDefault(id)?.Value;
        }
    }

    /// <inheritdoc />
    public IControl? GetControl(SensorId id)
    {
        lock (_gate)
        {
            return _controls.GetValueOrDefault(id);
        }
    }

    /// <summary>
    /// Adds a provider and brings it up.
    /// </summary>
    /// <returns>
    /// The provider's initialisation result. A provider that fails is still registered, so the
    /// settings UI can show it as unavailable and offer a retry rather than having it vanish.
    /// </returns>
    public async Task<ProviderInitializationResult> AddAsync(
        ISensorProvider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        ProviderInitializationResult result;

        try
        {
            result = await provider.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = ProviderInitializationResult.Failed(ex);
        }

        var state = new ProviderState(provider) { LastResult = result };
        provider.TopologyChanged += (_, _) => ProviderTopologyChanged?.Invoke(this, provider);

        lock (_gate)
        {
            _providers.Add(state);
        }

        Reindex();
        return result;
    }

    /// <summary>
    /// Refreshes every provider whose poll interval has elapsed.
    /// </summary>
    /// <param name="now">The current time, supplied by the caller so ticks share one timestamp.</param>
    /// <param name="cancellationToken">Cancels the refresh, typically at service shutdown.</param>
    /// <returns>Providers whose refresh threw, paired with the exception.</returns>
    public async Task<IReadOnlyDictionary<string, Exception>> RefreshDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ProviderState[] due;

        lock (_gate)
        {
            due = [.. _providers.Where(state => state.IsDue(now) && state.LastResult?.Succeeded == true)];
        }

        if (due.Length == 0)
        {
            return new Dictionary<string, Exception>();
        }

        var faults = new Dictionary<string, Exception>();

        // Refreshed concurrently: a slow provider should not delay a fast one within the same tick.
        var refreshes = due.Select(async state =>
        {
            try
            {
                await state.Provider.RefreshAsync(cancellationToken).ConfigureAwait(false);
                state.MarkRefreshed(now);
            }
            catch (Exception ex)
            {
                state.MarkRefreshed(now);

                lock (faults)
                {
                    faults[state.Provider.ProviderId] = ex;
                }
            }
        });

        await Task.WhenAll(refreshes).ConfigureAwait(false);
        return faults;
    }

    /// <summary>
    /// Rebuilds the flat sensor and control lookups from the registered providers.
    /// Called after a provider is added or re-initialised.
    /// </summary>
    public void Reindex()
    {
        lock (_gate)
        {
            var sensors = new Dictionary<SensorId, ISensor>();
            var controls = new Dictionary<SensorId, IControl>();

            foreach (var state in _providers)
            {
                if (state.LastResult?.Succeeded != true)
                {
                    continue;
                }

                foreach (var sensor in state.Provider.Sensors)
                {
                    sensors[sensor.Id] = sensor;
                }

                foreach (var control in state.Provider.Controls)
                {
                    controls[control.Id] = control;
                }
            }

            _sensors = sensors;
            _controls = controls;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        ProviderState[] providers;

        lock (_gate)
        {
            providers = [.. _providers];
            _providers.Clear();
            _sensors = [];
            _controls = [];
        }

        foreach (var state in providers)
        {
            try
            {
                await state.Provider.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A provider failing to close cleanly must not prevent the others from closing.
            }
        }
    }

    /// <summary>Tracks when a provider was last refreshed, so its cadence can be honoured.</summary>
    private sealed class ProviderState(ISensorProvider provider)
    {
        private DateTimeOffset _lastRefreshed = DateTimeOffset.MinValue;

        public ISensorProvider Provider { get; } = provider;

        public ProviderInitializationResult? LastResult { get; set; }

        public bool IsDue(DateTimeOffset now) =>
            now - _lastRefreshed >= Provider.PollInterval;

        public void MarkRefreshed(DateTimeOffset now) => _lastRefreshed = now;
    }
}
