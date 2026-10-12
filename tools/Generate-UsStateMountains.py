"""Generate aligned US state mountain masks from GMBA Inventory v2 Standard Basic.

Normal regeneration needs only Python's standard library and the retained USA
source subset. --archive extracts that subset anew from the official global ZIP.
The optional --preview uses Pillow for a visual verification contact sheet.
"""

from __future__ import annotations

import argparse
import base64
import gzip
import hashlib
import json
import math
from pathlib import Path
import struct
import textwrap
import zipfile

from StateBrushProjection import MASK_SIZE, load_states, make_projection, rasterize

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "EDMWMapDesigner/Assets/UsStateBrushes/Mountains"
SOURCE = ASSETS / "gmba-v2-standard-basic-usa.json.gz"
OUTPUT = ROOT / "EDMWMapDesigner/UI/UsStateMountainData.g.cs"
SOURCE_URL = "https://data.earthenv.org/mountains/standard/GMBA_Inventory_v2.0_standard_basic.zip"
LAYER = "GMBA_Inventory_v2.0_standard_basic"


def extract_source(archive_path):
    """Retain the original double coordinates and range IDs of USA records."""
    with zipfile.ZipFile(archive_path) as archive:
        dbf = archive.read(LAYER + ".dbf")
        shp = archive.read(LAYER + ".shp")
        projection = archive.read(LAYER + ".prj").decode("ascii")
    record_count, header_size, record_size = struct.unpack_from("<IHH", dbf, 4)
    fields, field_offset = [], 1
    for offset in range(32, header_size - 1, 32):
        name = dbf[offset:offset + 11].split(b"\0")[0].decode("ascii")
        length = dbf[offset + 16]
        fields.append((name, field_offset, length))
        field_offset += length
    features = []
    position = 100
    for index in range(record_count):
        _, words = struct.unpack_from(">II", shp, position)
        data = memoryview(shp)[position + 8:position + 8 + words * 2]
        position += 8 + words * 2
        row = dbf[header_size + index * record_size:header_size + (index + 1) * record_size]
        attributes = {name: row[offset:offset + length].decode("utf-8").strip()
                      for name, offset, length in fields
                      if name in {"GMBA_V2_ID", "MapName", "CountryISO", "Path"}}
        if "USA" not in [value.strip() for value in attributes["CountryISO"].split(",")]:
            continue
        if struct.unpack_from("<I", data)[0] != 5:
            raise ValueError("Expected a Polygon record")
        num_parts, num_points = struct.unpack_from("<II", data, 36)
        parts = list(struct.unpack_from(f"<{num_parts}I", data, 44)) + [num_points]
        points = list(struct.iter_unpack("<dd", data[44 + num_parts * 4:]))
        if len(points) != num_points:
            raise ValueError("Unexpected polygon coordinate count")
        features.append({"id": attributes["GMBA_V2_ID"], "name": attributes["MapName"],
                         "path": [name.strip() for name in attributes["Path"].split(">")],
                         "rings": [points[start:end] for start, end in zip(parts, parts[1:])]})
    document = {"source": SOURCE_URL, "source_sha256": hashlib.sha256(archive_path.read_bytes()).hexdigest(),
                "layer": LAYER, "license": "CC BY 4.0", "projection": projection,
                "filter": "CountryISO includes USA; coordinates retained without simplification",
                "features": features}
    ASSETS.mkdir(parents=True, exist_ok=True)
    SOURCE.write_bytes(gzip.compress(json.dumps(document, separators=(",", ":")).encode(), mtime=0))
    print(f"Retained {len(features)} USA mountain range records: {SOURCE.stat().st_size:,} bytes", flush=True)


def bounds(points):
    return (min(p[0] for p in points), min(p[1] for p in points),
            max(p[0] for p in points), max(p[1] for p in points))


def intersects(a, b):
    return a[0] <= b[2] and b[0] <= a[2] and a[1] <= b[3] and b[1] <= a[3]


def scan_polygon(rings, rows):
    """The silhouette's pixel-center/even-odd rule with edge row bucketing.

    The source has many detailed edges. Bucketing avoids visiting every edge
    on every raster row, without simplifying or changing the polygon geometry.
    All parts of a shapefile Polygon share even-odd filling, including holes.
    """
    crossings = [[] for _ in range(MASK_SIZE)]
    for ring in rings:
        for index, (bx, by) in enumerate(ring):
            ax, ay = ring[index - 1]
            if ay == by:
                continue
            first = max(0, math.ceil((min(ay, by) + 1) * MASK_SIZE / 2 - 0.5))
            last = min(MASK_SIZE - 1, math.ceil((max(ay, by) + 1) * MASK_SIZE / 2 - 0.5) - 1)
            for row in range(first, last + 1):
                y = 2 * (row + 0.5) / MASK_SIZE - 1
                if (ay > y) != (by > y):
                    crossings[row].append(ax + (y - ay) * (bx - ax) / (by - ay))
    for row, intersections in enumerate(crossings):
        intersections.sort()
        if len(intersections) % 2:
            raise ValueError("Unpaired mountain polygon scan crossing")
        for left, right in zip(intersections[::2], intersections[1::2]):
            start = max(0, math.ceil((left + 1) * MASK_SIZE / 2 - 0.5))
            end = min(MASK_SIZE - 1, math.floor((right + 1) * MASK_SIZE / 2 - 0.5))
            if end >= start:
                rows[row] |= ((1 << (end - start + 1)) - 1) << start


def generate():
    document = json.loads(gzip.decompress(SOURCE.read_bytes()))
    features = document["features"]
    for feature in features:
        points = [point for ring in feature["rings"] for point in ring]
        feature["bounds"] = bounds(points)
        feature["alaska_bounds"] = bounds([(lon - 360 if lon > 0 else lon, lat) for lon, lat in points])
    masks, silhouettes, statistics = {}, {}, {}
    for name, polygons in sorted(load_states().items()):
        transform = make_projection(name, polygons)
        state_bounds = bounds([point for polygon in polygons for ring in polygon for point in ring])
        state_mask = rasterize([[[transform(*point) for point in ring] for ring in polygon]
                                for polygon in polygons])
        rows = [0] * MASK_SIZE
        candidate_count = 0
        for feature in features:
            if not intersects(state_bounds, feature["alaska_bounds" if name == "Alaska" else "bounds"]):
                continue
            candidate_count += 1
            scan_polygon([[transform(*point) for point in ring] for ring in feature["rings"]], rows)
        unmasked = b"".join(row.to_bytes(MASK_SIZE // 8, "little") for row in rows)
        mask = bytes(mountain & land for mountain, land in zip(unmasked, state_mask))
        masks[name], silhouettes[name] = mask, state_mask
        occupied = sum(byte.bit_count() for byte in mask)
        land_count = sum(byte.bit_count() for byte in state_mask)
        statistics[name] = {"mountain_samples": occupied, "land_samples": land_count,
                            "candidate_ranges": candidate_count}
        print(f"{name}: {occupied:,} mountain samples ({occupied / land_count:.1%} of silhouette)", flush=True)
    return masks, silhouettes, statistics, document["source_sha256"]


def write_catalog(masks, output):
    lines = ["// <auto-generated>",
             "// GMBA/EarthEnv Mountain Inventory v2 Standard Basic, CC BY 4.0.",
             "// See Assets/UsStateBrushes/Mountains/README.md for attribution and regeneration.",
             "// </auto-generated>", "", "namespace EDMWMapDesigner.UI;", "",
             "internal static partial class UsStateFeatureCatalog", "{",
             "    private static Dictionary<string, string> CreateMountainMasks() => new(StringComparer.Ordinal)",
             "    {"]
    for name, mask in masks.items():
        compressed = gzip.compress(mask, compresslevel=9, mtime=0)
        if gzip.decompress(compressed) != mask:
            raise ValueError(f"Compression round trip failed: {name}")
        chunks = textwrap.wrap(base64.b64encode(compressed).decode("ascii"), 112)
        lines.append(f'        ["{name}"] =')
        for index, chunk in enumerate(chunks):
            lines.append(f'            "{chunk}"' + (" +" if index < len(chunks) - 1 else ","))
    lines.extend(["    };", "}", ""])
    output.write_text("\n".join(lines), encoding="utf-8")


def preview(masks, silhouettes, destination):
    from PIL import Image, ImageDraw
    cell_w, cell_h = 180, 160
    sheet = Image.new("RGB", (cell_w * 5, cell_h * 10), "#171a21")
    draw = ImageDraw.Draw(sheet)
    for index, (name, mask) in enumerate(masks.items()):
        x, y = index % 5 * cell_w, index // 5 * cell_h
        for data, color in [(silhouettes[name], "#647c58"), (mask, "#d0b48a")]:
            pixels = bytes(255 if data[bit >> 3] & (1 << (bit & 7)) else 0
                           for bit in range(MASK_SIZE * MASK_SIZE))
            tile = Image.frombytes("L", (MASK_SIZE, MASK_SIZE), pixels).resize((130, 130), Image.Resampling.LANCZOS)
            sheet.paste(color, (x + 25, y, x + 155, y + 130), tile)
        draw.text((x + 9, y + 138), name, fill="white")
    sheet.save(destination)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path)
    parser.add_argument("--output", type=Path, default=OUTPUT)
    parser.add_argument("--preview", type=Path)
    parser.add_argument("--statistics", type=Path)
    args = parser.parse_args()
    if args.archive:
        extract_source(args.archive)
    masks, silhouettes, statistics, source_hash = generate()
    write_catalog(masks, args.output)
    if args.preview:
        preview(masks, silhouettes, args.preview)
    if args.statistics:
        args.statistics.write_text(json.dumps(statistics, indent=2), encoding="utf-8")
    print(f"Generated {len(masks)} state masks; {args.output.stat().st_size:,} source bytes")
    print(f"Original global ZIP SHA-256: {source_hash}")
    print(f"Retained source SHA-256: {hashlib.sha256(SOURCE.read_bytes()).hexdigest()}")


if __name__ == "__main__":
    main()
