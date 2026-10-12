"""Rebuild geographic water masks from the bundled Natural Earth and USGS data.

Python 3.10+, standard library only. Optional --preview uses Pillow.
See Assets/UsStateBrushes/Water/README.md for sources and cartographic choices.
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

from StateBrushProjection import ROOT, MASK_SIZE, load_states, make_projection, rasterize

SOURCE = ROOT / "EDMWMapDesigner/Assets/UsStateBrushes/Water"
OUTPUT = ROOT / "EDMWMapDesigner/UI/UsStateWaterData.g.cs"
DATASETS = (
    "ne_10m_lakes", "ne_10m_lakes_north_america",
    "ne_10m_rivers_lake_centerlines", "ne_10m_rivers_north_america",
)


def read_shapes(path):
    """Read the XY multipart records and needed attributes, without a GIS runtime."""
    with zipfile.ZipFile(path) as archive:
        shp = archive.read(next(n for n in archive.namelist() if n.endswith(".shp")))
        dbf = archive.read(next(n for n in archive.namelist() if n.endswith(".dbf")))
    count = struct.unpack_from("<I", dbf, 4)[0]
    header, record_length = struct.unpack_from("<HH", dbf, 8)
    fields, offset = [], 1
    for position in range(32, header - 1, 32):
        name = dbf[position:position + 11].split(b"\0")[0].decode("ascii")
        length = dbf[position + 16]
        fields.append((name, offset, length))
        offset += length
    attributes = []
    for index in range(count):
        record = dbf[header + index * record_length:header + (index + 1) * record_length]
        attributes.append({name: record[offset:offset + length].decode("utf-8", "replace").strip()
                           for name, offset, length in fields})

    offset = 100
    while offset < len(shp):
        record_id, words = struct.unpack_from(">ii", shp, offset)
        offset += 8
        end = offset + words * 2
        kind = struct.unpack_from("<i", shp, offset)[0]
        if kind not in (0, 3, 5):
            raise ValueError(f"Unsupported shape type {kind}: {path}")
        if kind in (3, 5):
            bounds = struct.unpack_from("<4d", shp, offset + 4)
            part_count, point_count = struct.unpack_from("<ii", shp, offset + 36)
            starts = list(struct.unpack_from(f"<{part_count}i", shp, offset + 44)) + [point_count]
            point_start = offset + 44 + part_count * 4
            points = list(struct.iter_unpack("<dd", shp[point_start:point_start + point_count * 16]))
            parts = [points[first:last] for first, last in zip(starts, starts[1:])]
            yield {"kind": kind, "bounds": bounds, "parts": parts, **attributes[record_id - 1]}
        offset = end


def read_nhd(path, kind):
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    if data.get("error") or data.get("exceededTransferLimit"):
        raise ValueError(f"Incomplete USGS query response: {path}")
    if data["spatialReference"]["wkid"] != 4326:
        raise ValueError("USGS supplement must use WGS84 longitude/latitude")
    for feature in data["features"]:
        parts = feature["geometry"]["rings" if kind == 5 else "paths"]
        points = [point for part in parts for point in part]
        bounds = (min(p[0] for p in points), min(p[1] for p in points),
                  max(p[0] for p in points), max(p[1] for p in points))
        yield {"kind": kind, "parts": parts, "bounds": bounds,
               "name": feature["attributes"].get("GNIS_Name") or "", "scalerank": "9"}


def intersects(a, b):
    return a[0] <= b[2] and a[2] >= b[0] and a[1] <= b[3] and a[3] >= b[1]


def fill_polygon(rows, rings):
    """Even-odd scan conversion, restricted to rows crossed by the polygon."""
    edges = [(ring[i - 1], point) for ring in rings for i, point in enumerate(ring)
             if ring[i - 1][1] != point[1]]
    if not edges:
        return
    first = max(0, math.ceil(min(p[1] for e in edges for p in e) - .5))
    last = min(MASK_SIZE - 1, math.floor(max(p[1] for e in edges for p in e) - .5))
    for row in range(first, last + 1):
        y = row + .5
        crossings = sorted(ax + (y - ay) * (bx - ax) / (by - ay)
                           for (ax, ay), (bx, by) in edges if (ay > y) != (by > y))
        if len(crossings) % 2:
            raise ValueError("Unpaired water polygon crossings")
        for left, right in zip(crossings[::2], crossings[1::2]):
            start = max(0, math.ceil(left - .5))
            end = min(MASK_SIZE - 1, math.floor(right - .5))
            if end >= start:
                rows[row] |= ((1 << (end - start + 1)) - 1) << start


def clip_line(a, b):
    """Liang-Barsky clipping avoids walking far-away portions of a river."""
    x, y = a
    dx, dy = b[0] - x, b[1] - y
    lo, hi = 0., 1.
    for p, q in ((-dx, x + 4), (dx, MASK_SIZE + 4 - x),
                 (-dy, y + 4), (dy, MASK_SIZE + 4 - y)):
        if p == 0:
            if q < 0:
                return None
            continue
        t = q / p
        if p < 0:
            lo = max(lo, t)
        else:
            hi = min(hi, t)
        if lo > hi:
            return None
    return ((x + lo * dx, y + lo * dy), (x + hi * dx, y + hi * dy))


def draw_river(rows, parts, radius):
    # 7/9-mask-pixel cartographic strokes keep centerlines visible; this is not
    # a claim that rivers have those physical widths. No positions are invented.
    for part in parts:
        for a, b in zip(part, part[1:]):
            segment = clip_line(a, b)
            if segment is None:
                continue
            (ax, ay), (bx, by) = segment
            steps = max(1, math.ceil(max(abs(bx - ax), abs(by - ay)) * 2))
            previous = None
            for i in range(steps + 1):
                x, y = math.floor(ax + (bx - ax) * i / steps), math.floor(ay + (by - ay) * i / steps)
                if (x, y) == previous:
                    continue
                previous = (x, y)
                for row in range(max(0, y - radius), min(MASK_SIZE, y + radius + 1)):
                    # Circular cross-section, plus a center pixel.
                    half_width = math.floor(math.sqrt(radius * radius - (row - y) ** 2))
                    start, end = max(0, x - half_width), min(MASK_SIZE - 1, x + half_width)
                    if end >= start:
                        rows[row] |= ((1 << (end - start + 1)) - 1) << start


def build_masks():
    manifest = json.loads((SOURCE / "sources.json").read_text(encoding="utf-8"))
    for source in manifest["datasets"]:
        if hashlib.sha256((SOURCE / source["file"]).read_bytes()).hexdigest() != source["sha256"]:
            raise ValueError(f"Source checksum mismatch: {source['file']}")
    states = load_states()
    features = [feature for dataset in DATASETS for feature in read_shapes(SOURCE / (dataset + ".zip"))]
    supplements = {}
    for name in ("Hawaii", "Delaware", "Rhode Island"):
        stem = name.lower().replace(" ", "_")
        supplements[name] = list(read_nhd(SOURCE / (stem + "_nhd_lakes.json"), 5))
        supplements[name] += list(read_nhd(SOURCE / (stem + "_nhd_rivers.json"), 3))
    masks, silhouettes, counts = {}, {}, {}
    for name, polygons in sorted(states.items()):
        projection = make_projection(name, polygons)
        points = [p for polygon in polygons for ring in polygon for p in ring]
        extent = (min(p[0] for p in points), min(p[1] for p in points),
                  max(p[0] for p in points), max(p[1] for p in points))
        silhouette = rasterize([[[projection(*point) for point in polygon[0]]] for polygon in polygons])
        rows, used = [0] * MASK_SIZE, 0
        for feature in supplements.get(name, features):
            # Alaska's long Aleutian extent makes its mainland occupy only a
            # small part of the square. Keep NE's main rank 1-8 river network;
            # supplemental ranks 9-12 merge into dense water at useful map sizes.
            # Lake polygons retain their original geometry and selection.
            rank = float(feature.get("scalerank") or 9)
            if name == "Alaska" and feature["kind"] == 3 and rank > 8:
                continue
            bounds = feature["bounds"]
            if name == "Alaska" and bounds[0] > 0:
                bounds = (bounds[0] - 360, bounds[1], bounds[2] - 360, bounds[3])
            if not intersects(extent, bounds):
                continue
            parts = [[tuple((value + 1) * MASK_SIZE / 2 for value in projection(*point))
                      for point in part] for part in feature["parts"]]
            if feature["kind"] == 5:
                fill_polygon(rows, parts)
            else:
                draw_river(rows, parts, radius=4 if rank <= 5 else 3)
            used += 1
        mask = b"".join(row.to_bytes(MASK_SIZE // 8, "little") for row in rows)
        mask = bytes(water & land for water, land in zip(mask, silhouette))
        if not any(mask):
            raise ValueError(f"No mapped water survived clipping for {name}")
        masks[name], silhouettes[name], counts[name] = mask, silhouette, used
        print(f"{name}: {sum(b.bit_count() for b in mask):,} water samples; {used} candidate records", flush=True)
    return masks, silhouettes, counts


def preview(masks, silhouettes, destination):
    from PIL import Image, ImageDraw

    sheet = Image.new("RGB", (900, 1600), "#171a21")
    draw = ImageDraw.Draw(sheet)
    for index, name in enumerate(masks):
        x, y = index % 5 * 180, index // 5 * 160
        for mask, color in ((silhouettes[name], "#c3ad78"), (masks[name], "#369bc9")):
            pixels = bytes(255 if mask[bit >> 3] & (1 << (bit & 7)) else 0
                           for bit in range(MASK_SIZE * MASK_SIZE))
            image = Image.frombytes("L", (MASK_SIZE, MASK_SIZE), pixels)
            image = image.resize((130, 130), Image.Resampling.LANCZOS)
            sheet.paste(color, (x + 25, y, x + 155, y + 130), image)
        draw.text((x + 9, y + 138), name, fill="white")
    sheet.save(destination)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=OUTPUT)
    parser.add_argument("--preview", type=Path)
    args = parser.parse_args()
    masks, silhouettes, counts = build_masks()
    lines = ["// <auto-generated>",
             "// Natural Earth v5.0.0 + USGS NHD HI/DE/RI; public domain geographic hydrography.",
             "// Regenerate with tools/Generate-UsStateWater.py; see Assets/UsStateBrushes/Water/README.md.",
             "// </auto-generated>", "", "namespace EDMWMapDesigner.UI;", "",
             "internal static partial class UsStateFeatureCatalog", "{",
             "    private static Dictionary<string, string> CreateWaterMasks() => new(StringComparer.Ordinal)", "    {"]
    for name, mask in masks.items():
        compressed = gzip.compress(mask, compresslevel=9, mtime=0)
        if gzip.decompress(compressed) != mask:
            raise ValueError(f"Compression round trip failed: {name}")
        chunks = textwrap.wrap(base64.b64encode(compressed).decode("ascii"), 112)
        lines.append(f'        ["{name}"] =')
        for index, chunk in enumerate(chunks):
            lines.append(f'            "{chunk}"' + (" +" if index < len(chunks) - 1 else ","))
    lines.extend(["    };", "}", ""])
    args.output.write_text("\n".join(lines), encoding="utf-8")
    if args.preview:
        preview(masks, silhouettes, args.preview)
    print(f"Generated {len(masks)} geographic water masks: {args.output.stat().st_size:,} source bytes")
    for path in sorted(SOURCE.iterdir()):
        if path.suffix in (".zip", ".json"):
            print(f"SHA-256 {path.name}: {hashlib.sha256(path.read_bytes()).hexdigest()}")


if __name__ == "__main__":
    main()
