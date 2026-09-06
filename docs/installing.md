# Installing, starting, and getting rid of it

Everything about running Impeller on a machine that is not a debugger: where it goes, what starts
when, what it leaves behind, and how to remove all of it.

If you only want the short version, it is in the [README](../README.md). This is the version that
answers the follow-up questions.

---

## What you are installing

Two folders, from `scripts\publish.ps1` or from a release:

| Folder | Contains | Roughly |
| --- | --- | --- |
| `Impeller-Engine-<version>-win-x64` | The service. Runs as SYSTEM, owns the hardware | 160 MB, 71 MB zipped |
| `Impeller-<version>-win-x64` | The window and its tray icon. Runs as you | 345 MB, 136 MB zipped |

Both are self-contained: they carry their own copy of .NET, and the window carries its own copy of
the Windows App SDK. Nothing has to be installed first, and nothing on the machine can be broken by
a runtime update. That is where the size goes.

Neither is signed, so Windows SmartScreen will warn about both the first time. There is no way
around that short of a code-signing certificate.

---

## The engine

### Install it

Put the engine folder somewhere it can stay. `C:\Program Files\Impeller Engine` is the obvious
choice. **The path matters**: the Service Control Manager stores the executable's full path, so
moving the folder afterwards leaves a service pointing at nothing.

From an **administrator** terminal, in that folder:

```
.\Impeller.EngineService.exe install
.\Impeller.EngineService.exe start
```

`install` registers the service as **ImpellerEngine**, display name *Impeller Engine*, start type
automatic, and configures Windows to restart it if it ever falls over. It refuses politely if it is
already installed.

The verbs are in the engine binary rather than a separate installer on purpose: the path registered
with the SCM is then necessarily the path of the thing registering it, and there is nothing to get
wrong on an upgrade or a move.

### The other verbs

```
.\Impeller.EngineService.exe status      # installed? running?
.\Impeller.EngineService.exe stop
.\Impeller.EngineService.exe start
.\Impeller.EngineService.exe uninstall   # stops it first
```

`status` needs no elevation. The rest do, and say so rather than failing obscurely.

You can also use `services.msc`, or `sc.exe query ImpellerEngine`, or Task Manager's Services tab —
it is an ordinary Windows service with no tricks in it.

### Where it keeps its state

The engine works this out at startup rather than assuming it, and **the Settings page tells you
which answer it got** — the line under the configuration list reads *Kept in …*. That is not
decoration; it is there so "where did my configurations go" never needs reasoning about.

It looks in three places, in order:

1. Whatever `Engine:ConfigurationPath` names in `appsettings.json`, if anything. Set explicitly, it
   is used as given and never probed — someone who names a path means it.
2. A `Configurations` folder **beside the executable**, if that folder can actually be written to.
   The check is a real write, not an ACL inspection, because the service runs as SYSTEM and policy
   and virtualisation both get a say. This is what makes a portable install work: unzip it to
   `D:\Tools`, and everything it owns is in that one directory, which you can copy, move or delete.
3. `%ProgramData%\Impeller\Configurations` otherwise — which is what you get under `Program Files`,
   because that is not writable.

Either way the layout is the same, and only configurations live in the `Configurations` folder:

```
<state root>\
  Configurations\*.json          one file per configuration: curves, fans, limits, calibration
  Logs\engine-*.log              the engine's log, one file per day
  selected-configuration.json    which configuration is loaded
  sensor-identity.json           the stable ids Impeller gave this machine's hardware
  names.json                     the names you gave your fans and sensors
  plugins.json                   which programs you approved, and which fans each may drive
```

The four state files sit *beside* the folder rather than in it, and that is deliberate twice over.
Anything ending in `.json` inside `Configurations` is offered to you as a configuration you could
load, so a machine-state file in there shows up in the dropdown and fails when chosen. And a
configuration is portable between machines while these are emphatically not — copying the folder to
another PC should carry your curves and leave that PC's identities behind.

**`sensor-identity.json` is the one worth backing up.** It records which id this machine assigned to
which physical sensor, and it is what keeps a curve pointing at the right fan after a BIOS update
reorders them. Losing it does not lose your configurations; it means the ids inside them may no
longer resolve to anything.

### Upgrading

```
.\Impeller.EngineService.exe stop
```

Replace the folder's contents, keeping any `Configurations` folder and `*.json` state inside it if
this is a portable install, then:

```
.\Impeller.EngineService.exe start
```

`uninstall` then `install` is only needed if the folder itself moves.

---

## The window

Unzip it anywhere and run `Impeller.exe`. It runs as you, never asks for elevation, and needs no
installation step at all.

Closing the window leaves it in the notification area. **Exit** on the tray menu closes it properly.
Neither one stops your fans being managed — that is the service, and it carries on regardless.

### Starting it with Windows

**Settings → Starting with Windows → Open Impeller when I log in.**

That writes an ordinary `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value named `Impeller`,
pointing at the executable you ran it from. It appears in Task Manager's **Startup apps** tab, where
you can also turn it off — which is the point of using the mechanism Windows already has rather than
a scheduled task nobody thinks to look for.

Move the folder and the toggle will read as off, because the value points somewhere else. Switching
it on again rewrites it.

The second toggle, **And leave it in the notification area**, decides what that log-in launch does
when it gets there: open the window, or stay in the tray. Most people want the tray — the reason to
start Impeller with Windows is usually to have the icon there, not to be handed a window on top of
whatever you opened next.

It is stored as a `--minimised` argument on that same `Run` value, so the whole setting reads:

```
"C:\Path\To\Impeller.exe" --minimised
```

Two things follow from storing it there rather than in a settings file. Turning autostart off takes
it with it, which is why the second toggle greys out. And it applies to the log-in launch only —
double-clicking `Impeller.exe` yourself always opens the window, because that is somebody asking
for it.

**This setting is only about the window.** Your fans do not need it. The engine is a service; it
starts at boot, before any user logs in, and keeps going while you are logged out.

### Where the window keeps its state

`%LocalAppData%\Impeller` — window position and size, and the shell's own log. Nothing in here
matters; deleting it costs you a window that opens at its default size.

---

## Rig Fan Control, and other plugin apps

[Rig Fan Control](https://github.com/occamzchainsaw/rig-fan-control) installs the same way: unzip,
run, no elevation. Its own settings have a **Start with Windows** toggle that writes the same kind of
`Run` value, under the name `RigFanControl`.

Order does not matter. A plugin app started before the engine, or while the engine is restarting,
waits and reconnects on its own.

The first time one connects it appears on Impeller's **Plugins** page as *pending* and can do
nothing at all until you approve it and tick the fans it may drive. Its state lives in
`%AppData%\RigFanControl` (or the equivalent for whatever app it is); the approval and the grants
live in the engine's `plugins.json`, because they are decisions about this machine rather than about
that app.

---

## Removing all of it

```
.\Impeller.EngineService.exe uninstall
```

from an administrator terminal, in the engine's folder. That stops the service and removes it from
the SCM. **Every fan goes back to your motherboard's own control as it stops** — Impeller hands each
one back rather than leaving it at the last speed it was given.

Then:

1. Delete the engine folder and the window folder.
2. Delete `%LocalAppData%\Impeller`.
3. If it was not a portable install, delete `%ProgramData%\Impeller` — that is the configurations,
   names, sensor identities and plugin approvals. Keep it if you might come back; a portable install
   keeps all of that inside its own folder, which step 1 already removed.
4. Turn off *Open Impeller when I log in* before you delete the folder, or remove the `Impeller`
   value from `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` afterwards.
5. For plugin apps: their own folder, their own `Run` value, and `%AppData%\<app>`.

Nothing else is touched. No drivers are left installed, nothing is added to the PATH, and no
registry keys outside the two named above are written.

---

## Running it from a checkout

Worth writing down because it is not the same thing as an install, and mixing the two produces the
most confusing failure available.

The engine registers **by path**. Register the debug build and the service points at
`src\Impeller.EngineService\bin\Debug\net10.0-windows`, which means:

- That folder is where its state lives — a separate world from an installed copy's.
- **`dotnet build` fails while the service is running**, because the service has the DLLs open. Stop
  it, build, start it again. This is not a mystery once you have seen it; it is a wall of
  `MSB3027 ... locked by "Impeller Engine"` otherwise.

Have one or the other registered, not both. `Impeller.EngineService.exe status` from either folder
will tell you which one currently holds the name, because there is only one `ImpellerEngine` service
on a machine and whichever was installed last owns it.

The window has no such problem: run `Impeller.exe` out of `bin\Debug` and it connects to whichever
engine is running.
