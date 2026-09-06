# Impeller

A fan controller for Windows. It reads your machine's temperature sensors, works out how fast each
fan should be turning, and turns them at that speed — quietly, in the background, from the moment
the machine boots.

It exists because the fan curves in most motherboard firmware can only follow one sensor, usually
the CPU, and that is the wrong sensor about half the time. A case fan that ramps because the CPU
briefly spiked, while the graphics card underneath it is at 80 °C and its own fans are screaming,
is a fan doing the opposite of its job.

Impeller lets any fan follow any sensor, or a mix of several, and gets out of the way afterwards.

> **Windows 10 version 2004 or later, 64-bit.** Installing it needs administrator rights once.
> Using it does not.

---

## The three pieces, and why there are three

This is the one thing worth understanding before anything else, because it explains almost every
question people have afterwards.

**The engine** is a Windows service called *Impeller Engine*. It is the only part that touches
hardware. It starts with the machine, before anyone logs in, and keeps running whether or not
anybody is looking at it — so your fans are managed at the login screen, while you are logged out,
and while the screen is locked. If you never open the window again after setting it up, everything
keeps working.

**The window** is the app you actually use: the dashboard, the curve editor, the sensor list. It
talks to the engine and holds no power of its own. Closing it stops nothing. It is a viewer and an
editor, not the thing doing the work.

**The tray icon** is how you get the window back once you have closed it, and the only visible sign
that Impeller is there at all.

The split is deliberate. Touching fan headers means loading a driver and running with full system
privileges, and that is a thing you want as little of as possible: a small headless service that
does one job, rather than a whole UI framework running as SYSTEM. It also means a crash in the
window — or you closing it, or Windows updating it — cannot leave your fans unmanaged.

---

## Installing it

Download `Impeller-<version>-win-x64-Setup.exe` and run it. Choose where it goes; it installs the
service and the window together, registers the service, and starts it.

It needs administrator once, because registering a Windows service does. The window itself never
asks for elevation, and never will — it does not touch the hardware.

Upgrading is running the newer installer: it finds the previous install, replaces the binaries, and
leaves every configuration, fan name and plugin approval exactly where it was. Uninstalling is in
**Settings → Apps**, and it asks before deleting anything you made.

Unsigned, so SmartScreen will want a **More info → Run anyway** the first time.

If you would rather do it by hand, or want to know exactly what the installer did — where state is
kept, what starts when, how to remove all of it — that is
**[docs/installing.md](docs/installing.md)**.

---

## First run: from a fresh machine to a fan on a curve

Impeller starts with **nothing switched on**. Every fan it finds is listed as *not driven*, and it
writes nothing to your hardware until you ask it to. That is on purpose: guessing at a stranger's
cooling is not a defensible default, and a fan controller that starts by changing all your fan
speeds has already broken trust with someone whose machine was fine.

So there are four steps.

### 1. Find out which fan is which

Open the **Dashboard**. Every fan header on the page has an **Identify** button, which spins that
fan to full for five seconds and then hands it straight back. Listen for it, work out which fan it
is, and click the fan's name to rename it — *Front intake*, *Radiator*, *the loud one*. The name
sticks to the fan itself, so it follows you across configurations and shows up everywhere,
including in other apps that drive your fans through Impeller.

If a fan on the list is a header with nothing plugged into it, use the bin button to take it off the
page. You can add it back later from **Add a fan**.

### 2. Measure the fans (optional, but do it)

**Settings → Measuring the fans → Calibrate**. This takes a few minutes and takes over your fans
while it runs, so do it while you are not doing anything demanding.

It learns two things that nothing else can tell it: the duty at which each fan actually starts
turning from a standstill, and which tachometer belongs to which header. The second one matters
more than it sounds — matching a fan header to its speed sensor by name works on exactly one
motherboard chip and is wrong everywhere else, so Impeller works it out by measurement: drive a fan,
see which number moves.

Without calibration, everything works; you just may find a fan that will not restart from 0% until
it is given a hefty shove.

### 3. Make a curve

Open **Curves → New curve**. There are seven kinds and you almost certainly want the first one:

| Kind | What it is |
| --- | --- |
| **Graph** | Points you place on a temperature-to-speed graph. The one to start with. |
| **Linear** | A straight ramp between two temperatures and two speeds |
| **Auto** | Aims at a target temperature and finds the speed that holds it |
| **Trigger** | Two speeds, with a threshold to go up and a lower one to come back down |
| **Flat** | One speed, always |
| **Mix** | Combines other curves — the highest of them, the average, and so on |
| **Sync** | Follows another curve or another fan, with an offset |

Every curve names the sensor it follows, so this is where a case fan gets pointed at the graphics
card instead of the CPU.

### 4. Point a fan at it

Back on the **Dashboard**, each fan has a **Driven by** picker. Choose your curve. That is the
moment Impeller starts driving that fan, and the speed on the card starts moving.

The switch beside it takes the fan **by hand** instead — a slider, no curve involved, useful for
testing and for a fan you just want at a fixed speed. Switching it back off hands the fan to its
curve, or to the motherboard's own control if it hasn't got one.

---

## Coming from FanControl

If you already use [FanControl](https://github.com/Rem0o/FanControl.Releases) and have a
configuration you like, bring it across rather than rebuilding it.

**Settings → Bring a FanControl configuration across → Choose a file…**, and pick FanControl's
`userConfig.json` (it sits beside FanControl's own executable, wherever you unzipped it).

The file is read and never modified, and the result is **saved without being applied** — your fans
carry on doing exactly what they were doing. What you get is a list of notes about what came across
and what did not, because some things cannot: a curve that follows a sensor this machine does not
have, or hardware from a vendor backend Impeller reads differently. Read the notes, then switch to
the imported configuration from the list above them when you are ready.

One thing does land immediately: any names you gave your fans in FanControl. Names belong to the
sensor rather than to a configuration, so there is nowhere else for them to go — and an import never
overwrites a name you have already set here.

**Run one or the other, not both.** Two programs writing the same fan header take turns, and the
result is a fan that jitters between two opinions. The same goes for the fan control built into
your motherboard vendor's utility.

---

## Things that will otherwise surprise you

**The number on the card is not always the number you asked for.** A fan can have a minimum duty, a
maximum, a band it is told to avoid, and a limit on how fast it is allowed to change speed. Ask for
30% on a fan with a 45% minimum and it will run at 45%. The card shows what was actually written.

**A fan with no curve goes back to your motherboard.** It is not left at whatever speed it was last
given — Impeller hands it back to the firmware that was managing it before Impeller existed, and the
card says *Its own firmware*. Same when you close the window, and same when the engine stops.

**If the engine loses its grip, every fan goes to full.** A failsafe. A stuck fan controller that
holds the last speed it happened to be commanding is the failure mode that cooks hardware, so
Impeller would rather be loud than clever.

**Other apps can drive your fans through it.** Anything can ask the engine for a fan — see below —
and when something has one, the card names it and its slider goes dead rather than silently doing
nothing.

---

## Letting another app drive a fan

A program that wants to control a fan can ask the engine instead of writing to the hardware itself.
That is better for everyone: no second driver, no elevation, no two programs fighting over the same
register, and the fan goes back to its curve the moment that program exits or crashes.

Nothing is granted automatically. A program that connects shows up on the **Plugins** page as
*pending*, and can see nothing and do nothing until you approve it and tick the specific fans it may
drive. You can take a fan back, switch the program off, or forget it entirely, at any time.

[Rig Fan Control](https://github.com/occamzchainsaw/rig-fan-control) is a working example — a tray
app that drives one fan on a slider.

If you want to write one, start with **[docs/writing-a-plugin.md](docs/writing-a-plugin.md)**.

---

## When something is wrong

**Settings → Diagnostics → Collect the report** produces a text document of everything Impeller can
see: version, hardware detected, the configuration in force, and recent errors. It is copied to your
clipboard and sent nowhere. Attach it to a bug report.

The engine keeps its own log beside its configuration folder — see
[docs/installing.md](docs/installing.md) for where that is.

---

## Building it yourself

.NET 10 SDK, and Windows.

```
dotnet build Impeller.slnx
dotnet test  Impeller.slnx
.\scripts\publish.ps1 -Zip
```

`publish.ps1` produces the two folders described above, versioned, under `artifacts/publish`.

| Project | What it is |
| --- | --- |
| `Impeller.EngineService` | The service. Owns the tick loop, the hardware and the plugin host |
| `Impeller.App.Shell` | The window and the tray icon (WinUI 3) |
| `Impeller.App.ViewModels` | Everything the window does, with no UI framework in it |
| `Impeller.Core.Engine` | Curves, the control loop, ownership arbitration |
| `Impeller.Core.Abstractions` | The vocabulary both sides share |
| `Impeller.Core.Persistence` | Configuration files, sensor identity, the FanControl importer |
| `Impeller.Hardware.Lhm` | Hardware access, through LibreHardwareMonitor |
| `Impeller.Ipc.Contracts` | The engine-to-window protocol |
| `Impeller.Plugins.*` | The plugin protocol, its host, and the SDK other apps reference |
| `Impeller.Platform.Windows` | The Windows-specific bits: services, the registry, the notification area |
