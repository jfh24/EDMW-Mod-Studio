# Geographic data and artwork credits

EDMW Mod Studio includes offline geographic source inputs and derived US state brush masks. These credits apply to those assets; they do not assign a license to the application source or to the separately installed game.

The original source-specific documentation and manifests are retained under [`EDMWMapDesigner/Assets/UsStateBrushes`](../EDMWMapDesigner/Assets/UsStateBrushes/README.md). Preserve their notices with redistributed geographic assets.

## State silhouettes

The 50 state boundaries derive from the **U.S. Census Bureau 2024 Cartographic Boundary File, States and Equivalent Entities, 1:20,000,000**. District of Columbia and Puerto Rico are excluded.

- [Census source page](https://www.census.gov/geographies/mapping-files/2024/geo/carto-boundary-file.html)
- [Original KML ZIP](https://www2.census.gov/geo/tiger/GENZ2024/kml/cb_2024_us_state_20m.zip)
- [Retained source, hashes, projection, and regeneration notes](../EDMWMapDesigner/Assets/UsStateBrushes/README.md)

The retained Census metadata describes federal geographic data; Census Bureau employee works are generally public domain in the United States. The masks are projected, centered, normalized, rasterized, and compressed for the editor.

## Mountain ranges

Mountain masks derive from **GMBA Mountain Inventory v2, Standard Basic (2022)** by the Global Mountain Biodiversity Assessment and EarthEnv.

**© Global Mountain Biodiversity Assessment (GMBA). Used under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).**

- Dataset: Snethlage et al. (2022), [GMBA Mountain Inventory v2](https://doi.org/10.48601/earthenv-t9k2-1407).
- Publication: Snethlage et al. (2022), [A hierarchical inventory of the world's mountains for global comparative mountain science](https://doi.org/10.1038/s41597-022-01256-y), *Scientific Data*.
- [Dataset page](https://www.earthenv.org/mountains)
- [Source license notice](https://github.com/GMBA-biodiversity/Inventory/blob/main/LICENSE)
- [Retained subset and derivation details](../EDMWMapDesigner/Assets/UsStateBrushes/Mountains/README.md)

The application filters the source to USA-related records, projects each state's geometry, rasterizes the range footprints, and clips them to state silhouettes. These are modifications of the original data. Generalized scientific mountain definitions do not include every hill or named range.

## Lakes and rivers

**Made with Natural Earth.** The retained lake and river sources are **Natural Earth version 5.0.0, 1:10,000,000**. [Natural Earth's terms](https://www.naturalearthdata.com/about/terms-of-use/) place its raster and vector data in the public domain.

- [Natural Earth lakes and reservoirs](https://www.naturalearthdata.com/downloads/10m-physical-vectors/10m-lakes/)
- [Natural Earth rivers and lake centerlines](https://www.naturalearthdata.com/downloads/10m-physical-vectors/10m-rivers-lake-centerlines/)
- [Retained inputs, source manifest, query details, and regeneration notes](../EDMWMapDesigner/Assets/UsStateBrushes/Water/README.md)

**Hawaii, Delaware and Rhode Island hydrography: U.S. Geological Survey, National Hydrography Dataset (The National Map).** These supplements retain original, bounded service responses and their metadata. The documented service describes the data as public domain with no use constraints.

- [USGS National Hydrography Dataset](https://www.usgs.gov/national-hydrography/national-hydrography-dataset)
- [The National Map NHD service metadata](https://hydro.nationalmap.gov/arcgis/rest/services/nhd/MapServer)

Projection, clipping, selection, and cartographic river widths are adaptations for game brushes. River stroke widths represent visible symbols rather than measured physical widths; the masks are not an exhaustive hydrographic inventory.

## Forest regions

Selected forest cores derive from **ESA WorldCover 10 m 2021 v200**, Tree cover class 10.

**© ESA WorldCover project 2021 / Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover consortium.**

Used under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).

- Dataset citation: Zanaga et al. (2022), [ESA WorldCover 10 m 2021 v200](https://doi.org/10.5281/zenodo.7254221).
- [ESA data access, license, and citation](https://esa-worldcover.org/en/data-access)
- [Retained responses, source manifest, selection rules, and regeneration notes](../EDMWMapDesigner/Assets/UsStateBrushes/Forests/README.md)

The application samples source maps, reprojects them to state masks, smooths/generalizes tree cover, selects at most six dense cores, and caps forest coverage at 25% of the state's brush land samples. These adaptations represent selected major wooded areas, not total forest area or original 10 m precision. The use does not imply endorsement by ESA or the source authors.

## UI artwork

The progression and civilization category icon sheets were generated with the built-in image generation tool for the editor. Their source prompts, atlas dimensions, and transparency/cropping notes are preserved in the [progression artwork README](../EDMWMapDesigner/Assets/Progression/README.md).

Other UI artwork is bundled under `EDMWMapDesigner/Assets` and the map installer's `Assets` directory. This documentation does not infer additional licensing terms for artwork that has no separate license notice.

## Game data

The editor reads localized names, game records, textures, models, and animations from the user's chosen installation of **Empires: Dawn of the Modern World**. These game files are separate from the repository's source and retained geography datasets. Generated verification fixtures may include copied game data and are local, ignored outputs.
