# US state brush boundaries

The 50 state silhouettes are derived from the U.S. Census Bureau's **2024
Cartographic Boundary File, States and Equivalent Entities, 1:20,000,000**.
District of Columbia and Puerto Rico are excluded using an explicit list of
the 50 state names.

- [Census Bureau source page](https://www.census.gov/geographies/mapping-files/2024/geo/carto-boundary-file.html)
- [Original KML ZIP](https://www2.census.gov/geo/tiger/GENZ2024/kml/cb_2024_us_state_20m.zip)
- [Census data products guide](https://www.census.gov/programs-surveys/geography/guidance/tiger-data-products-guide.html)
- [Census research transparency and public access policy](https://www2.census.gov/foia/ds_policies/ds027.pdf)

The original ZIP is retained here for reproducible, offline regeneration. Its
SHA-256 is
`37337db59415f010c594fba96a48aa6e950e633dc0e555cc7b1ce8edd794c673`.
It includes Census metadata. Census Bureau employee works are generally public
domain in the United States; these brush masks are derived from that federal
government geographic data. No third-party rendered map or artwork is used.

## Geometry and runtime

The Census cartographic data is already simplified and clipped to shorelines.
Consequently Michigan keeps its two peninsulas and islands without filling
Great Lakes water. Polygon holes and disconnected land parts are preserved.
The source's Hawaii representation contains the eight main islands. Alaska's
positive longitudes are unwrapped across the antimeridian before projection,
keeping the Aleutian islands alongside the rest of Alaska.

Each state uses a local spherical Lambert conformal conic projection. Its
central meridian is the midpoint of its longitude bounds; standard parallels
are one sixth and five sixths of its latitude extent. The projected shape is
centered and uniformly scaled to fit `[-1, 1]` without stretching either axis.
North is upward (negative screen y). Small islands and details can disappear
when the brush is stamped at a small cell radius, as with any sampled shape.

The generator converts these polygons into 1024 by 1024 one-bit masks using
even-odd scan conversion at pixel centers. Each mask is gzip-compressed and
included in `UI/UsStateBrushData.g.cs`. `UI/UsStateBrush.cs` decompresses a
state's mask once on first use and performs constant-time bit lookups for
painting and previews. Each decoded state uses 128 KiB; all 50 together use
6.25 MiB. The ZIP and generator are not needed at runtime, and no network or
additional runtime library is required.

## Regeneration

From the repository root, using Python 3.10 or newer:

```text
python tools/Generate-UsStateBrushes.py
```

The generator uses only the Python standard library. It checks the exact
state-name set, nonempty masks, polygon scan crossings, and compression round
trips. To additionally create a contact sheet for visual inspection, install
Pillow and pass `--preview path/to/contact-sheet.png`.
