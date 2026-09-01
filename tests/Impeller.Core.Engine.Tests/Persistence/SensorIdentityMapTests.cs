using Impeller.Core.Abstractions;
using Impeller.Core.Persistence;

namespace Impeller.Core.Engine.Tests.Persistence;

/// <summary>
/// Covers the guarantee the whole sensor-identity design rests on: the same physical sensor
/// resolves to the same id across restarts, and a different one never does.
/// </summary>
public sealed class SensorIdentityMapTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "impeller-identity-tests",
        Guid.NewGuid().ToString("N"));

    private string MapPath => Path.Combine(_root, "sensor-identity.json");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static HardwareFingerprint Fingerprint(
        string hardware = "/lpc/nct6798d/0",
        int channel = 2,
        SensorKind kind = SensorKind.Control) =>
        new("lhm", hardware, channel, kind);

    [Fact]
    public void The_same_fingerprint_resolves_to_the_same_id()
    {
        var map = new InMemorySensorIdentityMap();

        Assert.Equal(map.GetOrCreate(Fingerprint()), map.GetOrCreate(Fingerprint()));
    }

    [Fact]
    public void Different_fingerprints_get_different_ids()
    {
        var map = new InMemorySensorIdentityMap();

        var first = map.GetOrCreate(Fingerprint(channel: 1));
        var second = map.GetOrCreate(Fingerprint(channel: 2));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_channel_shared_by_two_kinds_does_not_collide()
    {
        // A fan header and its tach line commonly share an index. Without the kind component they
        // would be the same fingerprint, and a curve pointed at a temperature could end up
        // reading an RPM.
        var map = new InMemorySensorIdentityMap();

        var control = map.GetOrCreate(Fingerprint(kind: SensorKind.Control));
        var speed = map.GetOrCreate(Fingerprint(kind: SensorKind.FanSpeed));

        Assert.NotEqual(control, speed);
    }

    [Fact]
    public void TryGet_does_not_mint()
    {
        var map = new InMemorySensorIdentityMap();

        Assert.False(map.TryGet(Fingerprint(), out _));
        Assert.Empty(map.Entries);
    }

    [Fact]
    public void Ids_survive_a_restart()
    {
        // The entire point of persisting the map: a saved configuration still refers to the fan
        // the user actually chose after the service restarts.
        var first = new JsonSensorIdentityMap(MapPath);
        var minted = first.GetOrCreate(Fingerprint());

        var second = new JsonSensorIdentityMap(MapPath);

        Assert.True(second.TryGet(Fingerprint(), out var reloaded));
        Assert.Equal(minted, reloaded);
    }

    [Fact]
    public void Every_fingerprint_component_round_trips()
    {
        var fingerprint = new HardwareFingerprint("lhm", "/amdcpu/0", 7, SensorKind.Temperature);
        var minted = new JsonSensorIdentityMap(MapPath).GetOrCreate(fingerprint);

        var reloaded = new JsonSensorIdentityMap(MapPath);
        var entry = Assert.Single(reloaded.Entries);

        Assert.Equal(fingerprint, entry.Key);
        Assert.Equal(minted, entry.Value);
    }

    [Fact]
    public void Absent_hardware_keeps_its_entry()
    {
        // Unplugging a USB fan controller must not cost it its id, or plugging it back in would
        // silently orphan every curve that referenced it.
        var map = new JsonSensorIdentityMap(MapPath);
        var usb = map.GetOrCreate(Fingerprint(hardware: "/nzxt/gridplus/0"));

        var afterUnplug = new JsonSensorIdentityMap(MapPath);

        Assert.True(afterUnplug.TryGet(Fingerprint(hardware: "/nzxt/gridplus/0"), out var restored));
        Assert.Equal(usb, restored);
    }

    [Fact]
    public void The_map_is_created_on_first_mint_not_on_open()
    {
        _ = new JsonSensorIdentityMap(MapPath);
        Assert.False(File.Exists(MapPath));
    }

    [Fact]
    public void A_corrupt_map_starts_empty_rather_than_throwing()
    {
        // Losing the mapping costs the user their sensor selections. Refusing to start costs them
        // fan control entirely, which is the worse failure.
        Directory.CreateDirectory(_root);
        File.WriteAllText(MapPath, "{ this is not json");

        var map = new JsonSensorIdentityMap(MapPath);

        Assert.Empty(map.Entries);
        Assert.NotEqual(SensorId.None, map.GetOrCreate(Fingerprint()));
    }

    [Fact]
    public void Entries_with_unparseable_ids_are_skipped_not_fatal()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            MapPath,
            """
            [
              { "ProviderId": "lhm", "HardwareKey": "/lpc/nct6798d/0", "Channel": 1, "Kind": "Control", "Id": "not-a-guid" },
              { "ProviderId": "lhm", "HardwareKey": "/lpc/nct6798d/0", "Channel": 2, "Kind": "Control", "Id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff" }
            ]
            """);

        var map = new JsonSensorIdentityMap(MapPath);

        Assert.Single(map.Entries);
        Assert.True(map.TryGet(Fingerprint(channel: 2), out _));
    }

    [Fact]
    public void No_temporary_file_is_left_behind()
    {
        var map = new JsonSensorIdentityMap(MapPath);
        map.GetOrCreate(Fingerprint());

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
}
