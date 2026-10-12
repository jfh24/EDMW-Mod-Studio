#pragma once

#include <stddef.h>
#include <stdint.h>
#include <vector>

namespace edmw {

constexpr size_t RowMajorCellIndex(int x, int y, int width) {
    return static_cast<size_t>(y) * static_cast<size_t>(width) +
           static_cast<size_t>(x);
}

// Empires_DMW.exe reads the generator height buffer as x * stride + y before
// copying it into the row-major world-cell grid. Keep all topology algorithms
// row-major and apply this boundary encoding exactly once before returning the
// completed height stage to the host.
bool EncodeHeightGridForEngine(
    float* heights,
    int width,
    int height,
    int activeWidth,
    int activeHeight);

constexpr int32_t kInnerNeutralKind = -15;
constexpr int32_t kOuterNeutralKind = -16;

enum class AlignmentTarget {
    Player,
    Neutral
};

enum class ManualFaunaCategory : uint8_t {
    None = 0,
    Water = 1,
    Land = 2
};

struct ResourcePosition {
    float x = 0.0f;
    float y = 0.0f;
    int32_t kind = 0;
};

struct ManualFaunaRequest {
    uint8_t kind = 0;
    int32_t sourceX = 0;
    int32_t sourceY = 0;
    uint32_t sequence = 0;
    uint32_t unitDescriptorIndexRaw = UINT32_MAX;
};

struct PlannedFaunaPlacement {
    uint8_t kind = 0;
    int32_t worldX = 0;
    int32_t worldY = 0;
    uint32_t sequence = 0;
    uint32_t unitDescriptorIndexRaw = UINT32_MAX;
};

struct FaunaPlannerConstraints {
    uint16_t edgeDistance = 1;
    uint16_t landShoreDistance = 1;
    uint16_t waterShoreDistance = 1;
    uint16_t minimumSeparation = 2;
};

struct AlignmentSummary {
    size_t aligned = 0;
    size_t moved = 0;
    size_t viableComponents = 0;
    size_t componentsUsed = 0;
    size_t maximumComponentOccupancy = 0;
    uint16_t requestedClearance = 0;
    uint16_t minimumClearance = 0;
    float minimumSeparation = 0.0f;
};

struct TopologyGridLayout {
    int width = 0;
    int height = 0;
    int activeWidth = 0;
    int activeHeight = 0;
    const uint8_t* playableCells = nullptr;
};

struct HeightGradientSummary {
    size_t adjustedCells = 0;
    float maximumBefore = 0.0f;
    float maximumAfter = 0.0f;
};

struct AccessRampSummary {
    size_t elevatedComponents = 0;
    size_t separatedLowlandRegions = 0;
    size_t corridors = 0;
    size_t corridorCells = 0;
    size_t adjustedCells = 0;
    float maximumCorridorStepAfter = 0.0f;
    float maximumLandStepAfter = 0.0f;
};

constexpr int8_t kPaintedCliffElevation = 126;

constexpr bool IsPaintedCliff(uint8_t terrain, int8_t elevation) {
    return terrain == 6 && elevation == kPaintedCliffElevation;
}

constexpr bool IsValidPaintedElevation(uint8_t terrain, int8_t elevation) {
    return elevation == 127 || (elevation >= -100 && elevation <= 100) ||
           IsPaintedCliff(terrain, elevation);
}

struct CliffHeightSummary {
    size_t cliffCells = 0;
    size_t rampComponents = 0;
    size_t rampCells = 0;
    size_t unconnectedRamps = 0;
    size_t steepBoundaryEdges = 0;
    float maximumBoundaryStep = 0.0f;
};

constexpr uint32_t kPreserveCoastlineFlag = 0x00000010u;
constexpr uint32_t kForcedElevationCliffMaskFlag = 0x00000020u;

constexpr bool PreservesAuthoredCoastline(uint32_t flags) {
    return (flags & kPreserveCoastlineFlag) != 0;
}

struct CoastHeightSummary {
    size_t shorelineCells = 0;
    size_t boundaryLandCells = 0;
    size_t adjustedCells = 0;
};

// Restore authored shore types and land that meets the playable boundary after
// stock smoothing. Terrain and playable masks use logical row-major order. The
// height buffer may use the host's transposed active square. Unflagged maps are
// exact no-ops; ordinary inland heights and nonplayable padding remain untouched.
// Cliff restoration runs afterward, including any explicitly painted ramps.
bool RestoreAuthoredCoastHeightGrid(
    uint32_t flags, const uint8_t* land, const uint8_t* terrain,
    const int8_t* elevationMask, const uint8_t* manualRampMask, const uint8_t* playableCells,
    int width, int height, int activeWidth, int activeHeight, bool engineLayout,
    float landThreshold, float stockElevationStep,
    float* heights, CoastHeightSummary& summary);

// Operates in logical row-major order. Only explicit cliff cells and connected
// user ramp corridors may change. Maps without marker 126 are exact no-ops.
bool RestorePaintedCliffHeights(
    const uint8_t* land,
    const uint8_t* terrain,
    const int8_t* elevationMask,
    const uint8_t* manualRampMask,
    int width,
    int height,
    float landThreshold,
    float stockElevationStep,
    float maximumRampStep,
    int rampHalfWidth,
    float* heights,
    CliffHeightSummary& summary);

// Read/write the host's transposed active square without encoding it twice.
bool RestorePaintedCliffHeightGrid(
    const uint8_t* land, const uint8_t* terrain, const int8_t* elevationMask,
    const uint8_t* manualRampMask, int width, int height,
    int activeWidth, int activeHeight, bool engineLayout,
    float landThreshold, float stockElevationStep, float maximumRampStep,
    int rampHalfWidth, float* heights, CliffHeightSummary& summary);

// Force authored numeric elevation transitions to remain cliffs after any
// smoothing or ramp pass. Equal-height adjoining cells share a flat plateau.
// A plateau moves only when its selected height is too close to another plane
// to cross the engine's 0.16 cliff threshold. Ordinary boundary cells return to
// their authored planes; terrain, elevation selections and padding are unchanged.
bool RestoreForcedElevationCliffHeightGrid(
    const uint8_t* terrain, const int8_t* elevationMask,
    const uint8_t* cliffMask, const uint8_t* playableCells,
    int width, int height, int activeWidth, int activeHeight, bool engineLayout,
    float landThreshold, float stockElevationStep,
    float* heights, CliffHeightSummary& summary);

bool ConstrainCoastalHeightGradient(
    const uint8_t* land,
    const uint16_t* coastalDistances,
    int width,
    int height,
    float landThreshold,
    float maximumLandWaterStep,
    float* heights,
    HeightGradientSummary& summary);

bool LimitLandHeightGradient(
    const uint8_t* land,
    int width,
    int height,
    float maximumLocalStep,
    float* heights,
    HeightGradientSummary& summary);

bool CarveElevatedLandAccessCorridors(
    const uint8_t* land,
    const uint8_t* terrain,
    const int8_t* elevationMask,
    const uint8_t* manualRampMask,
    int width,
    int height,
    float maximumRampStep,
    float maximumLandStep,
    int rampHalfWidth,
    float* heights,
    AccessRampSummary& summary,
    const uint8_t* cliffMask = nullptr);

int32_t QuantizeWorldCoordinate(float coordinate, int extent);

constexpr uint32_t kManualFaunaPlacementFlag = 0x00000008u;

bool IsNamedFauna(uint8_t sourceKind);
bool IsWaterFauna(uint8_t sourceKind);
bool IsLandFauna(uint8_t sourceKind);
bool IsLegacyUnsupportedFauna(uint8_t sourceKind);
bool IsFauna(uint8_t sourceKind);
bool IsUserPlaceable(uint8_t sourceKind);
ManualFaunaCategory FaunaCategory(uint8_t sourceKind);
const char* NamedFaunaToken(uint8_t sourceKind);
uint32_t NamedFaunaDescriptorIndex(uint8_t sourceKind);
uint32_t NamedFaunaGlobalUnitId(uint8_t sourceKind);
bool IsManualFaunaPlacementEnabled(uint32_t flags);

// Conservative, deterministic post-resource gates derived from the installed
// Default.dll. The +0x118 reservation bytes are authoritative; the +0x1F0
// surface-free cache is deliberately not an input.
bool IsManualFishCellEligible(
    const float* heights,
    const uint8_t* liveLand,
    const uint8_t* playableCells,
    const uint8_t* resourceReservations,
    int width,
    int height,
    int x,
    int y,
    float landThreshold,
    float maximumLocalHeightVariation);

bool IsManualLandFaunaCellEligible(
    const float* heights,
    const uint8_t* liveLand,
    const uint8_t* eligibility,
    const uint8_t* resourceReservations,
    int width,
    int height,
    int x,
    int y,
    float landThreshold,
    float maximumAnimalElevation,
    float maximumLocalHeightVariation);

// Final world height is a lifecycle/readability guard only because it uses a
// different numeric scale from the generator height grid. Cell-kind checks
// mirror the exact host consumers: named water units require kind 1, Fish
// accepts kinds 1 and 10, while land units have no positive kind requirement.
bool IsFinalWorldFaunaCellUsable(
    uint8_t sourceKind, uint32_t cellKind, float worldHeight);

// Plans one complete fauna category without mutating reservations on failure.
// Requests are sorted by category/kind/Y/X/original sequence. The authored
// projection is tried first; snapping then uses distance, Y, X as stable ties.
bool PlanManualFaunaCategory(
    const uint8_t* sourceTerrain,
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    int worldWidth,
    int worldHeight,
    const std::vector<uint32_t>& resourceSourceIndices,
    const std::vector<uint32_t>& directUnitSourceIndices,
    const uint8_t* playableCells,
    const uint8_t* liveLand,
    const uint8_t* blockedCells,
    const uint16_t* shoreDistances,
    ManualFaunaCategory category,
    const FaunaPlannerConstraints& constraints,
    const std::vector<ManualFaunaRequest>& requests,
    std::vector<uint8_t>& reservedCells,
    std::vector<PlannedFaunaPlacement>& placements,
    uint64_t& digest);

// Converts a point-resource kind to Default.dll's generated-resource category.
// Fish is committed through the separately validated transactional fauna planner.
int32_t ManualGeneratedResourceKind(uint8_t sourceKind);

bool MapWorldCellToSource(
    int x,
    int y,
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    int worldWidth,
    int worldHeight,
    const uint8_t* playableCells,
    uint32_t& sourceX,
    uint32_t& sourceY);

// Raw source map used only where a resource/tree record applies its own field
// transpose, and by the established player-start authored-point adapter.
bool BuildWorldSourceIndexMap(
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    int worldWidth,
    int worldHeight,
    const uint8_t* playableCells,
    std::vector<uint32_t>& sourceIndices);

// Converts the engine diamond's vertical axis to the editor's top-to-bottom
// authored axis. Keep the raw map above for resource/tree point records, whose
// record-level coordinate encoder already supplies this conversion.
bool BuildAuthoredWorldSourceIndexMap(
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    int worldWidth,
    int worldHeight,
    const uint8_t* playableCells,
    std::vector<uint32_t>& sourceIndices);

bool FindNearestWorldCellForSourcePoint(
    const std::vector<uint32_t>& sourceIndices,
    uint32_t sourceWidth,
    int32_t sourceX,
    int32_t sourceY,
    size_t& worldIndex);

// The established player-start path uses the raw map above, so mirror authored
// point Y during inverse lookup. This is algebraically identical to a direct
// lookup in BuildAuthoredWorldSourceIndexMap and preserves its runtime positions.
bool FindNearestWorldCellForAuthoredPoint(
    const std::vector<uint32_t>& sourceIndices,
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    int32_t sourceX,
    int32_t sourceY,
    size_t& worldIndex);

// Resource and tree vectors consume their two world-grid fields in the opposite
// order from direct unit records. Encode their raw-map world cell exactly once.
void EncodeResourcePointCoordinates(
    float terrainWorldX,
    float terrainWorldY,
    float& recordX,
    float& recordY);

float RecommendedAlignmentClearance(
    AlignmentTarget target,
    int worldWidth,
    int worldHeight);

bool BuildCoastalDistanceMap(
    const uint8_t* land,
    int width,
    int height,
    std::vector<uint16_t>& distances,
    const uint8_t* playableCells = nullptr);

// Distance from each playable cell to the actual playable boundary. Boundary
// cells are zero; propagation uses eight neighbors, matching the rectangular
// min-edge convention while respecting EDMW's diamond-shaped world mask.
bool BuildPlayableEdgeDistanceMap(
    const uint8_t* playableCells,
    int width,
    int height,
    std::vector<uint16_t>& distances);

float CoastalElevationForTerrain(
    uint8_t terrain,
    uint16_t coastalDistance,
    float landThreshold);

float StockElevationStep(
    float landThreshold,
    float maximumResourceElevation);

float StockElevationForTerrain(
    uint8_t terrain,
    uint16_t coastalDistance,
    float landThreshold,
    float maximumResourceElevation);

float ResourceCompatibleElevationForTerrain(
    uint8_t terrain,
    uint16_t coastalDistance,
    float landThreshold,
    float maximumResourceElevation,
    float detailOffset);

AlignmentSummary AlignResourcePositions(
    const uint8_t* terrain,
    const uint8_t* roles,
    uint32_t width,
    uint32_t height,
    int worldWidth,
    int worldHeight,
    AlignmentTarget target,
    float requiredClearance,
    const std::vector<ResourcePosition>& fixedPositions,
    std::vector<ResourcePosition>& positions,
    const uint8_t* playableCells = nullptr);

AlignmentSummary AlignResourcePositionsAdaptive(
    const uint8_t* terrain,
    const uint8_t* roles,
    uint32_t width,
    uint32_t height,
    int worldWidth,
    int worldHeight,
    AlignmentTarget target,
    float preferredClearance,
    float minimumClearance,
    const std::vector<ResourcePosition>& fixedPositions,
    std::vector<ResourcePosition>& positions,
    const uint8_t* playableCells = nullptr);

bool BuildPositionLandDomains(
    const uint8_t* terrain,
    const uint8_t* roles,
    uint32_t width,
    uint32_t height,
    int worldWidth,
    int worldHeight,
    AlignmentTarget target,
    uint16_t minimumClearance,
    const std::vector<ResourcePosition>& positions,
    std::vector<std::vector<ResourcePosition>>& domains,
    const uint8_t* playableCells = nullptr);

bool BuildPositionLandDomains(
    const uint8_t* terrain,
    const uint8_t* roles,
    uint32_t width,
    uint32_t height,
    int worldWidth,
    int worldHeight,
    AlignmentTarget target,
    uint16_t minimumClearance,
    const std::vector<ResourcePosition>& positions,
    const std::vector<ResourcePosition>& reservedPositions,
    std::vector<std::vector<ResourcePosition>>& domains,
    const uint8_t* playableCells = nullptr);

bool PositionMapsToPaintedLand(
    const uint8_t* terrain,
    uint32_t width,
    uint32_t height,
    int worldWidth,
    int worldHeight,
    const ResourcePosition& position,
    const uint8_t* playableCells = nullptr);

uint8_t RoleAtPosition(
    const uint8_t* roles,
    uint32_t width,
    uint32_t height,
    int worldWidth,
    int worldHeight,
    const ResourcePosition& position,
    const uint8_t* playableCells = nullptr);

bool BuildAuthoritativeLandMask(
    const uint8_t* terrain,
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    int targetWidth,
    int targetHeight,
    uint8_t* output);

bool BuildAuthoritativeLandMask(
    const uint8_t* terrain,
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    const TopologyGridLayout& layout,
    uint8_t* output);

bool BuildAuthoritativeEligibilityMask(
    const uint8_t* terrain,
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    int targetWidth,
    int targetHeight,
    uint8_t* output);

bool BuildAuthoritativeEligibilityMask(
    const uint8_t* terrain,
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    const TopologyGridLayout& layout,
    uint8_t* output);

bool ConstrainEligibilityToPaintedLand(
    const uint8_t* paintedEligibility,
    size_t count,
    uint8_t* stockEligibility);

bool BuildAuthoritativeSurfaceGrid(
    const uint8_t* terrain,
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    int targetWidth,
    int targetHeight,
    uint32_t landValue,
    uint32_t* output);

// Promotes a deterministic, evenly distributed minimum share of each connected
// land coastline to beach terrain. Returns the number of promoted cells.
size_t GuaranteeBeachCoastline(
    uint8_t* terrain,
    int width,
    int height,
    double minimumBeachShare = 0.60,
    const int8_t* elevationMask = nullptr,
    const uint8_t* cliffMask = nullptr);

bool BuildAuthoritativeSurfaceGrid(
    const uint8_t* terrain,
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    const TopologyGridLayout& layout,
    uint32_t landValue,
    uint32_t* output);

}  // namespace edmw
