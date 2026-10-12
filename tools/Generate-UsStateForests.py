"""Rebuild selected forest-core masks from the retained ESA WorldCover maps.

Requires Python 3.10+, NumPy and Pillow. Downloads are a separate, optional step;
normal regeneration is entirely offline. See Assets/UsStateBrushes/Forests.
"""

from __future__ import annotations

import argparse
import base64
import gzip
import hashlib
import json
import math
from pathlib import Path
import textwrap

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

from StateBrushProjection import ROOT, MASK_SIZE, load_states, make_projection, rasterize

SOURCE = ROOT / "EDMWMapDesigner/Assets/UsStateBrushes/Forests"
OUTPUT = ROOT / "EDMWMapDesigner/UI/UsStateForestData.g.cs"
LAND_CAP = .25
MAX_CORES = 6


def inverse_grid(name, polygons):
    """Inverse of the exact local Lambert fit used by StateBrushProjection."""
    points = [point for polygon in polygons for ring in polygon for point in ring]
    west, east = min(p[0] for p in points), max(p[0] for p in points)
    south, north = min(p[1] for p in points), max(p[1] for p in points)
    central = math.radians((west + east) / 2)
    phi1 = math.radians(south + (north - south) / 6)
    phi2 = math.radians(north - (north - south) / 6)
    t1, t2 = math.tan(math.pi / 4 + phi1 / 2), math.tan(math.pi / 4 + phi2 / 2)
    n = math.log(math.cos(phi1) / math.cos(phi2)) / math.log(t2 / t1)
    f = math.cos(phi1) * t1 ** n / n
    projected = []
    for lon, lat in points:
        rho = f / math.tan(math.pi / 4 + math.radians(lat) / 2) ** n
        theta = n * (math.radians(lon) - central)
        projected.append((rho * math.sin(theta), rho * math.cos(theta)))
    min_x, max_x = min(p[0] for p in projected), max(p[0] for p in projected)
    min_y, max_y = min(p[1] for p in projected), max(p[1] for p in projected)
    center_x, center_y = (min_x + max_x) / 2, (min_y + max_y) / 2
    half = max(max_x - min_x, max_y - min_y) / 2
    axis = (np.arange(MASK_SIZE, dtype=np.float64) + .5) * 2 / MASK_SIZE - 1
    x, y = np.meshgrid(axis * half + center_x, axis * half + center_y)
    rho = np.hypot(x, y)
    latitude = np.degrees(2 * np.arctan((f / rho) ** (1 / n)) - math.pi / 2)
    longitude = np.degrees(central + np.arctan2(x, y) / n)
    # Validate inverse/forward alignment at distributed mask cell centers.
    forward = make_projection(name, polygons)
    for row, col in ((100, 100), (200, 800), (512, 512), (900, 200)):
        px, py = forward(float(longitude[row, col]), float(latitude[row, col]))
        if abs(px - axis[col]) > 1e-10 or abs(py - axis[row]) > 1e-10:
            raise ValueError(f"Forest/state projection mismatch: {name}")
    return longitude, latitude


def map_tree_cover(name, polygons, maps):
    longitude, latitude = inverse_grid(name, polygons)
    trees = np.zeros((MASK_SIZE, MASK_SIZE), dtype=bool)
    valid = np.zeros_like(trees)
    for source in maps:
        path = SOURCE / source["file"]
        if hashlib.sha256(path.read_bytes()).hexdigest() != source["sha256"]:
            raise ValueError(f"Forest source checksum mismatch: {path.name}")
        with Image.open(path) as image:
            rgba = np.array(image.convert("RGBA"))
        if rgba.shape[:2] != (source["height"], source["width"]):
            raise ValueError(f"Forest source dimensions differ: {path.name}")
        west, south, east, north = source["bbox"]
        lon = np.where(longitude < -180, longitude + 360, longitude) if west > 0 else longitude
        columns = np.floor((lon - west) / (east - west) * source["width"]).astype(np.int32)
        rows = np.floor((north - latitude) / (north - south) * source["height"]).astype(np.int32)
        inside = (columns >= 0) & (columns < source["width"]) & (rows >= 0) & (rows < source["height"])
        samples = rgba[rows.clip(0, source["height"] - 1), columns.clip(0, source["width"] - 1)]
        tree = (samples[:, :, 0] == 0) & (samples[:, :, 1] == 100) & (samples[:, :, 2] == 0) & (samples[:, :, 3] > 0)
        trees |= inside & tree
        valid |= inside & (samples[:, :, 3] > 0)
    return trees, valid


def mean_box(values, radius):
    """Floating box means from summed-area tables, without SciPy."""
    side = radius * 2 + 1
    padded = np.pad(values.astype(np.float64), radius)
    summed = np.pad(padded, ((1, 0), (1, 0))).cumsum(axis=0).cumsum(axis=1)
    return (summed[side:, side:] - summed[:-side, side:] - summed[side:, :-side] + summed[:-side, :-side]) / (side * side)


def major_components(mask, minimum_area):
    """Keep up to six coherent 8-connected cores using scan-line components."""
    parents, areas, runs = [], [], []

    def find(index):
        while parents[index] != index:
            parents[index] = parents[parents[index]]
            index = parents[index]
        return index

    def union(first, second):
        first, second = find(first), find(second)
        if first != second:
            if areas[first] < areas[second]:
                first, second = second, first
            parents[second] = first
            areas[first] += areas[second]
        return first

    previous = []
    for y, row in enumerate(mask):
        crossings = np.flatnonzero(np.diff(np.pad(row.astype(np.int8), (1, 1))))
        current, cursor = [], 0
        for start, end in zip(crossings[::2], crossings[1::2]):
            start, end = int(start), int(end)
            index = len(parents)
            parents.append(index)
            areas.append(end - start)
            while cursor < len(previous) and previous[cursor][1] < start:
                cursor += 1
            other = cursor
            while other < len(previous) and previous[other][0] <= end:
                index = union(index, previous[other][2])
                other += 1
            current.append((start, end, index))
            runs.append((y, start, end, index))
        previous = current
    ranked = sorted(((area, index) for index, area in enumerate(areas)
                     if parents[index] == index and area >= minimum_area), reverse=True)
    keep = {index for _, index in ranked[:MAX_CORES]}
    result = np.zeros_like(mask)
    for y, start, end, index in runs:
        if find(index) in keep:
            result[y, start:end] = True
    return result, [area for area, _ in ranked[:MAX_CORES]]


def select_cores(trees, land, compact=False):
    # Smooth tree coverage over local and regional neighborhoods. Every selected
    # core is in a geographically observed, mostly tree-covered neighborhood.
    # Broad support ranks coherent forest interiors above isolated small stands.
    local = mean_box(trees, 5 if compact else 10)
    regional = mean_box(trees, 15 if compact else 30)
    density = .65 * local + .35 * regional
    land_area = int(land.sum())
    eligible = land & (local >= .55)
    cap = int(land_area * LAND_CAP)
    threshold = .50 if compact else .60
    if np.count_nonzero(eligible & (density >= threshold)) > cap:
        scores = density[eligible]
        threshold = max(threshold, float(np.partition(scores, len(scores) - cap)[len(scores) - cap]))
    # A slight density tie may put the raw set above the cap. Raising the
    # threshold uniformly is geographic/deterministic, not a random pixel cut.
    candidate = eligible & (density >= threshold)
    if candidate.sum() > cap:
        threshold = float(np.nextafter(threshold, math.inf))
        candidate = eligible & (density >= threshold)
    image = Image.fromarray(candidate.astype(np.uint8) * 255)
    # Opening removes thin spurs and speckled islands without adding remote trees.
    kernel = 3 if compact else 7
    opened = np.array(image.filter(ImageFilter.MinFilter(kernel)).filter(ImageFilter.MaxFilter(kernel))) > 0
    opened &= eligible & candidate
    minimum_area = max(20 if compact else 36, round(land_area * (.0002 if compact else .0015)))
    selected, component_areas = major_components(opened, minimum_area)
    # Plains/desert states still contain real compact woods. When no major
    # core survives, retain a few denser small cores rather than inventing
    # coverage or labeling the state entirely unforested.
    if not component_areas and not compact:
        return select_cores(trees, land, compact=True)
    if selected.sum() > cap or np.any(selected & ~land):
        raise ValueError("Forest selection exceeded its land cap or state boundary")
    return selected, threshold, component_areas, compact


def build_masks():
    manifest = json.loads((SOURCE / "sources.json").read_text(encoding="utf-8"))
    states = load_states()
    if {source["state"] for source in manifest["sources"]} != set(states):
        raise ValueError("Forest source manifest must cover exactly the 50 states")
    masks, lands, statistics = {}, {}, {}
    for name, polygons in sorted(states.items()):
        forward = make_projection(name, polygons)
        land_bytes = rasterize([[[forward(*point) for point in ring] for ring in polygon] for polygon in polygons])
        land = np.unpackbits(np.frombuffer(land_bytes, dtype=np.uint8), bitorder="little").reshape(MASK_SIZE, MASK_SIZE).astype(bool)
        trees, valid = map_tree_cover(name, polygons, [m for m in manifest["sources"] if m["state"] == name])
        # Tiny coastal edge mismatches are expected across source cartography;
        # reject maps that fail to cover the state's interior.
        validity = np.count_nonzero(valid & land) / np.count_nonzero(land)
        if validity < .95:
            raise ValueError(f"Incomplete WorldCover coverage for {name}: {validity:.1%}")
        selected, threshold, areas, compact = select_cores(trees, land)
        masks[name] = np.packbits(selected, bitorder="little").tobytes()
        lands[name] = land_bytes
        statistics[name] = {"land_samples": int(land.sum()), "source_tree_samples": int((trees & land).sum()),
                            "forest_samples": int(selected.sum()), "selected_cores": len(areas),
                            "core_areas": areas, "density_threshold": threshold, "source_coverage": validity,
                            "compact_forest_fallback": compact}
        print(f"{name}: {int(selected.sum()):,} forest samples ({selected.sum()/land.sum():.1%} of land), {len(areas)} cores", flush=True)
    return masks, lands, statistics


def preview(masks, lands, destination):
    sheet = Image.new("RGB", (900, 1600), "#171a21")
    draw = ImageDraw.Draw(sheet)
    for index, name in enumerate(masks):
        x, y = index % 5 * 180, index // 5 * 160
        for mask, color in ((lands[name], "#c3ad78"), (masks[name], "#326b38")):
            array = np.unpackbits(np.frombuffer(mask, dtype=np.uint8), bitorder="little").reshape(MASK_SIZE, MASK_SIZE) * 255
            layer = Image.fromarray(array).resize((130, 130), Image.Resampling.LANCZOS)
            sheet.paste(color, (x + 25, y, x + 155, y + 130), layer)
        draw.text((x + 9, y + 138), name, fill="white")
    sheet.save(destination)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=OUTPUT)
    parser.add_argument("--preview", type=Path)
    parser.add_argument("--statistics", type=Path)
    args = parser.parse_args()
    masks, lands, statistics = build_masks()
    lines = ["// <auto-generated>", "// Selected forest cores derived from ESA WorldCover 2021 v200, CC BY 4.0.",
             "// Regenerate with tools/Generate-UsStateForests.py; see Assets/UsStateBrushes/Forests/README.md.",
             "// </auto-generated>", "", "namespace EDMWMapDesigner.UI;", "",
             "internal static partial class UsStateFeatureCatalog", "{",
             "    private static Dictionary<string, string> CreateForestMasks() => new(StringComparer.Ordinal)", "    {"]
    for name, mask in masks.items():
        compressed = gzip.compress(mask, compresslevel=9, mtime=0)
        if gzip.decompress(compressed) != mask:
            raise ValueError(f"Forest compression round trip failed: {name}")
        chunks = textwrap.wrap(base64.b64encode(compressed).decode("ascii"), 112)
        lines.append(f'        ["{name}"] =')
        lines.extend(f'            "{chunk}"' + (" +" if i < len(chunks) - 1 else ",") for i, chunk in enumerate(chunks))
    lines.extend(["    };", "}", ""])
    args.output.write_text("\n".join(lines), encoding="utf-8")
    if args.preview:
        preview(masks, lands, args.preview)
    if args.statistics:
        args.statistics.write_text(json.dumps(statistics, indent=2) + "\n", encoding="utf-8")
    print(f"Generated {len(masks)} forest masks: {args.output.stat().st_size:,} source bytes")


if __name__ == "__main__":
    main()
