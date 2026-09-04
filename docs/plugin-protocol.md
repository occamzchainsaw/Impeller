# The Impeller plugin protocol

Reference for the wire contract between the engine and a plugin.

If you are writing a plugin in .NET, you want [the SDK's
README](../src/Impeller.Plugins.Sdk/README.md) instead — it covers everything you need and this
document covers what is underneath it. Read this if you are implementing the protocol yourself, in
another language, or if you want to know why a rule is the way it is.

---

## Shape

One duplex JSON-RPC connection over the named pipe `Impeller.Plugins`, framed with
`Content-Length` headers, payloads encoded with enums **by name**.

Two interfaces on that one connection:

- `IPluginHost` — the engine. The plugin calls these.
- `IPluginClient` — the plugin. The engine calls these.

Everything the engine sends is fire-and-forget except `PingAsync`. The engine never calls into a
plugin from its tick loop, and never waits on one for anything that could delay fan control.

The engine listens on a pipe separate from the shell's `Impeller.Engine`. That is not tidiness: a
shell is a person driving the engine while a window is open, a plugin is a program asking for a
standing permission it holds across restarts, and they need different admission rules. It also lets
the engine cut every plugin off without disturbing a window someone is looking at.

## Handshake

`HelloAsync(manifest)` is the first call, and the only one permitted before admission. Everything
else is refused until it has happened.

```
PluginManifest {
  Id              string   reverse DNS, three labels minimum, stable forever
  DisplayName     string   free to change
  Version         string
  ProtocolVersion int      the version you were built against
  Requests        []       capabilities you would like
}
```

A connection that has not said hello within **five seconds** is dropped, and connections that have
not said hello are counted separately and capped well below the pipe's instance limit. Without both,
any local process can open connections and never speak until the plugin channel is unusable, and the
only symptom is that plugins stop working.

### Id rules

`^[a-z0-9]+(\.[a-z0-9-]+){2,}$`, at most 128 characters, and not one of the reserved words
`lhm`, `custom`, `shell`, `engine`, `impeller`.

Three labels minimum because that is the only namespace a stranger can pick from without colliding
with another stranger. The reserved list is belt and braces — the pattern already excludes all five,
since none has three labels — but `shell` is the literal claimant id the engine uses for a user's
manual pins, and a plugin admitted under it could release them.

Validated in the SDK so an author fails at their own desk, and again at the handshake because the
engine has no reason to believe a connection was built with the SDK at all.

### Version rules

The engine declares a minimum and a current version. Below minimum is refused. **Above current is
also refused** — a newer plugin is never allowed to guess at an older engine, because the result is
a fan driven by two different sets of assumptions about what the calls mean.

Additive change does not move the minimum: a field added to a record is one an older plugin never
reads. The minimum moves only when something an admitted plugin relies on stops being true, and
moving it is a decision to break every plugin below it — never a side effect of a refactor.

### The answer

```
PluginAdmission {
  State           Pending | Approved | Refused
  Granted         []      capabilities actually granted, never more than requested
  Controls        []      the specific fans, never a wildcard
  Refusal         reason when State is Refused
  Message         a sentence you can show a user unchanged
  EngineVersion   string
  ProtocolVersion int
}
```

**`Pending` is a success.** A plugin the engine has never seen is remembered, granted nothing, and
allowed to sit there. A plugin that treats this as failure and exits leaves nothing in the Plugins
page for the user to approve, and can therefore never be approved.

`OnAdmissionChangedAsync` is pushed, unsolicited, whenever the user changes anything. The admission
from your handshake goes stale the moment someone opens that page.

## Capabilities

| Capability | Grants | Granularity |
|---|---|---|
| `ReadSensors` | See the sensor list; receive readings for sensors you **subscribe** to | All or nothing |
| `ControlFans` | Claim fans and set duties | **Per fan** |
| `ProvideHardware` | Contribute your own sensors and controls | Contract only — see below |

Reads are all-or-nothing to grant because per-sensor consent across a couple of hundred sensors is a
dialog nobody would read. What is per-sensor is the *subscription*: you receive only the ids you
asked for, so the engine is not serialising the whole machine every second for every plugin.

`ControlFans` is per fan because that is the difference between "this app may drive fans" and "this
app may drive *that* fan".

`GetSensorsAsync` lists every control whether or not you were granted it, so your settings window
can offer the user a fan to pick before they have granted it. **Listing is not permission.** Claiming
an ungranted control returns `NotPermitted`.

Each listed control also carries `Tachometer` — the sensor reporting that fan's real speed, or
`SensorRef.None`. The engine works the pairing out by measurement during calibration, so this is an
answer no plugin can reproduce from names or identifier strings. Reported whether or not
`ReadSensors` was granted, since knowing the id costs nothing and lets a settings window explain
why a speed readout is missing; subscribing to it still needs the grant.

## Claiming a fan

`AcquireAsync(control, maxSilence)` → `AcquireOutcome`.

Refusals, and what each actually means:

| Failure | Meaning |
|---|---|
| `NotAdmitted` | You have not said hello, or you were refused |
| `NotPermitted` | The user has not granted you this fan |
| `AlreadyOwned` | Someone else holds it; the outcome names who |
| `NotDriven` | Impeller is not driving this fan — it is switched off, or has no curve |
| `UnknownControl` | No control with that reference exists |
| `EngineUnavailable` | The engine is in failsafe and granting nothing |

`NotDriven` deserves explanation, because it is the one that surprises people. **A plugin may only
hold a fan that is enabled and has a curve assigned.** The curve is what the fan falls back to when
you let go or die. Without one there is no safe state to return to, and your process would be the
only thing standing between that fan and a stopped fan — so a crash would be a cooling failure
rather than a fan going back to its curve.

This bites in a way that feels backwards at first: switching a fan *off* in Impeller so that two
programs "don't fight" is exactly what stops a plugin being able to drive it.

### Leases

`maxSilence` is optional. Null means hold the fan until something changes — right for a slider a
person sets and walks away from. A value means: if no `SetDutyAsync` arrives for that fan within the
window, release *that one fan* back to its curve.

It is your own safety net against the failure a dead-process check cannot catch — your process
alive, your transport fine, and whatever produces your numbers quietly wedged. Only you know which
kind of plugin you are, which is why only you can set it.

### Requested versus commanded

What you ask for and what is written are different numbers. The engine applies the fan's configured
minimum and maximum, its avoided speed bands, its slew limiter and its start/stop behaviour to your
request exactly as it does to a curve's. Asking for 30% may command 45%; asking for 100% may ramp
there over several seconds.

`ControlSample` carries `RequestedDuty` and `CommandedDuty` separately for this reason. A window
that shows one and calls it the other will eventually tell its user something untrue.

## Losing a fan

`OnControlLostAsync(control, reason)` is the most important notification in the contract. Without
it, a plugin whose fan the failsafe took goes on showing the duty it last asked for while the fan
runs at full speed, with no way to know the difference.

| Reason | Cause | Sensible response |
|---|---|---|
| `Failsafe` | The engine took every fan | **Do not re-acquire.** Something is wrong and the fan is where it should be |
| `Revoked` | The user took this fan back | Nothing; you are still connected |
| `PluginDisabled` | The user switched you off | Nothing; your connection is closing |
| `TakenByUser` | Someone grabbed it by hand | Nothing |
| `LeaseExpired` | Your own lease lapsed | Fix why you stopped writing, then re-acquire |
| `ConfigurationChanged` | That fan is no longer driven | Nothing until it is again |
| `Released` | You released it | Reported for symmetry, so your state machine has one path |

**Claims are never restored.** Not on reconnect, not after a failsafe clears, not ever. A claim is a
fact about a session; when the session ends so does the claim. Note the deliberate asymmetry with a
user's manual pin, which *is* restored across a restart: a pin is an instruction a person left
behind, a claim is something a running program was doing.

## Health, and going away

The engine pings you every five seconds and expects an answer within ten. Three consecutive misses
and your session is dropped and your fans released.

**The engine pings you, rather than you sending heartbeats.** A heartbeat proves only that your
transport thread is alive, which a completely wedged application still has. An answered ping proves
your message loop is turning. For the ordinary failures — your process dying, the pipe closing — none
of this is involved, because the connection reports those immediately; the ping earns its keep
against hung-but-alive.

Every route out of your session ends the same way: `ForceReleaseAllFrom(yourId)`, and every fan you
held is back on its curve within a tick. **A killed process and a graceful exit are the same event as
far as the fans are concerned.** That equivalence is the entire safety argument, and it is why there
is no cleanup you can fail to do.

### One session per id

At most one live connection may use a manifest id. Two sharing one could release each other's fans,
set duties on them, and free them all by dying — everything downstream treats a claimant id as an
identity.

A flat refusal would lock a user out of their own plugin after it hung, so: no live session, admit;
existing session already finished, reap it and admit; existing session answers a two-second ping,
refuse the newcomer as `AlreadyConnected`; existing session does not answer, evict it — releasing its
fans — and admit the newcomer. Restarting a hung plugin is the common case and it works.

### Backpressure

Notifications to a plugin are serialised per session and bounded. Readings coalesce — latest wins,
because the tick reads a duty once and everything between two ticks except the last is redundant by
construction. Notifications that carry meaning, like losing a fan, queue intact. A plugin that stops
reading its pipe drops readings and goes unhealthy rather than growing the engine's memory.

## Identity and trust

On accept, before reading a byte of the handshake, the engine reads the connecting process's
executable path and user SID. An approval is bound to both. A later connection with the same
manifest id from a different program or account drops back to `Pending` and prompts again, naming
both.

**This is not a boundary against a same-user adversary, and nothing built on it should suggest it
is.** Mapping a process id to a path has an inherent reuse race; anyone who can write to an approved
program's install directory wins; anyone who can inject into the approved process wins. What it
defends against is *silent inheritance* — a different program quietly picking up permissions a user
granted to something else by announcing the right string.

Rejected alternatives, and why: hashing the executable would re-prompt on every plugin update, which
trains users to click yes and destroys the value of every future prompt; per-plugin secrets solve
nothing against an adversary who can read the file they are stored in.

## What is stored

`plugins.json`, beside the `Configurations` folder — never inside it, because anything ending in
`.json` in that folder is offered to the user as a configuration they could load, and because a
configuration copied to another machine must not carry that machine's plugin approvals.

Per plugin: display name and version last seen, approval state, enabled or not, granted fans,
approved image path and SID. Grants, never claims.

An unreadable file falls back to *every plugin pending and granted nothing*. That is the safe
direction: the user is asked again, and until they answer, nothing outside the engine drives a fan.

**Impeller stores nothing else for you.** There is no plugin settings API and there will not be one;
your configuration is yours to keep wherever your application already keeps things.

## The provider role

`ProvideHardware`, `DeclareHardwareAsync`, `PushReadingsAsync` and `OnWriteRequestedAsync` exist and
are wire-complete. The engine side is **not implemented**, and every declaration is answered with
`NotImplementedInThisBuild` — a distinct outcome, never a refusal, so that an author is never
debugging a permissions problem that is really a missing feature.

The shape was fixed early because one part of it constrains everything and cannot be retrofitted:
**the engine cannot failsafe a fan a plugin provides.** Writing to it means posting a message to a
process that may well be the reason the engine is failsafing, and if that process is gone the fan is
stuck with nothing in the engine able to move it. The only real answer is a plugin-side failsafe —
the SDK drives your hardware to a declared safe duty when the pipe drops — and that had to be in the
SDK's shape from the first release.
