# Writes thumbnail.svg (600x300) and banner.svg (1000x250) for the mod
# page, then renders each to PNG with Inkscape.
import os
import subprocess

HERE = os.path.dirname(os.path.abspath(__file__))
INKSCAPE = r"C:\Program Files\Inkscape\bin\inkscape.exe"

# The instrument: a load-order panel. One row per mod, with its on/off
# box, a name bar and an update mark.
ROWS = [
    # (ticked, name bar width, mark colour)
    (True, 62, "#73d973"),
    (True, 48, "#73d973"),
    (True, 70, "#f2b340"),
    (False, 40, "#5a5f4a"),
    (True, 56, "#73d973"),
]


def instrument():
    out = ['<rect x="-78" y="-66" width="156" height="132" rx="4" fill="#0c0e08" opacity="0.8"/>',
           '<rect x="-78" y="-66" width="156" height="132" rx="4" fill="none" stroke="#d49b3d" '
           'stroke-width="1.4" stroke-dasharray="6 7" opacity="0.7"/>',
           '<rect x="-70" y="-58" width="140" height="14" fill="#2a2e1e"/>',
           '<rect x="-64" y="-54" width="34" height="6" fill="#8a7a48" opacity="0.8"/>']
    y = -36
    for i, (ticked, width, mark) in enumerate(ROWS):
        if i % 2 == 0:
            out.append('<rect x="-70" y="%d" width="140" height="18" fill="#191b12" opacity="0.9"/>' % (y - 3))
        out.append('<rect x="-64" y="%d" width="11" height="11" fill="none" stroke="#8a7a48" stroke-width="1.2"/>' % y)
        if ticked:
            out.append('<polyline points="-62,%d -59,%d -55,%d" fill="none" stroke="#d49b3d" stroke-width="2" '
                       'stroke-linecap="round" stroke-linejoin="round"/>' % (y + 6, y + 9, y + 2))
        out.append('<rect x="-46" y="%d" width="%d" height="5" fill="#c8b893" opacity="%s"/>'
                   % (y + 3, width, "0.85" if ticked else "0.35"))
        out.append('<circle cx="58" cy="%d" r="4" fill="%s"/>' % (y + 5, mark))
        y += 19
    return "\n    ".join(out)


def svg(width, height, inst_x, inst_y, inst_scale, title_x, title_y, title_size, sub_size):
    return '''<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" width="{w}" height="{h}" font-family="'Arial Narrow','Helvetica Condensed',sans-serif">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0%" stop-color="#191b12"/>
      <stop offset="100%" stop-color="#0a0c06"/>
    </linearGradient>
    <radialGradient id="glow" cx="{gx}" cy="0.56" r="0.45">
      <stop offset="0%" stop-color="#d49b3d" stop-opacity="0.16"/>
      <stop offset="100%" stop-color="#d49b3d" stop-opacity="0"/>
    </radialGradient>
    <pattern id="grid" x="0" y="0" width="24" height="24" patternUnits="userSpaceOnUse">
      <path d="M 24 0 L 0 0 0 24" fill="none" stroke="#3a3f2e" stroke-width="0.5" opacity="0.4"/>
    </pattern>
    <filter id="grain">
      <feTurbulence type="fractalNoise" baseFrequency="1.2" numOctaves="2" seed="21"/>
      <feColorMatrix values="0 0 0 0 0  0 0 0 0 0  0 0 0 0 0  0 0 0 0.16 0"/>
    </filter>
  </defs>

  <rect width="{w}" height="{h}" fill="url(#bg)"/>
  <rect width="{w}" height="{h}" fill="url(#grid)"/>
  <rect width="{w}" height="{h}" fill="url(#glow)"/>
  <rect width="{w}" height="{h}" fill="#000" filter="url(#grain)" opacity="0.45"/>

  <rect x="10" y="10" width="{fw}" height="{fh}" fill="none" stroke="#8a7a48" stroke-width="2"/>
  <rect x="18" y="18" width="{fw2}" height="{fh2}" fill="none" stroke="#8a7a48" stroke-width="1" opacity="0.55"/>

  <g stroke="#d49b3d" stroke-width="3" fill="none">
    <polyline points="34,58 34,30 62,30"/>
    <polyline points="{r},58 {r},30 {r2},30"/>
    <polyline points="34,{b2} 34,{b} 62,{b}"/>
    <polyline points="{r},{b2} {r},{b} {r2},{b}"/>
  </g>

  <text x="{cx}" y="46" text-anchor="middle" font-size="12" font-weight="800" letter-spacing="6" fill="#c8b893" opacity="0.9">FIELD MANUAL · LOAD ORDER AND SUPPLY</text>
  <line x1="90" y1="56" x2="{lx}" y2="56" stroke="#8a7a48" stroke-width="0.8" opacity="0.4"/>

  <g transform="translate({ix} {iy}) scale({isc})">
    {inst}
  </g>

  <g transform="translate({tx} {ty})">
    <text x="0" y="0" font-size="{ts}" font-weight="900" fill="#c8b893" letter-spacing="3" opacity="0.95">MOD MANAGER</text>
    <line x1="0" y1="14" x2="{ul}" y2="14" stroke="#d49b3d" stroke-width="2" opacity="0.65"/>
    <text x="0" y="{s1}" font-size="{ss}" font-weight="700" fill="#b8aa88" letter-spacing="3">INSTALL · UPDATE · CONFLICTS · PROFILES</text>
    <text x="0" y="{s2}" font-size="{ss}" font-weight="700" fill="#b8aa88" letter-spacing="3">ROAD TO VOSTOK</text>
  </g>

  <line x1="90" y1="{fl}" x2="{lx}" y2="{fl}" stroke="#8a7a48" stroke-width="0.8" opacity="0.4"/>
  <text x="{cx}" y="{ft}" text-anchor="middle" font-size="11" font-weight="700" letter-spacing="5" fill="#7a6e4a" opacity="0.85">RTV TOOL · MOD MANAGER</text>
</svg>
'''.format(w=width, h=height, gx=round(inst_x / width, 2),
           fw=width - 20, fh=height - 20, fw2=width - 36, fh2=height - 36,
           r=width - 34, r2=width - 62, b=height - 30, b2=height - 58,
           cx=width // 2, lx=width - 90,
           ix=inst_x, iy=inst_y, isc=inst_scale, inst=instrument(),
           tx=title_x, ty=title_y, ts=title_size, ul=int(title_size * 6.4),
           ss=sub_size, s1=14 + sub_size + 11, s2=14 + 2 * sub_size + 18,
           fl=height - 54, ft=height - 34)


FILES = {
    "thumbnail": svg(600, 300, 130, 160, 1.0, 236, 150, 41, 11),
    "banner": svg(1000, 250, 190, 136, 0.86, 330, 124, 62, 14),
}

for name, text in FILES.items():
    path = os.path.join(HERE, name + ".svg")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    subprocess.run([INKSCAPE, path, "--export-type=png",
                    "--export-filename=" + os.path.join(HERE, name + ".png")], check=True)
    print("wrote", name)
