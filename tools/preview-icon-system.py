"""Generate a source-derived SVG design specimen; never launches or builds Musync."""
import html
import importlib.util
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("lucide_source", ROOT / "tools/sync-lucide-icons.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
paths = {name: [module.shape_path(e) for e in ET.parse(module.ASSETS / f"{name}.svg").getroot()]
         for name in module.ICONS}
parts = ['<svg xmlns="http://www.w3.org/2000/svg" width="1320" height="900" viewBox="0 0 1320 900">',
         '<rect width="1320" height="900" fill="#FFFFFF"/>',
         '<g font-family="Inter, Segoe UI, sans-serif" fill="#251A35">']


def text(x, y, value, size=14, color="#251A35", weight=400):
    parts.append(f'<text x="{x}" y="{y}" font-size="{size}" fill="{color}" font-weight="{weight}">{html.escape(value)}</text>')


def rect(x, y, w, h, fill, radius=0):
    parts.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{radius}" fill="{fill}"/>')


def line(x1, y1, x2, y2):
    parts.append(f'<path d="M{x1},{y1} L{x2},{y2}" stroke="#EDE8EF" fill="none"/>')


def icon(name, x, y, size=20, color="#251A35", filled=False):
    fill = color if filled else "none"
    parts.append(f'<g transform="translate({x},{y}) scale({size/24})" fill="{fill}" stroke="{color}" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">')
    for path in paths[name]:
        parts.append(f'<path d="{html.escape(path, quote=True)}"/>')
    parts.append('</g>')


def button(name, x, y, size=36, icon_size=20, fill="#F5F4F7", color="#251A35", radius=10, filled=False):
    rect(x, y, size, size, fill, radius)
    icon(name, x+(size-icon_size)/2, y+(size-icon_size)/2, icon_size, color, filled)


text(48, 62, "Musync", 34, weight=700)
text(48, 96, "Lucide icon system", 20)
text(1010, 60, "SOURCE DESIGN · WIP", 12, "#757180", 600)
text(857, 96, "24-unit canvas · round caps & joins", 15, "#757180")
line(48, 126, 1272, 126)

text(48, 165, "One family. Three sizes.", 23, weight=650)
for x, size, label, detail in [(48,16,"16 px","Compact controls & metadata"),
                              (335,20,"20 px","Navigation & secondary actions"),
                              (640,24,"24 px","Primary playback & artwork")]:
    icon("heart", x, 195, size)
    icon("volume-2", x+40, 195, size)
    icon("search", x+80, 195, size)
    text(x, 251, label, 18, weight=600)
    text(x, 278, detail, 13, "#757180")
button("plus", 970, 196)
button("play", 1120, 192, 44, 24, "#251A35", "#FFFFFF", 22)
text(938, 251, "36 px target", 17, weight=600)
text(1100, 251, "44 px target", 17, weight=600)
text(938, 278, "20 px icon", 13, "#757180")
text(1100, 278, "24 px icon", 13, "#757180")
line(48, 314, 1272, 314)

text(48, 355, "Navigation", 21, weight=650)
rect(48, 374, 250, 244, "#F5F4F7", 12)
for i, (name, label) in enumerate([("house","Home"),("library","Library"),("heart","Liked tracks"),
                                  ("list-music","Playlists"),("settings","Settings")]):
    y = 386 + i*44
    icon(name, 68, y+10, 20, "#D83855" if i==0 else "#757180")
    text(100, y+26, label, 14, "#251A35" if i==0 else "#757180", 600 if i==0 else 400)
text(48, 647, "Fixed 20 px icon slots · 12 px text gap", 13, "#757180")

text(344, 355, "Music & playback", 21, weight=650)
rect(344, 388, 48, 48, "#D9556B", 9)
icon("music-2", 356, 400, 24, "#FFFFFF")
text(408, 407, "Afterglow", 15, weight=600)
text(408, 429, "Illustrative track", 12, "#757180")
text(670, 418, "0:45", 12, "#757180")
button("heart", 720, 394, color="#D83855", fill="#FFFFFF")
button("plus", 764, 394, fill="#FFFFFF")
rect(344, 468, 488, 94, "#251A35", 12)
for name, x in [("shuffle",364),("skip-back",404),("skip-forward",504),("repeat",544)]:
    button(name, x, 487, fill="#251A35", color="#F8F5FC")
button("play", 452, 483, 44, 24, "#FFFFFF", "#251A35", 22)
rect(644, 505, 158, 3, "#80738F", 1.5)
rect(644, 505, 65, 3, "#F87C90", 1.5)
parts.append('<circle cx="709" cy="506.5" r="6" fill="#F87C90"/>')
text(344, 598, "36 px secondary actions · 44 px primary action", 13, "#757180")
text(344, 621, "Original canvases retained; strokes scale uniformly.", 13, "#757180")

text(884, 355, "Speaker controls", 21, weight=650)
for i, (name, label) in enumerate([("bluetooth","Bluetooth"),("airplay","AirPlay"),("cast","Chromecast"),("monitor","Windows")]):
    x, y = 884+(i%2)*196, 389+(i//2)*36
    icon(name, x, y, 16, "#757180")
    text(x+22, y+13, label, 13)
text(884, 488, "Volume", 13, weight=600)
button("volume-2", 1208, 465, fill="#FFFFFF")
rect(884, 510, 340, 3, "#E8E1ED", 1.5)
rect(884, 510, 224, 3, "#F34A64", 1.5)
parts.append('<circle cx="1108" cy="511.5" r="6" fill="#F34A64"/>')
text(884, 555, "Sync delay", 13)
button("minus", 1054, 532, icon_size=16)
rect(1096, 532, 76, 36, "#F5F4F7", 6)
text(1120, 555, "120", 13)
button("plus", 1178, 532, icon_size=16)
text(1220, 555, "ms", 11, "#757180")
text(884, 598, "16 px metadata · 20 px mute icon", 13, "#757180")
text(884, 621, "Delay values here are illustrative.", 13, "#757180")
line(48, 679, 1272, 679)

text(48, 721, "State pairs", 21, weight=650)
for name, x, filled in [("play",48,False),("pause",90,False),("heart",168,False),
                        ("heart",210,True),("volume-2",288,False),("volume-x",330,False)]:
    button(name, x, 747, color="#D83855" if name=="heart" else "#251A35", filled=filled)
text(48, 817, "Same geometry family in every state", 13, "#757180")
text(550, 721, "Window & form controls", 21, weight=650)
for i,name in enumerate(["minus","maximize","copy","x"]):
    rect(550+i*42, 749, 42, 32, "#F5F4F7")
    icon(name, 563+i*42, 757, 16, "#757180")
rect(782, 755, 20, 20, "#D83855", 6)
icon("check", 784, 757, 16, "#FFFFFF")
icon("chevron-down", 838, 757, 16, "#757180")
text(550, 817, "16 px indicators · names on their parent controls", 13, "#757180")
text(48, 871, "Design specimen generated from pinned source vectors. Component preview, not a screenshot of the running app.", 12, "#757180")
parts.extend(['</g>', '</svg>'])
svg = "\n".join(parts) + "\n"
(ROOT / "design/icon-system-preview.svg").write_text(svg, encoding="utf-8")
(ROOT / "design/icon-system-preview.html").write_text(
    '<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">'
    '<title>Musync — Lucide icon specimen</title><style>body{margin:0;background:#ece9ef}main{max-width:1320px;margin:auto}svg{display:block;width:100%;height:auto}</style>'
    '<main aria-label="Musync Lucide icon sizing and control design specimen">'+svg+'</main></html>', encoding="utf-8")
print("Saved SVG and HTML icon specimens; no app build.")
