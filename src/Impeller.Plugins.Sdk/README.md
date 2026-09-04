# Impeller plugin SDK

Write a program that asks the Impeller engine to drive a fan, instead of driving the hardware
yourself.

Your plugin touches no registers, loads no driver, and needs no elevation. It runs as an ordinary
user application and asks the engine — which already owns the hardware and already arbitrates
between everything that wants a say in it — to set a duty on its behalf.

That is not a limitation dressed up as a feature. Two programs writing the same Super I/O fan
register is the failure this exists to remove: last writer wins, neither knows the other is there,
and the user gets a fan that behaves differently depending on which app started last.

## The shape of it

```csharp
using Impeller.Plugins.Abstractions;
using Impeller.Plugins.Sdk;

var client = new ImpellerClient(new PluginManifest(
    Id: "com.example.myplugin",          // reverse DNS, three labels minimum, never changes
    DisplayName: "My Plugin",            // change this whenever you like
    Version: "1.0.0",
    ProtocolVersion: PluginProtocol.CurrentVersion,
    Requests: [PluginCapability.ReadSensors, PluginCapability.ControlFans]));

client.StateChanged += (_, state) => Console.WriteLine($"{state}: {client.StatusMessage}");
client.ControlLost += (_, lost) => Console.WriteLine($"lost {lost.Control}: {lost.Reason}");

client.Start();
```

`Start` returns immediately and the client reconnects for as long as it lives. You do not need to
handle the engine being restarted.

## Id and display name

`Id` is the key everything is stored under: the user's approval, the fans they granted you, whether
they disabled you. It is reverse DNS with at least three lowercase labels, it is validated in this
SDK's constructor so you find a mistake at your own desk, and **it must never change** — changing it
loses every permission your users gave you.

`DisplayName` is what they see. Change it freely. Nothing keys off it.

## Being approved

A plugin the engine has not seen before is `Pending` and granted nothing. It may connect, say hello,
and wait. **This is a success, not an error.** A plugin that treats it as a failure and exits leaves
nothing in Impeller's Plugins page for the user to approve, and can never be approved at all.

Show `AwaitingApproval` as "waiting for approval in Impeller", not as an error. When the user
approves you, `AdmissionChanged` fires — you do not need to reconnect or poll.

## Driving a fan

```csharp
var outcome = await client.AcquireAsync(fan);

if (outcome.Granted)
{
    await client.SetDutyAsync(fan, 40f);
}
else
{
    // outcome.Message is a sentence you can show a user unchanged.
}
```

**What you ask for and what gets written are different numbers.** The engine applies the fan's
configured minimum and maximum, its avoided speed bands, and its slew limiter to your request
exactly as it does to a curve's. Asking for 30% may command 45% because of a minimum, or take
several seconds to ramp there. `Readings` carries both `RequestedDuty` and `CommandedDuty` for
this reason — if you show one, show both, or your window will confidently lie.

### Leases

`AcquireAsync(fan, maxSilence: TimeSpan.FromSeconds(5))` says: if I stop sending duties for five
seconds, take the fan back.

Use it if you compute a duty on a schedule. It is your own safety net against the case a
dead-process check cannot catch — your process alive, its transport fine, and whatever produces
your numbers quietly wedged. Set it to a few times your update interval.

Leave it null if a person sets a value and walks away. That is the default.

### Losing a fan

You will lose fans, and `ControlLost` tells you why. The reasons are not interchangeable:

| Reason | What it means | What to do |
|---|---|---|
| `Failsafe` | The engine took every fan; something is wrong | **Do not re-acquire.** The fan is at its safe duty and should stay there |
| `Revoked` | The user took this fan back | Nothing. You are still connected and may still read |
| `PluginDisabled` | The user switched you off | Nothing. Your connection is closing |
| `TakenByUser` | They grabbed it by hand in Impeller | Nothing. Let them |
| `LeaseExpired` | Your own lease lapsed | Fix why you stopped writing, then re-acquire |
| `ConfigurationChanged` | That fan is no longer driven by Impeller | Nothing until it is again |

**Claims are never restored for you** — not on reconnect, not after a failsafe clears. Whether to
take a fan again depends on why you lost it, and only you can decide that. Re-acquiring on a blind
timer is how a plugin ends up fighting a failsafe.

Dying is a perfectly good way to release a fan. There is no cleanup you can fail to do, because you
were never the thing writing to the hardware — the engine notices the pipe close and the fan is back
on its curve within a tick. `ReleaseAsync` is politeness, not a requirement.

## Reading a fan's speed

Each control in `GetMachineAsync()` names the sensor that reports its actual RPM:

```csharp
var machine = await client.GetMachineAsync();
var fan = machine!.Controls.First(control => control.Granted);

if (!fan.Tachometer.IsNone)
{
    await client.SubscribeAsync([fan.Tachometer]);
}
```

Do not try to work this out yourself. Impeller establishes the pairing by measurement during
calibration — it drives a fan and watches which tachometer moves — and matching on names or on
identifier strings gives a different, worse answer that happens to work on one motherboard.

`SensorRef.None` means the engine genuinely does not know of one, which is a real state: not every
header has a tachometer wired to it, and not every configuration has been calibrated.

## Reading sensors

Subscribe to the ids you actually use:

```csharp
await client.SubscribeAsync([cpuTemperature, waterTemperature]);
client.Readings += (_, readings) => { /* readings.Sensors, readings.Controls */ };
```

Subscribing to everything is possible and almost always wrong: a typical machine reports a couple of
hundred sensors, and asking for all of them puts all of them on the wire every second to tell you
about the three you cared about.

Fans you were granted arrive in `readings.Controls` whether or not you subscribed to them — losing
track of a fan you are driving should not depend on remembering to ask.

## What Impeller stores for you

**Nothing except your grants.** There is no plugin settings API and there will not be one. Your
configuration is yours to store wherever your application already stores things. What Impeller
remembers is which fans the user let you have.

## Contributing your own hardware

`PluginCapability.ProvideHardware` and `DeclareHardwareAsync` exist and are answered with
`NotImplementedInThisBuild`. That is a real answer, not a refusal — your declaration is well-formed
and your permissions are fine; the engine side is not written yet.

If you implement `IPluginHardware`, `ApplyFailsafe` is called on every disconnect, and that half
works today. It has to live on your side: the engine cannot put your hardware somewhere safe by
posting a message to a process that may be gone, so a fan you own is only ever as safe as your own
failsafe.

## Threading

Events are raised on the connection's thread. Marshal to your UI thread before touching a window.
Handlers that throw are swallowed rather than allowed to fault the transport — a bug in your window
code should not cost you your fans — but do not rely on that to hide real errors.
