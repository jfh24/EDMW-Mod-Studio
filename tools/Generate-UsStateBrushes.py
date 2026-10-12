"""Rebuild the offline US state brush catalog from Census cartographic boundaries.

Requires Python 3.10+ and its standard library. No geometry or image dependencies.
The optional --preview contact sheet uses Pillow solely for visual inspection.
"""

from __future__ import annotations

import argparse
import base64
import gzip
import hashlib
import math
from pathlib import Path
import textwrap
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "EDMWMapDesigner/Assets/UsStateBrushes/cb_2024_us_state_20m.zip"
OUTPUT = ROOT / "EDMWMapDesigner/UI/UsStateBrushData.g.cs"
MASK_SIZE = 1024
NS = {"k": "http://www.opengis.net/kml/2.2"}
EXPECTED_NAMES = set("""Alabama|Alaska|Arizona|Arkansas|California|Colorado|Connecticut|
Delaware|Florida|Georgia|Hawaii|Idaho|Illinois|Indiana|Iowa|Kansas|Kentucky|Louisiana|
Maine|Maryland|Massachusetts|Michigan|Minnesota|Mississippi|Missouri|Montana|Nebraska|
Nevada|New Hampshire|New Jersey|New Mexico|New York|North Carolina|North Dakota|
Ohio|Oklahoma|Oregon|Pennsylvania|Rhode Island|South Carolina|South Dakota|Tennessee|
Texas|Utah|Vermont|Virginia|Washington|West Virginia|Wisconsin|Wyoming""".replace("\n", "").split("|"))


def read_states(source: Path) -> dict[str, list[list[list[tuple[float, float]]]]]:
    with zipfile.ZipFile(source) as archive:
        document = ET.fromstring(archive.read("cb_2024_us_state_20m.kml"))
    states = {}
    for placemark in document.findall(".//k:Placemark", NS):
        attributes = {item.attrib["name"]: item.text
                      for item in placemark.findall(".//k:SimpleData", NS)}
        name = attributes["NAME"]
        if name not in EXPECTED_NAMES:
            continue
        polygons = []
        for polygon in placemark.findall(".//k:Polygon", NS):
            rings = []
            for coordinates in polygon.findall(".//k:LinearRing/k:coordinates", NS):
                ring = []
                for value in coordinates.text.split():
                    lon, lat = map(float, value.split(",")[:2])
                    # The western Aleutians belong next to the rest of Alaska,
                    # rather than on the opposite side of a 360-degree extent.
                    if name == "Alaska" and lon > 0:
                        lon -= 360
                    ring.append((lon, lat))
                rings.append(ring)
            polygons.append(rings)
        states[name] = polygons
    if set(states) != EXPECTED_NAMES:
        raise ValueError(f"Missing/extra states: {set(states) ^ EXPECTED_NAMES}")
    return states


def project(polygons):
    """Local spherical Lambert conformal conic, then center and uniformly fit."""
    points = [point for polygon in polygons for ring in polygon for point in ring]
    min_lon, max_lon = min(p[0] for p in points), max(p[0] for p in points)
    min_lat, max_lat = min(p[1] for p in points), max(p[1] for p in points)
    central_lon = math.radians((min_lon + max_lon) / 2)
    latitude_span = max_lat - min_lat
    phi1 = math.radians(min_lat + latitude_span / 6)
    phi2 = math.radians(max_lat - latitude_span / 6)
    t1, t2 = math.tan(math.pi / 4 + phi1 / 2), math.tan(math.pi / 4 + phi2 / 2)
    n = math.log(math.cos(phi1) / math.cos(phi2)) / math.log(t2 / t1)
    f = math.cos(phi1) * t1 ** n / n

    def transform(point):
        lon, lat = map(math.radians, point)
        rho = f / math.tan(math.pi / 4 + lat / 2) ** n
        theta = n * (lon - central_lon)
        # A screen has y increasing downward. The omitted latitude of origin
        # is a constant translation, canceled by the subsequent centering.
        return rho * math.sin(theta), rho * math.cos(theta)

    projected = [[[transform(point) for point in ring] for ring in polygon]
                 for polygon in polygons]
    points = [point for polygon in projected for ring in polygon for point in ring]
    min_x, max_x = min(p[0] for p in points), max(p[0] for p in points)
    min_y, max_y = min(p[1] for p in points), max(p[1] for p in points)
    center_x, center_y = (min_x + max_x) / 2, (min_y + max_y) / 2
    half_extent = max(max_x - min_x, max_y - min_y) / 2
    return [[[((x - center_x) / half_extent, (y - center_y) / half_extent)
              for x, y in ring] for ring in polygon] for polygon in projected]


def rasterize(polygons) -> bytes:
    """Even-odd scan conversion preserves holes and unions separate islands."""
    rows = [0] * MASK_SIZE
    for polygon in polygons:
        edges = [(ring[i - 1], point) for ring in polygon
                 for i, point in enumerate(ring) if ring[i - 1][1] != point[1]]
        for row in range(MASK_SIZE):
            y = 2 * (row + 0.5) / MASK_SIZE - 1
            intersections = []
            for (ax, ay), (bx, by) in edges:
                if (ay > y) != (by > y):
                    intersections.append(ax + (y - ay) * (bx - ax) / (by - ay))
            intersections.sort()
            if len(intersections) % 2:
                raise ValueError("A scan line has unpaired polygon crossings")
            for left, right in zip(intersections[::2], intersections[1::2]):
                start = max(0, math.ceil((left + 1) * MASK_SIZE / 2 - 0.5))
                end = min(MASK_SIZE - 1, math.floor((right + 1) * MASK_SIZE / 2 - 0.5))
                if end >= start:
                    rows[row] |= ((1 << (end - start + 1)) - 1) << start
    result = b"".join(row.to_bytes(MASK_SIZE // 8, "little") for row in rows)
    if not any(result):
        raise ValueError("Generated an empty state mask")
    return result


def preview(masks, destination):
    from PIL import Image, ImageDraw

    cell_w, cell_h = 180, 160
    sheet = Image.new("RGB", (cell_w * 5, cell_h * 10), "#171a21")
    draw = ImageDraw.Draw(sheet)
    for index, (name, mask) in enumerate(masks.items()):
        pixels = bytes(255 if mask[bit >> 3] & (1 << (bit & 7)) else 0
                       for bit in range(MASK_SIZE * MASK_SIZE))
        silhouette = Image.frombytes("L", (MASK_SIZE, MASK_SIZE), pixels)
        silhouette = silhouette.resize((130, 130), Image.Resampling.LANCZOS)
        x, y = index % 5 * cell_w, index // 5 * cell_h
        sheet.paste("#d7bd79", (x + 25, y, x + 155, y + 130), silhouette)
        draw.text((x + 9, y + 138), name, fill="white")
    sheet.save(destination)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=SOURCE)
    parser.add_argument("--output", type=Path, default=OUTPUT)
    parser.add_argument("--preview", type=Path)
    args = parser.parse_args()
    states = read_states(args.source)
    masks = {name: rasterize(project(states[name])) for name in sorted(states)}
    lines = ["// <auto-generated>",
             "// Census Bureau 2024 cartographic state boundaries, 1:20,000,000.",
             "// Regenerate with tools/Generate-UsStateBrushes.py; see Assets/UsStateBrushes/README.md.",
             "// </auto-generated>", "", "namespace EDMWMapDesigner.UI;", "",
             "internal static partial class UsStateBrushCatalog", "{",
             "    private static UsStateBrush[] CreateBrushes() =>", "    ["]
    for name, mask in masks.items():
        compressed = gzip.compress(mask, compresslevel=9, mtime=0)
        if gzip.decompress(compressed) != mask:
            raise ValueError(f"Compression round trip failed: {name}")
        chunks = textwrap.wrap(base64.b64encode(compressed).decode("ascii"), 112)
        lines.append(f'        new("{name}",')
        for index, chunk in enumerate(chunks):
            lines.append(f'            "{chunk}"' + (" +" if index < len(chunks) - 1 else "),"))
    lines.extend(["    ];", "}", ""])
    args.output.write_text("\n".join(lines), encoding="utf-8")
    if args.preview:
        preview(masks, args.preview)
    print(f"Generated {len(masks)} nonempty {MASK_SIZE}x{MASK_SIZE} masks: {args.output}")
    print(f"Source SHA-256: {hashlib.sha256(args.source.read_bytes()).hexdigest()}")
    print(f"Runtime uncompressed storage (all states): {sum(map(len, masks.values())):,} bytes")
    print(f"Generated source: {args.output.stat().st_size:,} bytes")
    for name in ("Alaska", "Hawaii", "Michigan"):
        print(f"{name}: {len(states[name])} source polygons; "
              f"{sum(byte.bit_count() for byte in masks[name]):,} occupied samples")


if __name__ == "__main__":
    main()
