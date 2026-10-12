"""Download fixed ESA WorldCover 2021 cartographic source maps for state brushes.

These are class-color WMS maps for cartographic use, not full-resolution
scientific raster data. Requires Python 3.10+, Pillow, and explicit network access.
Ordinary regeneration uses the downloaded files offline instead.
"""

from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
import hashlib
import io
import json
import math
from pathlib import Path
import time
import urllib.parse
import urllib.request

from PIL import Image
from StateBrushProjection import ROOT, load_states

SOURCE = ROOT / "EDMWMapDesigner/Assets/UsStateBrushes/Forests"
SERVICE = "https://titiler.terrascope.be/wms"
LAYER = "esa-worldcover-map-10m-2021-v2_map"


def jobs():
    for name, polygons in sorted(load_states().items()):
        points = [point for polygon in polygons for ring in polygon for point in ring]
        west = math.floor(min(p[0] for p in points) * 100) / 100 - .01
        south = math.floor(min(p[1] for p in points) * 100) / 100 - .01
        east = math.ceil(max(p[0] for p in points) * 100) / 100 + .01
        north = math.ceil(max(p[1] for p in points) * 100) / 100 + .01
        boxes = [(west, south, east, north)]
        if west < -180:
            # Retain western Aleutians across the antimeridian as a separate map.
            boxes = [(west + 360, south, 180., north), (-180., south, east, north)]
        for index, bbox in enumerate(boxes):
            west, south, east, north = bbox
            # Uniform longitude/latitude source sampling, longest edge 1536px.
            scale = 1536 / max(east - west, north - south)
            width = max(64, round((east - west) * scale))
            height = max(64, round((north - south) * scale))
            params = {"SERVICE": "WMS", "VERSION": "1.3.0", "REQUEST": "GetMap",
                      "LAYERS": LAYER, "STYLES": "", "CRS": "EPSG:4326",
                      "BBOX": f"{south:.2f},{west:.2f},{north:.2f},{east:.2f}",
                      "WIDTH": str(width), "HEIGHT": str(height), "FORMAT": "image/png",
                      "TRANSPARENT": "TRUE", "TIME": "2021-01-01"}
            yield {"state": name, "file": name.replace(" ", "_") + (f"_{index}" if len(boxes) > 1 else "") + ".png",
                   "bbox": bbox, "width": width, "height": height,
                   "url": SERVICE + "?" + urllib.parse.urlencode(params)}


def download(job):
    path = SOURCE / job["file"]
    if not path.exists():
        for attempt in range(3):
            try:
                with urllib.request.urlopen(job["url"], timeout=90) as response:
                    data = response.read()
                with Image.open(io.BytesIO(data)) as image:
                    if image.size != (job["width"], job["height"]):
                        raise ValueError(f"Unexpected map dimensions: {job['state']}")
                    image.verify()
                path.write_bytes(data)
                break
            except Exception:
                if attempt == 2:
                    raise
                time.sleep(2 * (attempt + 1))
    with Image.open(path) as image:
        if image.size != (job["width"], job["height"]):
            raise ValueError(f"Cached map dimensions differ: {path}")
    job["sha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
    print(f"Downloaded {job['file']}: {path.stat().st_size:,} bytes", flush=True)
    return job


def main():
    SOURCE.mkdir(parents=True, exist_ok=True)
    with ThreadPoolExecutor(max_workers=4) as pool:
        sources = list(pool.map(download, jobs()))
    manifest = {"dataset": "ESA WorldCover 10 m 2021 v200", "retrieved": "2026-10-01",
                "license": "CC BY 4.0", "license_url": "https://creativecommons.org/licenses/by/4.0/",
                "dataset_doi": "https://doi.org/10.5281/zenodo.7254221",
                "layer": LAYER, "tree_class": 10, "tree_rgb": [0, 100, 0],
                "attribution": "© ESA WorldCover project 2021 / Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover consortium",
                "sources": sources}
    (SOURCE / "sources.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Retained {len(sources)} maps for 50 states", flush=True)


if __name__ == "__main__":
    main()
