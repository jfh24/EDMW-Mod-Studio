# Selected geographic forest areas for US state brushes

The Forests option uses selected, dense forest cores in their real mapped
locations. It deliberately represents a few coherent wooded regions rather
than every tree-covered pixel, leaving space for playable terrain. Each state
has at most six selected cores and at most 25% of its brush land samples
painted as forest. These are cartographic game templates, not measurements
of a state's total forest area.

## Source and license

The source is **ESA WorldCover 10 m 2021 v200**, using its **Tree cover class
10**. It is based on Sentinel satellite data and covers all 50 states,
including Alaska and Hawaii. This is observed land-cover mapping, not
administrative National Forest boundaries.

- [ESA WorldCover data access, license and citation](https://esa-worldcover.org/en/data-access)
- [Dataset DOI: ESA WorldCover 10 m 2021 v200](https://doi.org/10.5281/zenodo.7254221)
- [Creative Commons Attribution 4.0 International](https://creativecommons.org/licenses/by/4.0/)
- [Official Terrascope WMS capabilities](https://titiler.terrascope.be/wms?request=GetCapabilities&service=WMS)

Required map attribution:

**© ESA WorldCover project 2021 / Contains modified Copernicus Sentinel data
(2021) processed by ESA WorldCover consortium**

Data citation: Zanaga, D., Van De Kerchove, R., Daems, D., De Keersmaecker, W.,
Brockmann, C., Kirches, G., Wevers, J., Cartus, O., Santoro, M., Fritz, S.,
Lesiv, M., Herold, M., Tsendbazar, N.E., Xu, P., Ramoino, F., Arino, O., 2022.
*ESA WorldCover 10 m 2021 v200*. DOI: 10.5281/zenodo.7254221.

The brush data is modified from the source through sampling, reprojection,
generalization and core selection, as described below. CC BY 4.0 permits
reuse and modification with attribution; there is no endorsement by ESA or
the dataset authors. Preserve this attribution and license link when
redistributing the derived geographic brush assets.

## Retained source maps

ESA recommends its WMS/WMTS maps for cartographic products. Those RGB class
maps are not intended for quantitative scientific analysis. This generator
uses them only to make geographic game-brush shapes; it makes no claims of
original 10 m precision or quantitative forest-area accuracy.

The 51 original PNG responses were retrieved on 2026-10-01 from Terrascope's
`esa-worldcover-map-10m-2021-v2_map` layer at fixed time `2021-01-01`, using
WMS 1.3.0 and EPSG:4326. Every state's geographic bounding box is padded
slightly, and the source map's longest edge is 1536 pixels. Alaska uses two
maps to preserve the western Aleutians across the antimeridian.

`sources.json` retains every exact request URL, geographic bounding box,
pixel dimensions and SHA-256 checksum, as well as the dataset, license and
attribution. The PNGs are unmodified source responses. The retained service
capabilities record the layer and time; `ESAWorldCover_ColorLegend.qml` is
the official class-color legend linked by ESA. Tree cover has exact RGB
`(0,100,0)` and class value 10. Other land-cover classes, including grass,
cropland and shrubs, are excluded from the source tree mask.

## Geographic fit and selection

1. Reproject the source tree-class samples into the existing 1024×1024 state
   brush mask using the exact local Lambert projection and state bounds from
   `tools/StateBrushProjection.py`. Distributed inverse/forward checks ensure
   alignment with the original Census silhouette.
2. Smooth local tree coverage with box-filter radii of 10 and 30 source-mask
   pixels. The core score is 65% local coverage plus 35% regional coverage.
   Candidate cores require at least 55% local tree coverage and score 0.60.
3. If candidates exceed 25% of state land samples, raise the density threshold
   to retain only the densest forest interiors. Density ties are removed
   uniformly, never by random placement or arbitrary coordinate ordering.
4. Apply a 7-pixel morphological opening to remove narrow spurs and isolated
   speckle. Keep at most the six largest 8-connected patches, each at least
   0.15% of state land samples or 36 mask pixels, whichever is larger.
5. Where no major core survives, use smaller geographic neighborhoods to keep
   a few actual compact woods: radii 5/15, score 0.50, the same 55% local tree
   minimum, a 3-pixel opening, and minimum patch size 0.02% of land or 20
   pixels. This applies to Iowa, Kansas, Nebraska, Nevada and North Dakota.
   It does not fabricate forests or force a minimum forest percentage.
6. Clip to the exact state land mask. Enforce the 25% cap after selection.

The output contains all 50 state keys. Current selected coverage ranges from
about 0.06% in Nebraska to 24.9% in Kentucky. Alaska has 14.7%, Hawaii 11.9%,
Washington 24.2%, and Maine 22.5% of its **brush land samples** selected.
These sample fractions describe the game masks, not geodetic area or actual
state forest percentages. Small patches can disappear at small brush sizes.

The game applies the independently generated layers with water first,
mountains second and forests third, so rivers/lakes and range corridors can
remain readable through the selected forest regions.

## Reproduction

With Python 3.10 or newer, NumPy and Pillow, regenerate entirely offline:

```text
python tools/Generate-UsStateForests.py
```

Optional visual and statistical checks:

```text
python tools/Generate-UsStateForests.py --preview .verification/us-state-forests.png --statistics .verification/us-state-forests.json
```

The generator verifies source checksums/dimensions, exact 50-state coverage,
source coverage, projection alignment, clipping, the 25% land cap and gzip
round trips. Each final row-major mask contains 1024×1024 bits with little-endian
bit order, embedded as gzip/Base64 in `UI/UsStateForestData.g.cs`. NumPy,
Pillow and source maps are needed only for regeneration, not by the app.

`tools/Download-UsStateForests.py` documents and reproduces the original
bounded WMS requests if reacquisition is needed. It reuses existing files
and writes the source manifest after all responses are validated. Routine
builds and mask regeneration do not access the network.
