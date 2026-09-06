using Impeller.App.ViewModels;
using Impeller.App.ViewModels.Engine;
using Impeller.App.ViewModels.Notifications;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Ipc.Contracts;
using Impeller.Plugins.Abstractions;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Impeller.Core.Engine.Tests.ViewModels;

/// <summary>
/// The Plugins page calls a fan what the user calls it.
/// </summary>
/// <remarks>
/// Written after the page was found showing provider names — "Fan #3" where the dashboard, the
/// sensor tree and the plugins themselves all said "Front Intake". Deciding which program may drive
/// a fan is a question about the fan the user named, and answering it in the hardware's vocabulary
/// asks them to translate their own labels back.
///
/// Every descriptor carries both names, so this class of mistake is one character wide and invisible
/// on review. This drives the real view model over a real connection to catch it.
/// </remarks>
public sealed class PluginsPageNamingTests
{
    private const string ProviderName = "Fan #3";
    private const string UserName = "Front Intake";
    private const string PluginId = "com.example.rigfan";

    private static readonly SensorId Control = SensorId.New();

    [Fact]
    public async Task A_renamed_fan_is_offered_by_the_name_the_user_gave_it()
    {
        await using var rig = new Rig();

        var page = await rig.OpenAsync();

        var plugin = Assert.Single(page.Plugins);
        var fan = Assert.Single(plugin.Fans);

        Assert.Equal(UserName, fan.Name);
        Assert.NotEqual(ProviderName, fan.Name);
    }

    /// <summary>
    /// The row is the fan it claims to be, not just a correctly spelled one.
    /// </summary>
    /// <remarks>
    /// Pinned alongside the name because the two failures look identical on screen: a row labelled
    /// with the wrong fan's name and a row labelled correctly but carrying the wrong id both read
    /// as "the list is fine". Only the id decides what the engine actually grants.
    /// </remarks>
    [Fact]
    public async Task The_row_carries_the_id_the_grant_is_keyed_on()
    {
        await using var rig = new Rig();

        var fan = Assert.Single(Assert.Single((await rig.OpenAsync()).Plugins).Fans);

        Assert.Equal(Control, fan.Id);
        Assert.True(fan.Granted);
        Assert.True(fan.Claimable);
    }

    /// <summary>A connected page over an in-memory pair, with one plugin and one renamed fan.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<IDisposable> _open = [];
        private readonly EngineConnection _connection;

        private PluginsViewModel? _page;

        public Rig()
        {
            _connection = new EngineConnection
            {
                IsEngineInstalled = () => true,
                Connect = Open,
            };

            _connection.Start();
        }

        public async Task<PluginsViewModel> OpenAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

            while (_connection.Snapshot is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            Assert.NotNull(_connection.Snapshot);

            _page = new PluginsViewModel(_connection, new NotificationCenter());
            await _page.LoadAsync();

            return _page;
        }

        public async ValueTask DisposeAsync()
        {
            _page?.Dispose();
            await _connection.DisposeAsync();

            foreach (var open in _open)
            {
                open.Dispose();
            }
        }

        private Task<Stream> Open(CancellationToken cancellationToken)
        {
            var (client, server) = FullDuplexStream.CreatePair();

            var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(
                server,
                server,
                new SystemTextJsonFormatter { JsonSerializerOptions = ImpellerJson.CompactOptions }));

            rpc.AddLocalRpcTarget(new StubEngine(), null);
            rpc.StartListening();

            _open.Add(rpc);
            _open.Add(server);

            return Task.FromResult<Stream>(client);
        }
    }

    /// <summary>Only the calls this page makes. Anything else would never be reached.</summary>
    private sealed class StubEngine
    {
        private int _calls;

        /// <summary>How many times the page asked, which the duplication bug made visible.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public Task<EngineHandshake> HelloAsync(ShellHello hello, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(EngineHandshakeCheck.Accept("0.0.0-test"));
        }

        public Task<EngineSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);

            return Task.FromResult(new EngineSnapshot(
                new EngineStatus("0.0.0-test", 1, DateTimeOffset.UnixEpoch, false, "."),
                [],
                [
                    new ControlDescriptor(
                        Control,
                        ProviderName,

                        // The engine has already resolved the user's name onto the descriptor. The
                        // page's only job is to use the one it was given.
                        UserName,
                        "Nuvoton NCT6687D",
                        "lhm",
                        "lhm/lpc/nct6687d/0/control/3",
                        new Duty(40f),
                        new Duty(40f),
                        false,
                        ControlOwnerKind.Curve,
                        null,
                        Claimable: true),
                ],
                "Default",
                new ImpellerConfiguration { Name = "Default" },
                new ConfigurationValidation([]),
                ["Default"]));
        }

        public Task<EquatableArray<PluginSummary>> ListPluginsAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);

            return Task.FromResult<EquatableArray<PluginSummary>>(
            [
                new PluginSummary(
                    PluginId,
                    "Rig Fan Control",
                    "0.1.0",
                    PluginAdmissionState.Approved,
                    Enabled: true,
                    Connected: true,
                    [PluginCapability.ControlFans],
                    [PluginCapability.ControlFans],
                    [Control],
                    @"C:\Apps\RigFanControl.exe",
                    "S-1-5-21-0-0-0-1001",
                    IdentityChanged: false,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch),
            ]);
        }
    }
}
