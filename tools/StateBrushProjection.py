"""Shared coordinate system for geographic layers on the Census state brushes.

The original silhouette generator remains the reference for mask scan conversion.
Feature layers must use the state's bounds, never their own feature bounds.
"""

from __future__ import annotations

import importlib.util
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
_spec = importlib.util.spec_from_file_location(
    "state_silhouettes", ROOT / "tools/Generate-UsStateBrushes.py")
_silhouettes = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_silhouettes)
MASK_SIZE = _silhouettes.MASK_SIZE
rasterize = _silhouettes.rasterize


def load_states():
    return _silhouettes.read_states(_silhouettes.SOURCE)


def make_projection(name, polygons):
    """Return a lon/lat-to-normalized-xy function aligned with the state mask."""
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

    def raw(lon, lat):
        if name == "Alaska" and lon > 0:
            lon -= 360
        lon, lat = math.radians(lon), math.radians(lat)
        rho = f / math.tan(math.pi / 4 + lat / 2) ** n
        theta = n * (lon - central_lon)
        return rho * math.sin(theta), rho * math.cos(theta)

    projected = [raw(*point) for point in points]
    min_x, max_x = min(p[0] for p in projected), max(p[0] for p in projected)
    min_y, max_y = min(p[1] for p in projected), max(p[1] for p in projected)
    center_x, center_y = (min_x + max_x) / 2, (min_y + max_y) / 2
    half_extent = max(max_x - min_x, max_y - min_y) / 2

    def transform(lon, lat):
        x, y = raw(lon, lat)
        return (x - center_x) / half_extent, (y - center_y) / half_extent

    return transform
