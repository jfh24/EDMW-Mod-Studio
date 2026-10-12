# Geographic water layers for US state brushes

These offline source files provide real mapped lake, reservoir and river
locations for the Water bodies option. All 50 states have a nonempty water
mask. The maps are generalized geographic brush templates, not an exhaustive
inventory of every pond, stream, wetland or seasonal watercourse.

## Sources and permission

The following **Natural Earth version 5.0.0**, 1:10,000,000 datasets are retained
as their original ZIP downloads, including their metadata:

- `ne_10m_lakes.zip`
- `ne_10m_lakes_north_america.zip`
- `ne_10m_rivers_lake_centerlines.zip`
- `ne_10m_rivers_north_america.zip`

[Natural Earth lakes and reservoirs](https://www.naturalearthdata.com/downloads/10m-physical-vectors/10m-lakes/)
and [rivers and lake centerlines](https://www.naturalearthdata.com/downloads/10m-physical-vectors/10m-rivers-lake-centerlines/)
describe the source geometry and North America supplements. The supplementary
North America data derives from the North American Environmental Atlas and
the Commission for Environmental Cooperation. Natural Earth adjusts and
generalizes those features to fit its other map layers.

[Natural Earth's terms](https://www.naturalearthdata.com/about/terms-of-use/)
place all its raster and vector data in the public domain and permit personal,
educational and commercial use, modification and redistribution. Credit:
**Made with Natural Earth.**

Natural Earth contains no Hawaiian hydrography, no inland Delaware features
that survive state clipping, and sparse Rhode Island coverage. These states
use bounded queries from the **U.S. Geological Survey National Hydrography
Dataset (NHD), The National Map**:

- [Official NHD description](https://www.usgs.gov/national-hydrography/national-hydrography-dataset)
- [The National Map NHD service and use constraints](https://hydro.nationalmap.gov/arcgis/rest/services/nhd/MapServer)
- Layers 5 and 11 for Hawaii; layers 4 and 10 for Delaware and Rhode Island.

The service describes these data as public domain, open and non-proprietary,
with no use constraints. Its metadata when queried stated: “USGS TNM –
National Hydrography Dataset. Data Refreshed July, 2026.” Credit:
**Hawaii, Delaware and Rhode Island hydrography: U.S. Geological Survey,
National Hydrography Dataset (The National Map).** NHD is a retained mapping
dataset; the USGS now develops the 3D Hydrography Program.

The six JSON files are original, complete USGS query responses in WGS84:

| State | River segments | Lake/reservoir polygons |
| --- | ---: | ---: |
| Hawaii | 897 | 147 |
| Delaware | 680 | 379 |
| Rhode Island | 390 | 455 |

River queries select named perennial stream/river features (`FCode=46006`)
of stream order at least 3. Lake queries select lake/pond and reservoir
feature classes with mapped area at least 0.03 km², excluding swamp/marsh
features. The bounding boxes include some neighboring features that are
subsequently clipped away. Server generalization uses 0.001° for rivers and
0.0005° for lake outlines, rounded to five decimal places. Every response
was checked for `exceededTransferLimit` and errors.

The exact download and query URLs, retrieval date, source versions, counts
and SHA-256 hashes are preserved in `sources.json`. All source data was
retrieved on 2026-10-01. The original responses allow offline regeneration
even if the remote services later change.

## Projection, clipping and cartographic choices

Water coordinates use the exact same local Lambert conformal projection,
state bounds and centered uniform normalization as the Census state
silhouettes, through `tools/StateBrushProjection.py`. They are never fitted
to a separate water-feature bounding box. Alaska's positive longitudes are
unwrapped across the antimeridian with the state boundary.

Lake polygons use even-odd scan conversion, preserving islands/holes.
River centerlines have a 7-pixel cartographic stroke in the 1024×1024 source
mask, or 9 pixels for Natural Earth scale ranks 1–5, so narrow rivers remain
readable when sampled onto map cells. These widths are symbols, not measured
physical river widths. Lake extents reflect the source snapshot, not current
lake levels.

Alaska uses the Natural Earth main river network (scale ranks 1–8); its
supplemental rank 9–12 rivers are omitted. Its long Aleutian extent makes
the mainland occupy a small portion of the square brush, where dense minor
drainage merges into excessive water at practical map sizes. All lake
polygons are retained. This selection plus the wider river symbols yields
11.3% water samples inside the Alaska silhouette, compared with 38.9% if
every minor river used those widths. Other states retain all selected
source river features.

The resulting mask is clipped to the state's outer polygon rings. The
bundled Census 20m state data contains no interior rings; this approach
also allows enclosed inland water if later boundary data contains holes.
State coastlines, external oceans, bays, and the adjacent Great Lakes
remain outside the brush footprint. Small features may disappear at small
brush sizes or in low-resolution map cells.

Each output mask has 1024×1024 bits (128 KiB), little-endian within each byte,
in row-major order. Gzip-compressed Base64 payloads are embedded in
`UI/UsStateWaterData.g.cs`; no network or external GIS runtime is used by the
app.

## Regeneration and verification

From the repository root with Python 3.10 or newer:

```text
python tools/Generate-UsStateWater.py
```

The generator uses only the Python standard library and the bundled source
files. It verifies the source hashes, expected state set, nonempty state
water masks, valid polygon crossings, complete USGS responses and gzip
round trips. To render a contact sheet with optional Pillow:

```text
python tools/Generate-UsStateWater.py --preview .verification/us-state-water.png
```

The source and masks were checked using geographically known points in
Great Salt Lake, Lake Okeechobee, Lake Tahoe, Iliamna Lake and the Wailuku
River, with a nearby Utah land point as a negative check.
