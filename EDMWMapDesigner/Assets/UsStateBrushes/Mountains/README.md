# Mountain range brush layer

These masks are derived from **GMBA Mountain Inventory v2, Standard Basic**
(2022), by the Global Mountain Biodiversity Assessment (GMBA) and EarthEnv.
The source contains geographic polygons derived from terrain data and mapped
mountain ranges. The brush uses the full mapped range footprints and paints
them as cliff terrain. No schematic centerlines or coverage limits are applied.

## Attribution and license

Copyright Global Mountain Biodiversity Assessment (GMBA). Used under the
[Creative Commons Attribution 4.0 International license](https://creativecommons.org/licenses/by/4.0/).
The source author's [license notice](https://github.com/GMBA-biodiversity/Inventory/blob/main/LICENSE)
and [dataset page](https://www.earthenv.org/mountains) identify this license.
Retain this attribution with distributed copies of the derived brush data.

Publication citation: Snethlage, M.A., Geschke, J., Spehn, E.M., Ranipeta, A.,
Yoccoz, N.G., Körner, Ch., Jetz, W., Fischer, M. & Urbach, D. (2022).
*A hierarchical inventory of the world's mountains for global comparative
mountain science.* Scientific Data. https://doi.org/10.1038/s41597-022-01256-y

Dataset citation: Snethlage, M.A., Geschke, J., Spehn, E.M., Ranipeta, A.,
Yoccoz, N.G., Körner, Ch., Jetz, W., Fischer, M. & Urbach, D. (2022).
*GMBA Mountain Inventory v2.* GMBA-EarthEnv.
https://doi.org/10.48601/earthenv-t9k2-1407

## Source and derivation

- Original archive: [GMBA Standard Basic ZIP](https://data.earthenv.org/mountains/standard/GMBA_Inventory_v2.0_standard_basic.zip).
- Original archive SHA-256: `91b7a37e4331cea01fb8938d535d0fbfcec8aae4173e4b073e46e2896b74f198`.
- Retained source: `gmba-v2-standard-basic-usa.json.gz`.
- Retained source SHA-256: `a7a85d0ac8500205a1dbb087b7e1b03806246a83ca2d4a33ce45ebc905ac3bf9`.

The retained subset contains all 1,279 source records whose `CountryISO` field
includes `USA`, including ranges that cross international borders. Source
polygon coordinates, holes, range names, IDs, and hierarchy paths are retained
without simplification. Every source range intersecting a state contributes to
that state's mask. JSON encoding, country filtering, per-state projection,
rasterization, and state clipping are modifications made for this application.

`tools/StateBrushProjection.py` provides exactly the same state-centered
projection as the base Census silhouettes. Alaska longitudes are unwrapped
across the antimeridian. Each mountain mask is sampled at 1024 by 1024 pixel
centers, clipped to the base state silhouette, then packed and compressed.
Polygon holes and separate land parts are retained. No random terrain or
procedural ridgelines are added.
The generated `UI/UsStateMountainData.g.cs` contains an entry for all 50 states.
The compact masks are built into the app; no network access is needed to use
the brushes.

## Scope

The Standard inventory follows GMBA's scientific definition of mountainous
terrain. It captures major mountain systems including the Rockies,
Appalachians, Cascades, Sierra Nevada, Alaska ranges, and Hawaiian mountains.
It does not claim to include every named hill or range. Lower uplands and
gentler terrain can fall outside this definition; for example, the masks for
Missouri and Michigan are empty. Empty masks do not mean that a state has no
real-world mountains. Small ranges and narrow details may disappear when the
brush is stamped onto a small game grid.

## Regeneration and verification

From the repository root, with Python 3.10 or newer:

```text
python tools/Generate-UsStateMountains.py
```

To re-extract the retained subset from a freshly downloaded original archive,
add `--archive path/to/GMBA_Inventory_v2.0_standard_basic.zip`. Optional
`--preview path/to/mountains.png` produces a contact sheet using Pillow;
`--statistics path/to/statistics.json` records per-state sample counts.
Normal generation uses only the standard library and the retained source.

Generation checks polygon scan crossings and compression round trips.
Independent verification confirmed that the shared projection reproduces
all 50 original silhouette masks exactly and that all mountain samples stay
inside their state silhouette. Geographic fixtures include Mount Elbert,
Mount Whitney, Denali, Mauna Kea, and Mount Washington, with plains/valley
negative checks at Limon, Fresno, Bethel, and Orlando.
