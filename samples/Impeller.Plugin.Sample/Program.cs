using Impeller.Plugins.Abstractions;
using Impeller.Plugins.Sdk;

// A minimal Impeller plugin, and the shortest honest one: it connects, waits to be approved, takes
// the first fan it is granted, and holds it at a duty that follows the hottest temperature it can
// see. Everything it does, a real plugin does too.
//
// Run Impeller, run this, then approve it on the Plugins page. Nothing here needs administrator.

var manifest = new PluginManifest(
    // Reverse DNS, three labels minimum, and never changed once shipped: it is the key the user's
    // approval and their granted fans are stored under.
    Id: "com.example.sampleplugin",

    // Free to change whenever. Nothing keys off it.
    DisplayName: "Impeller Sample Plugin",
    Version: "1.0.0",
    ProtocolVersion: PluginProtocol.CurrentVersion,
    Requests: [PluginCapability.ReadSensors, PluginCapability.ControlFans]);

await using var impeller = new ImpellerClient(manifest);

SensorRef fan = SensorRef.None;
SensorRef temperature = SensorRef.None;

impeller.StateChanged += (_, state) =>
    Console.WriteLine($"[{state}] {impeller.StatusMessage}");

// Losing a fan is normal and the reason matters. Re-acquiring blindly on a timer is how a plugin
// ends up fighting a failsafe it should be staying out of the way of.
impeller.ControlLost += (_, lost) =>
{
    Console.WriteLine($"Lost the fan: {lost.Reason}.");

    if (!lost.WorthRetrying)
    {
        fan = SensorRef.None;
    }
};

impeller.AdmissionChanged += async (_, admission) =>
{
    if (admission.State != PluginAdmissionState.Approved)
    {
        // Pending is not an error. Say so, and wait: exiting here would leave nothing on the
        // Plugins page for the user to approve.
        return;
    }

    if (await impeller.GetMachineAsync().ConfigureAwait(false) is not { } machine)
    {
        return;
    }

    // The hottest thing this plugin is allowed to see. A real plugin lets the user pick.
    temperature = machine.Sensors
        .Where(sensor => sensor.Kind == PluginSensorKind.Temperature && sensor.Value is not null)
        .OrderByDescending(sensor => sensor.Value)
        .Select(sensor => sensor.Id)
        .FirstOrDefault();

    if (!temperature.IsNone)
    {
        await impeller.SubscribeAsync([temperature]).ConfigureAwait(false);
    }

    // The first fan the user granted. Listing is not permission, so this filters on Granted.
    var granted = machine.Controls.FirstOrDefault(control => control.Granted && control.Driven);

    if (granted is null)
    {
        Console.WriteLine("Approved, but no fan has been granted yet.");
        return;
    }

    // A five-second lease. This plugin writes on every tick, so if it ever stops the fan should go
    // back to its curve rather than sit at whatever was last asked for.
    var outcome = await impeller
        .AcquireAsync(granted.Id, maxSilence: TimeSpan.FromSeconds(5))
        .ConfigureAwait(false);

    Console.WriteLine(outcome.Message);

    if (outcome.Granted)
    {
        fan = granted.Id;
    }
};

impeller.Readings += async (_, readings) =>
{
    if (fan.IsNone)
    {
        return;
    }

    var reading = readings.Sensors.FirstOrDefault(sample => sample.Id == temperature);
    var duty = DutyFor(reading.Value);

    await impeller.SetDutyAsync(fan, duty).ConfigureAwait(false);

    // Requested and commanded are different numbers, and a plugin that shows only one of them will
    // eventually tell its user something untrue. The engine applies the fan's own minimum, its
    // avoided bands and its slew limiter to whatever is asked for.
    if (readings.Controls.FirstOrDefault(control => control.Id == fan) is { } state)
    {
        Console.WriteLine(
            $"{reading.Value,6:0.#} °C   asked {state.RequestedDuty,5:0.#}%   "
            + $"commanded {state.CommandedDuty,5:0.#}%");
    }
};

impeller.Start();

Console.WriteLine("Running. Approve this plugin in Impeller, then grant it a fan. Ctrl+C to stop.");

var stopping = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.TrySetResult();
};

await stopping.Task;

// Politeness, not a requirement: dying would return the fan to its curve just the same. There is no
// cleanup this plugin can fail to do, because it was never the thing writing to the hardware.
if (!fan.IsNone)
{
    await impeller.ReleaseAsync(fan);
}

await impeller.StopAsync();

// A plain ramp: idle below 40 °C, full by 80 °C.
static float DutyFor(float? temperature) => temperature switch
{
    null => 30f,
    < 40f => 30f,
    > 80f => 100f,
    { } value => 30f + ((value - 40f) / 40f * 70f),
};
