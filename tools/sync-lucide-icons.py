"""Vendor a pinned Lucide subset and derive Avalonia paths without an SVG runtime dependency.

Run normally to regenerate from local SVGs; use --download to fetch the pinned originals.
This only writes source assets and geometry, and does not build Musync.
"""
import argparse
import hashlib
import json
import re
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REVISION = "440a1e1dfb1e712505308da46bead3c28b1f1398"
ICONS = (
    "house", "library", "radio", "music-2", "heart", "list-music", "settings", "search",
    "speaker", "volume-2", "volume-x", "bluetooth", "airplay", "cast", "monitor",
    "play", "pause", "skip-back", "skip-forward", "shuffle", "repeat", "square",
    "plus", "minus", "x", "refresh-cw", "chevron-down", "check", "copy", "maximize", "link-2", "picture-in-picture-2",
)
ASSETS = ROOT / "src/Musync.App/Assets/Icons/Lucide"
BASE = f"https://raw.githubusercontent.com/lucide-icons/lucide/{REVISION}"


def number(value):
    return f"{float(value):.12g}"


def shape_path(element):
    tag = element.tag.rsplit("}", 1)[-1]
    a = element.attrib
    if tag == "path":
        return a["d"]
    if tag == "line":
        return f'M {a["x1"]},{a["y1"]} L {a["x2"]},{a["y2"]}'
    if tag in ("polyline", "polygon"):
        coords = re.findall(r"[-+]?(?:\d*\.\d+|\d+)(?:[eE][-+]?\d+)?", a["points"])
        assert len(coords) % 2 == 0
        pairs = [",".join(coords[i:i + 2]) for i in range(0, len(coords), 2)]
        return "M " + " L ".join(pairs) + (" Z" if tag == "polygon" else "")
    if tag in ("circle", "ellipse"):
        cx, cy = float(a["cx"]), float(a["cy"])
        rx = float(a.get("rx", a.get("r", 0)))
        ry = float(a.get("ry", a.get("r", 0)))
        return (f"M {number(cx-rx)},{number(cy)} "
                f"A {number(rx)},{number(ry)} 0 1 0 {number(cx+rx)},{number(cy)} "
                f"A {number(rx)},{number(ry)} 0 1 0 {number(cx-rx)},{number(cy)} Z")
    if tag == "rect":
        x, y = float(a.get("x", 0)), float(a.get("y", 0))
        w, h = float(a["width"]), float(a["height"])
        rx = min(float(a.get("rx", a.get("ry", 0))), w / 2)
        ry = min(float(a.get("ry", a.get("rx", 0))), h / 2)
        if rx == 0 or ry == 0:
            return f"M {number(x)},{number(y)} h {number(w)} v {number(h)} h {number(-w)} Z"
        return (f"M {number(x+rx)},{number(y)} H {number(x+w-rx)} "
                f"A {number(rx)},{number(ry)} 0 0 1 {number(x+w)},{number(y+ry)} "
                f"V {number(y+h-ry)} A {number(rx)},{number(ry)} 0 0 1 {number(x+w-rx)},{number(y+h)} "
                f"H {number(x+rx)} A {number(rx)},{number(ry)} 0 0 1 {number(x)},{number(y+h-ry)} "
                f"V {number(y+ry)} A {number(rx)},{number(ry)} 0 0 1 {number(x+rx)},{number(y)} Z")
    raise ValueError(f"Unsupported SVG element: {tag}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--download", action="store_true")
    args = parser.parse_args()
    ASSETS.mkdir(parents=True, exist_ok=True)
    if args.download:
        for name in ICONS:
            with urllib.request.urlopen(f"{BASE}/icons/{name}.svg", timeout=30) as response:
                (ASSETS / f"{name}.svg").write_bytes(response.read())
        with urllib.request.urlopen(f"{BASE}/LICENSE", timeout=30) as response:
            (ASSETS / "LICENSE").write_bytes(response.read())

    entries, assets = [], []
    for name in ICONS:
        source = ASSETS / f"{name}.svg"
        svg = ET.fromstring(source.read_bytes())
        assert svg.attrib["viewBox"] == "0 0 24 24", name
        assert svg.attrib["stroke-width"] == "2", name
        assert svg.attrib["stroke-linecap"] == svg.attrib["stroke-linejoin"] == "round", name
        paths = [shape_path(element) for element in svg]
        entries.append(f'        ["{name}"] = [{", ".join(json.dumps(p) for p in paths)}],')
        assets.append({"name": name, "file": source.name,
                       "sha256": hashlib.sha256(source.read_bytes()).hexdigest()})
    generated = (
        "// Generated from the pinned Lucide SVGs by tools/sync-lucide-icons.py.\n"
        "// Lucide ISC / derived Feather MIT notices: Assets/Icons/Lucide/LICENSE.\n"
        f"// Upstream revision: {REVISION}\n"
        "namespace Musync.App.Controls;\n\n"
        "internal static class LucideGeometry\n{\n"
        "    internal static readonly IReadOnlyDictionary<string, string[]> Paths =\n"
        "        new Dictionary<string, string[]>(StringComparer.Ordinal)\n    {\n"
        + "\n".join(entries) + "\n    };\n}\n"
    )
    (ROOT / "src/Musync.App/Controls/LucideGeometry.cs").write_text(generated, encoding="utf-8")
    (ASSETS / "manifest.json").write_text(json.dumps({
        "source": "https://github.com/lucide-icons/lucide", "revision": REVISION,
        "canvas": [24, 24], "strokeWidth": 2, "lineCap": "round", "lineJoin": "round",
        "license": "LICENSE", "assets": assets,
    }, indent=2) + "\n", encoding="utf-8")
    print(f"Prepared {len(ICONS)} pinned Lucide SVGs and Avalonia geometry; no app build.")


if __name__ == "__main__":
    main()
