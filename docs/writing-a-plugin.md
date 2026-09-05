# Writing a plugin

How to make your app drive a fan.

If your program wants to control a fan on the machine it is running on — a sim rig blower on a
slider, a fan that follows your render queue, an ambient controller that reacts to something only
your app knows — this is the way to do it that does not involve a kernel driver.

The reference for the wire protocol itself is **[plugin-protocol.md](plugin-protocol.md)**. This
document is the narrative: what to reference, what to call, in what order, and the two things
everybody gets wrong.

---

## Why ask the engine instead of writing the register

You could open LibreHardwareMonitor, walk the Super I/O chip, and write the PWM register yourself.
Rig Fan Control did exactly that for years. Here is what changes when you stop.

**You stop needing administrator.** Writing fan registers means loading a signed kernel driver,
which means elevation, which means a UAC prompt at every launch and a scheduled task at highest run
level just to start with Windows. Through the engine, your app is an ordinary user process.

**You stop fighting.** Two programs writing the same register take turns, and the fan jitters
between two opinions with nothing anywhere reporting a conflict. The engine arbitrates: one owner
per fan, and everyone else is told who has it, by name.

**Your crash stops being a cooling failure.** The pipe is your only route to the hardware, so
severing it fully neuters you. When your process dies — cleanly, or by being killed, or by hanging —
the engine notices and the fan goes back to its curve within a second. There is no cleanup you can
fail to do, because there is no cleanup.

**You get things you cannot work out yourself.** Which tachometer belongs to which fan header, for
one. Impeller establishes that by measurement during calibration — drive a fan, see which number
moves. Matching them by name works on one motherboard chip and is wrong everywhere else.

---

## Referencing the SDK

`Impeller.Plugins.Sdk`, targeting `net10.0`. It brings `Impeller.Plugins.Abstractions` with it,
which is the wire vocabulary and has no dependency on Impeller's internals — nothing in the engine
leaks into your project.

Until it is on a public feed, build it from the Impeller repository:

```
.\scripts\pack-sdk.ps1
```

That produces both packages into `artifacts\packages`. Point a `nuget.config` at that folder:

```xml
<configuration>
  <packageSources>
    <add key="impeller-local" value="C:\path\to\Impeller\artifacts\packages" />
  </packageSources>
</configuration>
```

```xml
<PackageReference Include="Impeller.Plugins.Sdk" Version="0.1.0" />
```

Nothing else. You do not reference the engine, the shell, or anything Windows-specific.

---

## The whole thing, in about sixty lines

A complete plugin that takes the first fan it is granted and holds it at 60%. This is the entire
hardware story — there is no other part:

```csharp
using Impeller.Plugins.Abstractions;
using Impeller.Plugins.Sdk;

// The manifest is your identity. The id is reverse-DNS, at least three labels, and permanent: it
// is the key your approval and your granted fans are stored under on the user's machine. The
// display name is what they read, and you can change it whenever you like.
var manifest = new PluginManifest(
    Id: "com.example.myapp",
    DisplayName: "My App",
    Version: "1.0.0",
    ProtocolVersion: PluginProtocol.CurrentVersion,
    Requests: [PluginCapability.ReadSensors, PluginCapability.ControlFans]);

await using var impeller = new ImpellerClient(manifest);

var fan = SensorRef.None;

// Fires whenever a fan stops being yours, for any of seven reasons. Handle it: your window must
// not go on showing a duty you no longer command.
impeller.ControlLost += (_, lost) =>
{
    Console.WriteLine($"Lost {lost.Control}: {lost.Reason}.");
    fan = SensorRef.None;
};

// Admission is not approval. You are admitted the moment the engine accepts your handshake; the
// user approves you separately, on the Plugins page, and until they do this fires with Pending.
impeller.AdmissionChanged += async (_, admission) =>
{
    if (admission.State != PluginAdmissionState.Approved)
    {
        Console.WriteLine(admission.Message);
        return;
    }

    if (await impeller.GetMachineAsync().ConfigureAwait(false) is not { } machine)
    {
        return;
    }

    // Granted: the user ticked this fan for you. Claimable: it is on Impeller's dashboard at all.
    if (machine.Controls.FirstOrDefault(c => c.Granted && c.Claimable) is not { } granted)
    {
        Console.WriteLine("Approved, but no fan has been granted yet.");
        return;
    }

    // Its tachometer, if Impeller knows one — established by measurement during calibration, so
    // it is an answer you could not work out for yourself. Subscribe and readings arrive below.
    if (!granted.Tachometer.IsNone)
    {
        await impeller.SubscribeAsync([granted.Tachometer]).ConfigureAwait(false);
    }

    var outcome = await impeller.AcquireAsync(granted.Id).ConfigureAwait(false);

    // Written for a person to read, and it names the holder when something else has the fan.
    Console.WriteLine(outcome.Message);

    if (outcome.Granted)
    {
        fan = granted.Id;
        await impeller.SetDutyAsync(fan, 60f).ConfigureAwait(false);
    }
};

// Requested and commanded are different numbers. Showing only your own request is a lie whenever
// the fan has a minimum duty, an avoided band, or a slew limit.
impeller.Readings += (_, readings) =>
{
    if (readings.Controls.FirstOrDefault(c => c.Id == fan) is { } state)
    {
        Console.WriteLine($"asked {state.RequestedDuty:0.#}%, commanded {state.CommandedDuty:0.#}%");
    }
};

// Connects, says hello, and reconnects on its own for the life of the object. Impeller not running
// yet, restarting, or being upgraded is not something you have to handle.
impeller.Start();

Console.ReadLine();

// Politeness, not a requirement: dying returns the fan to its curve just the same.
if (!fan.IsNone)
{
    await impeller.ReleaseAsync(fan);
}

await impeller.StopAsync();
```

That listing is compiled against the packaged SDK rather than transcribed, so it builds as it
stands. `samples/Impeller.Plugin.Sample` is the same idea with a curve in it, and is likewise built
from the folder feed rather than a project reference — which is the only honest test of an SDK.
[Rig Fan Control](https://github.com/occamzchainsaw/rig-fan-control) is the real one: a tray app
whose entire hardware story is a single file, `RigFan.cs`.

---

## The order things happen in

**Manifest → connect → hello → admission → subscribe → acquire → set duty.**

`HelloAsync` is the only call permitted before admission; everything else is refused until the
engine has admitted you. `Start()` does the connect and the hello for you, and returns immediately —
it does not block waiting for Impeller, because Impeller may not be running yet and that is a
normal state rather than a failure. Watch `StateChanged` and `AdmissionChanged` instead of awaiting
anything. `StopAsync()` shuts it down.

**Admission is not approval.** You are admitted as soon as the engine accepts your handshake — that
just means your protocol version is acceptable and no other instance of your id is live. Approval is
a decision the *user* makes, on the Plugins page, and a plugin seen for the first time sits at
`Pending`, granted nothing. That is the only defensible default for a program that turned up asking
to control someone's cooling.

So: **build a UI for `Pending`.** Rig Fan Control's flyout says "waiting for approval in Impeller"
and stays useful. Treating it as a failure produces an app that looks broken while working correctly.

Admission can change while you are connected — the user approves you, or revokes you — and
`AdmissionChanged` tells you.

**Capabilities are requested in the manifest and granted by the user.** `ReadSensors` is all or
nothing to grant; you then *subscribe* to the specific sensors you want and receive only those,
because re-sending two hundred readings a second to say nothing new is a cost per connected plugin. `ControlFans`
is granted **per fan**: the manifest asks, the user ticks individual fans. That is the difference
between "this app may drive fans" and "this app may drive *that* fan", and it is the whole point.

---

## Requested duty is not commanded duty

The first of the two things everybody gets wrong.

You ask for 30%. The fan runs at 45%. Nothing is broken.

Between your request and the hardware sit the fan's configured minimum and maximum, any duty band it
is told to avoid, its slew limit, and the start/stop gate that gives a stalled fan a shove to get it
turning. A fan with a 45% minimum runs at 45% however politely you ask for 30. A fan with a slew
limit gets to your number over several seconds rather than at once.

So the SDK reports both, separately, on every `ControlInfo`:

- **`RequestedDuty`** — what the current owner asked for.
- **`CommandedDuty`** — what was actually written to the hardware.

Show both, or show the commanded one. An app that displays only its own request is lying whenever
the two differ, and the user has no way to tell "my slider did nothing" from "my slider did
something and the engine adjusted it".

---

## A lease is a choice with two right answers

The second one. `AcquireAsync` takes an optional `maxSilence`.

**Null — hold it indefinitely.** Right for a fan a person sets and walks away from. A slider in a
tray app is exactly this: the user chose 60%, and 60% is what they want in an hour when nothing has
sent an update because nothing has changed.

**A value — release this fan if I go quiet for that long.** Right for a plugin whose duty is
computed from something: telemetry, a queue depth, a temperature you read yourself. Ask for five
seconds and a fan recovers on its own when *your logic* wedges — your process alive, your transport
fine, and whatever produces your numbers quietly stuck. That is the failure a dead-process check
cannot catch, and only you know whether your app has it.

A lapsed lease releases **that one fan**, not your session. You are told, and you can acquire again.

---

## Every way you lose a fan

Seven reasons, all delivered to `ControlLost`, all of them things to handle rather than decorate:

| Reason | What happened |
| --- | --- |
| `Released` | You let go |
| `TakenByUser` | The user took the fan by hand in Impeller |
| `Revoked` | The user untucked this fan on the Plugins page |
| `PluginDisabled` | The user switched your app off entirely |
| `LeaseExpired` | Your `maxSilence` window lapsed |
| `ConfigurationChanged` | The fan is no longer in Impeller's configuration |
| `Failsafe` | The engine lost its grip and took every fan to full |

The one that matters most is `Failsafe`, because it is the one that would otherwise be silent: the
engine takes every control, your `SetDutyAsync` starts returning `false`, and your window goes on
showing 40% while the fan runs at 100%. Claims are **not** restored after a failsafe clears — you
re-acquire.

After any of these, the fan is on its curve, or on the motherboard's own control if it has none.
Never stuck at the number you last asked for.

---

## Refusals worth expecting

`AcquireAsync` can say no, and every outcome carries a `Message` written for a person to read —
including the name of whoever holds the fan, so your user knows what to close.

- **`NotPermitted`** — the user has not granted you this fan. `ControlInfo.Granted` says this ahead
  of time, so grey the row rather than waiting for the refusal.
- **`NotAdmitted`** — you have not said hello, or you were refused.
- **`UnknownControl`** — no fan with that reference exists.
- **`AlreadyOwned`** — someone else has it. The outcome names them.
- **`NotDriven`** — the fan is not in Impeller's configuration. Note that it does *not* have to be
  switched on there, and it does *not* need a curve; driving a fan Impeller is not driving is the
  entire point of this channel. `ControlInfo.Claimable` reports this ahead of time.
- **`EngineUnavailable`** — the engine is in failsafe and granting nothing.

---

## Rules and small print

**Ids.** `^[a-z0-9]+(\.[a-z0-9-]+){2,}$` — reverse DNS, three labels minimum, validated in the SDK
so you fail at your own desk and again at the handshake so the engine never trusts the SDK.
`lhm`, `custom`, `shell`, `engine` and `impeller` are reserved.

**One live session per id.** A second connection announcing the same id is refused while the first
is answering pings, and evicts it if it is not. So a hung copy of your app cannot lock the user out
of their own plugin.

**Impeller stores nothing for you except your grants.** Your settings are your problem — put them
wherever your app puts settings. `plugins.json` holds the user's decisions about you, not your state.

**Approval is bound to your executable's path and the user's SID.** Ship an update in place and
nothing changes; move to a different folder and the user is asked again, and told both paths. This
is a boundary against silent inheritance of a standing grant, not against a same-user adversary —
anyone who can write to your install directory has already won, and the dialog does not pretend
otherwise.

**Nothing you do can stall the tick loop.** The engine never calls into a plugin during a tick, so a
slow or wedged plugin cannot affect anyone's fans but its own. That is a design property rather than
a courtesy, and it is why plugins are separate processes.

**Contributing your own hardware** — a plugin that provides sensors and controls rather than
consuming them — has its wire shape fixed and its engine side unbuilt. Requesting
`PluginCapability.ProvideHardware` returns `NotImplementedInThisBuild` rather than a refusal, so you
are never debugging a permissions problem that is really a missing feature.
