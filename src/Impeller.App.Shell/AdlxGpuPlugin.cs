using Impeller.Core.Abstractions;
using Impeller.Hardware.Adlx;
using Impeller.Plugins.Abstractions;
using Impeller.Plugins.Sdk;
using Microsoft.Extensions.Logging;

namespace Impeller.App.Shell;

/// <summary>
/// Hands the engine an AMD GPU's fan, over the ordinary plugin pipe rather than as a hardware
/// backend of its own.
/// </summary>
/// <remarks>
/// <para>
/// AMD's ADLX library grants full fan-tuning functionality only to a process running in an
/// interactive session; the engine service runs in Session 0, where <c>ADLXInitialize</c> simply
/// fails. This shell already runs where the user is logged in and already stays resident in the
/// tray, so it is the natural place to open ADLX. What it finds is declared to the engine through
/// <see cref="PluginCapability.ProvideHardware"/> and driven by ordinary
/// <see cref="IPluginClient.OnWriteRequestedAsync"/> calls from then on — as far as a curve is
/// concerned, this control is no different from one LibreHardwareMonitor found itself.
/// </para>
/// <para>
/// Like any plugin, this one starts unapproved. The user grants it on the Plugins page once, the
/// same way they would any third-party one — nothing here is special-cased past that point.
/// </para>
/// </remarks>
public sealed partial class AdlxGpuPlugin : IPluginHardware, IAsyncDisposable
{
    /// <summary>Reverse DNS, three labels, and never changed again — see <c>RigFan.PluginId</c>.</summary>
    private const string PluginId = "com.occamzchainsaw.impeller.adlx";

    private readonly ILogger _logger;
    private readonly ImpellerClient _client;
    private readonly AdlxSensorProvider _adlx;

    private Dictionary<string, IControl> _controlsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SensorRef> _declaredRefs = new(StringComparer.Ordinal);
    private bool _declared;

    public AdlxGpuPlugin(ILogger<AdlxGpuPlugin> logger)
    {
        _logger = logger;

        // Identity here is throwaway on purpose: the SensorId this mints is never seen by anything.
        // The id a curve actually binds to is minted by the engine's own identity map, from the
        // declared key, when DeclareHardwareAsync is admitted.
        _adlx = new AdlxSensorProvider(new InMemorySensorIdentityMap());

        _client = new ImpellerClient(
            new PluginManifest(
                PluginId,
                "Impeller AMD GPU Fan",
                typeof(AdlxGpuPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                PluginProtocol.CurrentVersion,
                [PluginCapability.ProvideHardware]),
            new ImpellerClientOptions { Hardware = this });

        _client.AdmissionChanged += (_, _) => _ = OnAdmissionChangedAsync();
    }

    /// <summary>
    /// Opens ADLX and starts trying to reach the engine.
    /// </summary>
    /// <remarks>
    /// One attempt, plus one retry — the driver can still be loading a few seconds after logon at
    /// boot, and by the second attempt it never is. A machine with no AMD GPU costs nothing beyond
    /// that: <see cref="AdlxSensorProvider"/> reports zero controls and this never connects.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!await TryDiscoverAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await TryDiscoverAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_controlsByKey.Count > 0)
        {
            _client.Start();
        }
    }

    /// <inheritdoc />
    void IPluginHardware.Write(SensorRef control, float percent)
    {
        foreach (var (key, reference) in _declaredRefs)
        {
            if (reference == control && _controlsByKey.TryGetValue(key, out var owned))
            {
                owned.Write(new Duty(percent));
                return;
            }
        }
    }

    /// <inheritdoc />
    void IPluginHardware.ApplyFailsafe()
    {
        // Full speed, matching the engine's own default failsafe duty — a silent connection is not
        // the moment to guess at something gentler for a GPU.
        foreach (var control in _controlsByKey.Values)
        {
            try
            {
                control.Write(Duty.Full);
            }
            catch (Exception ex)
            {
                Log.FailsafeWriteFailed(_logger, ex);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync().ConfigureAwait(false);
        await _adlx.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<bool> TryDiscoverAsync(CancellationToken cancellationToken)
    {
        ProviderInitializationResult result;

        try
        {
            result = await _adlx.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.DiscoveryFailed(_logger, ex);
            return false;
        }

        if (!result.Succeeded || _adlx.Controls.Count == 0)
        {
            return false;
        }

        var byKey = new Dictionary<string, IControl>(StringComparer.Ordinal);

        for (var i = 0; i < _adlx.Controls.Count; i++)
        {
            byKey[$"fan{i}"] = _adlx.Controls[i];
        }

        _controlsByKey = byKey;
        Log.Discovered(_logger, byKey.Count);
        return true;
    }

    private async Task OnAdmissionChangedAsync()
    {
        if (_declared || !_client.IsReady || _controlsByKey.Count == 0)
        {
            return;
        }

        var controls = _controlsByKey
            .Select(pair => new DeclaredControl(
                pair.Key,
                pair.Value.Name,
                pair.Value.SupportsAutomaticMode,
                Duty.Full.Percent))
            .ToArray();

        var device = new HardwareDeclaration("gpu", "AMD GPU", [], controls);
        var admission = await _client.DeclareHardwareAsync([device]).ConfigureAwait(false);

        if (!admission.Accepted)
        {
            Log.DeclarationNotAccepted(_logger, admission.Outcome, admission.Message);
            return;
        }

        foreach (var (key, reference) in admission.Controls)
        {
            _declaredRefs[key] = reference;
        }

        _declared = true;
        Log.Declared(_logger, admission.Controls.Count);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 60, Level = LogLevel.Information, Message = "AMD GPU fan control: {Count} control(s) found.")]
        public static partial void Discovered(ILogger logger, int count);

        [LoggerMessage(EventId = 61, Level = LogLevel.Warning, Message = "Opening AMD ADLX failed.")]
        public static partial void DiscoveryFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 62, Level = LogLevel.Information, Message = "Declared {Count} AMD GPU fan control(s) to Impeller.")]
        public static partial void Declared(ILogger logger, int count);

        [LoggerMessage(EventId = 63, Level = LogLevel.Warning, Message = "Impeller did not accept the AMD GPU fan declaration: {Outcome} — {Message}")]
        public static partial void DeclarationNotAccepted(ILogger logger, ProviderOutcome outcome, string message);

        [LoggerMessage(EventId = 64, Level = LogLevel.Error, Message = "Failed to drive an AMD GPU fan to its failsafe duty.")]
        public static partial void FailsafeWriteFailed(ILogger logger, Exception exception);
    }
}
