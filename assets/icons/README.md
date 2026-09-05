# The marks

Two programs, one pair. Impeller and Rig Fan Control sit in the same notification area at the same
moment, so the requirement neither can fail is being told apart *from each other* at 16 pixels —
which is where Windows draws a tray icon and therefore where almost everyone will ever see either
one.

| | Impeller | Rig Fan Control |
|---|---|---|
| mark | three-blade rotor, open hub | knob, solid disc, indicator cut out |
| silhouette | radial, airy | compact, dense |
| colour | teal `#12A5B4` | amber `#F08A24` |
| says | drives every fan, by rule | holds one fan, by hand |

Teal against amber rather than, say, red against green: those two survive both common colour
deficiencies, and both hold their own against a light taskbar and a dark one, which Windows does
not tint for you. Rig Fan Control's indicator is *cut out* of the disc rather than drawn on it, so
it shows whatever is behind it and cannot be the wrong colour against either theme.

Three shapes were tried and rejected for Rig Fan Control, all at 16 pixels, which is the only size
that gets a vote: a ball on a vertical track (reads as a light bulb), a crossbar on that track
(reads as a plus sign), and a single rotor blade alone (reads as a croissant).

## Re-rendering

The SVGs are the source. Everything else here is generated.

```
python make-svg.py                 # the two marks, from geometry on a 16-unit grid
pwsh -File render.ps1              # SVG -> PNG at every size Windows asks for
python pack-ico.py impeller AppIcon.ico
python pack-ico.py rigfan trayicon.ico
```

Then copy `AppIcon.ico` to `src/Impeller.App.Shell/Assets/`, and `trayicon.ico` plus a 512 px
`rigfan-512.png` (as `trayicon.png`) into the Rig Fan Control repository.

**Rendering** is headless Edge, because it is on every Windows machine and it is a real renderer.
Two things about it are load-bearing and are handled in `render.ps1`:

- Each render needs *its own* user-data directory. Share one and the second instance defers to the
  first, which then never writes its screenshot.
- At 128 px, and at 128 px alone, it produces a fully transparent image every time. Asking for half
  the window at twice the device scale gives the same pixel count from the same vector and works.
  Every size is checked for blankness rather than special-casing that one, because a silently empty
  frame is exactly the defect that ships.

**Packing** is done here rather than by an imaging library, so there is nothing to install.
Sizes up to 64 go in as uncompressed DIBs and 128 and 256 as PNG, which is the split every icon
editor emits.

## Sizes

16, 20, 24, 32, 40, 48, 64, 128, 256. The 20, 24 and 40 are the ones Windows asks for at scaled
DPI and that hand-made icons usually omit, which is why a tray icon can look crisp on one machine
and mushy on another.
