"""Emits the two marks as SVG on a 16-unit grid.

Everything is in sixteenths so the geometry lands on pixel boundaries at the sizes Windows actually
asks for. Designed at 16 and scaled up, not the reverse: at 16 pixels a stroke is under three of
them, and a mark drawn at 256 and reduced turns to porridge exactly where most people will see it.
"""
import io
import math
import os

HERE = os.path.dirname(os.path.abspath(__file__))

TEAL = '#12A5B4'
AMBER = '#F08A24'


def polar(radius, degrees):
    """A point at a radius and a bearing, measured clockwise from east on a y-down grid."""
    a = math.radians(degrees)
    return 8 + radius * math.cos(a), 8 + radius * math.sin(a)


def blade(bearing, inner, outer, sweep, bow, mid):
    """A blade: a quadratic sweep from the hub out to the rim, stroked with round caps."""
    x0, y0 = polar(inner, bearing)
    cx, cy = polar(mid, bearing + bow)
    x1, y1 = polar(outer, bearing + sweep)
    return 'M%.3f %.3f Q%.3f %.3f %.3f %.3f' % (x0, y0, cx, cy, x1, y1)


def radial(bearing, inner, outer):
    x0, y0 = polar(inner, bearing)
    x1, y1 = polar(outer, bearing)
    return 'M%.3f %.3f L%.3f %.3f' % (x0, y0, x1, y1)


IMPELLER = '''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" width="100%%" height="100%%">
  <title>Impeller</title>

  <!--
    Three blades sweeping clockwise around an open hub. Impeller drives every fan on the machine by
    rule, and a rotor is what the program is named after.

    Its pair, Rig Fan Control, is a solid disc: open against closed, radial against compact, cool
    against warm. They sit in the same notification area at the same moment, so being told apart
    from each other at 16 pixels is the requirement neither can fail. Teal and amber also survive
    the two common colour deficiencies, where a red and a green pair would not.
  -->
  <g fill="none" stroke="%s" stroke-width="2.8" stroke-linecap="round">
%s
  </g>
</svg>
'''

RIGFAN = '''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" width="100%%" height="100%%">
  <title>Rig Fan Control</title>

  <!--
    A knob: one fan, set to a speed by a person and left there. Solid and compact against Impeller's
    open rotor, and warm against its cool, so the two are told apart by silhouette before colour.

    The indicator is cut out of the disc rather than drawn on top of it, which is what makes the
    mark work on a light taskbar and a dark one without being drawn twice: the slot shows whatever
    is behind it, and cannot be the wrong colour against either.

    Three earlier attempts are recorded so they are not repeated. A ball on a vertical track reads
    as a light bulb. A crossbar on that track reads as a plus sign. A single blade with nothing
    around it reads as a croissant. All three were judged at 16 pixels, which is the only size that
    gets a vote.
  -->
  <mask id="indicator">
    <rect width="16" height="16" fill="white" />
    <path d="%s" fill="none" stroke="black" stroke-width="2.4" stroke-linecap="round" />
  </mask>

  <circle cx="8" cy="8" r="6.7" fill="%s" mask="url(#indicator)" />
</svg>
'''

blades = '\n'.join(
    '    <path d="%s" />' % blade(bearing, 3.1, 6.1, 62.0, 22.0, 5.4)
    for bearing in (-90, 30, 150))

files = [
    ('impeller.svg', IMPELLER % (TEAL, blades)),
    ('rigfan.svg', RIGFAN % (radial(-55, 1.9, 5.3), AMBER)),
]

for name, svg in files:
    io.open(os.path.join(HERE, name), 'w', encoding='utf-8', newline='\n').write(svg)
    print('wrote ' + name)
