#include "ResourceAlignment.h"

#include <algorithm>
#include <stdint.h>
#include <cmath>
#include <queue>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <vector>

namespace {

bool Expect(bool condition, const char* message) {
    if (condition) return true;
    fprintf(stderr, "FAILED: %s\n", message);
    return false;
}

bool TestAsymmetricProjection() {
    constexpr int kSize = 4;
    std::vector<uint8_t> terrain(kSize * kSize, 0);
    terrain[0 * kSize + 0] = 2;
    terrain[1 * kSize + 2] = 2;
    terrain[3 * kSize + 3] = 2;

    std::vector<uint8_t> actual(kSize * kSize, 0xFF);
    if (!Expect(edmw::BuildAuthoritativeLandMask(
                    terrain.data(), kSize, kSize, kSize, kSize, actual.data()),
                "asymmetric land projection succeeds"))
        return false;

    std::vector<uint8_t> expected(kSize * kSize, 1);
    expected[3 * kSize + 0] = 0;
    expected[2 * kSize + 2] = 0;
    expected[0 * kSize + 3] = 0;
    return Expect(actual == expected,
                  "terrain projection mirrors editor top-to-bottom for the engine");
}

bool TestHeightGridBoundaryEncodingIsExactlyOneTranspose() {
    constexpr int kWidth = 7;
    constexpr int kHeight = 6;
    constexpr int kActive = 4;
    std::vector<float> heights(kWidth * kHeight, -999.0f);
    for (int y = 0; y < kActive; ++y)
        for (int x = 0; x < kActive; ++x)
            heights[edmw::RowMajorCellIndex(x, y, kWidth)] =
                static_cast<float>(100 * y + x);
    const std::vector<float> before = heights;

    if (!Expect(edmw::EncodeHeightGridForEngine(
                    heights.data(), kWidth, kHeight, kActive, kActive),
                "height boundary encoding accepts a padded active square"))
        return false;
    for (int y = 0; y < kActive; ++y) {
        for (int x = 0; x < kActive; ++x) {
            const float logical = before[edmw::RowMajorCellIndex(x, y, kWidth)];
            const float hostRead = heights[static_cast<size_t>(x) * kWidth + y];
            if (!Expect(hostRead == logical,
                        "host x-stride height lookup sees the logical row-major cell"))
                return false;
        }
    }
    for (int y = 0; y < kHeight; ++y)
        for (int x = 0; x < kWidth; ++x)
            if ((x >= kActive || y >= kActive) &&
                !Expect(heights[edmw::RowMajorCellIndex(x, y, kWidth)] ==
                            before[edmw::RowMajorCellIndex(x, y, kWidth)],
                        "height boundary encoding leaves padding untouched"))
                return false;
    return Expect(!edmw::EncodeHeightGridForEngine(
                      heights.data(), kWidth, kHeight, kActive, kActive - 1),
                  "non-square active height domains fail closed");
}

bool TestAsymmetricSurfaceProjectionMirrorsEditorY() {
    constexpr int size = 4;
    std::vector<uint8_t> terrain(size * size, 0);
    terrain[edmw::RowMajorCellIndex(1, 0, size)] = 3;
    terrain[edmw::RowMajorCellIndex(2, 3, size)] = 5;
    std::vector<uint32_t> surface(size * size, UINT32_MAX);
    if (!Expect(edmw::BuildAuthoritativeSurfaceGrid(
                    terrain.data(), size, size, size, size, 1024, surface.data()),
                "asymmetric surface projection succeeds"))
        return false;
    return Expect(surface[edmw::RowMajorCellIndex(1, 3, size)] == 1024 &&
                      surface[edmw::RowMajorCellIndex(2, 0, size)] == 1026 &&
                      surface[edmw::RowMajorCellIndex(1, 0, size)] == 0,
                  "surface kinds mirror editor top-to-bottom exactly once");
}

bool TestRowMajorWorldSourceIndexLayout() {
    constexpr int kWidth = 5;
    constexpr int kHeight = 4;
    std::vector<uint32_t> sourceIndices;
    if (!Expect(edmw::BuildWorldSourceIndexMap(
                    kWidth, kHeight, kWidth, kHeight, nullptr, sourceIndices),
                "row-major source-index map succeeds"))
        return false;

    for (int y = 0; y < kHeight; ++y) {
        for (int x = 0; x < kWidth; ++x) {
            const size_t worldIndex = edmw::RowMajorCellIndex(x, y, kWidth);
            const uint32_t expectedSource = static_cast<uint32_t>(
                edmw::RowMajorCellIndex(x, y, kWidth));
            if (!Expect(sourceIndices[worldIndex] == expectedSource,
                        "world source indices use y * width + x"))
                return false;
        }
    }
    return Expect(edmw::RowMajorCellIndex(1, 3, kWidth) == 16 &&
                  edmw::RowMajorCellIndex(3, 1, kWidth) == 8,
                  "asymmetric coordinates are not transposed");
}

bool TestPlayableMaskProjection() {
    constexpr int kSize = 5;
    std::vector<uint8_t> terrain(kSize * kSize, 2);
    std::vector<uint8_t> playable(kSize * kSize, 0);
    playable[0 * kSize + 1] = 1;
    playable[1 * kSize + 1] = 1;
    playable[1 * kSize + 2] = 1;
    playable[2 * kSize + 1] = 1;
    playable[2 * kSize + 3] = 1;
    playable[3 * kSize + 2] = 1;

    const edmw::TopologyGridLayout layout = {
        kSize, kSize, kSize, kSize, playable.data()
    };
    std::vector<uint8_t> actual(kSize * kSize, 0xFF);
    if (!Expect(edmw::BuildAuthoritativeLandMask(
                    terrain.data(), kSize, kSize, layout, actual.data()),
                "playable-mask land projection succeeds"))
        return false;

    for (int y = 0; y < kSize; ++y) {
        for (int x = 0; x < kSize; ++x) {
            const uint8_t expected = playable[static_cast<size_t>(y) * kSize + x] ? 0 : 1;
            if (!Expect(actual[static_cast<size_t>(y) * kSize + x] == expected,
                        "engine-grid projection preserves the playable-mask orientation"))
                return false;
        }
    }
    return true;
}

bool TestAsymmetricDiamondProjectionWithPadding() {
    constexpr int kSourceSize = 5;
    constexpr int kActiveSize = 5;
    constexpr int kTargetWidth = 7;
    constexpr int kTargetHeight = 6;
    std::vector<uint8_t> terrain(kSourceSize * kSourceSize, 0);
    terrain[4 * kSourceSize + 0] = 2;
    terrain[3 * kSourceSize + 1] = 2;
    terrain[3 * kSourceSize + 3] = 2;
    terrain[0 * kSourceSize + 4] = 2;

    std::vector<uint8_t> playable(kActiveSize * kActiveSize, 0);
    for (int y = 0; y < kActiveSize; ++y)
        for (int x = 0; x < kActiveSize; ++x)
            if (abs(x - 2) + abs(y - 2) <= 2)
                playable[static_cast<size_t>(y) * kActiveSize + x] = 1;

    const edmw::TopologyGridLayout layout = {
        kTargetWidth, kTargetHeight, kActiveSize, kActiveSize, playable.data()
    };
    std::vector<uint8_t> actual(kTargetWidth * kTargetHeight, 0xFF);
    if (!Expect(edmw::BuildAuthoritativeLandMask(
                    terrain.data(), kSourceSize, kSourceSize, layout, actual.data()),
                "asymmetric diamond projection with padding succeeds"))
        return false;

    std::vector<uint8_t> expected(kTargetWidth * kTargetHeight, 1);
    expected[0 * kTargetWidth + 2] = 0;
    expected[1 * kTargetWidth + 2] = 0;
    expected[2 * kTargetWidth + 3] = 0;
    expected[4 * kTargetWidth + 2] = 0;
    if (!Expect(actual == expected,
                "diamond terrain projection mirrors top-to-bottom and leaves padding as water"))
        return false;

    std::vector<uint8_t> paintedEligibility(kTargetWidth * kTargetHeight, 0xFF);
    if (!Expect(edmw::BuildAuthoritativeEligibilityMask(
                    terrain.data(), kSourceSize, kSourceSize, layout,
                    paintedEligibility.data()),
                "diamond eligibility projection succeeds"))
        return false;
    for (size_t index = 0; index < actual.size(); ++index) {
        const bool land = actual[index] == 0;
        if (!Expect((paintedEligibility[index] != 0) == land,
                    "painted eligibility agrees with the projected land grid"))
            return false;
    }

    std::vector<uint8_t> stockEligibility(actual.size(), 7);
    for (size_t index = 0; index < stockEligibility.size(); index += 3)
        stockEligibility[index] = 0;
    const std::vector<uint8_t> originalStockEligibility = stockEligibility;
    if (!Expect(edmw::ConstrainEligibilityToPaintedLand(
                    paintedEligibility.data(), paintedEligibility.size(),
                    stockEligibility.data()),
                "painted land constrains stock eligibility"))
        return false;
    for (size_t index = 0; index < stockEligibility.size(); ++index) {
        const uint8_t expectedEligibility = paintedEligibility[index]
            ? originalStockEligibility[index] : 0;
        if (!Expect(stockEligibility[index] == expectedEligibility,
                    "eligibility intersection preserves stock exclusions"))
            return false;
    }
    return true;
}

bool TestDiamondAxesAndRecordSpecificOrientation() {
    constexpr int kSize = 5;
    std::vector<uint8_t> playable(kSize * kSize, 0);
    for (int y = 0; y < kSize; ++y)
        for (int x = 0; x < kSize; ++x)
            if (abs(x - 2) + abs(y - 2) <= 2)
                playable[edmw::RowMajorCellIndex(x, y, kSize)] = 1;

    uint32_t sourceX = UINT32_MAX;
    uint32_t sourceY = UINT32_MAX;
    // Moving one playable world cell along the minimap's horizontal (x+y) axis
    // must move along editor X, without leaking into editor Y.
    if (!Expect(edmw::MapWorldCellToSource(
                    2, 1, kSize, kSize, kSize, kSize, playable.data(),
                    sourceX, sourceY),
                "diamond axis probe succeeds") ||
        !Expect(sourceX == 1 && sourceY == 1,
                "diamond projection keeps editor X and Y axes distinct"))
        return false;

    std::vector<uint32_t> sourceIndices;
    std::vector<uint32_t> authoredSourceIndices;
    size_t directWorldIndex = SIZE_MAX;
    size_t authoredWorldIndex = SIZE_MAX;
    size_t playerWorldIndex = SIZE_MAX;
    if (!Expect(edmw::BuildWorldSourceIndexMap(
                    kSize, kSize, kSize, kSize, playable.data(), sourceIndices),
                "diamond inverse-map source succeeds") ||
        !Expect(edmw::BuildAuthoredWorldSourceIndexMap(
                    kSize, kSize, kSize, kSize, playable.data(), authoredSourceIndices),
                "diamond authored inverse-map source succeeds") ||
        !Expect(edmw::FindNearestWorldCellForSourcePoint(
                    sourceIndices, kSize, 1, 1, directWorldIndex),
                "direct resource placement inverse-map succeeds") ||
        !Expect(directWorldIndex == edmw::RowMajorCellIndex(2, 1, kSize),
                "direct resource placement preserves its raw record projection") ||
        !Expect(edmw::FindNearestWorldCellForSourcePoint(
                    authoredSourceIndices, kSize, 1, 3, authoredWorldIndex),
                "direct unit authored inverse-map succeeds") ||
        !Expect(authoredWorldIndex == directWorldIndex,
                "direct units mirror top-to-bottom without changing resource points") ||
        !Expect(edmw::FindNearestWorldCellForAuthoredPoint(
                    sourceIndices, kSize, kSize, 1, 3, playerWorldIndex),
                "player placement authored-point inverse-map succeeds") ||
        !Expect(playerWorldIndex == edmw::RowMajorCellIndex(2, 1, kSize),
                "existing player-start runtime coordinates remain unchanged"))
        return false;

    return true;
}

bool TestFaunaRecordFormatsUseSeparateVerticalTransforms() {
    constexpr int size = 5;
    std::vector<uint8_t> terrain(size * size, 0);
    std::vector<uint8_t> playable(size * size, 1);
    std::vector<uint8_t> liveLand(size * size, 0);
    std::vector<uint8_t> blocked(size * size, 0);
    blocked[edmw::RowMajorCellIndex(1, 2, size)] = 1;
    std::vector<uint8_t> reservations(size * size, 0);
    std::vector<uint32_t> resourceSourceIndices;
    std::vector<uint32_t> directUnitSourceIndices;
    if (!Expect(edmw::BuildWorldSourceIndexMap(
                    size, size, size, size, nullptr, resourceSourceIndices) &&
                edmw::BuildAuthoredWorldSourceIndexMap(
                    size, size, size, size, nullptr, directUnitSourceIndices),
                "separate fauna coordinate maps are available"))
        return false;

    const std::vector<edmw::ManualFaunaRequest> requests = {
        {3, 1, 2, 0, UINT32_MAX},
        {29, 3, 1, 1, 134}
    };
    edmw::FaunaPlannerConstraints constraints;
    constraints.edgeDistance = 0;
    constraints.landShoreDistance = 0;
    constraints.waterShoreDistance = 0;
    constraints.minimumSeparation = 0;
    std::vector<edmw::PlannedFaunaPlacement> placements;
    uint64_t digest = 0;
    return Expect(edmw::PlanManualFaunaCategory(
                      terrain.data(), size, size, size, size,
                      resourceSourceIndices, directUnitSourceIndices,
                      playable.data(), liveLand.data(), blocked.data(), nullptr,
                      edmw::ManualFaunaCategory::Water, constraints, requests,
                      reservations, placements, digest),
                  "mixed Fish and Dolphin category plans") &&
           Expect(placements.size() == 2 &&
                      placements[0].kind == 3 &&
                      placements[0].worldX == 2 && placements[0].worldY == 1 &&
                      placements[1].kind == 29 &&
                      placements[1].worldX == 3 && placements[1].worldY == 3,
                  "Fish gates its transposed consumer cell while Dolphin mirrors top-to-bottom");
}

bool TestResourcePointCoordinatesAreTransposedOnce() {
    float recordX = -1.0f;
    float recordY = -1.0f;
    edmw::EncodeResourcePointCoordinates(19.0f, 7.0f, recordX, recordY);
    if (!Expect(recordX == 7.0f && recordY == 19.0f,
                "resource point records transpose terrain-grid coordinates"))
        return false;

    // A second conversion would restore the original coordinates and reproduce
    // the observed vertical inversion, so callers must encode exactly once.
    float twiceX = -1.0f;
    float twiceY = -1.0f;
    edmw::EncodeResourcePointCoordinates(recordX, recordY, twiceX, twiceY);
    return Expect(twiceX == 19.0f && twiceY == 7.0f,
                  "resource coordinate transpose is intentionally not idempotent");
}

bool TestManualResourceKindMappings() {
    const int32_t generatedKinds[] = {3, 1, 0};
    const uint8_t sourceKinds[] = {1, 4, 5};
    for (size_t index = 0; index < sizeof(sourceKinds) / sizeof(sourceKinds[0]); ++index) {
        if (!Expect(edmw::ManualGeneratedResourceKind(sourceKinds[index]) ==
                        generatedKinds[index],
                    "supported manual resource maps to the correct Default.dll category"))
            return false;
    }

    const uint8_t faunaKinds[] = {
        0, 3, 16, 17, 18, 19, 20, 21, 22,
        23, 24, 25, 26, 27, 28, 29, 30, 31
    };
    for (const uint8_t sourceKind : faunaKinds) {
        if (!Expect(edmw::ManualGeneratedResourceKind(sourceKind) == -1,
                    "fauna bypasses the mineral/berry resource rewrite") ||
            !Expect(edmw::IsFauna(sourceKind),
                    "every compatibility fauna kind is classified as fauna"))
            return false;
    }
    const uint8_t modelAliases[] = {0, 17, 19, 21, 24, 25, 26, 28};
    for (const uint8_t sourceKind : modelAliases)
        if (!Expect(edmw::IsLegacyUnsupportedFauna(sourceKind) &&
                        !edmw::IsUserPlaceable(sourceKind),
                    "legacy model aliases stay readable but inert"))
            return false;
    if (!Expect(!edmw::IsLegacyUnsupportedFauna(3),
                "Fish is not a legacy alias") ||
        !Expect(edmw::IsWaterFauna(3) && edmw::IsWaterFauna(29) &&
                    !edmw::IsWaterFauna(21) && !edmw::IsWaterFauna(20),
                "Fish and Dolphin are the supported water category") ||
        !Expect(edmw::IsLandFauna(16) && edmw::IsLandFauna(31) &&
                    !edmw::IsLandFauna(24) && !edmw::IsLandFauna(17) &&
                    !edmw::IsLandFauna(21) && !edmw::IsLandFauna(29),
                "only distinct-model named land fauna are in the land category") ||
        !Expect(edmw::FaunaCategory(3) == edmw::ManualFaunaCategory::Water &&
                    edmw::FaunaCategory(16) == edmw::ManualFaunaCategory::Land &&
                    edmw::FaunaCategory(0) == edmw::ManualFaunaCategory::None,
                "fauna category ownership is independent and explicit") ||
        !Expect(edmw::IsUserPlaceable(1) && edmw::IsUserPlaceable(4) &&
                    edmw::IsUserPlaceable(5) && edmw::IsUserPlaceable(3) &&
                    edmw::IsUserPlaceable(16) && edmw::IsUserPlaceable(31) &&
                    !edmw::IsUserPlaceable(0),
                "Fish and distinct-model fauna are placeable while aliases stay inert") ||
        !Expect(!edmw::IsManualFaunaPlacementEnabled(0x00000007u) &&
                    edmw::IsManualFaunaPlacementEnabled(0x0000000Fu),
                "the fauna capability bit is separate from existing placement bits"))
        return false;

    static const char* const expectedTokens[] = {
        "Res-Deer", "Res-Goat", "Res-BighornSheep", "Res-Tiger",
        "Res-Bear", "Res-Shark", "Res-Squirrel", "Res-Sheep",
        "Res-Eagle", "Res-Walrus", "Res-Wolf", "Res-Chicken",
        "Res-Horse", "Res-Dolphin", "Res-WildPig", "Res-Elk"
    };
    static const uint32_t expectedDescriptorIndices[] = {
        59, 66, 67, 70, 72, 76, 79, 80,
        81, 83, 92, 93, 94, 134, 510, 611
    };
    static const uint32_t expectedGlobalUnitIds[] = {
        261, 291, 292, 295, 301, 314, 325, 326,
        327, 349, 370, 372, 373, 661, 1501, 1618
    };
    for (uint8_t sourceKind = 16; sourceKind <= 31; ++sourceKind) {
        const char* actual = edmw::NamedFaunaToken(sourceKind);
        if (edmw::IsLegacyUnsupportedFauna(sourceKind)) {
            if (!Expect(actual == nullptr &&
                            edmw::NamedFaunaDescriptorIndex(sourceKind) == UINT32_MAX &&
                            edmw::NamedFaunaGlobalUnitId(sourceKind) == UINT32_MAX,
                        "model aliases have no active native descriptor binding"))
                return false;
        } else if (!Expect(actual != nullptr &&
                               strcmp(actual, expectedTokens[sourceKind - 16]) == 0 &&
                               edmw::NamedFaunaDescriptorIndex(sourceKind) ==
                                   expectedDescriptorIndices[sourceKind - 16] &&
                               edmw::NamedFaunaGlobalUnitId(sourceKind) ==
                                   expectedGlobalUnitIds[sourceKind - 16],
                           "supported fauna maps to its exact database identity")) {
            return false;
        }
    }
    return Expect(edmw::NamedFaunaToken(3) == nullptr &&
                      edmw::NamedFaunaDescriptorIndex(3) == UINT32_MAX &&
                      edmw::NamedFaunaGlobalUnitId(3) == UINT32_MAX &&
                      edmw::ManualGeneratedResourceKind(2) == -1 &&
                      edmw::ManualGeneratedResourceKind(32) == -1,
                  "non-named and unknown manual resource kinds are rejected");
}

bool TestTransactionalFaunaPlanner() {
    constexpr int size = 10;
    std::vector<uint8_t> terrain(size * size, 0);
    for (int y = 4; y < size; ++y)
        for (int x = 0; x < size; ++x)
            terrain[edmw::RowMajorCellIndex(x, y, size)] = 3;
    std::vector<uint8_t> playable(size * size, 1);
    std::vector<uint8_t> liveLand(size * size, 0);
    for (size_t index = 0; index < terrain.size(); ++index)
        liveLand[index] = terrain[index] > 1 ? 1 : 0;
    std::vector<uint16_t> shore;
    std::vector<uint32_t> sourceIndices;
    if (!Expect(edmw::BuildWorldSourceIndexMap(
                    size, size, size, size, nullptr, sourceIndices) &&
                edmw::BuildCoastalDistanceMap(
                    liveLand.data(), size, size, shore),
                "fauna planner topology is available"))
        return false;

    std::vector<uint8_t> blocked(size * size, 0);
    blocked[edmw::RowMajorCellIndex(1, 1, size)] = 1;
    std::vector<uint8_t> initialReservations(size * size, 0);
    const std::vector<edmw::ManualFaunaRequest> waterRequests = {
        {29, 8, 2, 0, 303},
        {29, 5, 1, 1, 202},
        {3, 1, 1, 2, UINT32_MAX}
    };
    edmw::FaunaPlannerConstraints constraints;
    constraints.edgeDistance = 1;
    constraints.landShoreDistance = 1;
    constraints.waterShoreDistance = 1;
    constraints.minimumSeparation = 2;

    std::vector<uint8_t> diamond(size * size, 0);
    for (int y = 1; y < size - 1; ++y)
        for (int x = 1; x < size - 1; ++x)
            if (x + y >= 5 && x + y <= 13 && x - y >= -5 && x - y <= 5)
                diamond[edmw::RowMajorCellIndex(x, y, size)] = 1;
    std::vector<uint16_t> diamondEdge;
    std::vector<uint16_t> diamondShore;
    if (!Expect(edmw::BuildPlayableEdgeDistanceMap(
                    diamond.data(), size, size, diamondEdge) &&
                diamondEdge[edmw::RowMajorCellIndex(4, 1, size)] == 0 &&
                diamondEdge[edmw::RowMajorCellIndex(5, 4, size)] > 0 &&
                diamondEdge[edmw::RowMajorCellIndex(1, 1, size)] == UINT16_MAX &&
                edmw::BuildCoastalDistanceMap(liveLand.data(), size, size,
                    diamondShore, diamond.data()) &&
                diamondShore[edmw::RowMajorCellIndex(1, 1, size)] == UINT16_MAX,
                "edge and coast distances respect the playable diamond"))
        return false;

    std::vector<uint8_t> firstReservations = initialReservations;
    std::vector<edmw::PlannedFaunaPlacement> first;
    uint64_t firstDigest = 0;
    if (!Expect(edmw::PlanManualFaunaCategory(
                    terrain.data(), size, size, size, size, sourceIndices, sourceIndices,
                    playable.data(), liveLand.data(), blocked.data(), shore.data(),
                    edmw::ManualFaunaCategory::Water, constraints, waterRequests,
                    firstReservations, first, firstDigest),
                "complete water fauna category plans") ||
        !Expect(first.size() == 3 && first[0].kind == 3 && first[1].kind == 29 &&
                    first[2].kind == 29,
                "water requests use the stable category/kind ordering") ||
        !Expect(first[0].worldX == 2 && first[0].worldY == 1 &&
                    first[1].worldX == 5 && first[1].worldY == 1 &&
                    first[2].worldX == 8 && first[2].worldY == 2,
                "blocked exact cell snaps by distance then Y/X while valid exact cells stay exact") ||
        !Expect(first[1].unitDescriptorIndexRaw == 202 && firstDigest != 0,
                "parser-resolved descriptor identity contributes to the peer digest"))
        return false;

    auto reversed = waterRequests;
    std::reverse(reversed.begin(), reversed.end());
    for (int repetition = 0; repetition < 100; ++repetition) {
        std::vector<uint8_t> repeatedReservations = initialReservations;
        std::vector<edmw::PlannedFaunaPlacement> repeated;
        uint64_t repeatedDigest = 0;
        if (!Expect(edmw::PlanManualFaunaCategory(
                        terrain.data(), size, size, size, size, sourceIndices, sourceIndices,
                        playable.data(), liveLand.data(), blocked.data(), shore.data(),
                        edmw::ManualFaunaCategory::Water, constraints, reversed,
                        repeatedReservations, repeated, repeatedDigest) &&
                    repeated.size() == first.size() &&
                    memcmp(repeated.data(), first.data(),
                           first.size() * sizeof(first[0])) == 0 &&
                    repeatedDigest == firstDigest &&
                    repeatedReservations == firstReservations,
                    "100 reordered identical peers produce byte-identical fauna plans"))
            return false;
    }

    const std::vector<edmw::ManualFaunaRequest> landRequests = {
        {16, 5, 2, 0, 404}
    };
    std::vector<edmw::PlannedFaunaPlacement> land;
    uint64_t landDigest = 0;
    if (!Expect(edmw::PlanManualFaunaCategory(
                    terrain.data(), size, size, size, size, sourceIndices, sourceIndices,
                    playable.data(), liveLand.data(), blocked.data(), shore.data(),
                    edmw::ManualFaunaCategory::Land, constraints, landRequests,
                    firstReservations, land, landDigest) &&
                land.size() == 1 && land[0].worldX == 5 && land[0].worldY == 4,
                "land fauna snaps off water onto the nearest live land cell"))
        return false;

    std::vector<uint8_t> noLand = blocked;
    for (int y = 4; y < size; ++y)
        for (int x = 0; x < size; ++x)
            noLand[edmw::RowMajorCellIndex(x, y, size)] = 1;
    const std::vector<uint8_t> reservationsBeforeFailure = firstReservations;
    std::vector<edmw::PlannedFaunaPlacement> failed = land;
    uint64_t failedDigest = 0;
    return Expect(!edmw::PlanManualFaunaCategory(
                      terrain.data(), size, size, size, size, sourceIndices, sourceIndices,
                      playable.data(), liveLand.data(), noLand.data(), shore.data(),
                      edmw::ManualFaunaCategory::Land, constraints, landRequests,
                      firstReservations, failed, failedDigest) &&
                  failed.empty() && firstReservations == reservationsBeforeFailure,
                  "failed category rolls back placements and reservations completely");
}

bool TestManualFaunaRuntimeCellGates() {
    constexpr int size = 8;
    constexpr float threshold = 10.0f / 22.0f;
    constexpr float animalCeiling = 12.0f / 22.0f;
    constexpr float variation = 1.0f / 22.0f;
    const size_t count = static_cast<size_t>(size) * size;
    std::vector<float> heights(count, 7.0f / 22.0f);
    std::vector<uint8_t> liveLand(count, 0);
    std::vector<uint8_t> playable(count, 1);
    std::vector<uint8_t> reservations(count, 0);
    std::vector<uint8_t> eligibility(count, 1);

    if (!Expect(edmw::IsManualFishCellEligible(
                    heights.data(), liveLand.data(), playable.data(),
                    reservations.data(), size, size, 3, 3,
                    threshold, variation),
                "Fish accepts a clear deep-water 4x4 footprint") ||
        !Expect(!edmw::IsManualFishCellEligible(
                    heights.data(), liveLand.data(), playable.data(),
                    reservations.data(), size, size, 0, 0,
                    threshold, variation),
                "Fish rejects the playable edge"))
        return false;
    reservations[edmw::RowMajorCellIndex(3, 3, size)] = 1;
    if (!Expect(!edmw::IsManualFishCellEligible(
                    heights.data(), liveLand.data(), playable.data(),
                    reservations.data(), size, size, 3, 3,
                    threshold, variation),
                "Fish respects the real +0x118 resource reservation grid"))
        return false;
    reservations.assign(count, 0);
    heights[edmw::RowMajorCellIndex(3, 3, size)] = threshold - variation;
    if (!Expect(!edmw::IsManualFishCellEligible(
                    heights.data(), liveLand.data(), playable.data(),
                    reservations.data(), size, size, 3, 3,
                    threshold, variation),
                "Fish rejects shallow water without consulting +0x1F0 cache bits"))
        return false;

    heights.assign(count, threshold + variation * 0.25f);
    liveLand.assign(count, 1);
    if (!Expect(edmw::IsManualLandFaunaCellEligible(
                    heights.data(), liveLand.data(), eligibility.data(),
                    reservations.data(), size, size, 3, 3, threshold,
                    animalCeiling, variation),
                "land fauna accepts a clear eligible low-slope footprint"))
        return false;
    heights[edmw::RowMajorCellIndex(3, 3, size)] = animalCeiling + 0.01f;
    if (!Expect(!edmw::IsManualLandFaunaCellEligible(
                    heights.data(), liveLand.data(), eligibility.data(),
                    reservations.data(), size, size, 3, 3, threshold,
                    animalCeiling, variation),
                "land fauna rejects elevation above the Animal ceiling"))
        return false;
    heights.assign(count, threshold + variation * 0.25f);
    eligibility[edmw::RowMajorCellIndex(4, 4, size)] = 0;
    return Expect(!edmw::IsManualLandFaunaCellEligible(
                      heights.data(), liveLand.data(), eligibility.data(),
                      reservations.data(), size, size, 3, 3, threshold,
                      animalCeiling, variation),
                  "land fauna requires the stock herd 2x2 eligibility footprint");
}

bool TestFinalWorldFaunaCellGuardUsesNoGeneratorHeightThreshold() {
    return Expect(edmw::IsFinalWorldFaunaCellUsable(16, 0, 24566.0f),
                  "final land world height is accepted without generator-scale thresholds") &&
           Expect(edmw::IsFinalWorldFaunaCellUsable(16, 1, 24566.0f),
                  "land consumer does not impose a positive final cell-kind gate") &&
           Expect(edmw::IsFinalWorldFaunaCellUsable(29, 1, -4.08f),
                  "named water unit accepts the exact host water-cell kind") &&
           Expect(!edmw::IsFinalWorldFaunaCellUsable(29, 10, -4.08f),
                  "named water unit rejects a kind its host consumer rejects") &&
           Expect(!edmw::IsFinalWorldFaunaCellUsable(21, 1, -4.08f),
                  "legacy Shark alias has no active final-cell policy") &&
           Expect(edmw::IsFinalWorldFaunaCellUsable(3, 1, -4.08f) &&
                  edmw::IsFinalWorldFaunaCellUsable(3, 10, -2.83f),
                  "Fish accepts both exact host water-cell kinds") &&
           Expect(!edmw::IsFinalWorldFaunaCellUsable(3, 0, -4.08f),
                  "Fish rejects a final kind its host consumer rejects") &&
           Expect(!edmw::IsFinalWorldFaunaCellUsable(16, 9, 0.0f),
                  "invalid final world-cell kind is rejected") &&
           Expect(!edmw::IsFinalWorldFaunaCellUsable(
                      16, 0, std::numeric_limits<float>::quiet_NaN()),
                  "non-finite final world height is rejected") &&
           Expect(!edmw::IsFinalWorldFaunaCellUsable(6, 0, 0.0f),
                  "legacy generic Wildlife has no final-cell policy");
}

bool TestDeterminismAndValidation() {
    constexpr int kSize = 8;
    std::vector<uint8_t> terrain(kSize * kSize, 0);
    for (int y = 0; y < kSize; ++y)
        for (int x = 0; x < kSize; ++x)
            terrain[static_cast<size_t>(y) * kSize + x] =
                ((x * 3 + y * 5) % 7) < 3 ? 2 : 0;

    std::vector<uint8_t> first(kSize * kSize, 0xAA);
    std::vector<uint8_t> second(kSize * kSize, 0x55);
    if (!Expect(edmw::BuildAuthoritativeLandMask(
                    terrain.data(), kSize, kSize, kSize, kSize, first.data()),
                "first deterministic projection succeeds") ||
        !Expect(edmw::BuildAuthoritativeLandMask(
                    terrain.data(), kSize, kSize, kSize, kSize, second.data()),
                "second deterministic projection succeeds"))
        return false;
    if (!Expect(first == second, "identical topology inputs produce identical masks"))
        return false;
    return Expect(!edmw::BuildAuthoritativeLandMask(
                      nullptr, kSize, kSize, kSize, kSize, first.data()),
                  "invalid topology input is rejected");
}

bool TestDeterministicInvalidNeutralRepair() {
    constexpr int kSize = 16;
    std::vector<uint8_t> terrain(kSize * kSize, 0);
    std::vector<uint8_t> roles(kSize * kSize, 0);
    std::vector<uint8_t> playable(kSize * kSize, 1);
    for (int y = 3; y <= 12; ++y)
        for (int x = 3; x <= 12; ++x)
            terrain[edmw::RowMajorCellIndex(x, y, kSize)] = 3;

    const std::vector<edmw::ResourcePosition> fixed = {{5.0f, 5.0f, 1}};
    std::vector<edmw::ResourcePosition> first = {{0.0f, 0.0f, 2}};
    std::vector<edmw::ResourcePosition> second = first;
    const auto firstSummary = edmw::AlignResourcePositionsAdaptive(
        terrain.data(), roles.data(), kSize, kSize, kSize, kSize,
        edmw::AlignmentTarget::Neutral, 3.0f, 2.0f, fixed, first,
        playable.data());
    const auto secondSummary = edmw::AlignResourcePositionsAdaptive(
        terrain.data(), roles.data(), kSize, kSize, kSize, kSize,
        edmw::AlignmentTarget::Neutral, 3.0f, 2.0f, fixed, second,
        playable.data());
    if (!Expect(firstSummary.aligned == 1 && secondSummary.aligned == 1,
                "invalid neutral center is repairable") ||
        !Expect(first[0].x == second[0].x && first[0].y == second[0].y,
                "neutral repair is deterministic") ||
        !Expect(edmw::PositionMapsToPaintedLand(
                    terrain.data(), kSize, kSize, kSize, kSize,
                    first[0], playable.data()),
                "repaired neutral center lands on painted land"))
        return false;
    for (int peer = 0; peer < 100; ++peer) {
        std::vector<edmw::ResourcePosition> peerPositions = {{0.0f, 0.0f, 2}};
        const auto peerSummary = edmw::AlignResourcePositionsAdaptive(
            terrain.data(), roles.data(), kSize, kSize, kSize, kSize,
            edmw::AlignmentTarget::Neutral, 3.0f, 2.0f, fixed,
            peerPositions, playable.data());
        if (!Expect(peerSummary.aligned == 1 &&
                    peerPositions[0].x == first[0].x &&
                    peerPositions[0].y == first[0].y,
                    "identical peers produce identical neutral repair"))
            return false;
    }
    return Expect(fixed[0].x == 5.0f && fixed[0].y == 5.0f,
                  "neutral repair does not mutate fixed player starts");
}

bool TestPaintedCliffHeightsAndRamps() {
    constexpr int size = 100;
    constexpr float threshold = 10.0f / 22.0f;
    constexpr float step = 1.0f / 22.0f;
    constexpr float plateau = threshold + 8.0f * step;
    const size_t count = size * size;
    const auto at = [=](int x, int y) { return edmw::RowMajorCellIndex(x, y, size); };
    std::vector<uint8_t> land(count, 0), terrain(count, 0), ramps(count, 0);
    std::vector<int8_t> elevation(count, 127);
    std::vector<float> heights(count, threshold - step);
    for (int y = 3; y < size - 3; ++y) for (int x = 3; x < size - 3; ++x) {
        const size_t i = at(x, y);
        land[i] = 1;
        terrain[i] = x == 3 || y == 3 || x == size - 4 || y == size - 4 ? 2 : 3;
        heights[i] = terrain[i] == 2 ? threshold - step : threshold + step;
        if (x >= 20 && x <= 80 && y >= 20 && y <= 80) {
            terrain[i] = 6;
            elevation[i] = 126;
            heights[i] = threshold + step * 4.0f;
        }
    }
    // Legacy Mountain100 and ordinary Mountain/Auto must retain their old heights.
    terrain[at(10, 10)] = terrain[at(11, 10)] = 6;
    elevation[at(10, 10)] = 100;
    heights[at(10, 10)] = heights[at(11, 10)] = threshold + step * 4.0f;
    std::vector<uint16_t> coast;
    edmw::HeightGradientSummary gradient;
    edmw::AccessRampSummary access;
    if (!Expect(edmw::BuildCoastalDistanceMap(land.data(), size, size, coast) &&
            edmw::ConstrainCoastalHeightGradient(land.data(), coast.data(), size, size,
                threshold, step * 2.0f, heights.data(), gradient) &&
            edmw::LimitLandHeightGradient(land.data(), size, size, 0.16f,
                heights.data(), gradient) &&
            edmw::CarveElevatedLandAccessCorridors(land.data(), terrain.data(), elevation.data(),
                ramps.data(), size, size, 0.04f, 0.16f, 3, heights.data(), access),
            "ordinary topology pipeline succeeds before cliff restoration")) return false;
    for (size_t i = 0; i < count; ++i)
        if (coast[i] == 1 && terrain[i] <= 2) heights[i] = threshold - step;
    const std::vector<float> before = heights;
    edmw::CliffHeightSummary summary;
    const auto restore = [&]() {
        return edmw::RestorePaintedCliffHeights(land.data(), terrain.data(), elevation.data(),
            ramps.data(), size, size, threshold, step, 0.04f, 2, heights.data(), summary);
    };
    if (!Expect(restore(), "cliffs restore after coastal, slope and automatic ramp smoothing") ||
        !Expect(heights[at(50, 50)] == plateau && summary.steepBoundaryEdges > 0,
            "cliff plateau has steep boundary edges") ||
        !Expect(heights[at(20, 50)] - heights[at(19, 50)] > 0.25f,
            "final cliff boundary exceeds the former traversable 0.16 limit")) return false;
    for (size_t i = 0; i < count; ++i)
        if (elevation[i] != 126 && !Expect(heights[i] == before[i],
            "restoration preserves all ordinary terrain, water, beach, Auto and legacy100")) return false;
    const auto restored = heights;
    if (!Expect(restore() && heights == restored, "unramped cliff restoration is idempotent")) return false;
    // Model a stock 3x3 blur: the post-stock pass must restore a real edge jump,
    // not merely report that plateau cells have a particular absolute height.
    for (int y = 1; y < size - 1; ++y) for (int x = 1; x < size - 1; ++x) {
        float sum = 0.0f;
        for (int dy = -1; dy <= 1; ++dy) for (int dx = -1; dx <= 1; ++dx)
            sum += restored[at(x + dx, y + dy)];
        heights[at(x, y)] = sum / 9.0f;
    }
    const auto filtered = heights;
    if (!Expect(restore() && heights[at(20, 50)] - heights[at(19, 50)] > 0.16f,
            "late restoration preserves a steep edge after a stock-style height blur")) return false;
    for (size_t i = 0; i < count; ++i)
        if (elevation[i] != 126 && !Expect(heights[i] == filtered[i],
            "post-stock restore preserves the stock result outside authored cliffs")) return false;

    heights = before;
    for (int x = 18; x <= 25; ++x) ramps[at(x, 50)] = 1;
    if (!Expect(restore() && summary.rampComponents == 1 && summary.rampCells > 0,
            "a painted ramp becomes a connected corridor") ||
        !Expect(heights[at(20, 50)] < plateau && summary.steepBoundaryEdges > 0,
            "manual ramp cuts its entry while retaining other cliff faces")) return false;
    std::vector<uint8_t> visited(count, 0);
    std::queue<size_t> pending;
    pending.push(at(10, 50)); visited[at(10, 50)] = 1;
    bool reachedPlateau = false;
    while (!pending.empty()) {
        const size_t current = pending.front(); pending.pop();
        reachedPlateau = reachedPlateau || (elevation[current] == 126 && heights[current] == plateau);
        const int x = static_cast<int>(current % size), y = static_cast<int>(current / size);
        for (int dy = -1; dy <= 1; ++dy) for (int dx = -1; dx <= 1; ++dx) {
            const int nx = x + dx, ny = y + dy;
            if (nx < 0 || ny < 0 || nx >= size || ny >= size) continue;
            const size_t next = at(nx, ny);
            if (!land[next] || visited[next] ||
                std::fabs(heights[next] - heights[current]) > 0.04001f) continue;
            visited[next] = 1; pending.push(next);
        }
    }
    if (!Expect(reachedPlateau, "manual ramp connects ordinary land to full-height plateau without a low hole"))
        return false;
    const auto ramped = heights;
    if (!Expect(restore() && heights == ramped, "manual corridor restoration is idempotent") ||
        !Expect(heights[at(10, 10)] == before[at(10, 10)] &&
            heights[at(80, 50)] == plateau && heights[at(3, 50)] == before[at(3, 50)],
            "manual corridor preserves distant terrain, beach and cliff faces")) return false;

    std::fill(elevation.begin(), elevation.end(), static_cast<int8_t>(127));
    elevation[at(10, 10)] = 100;
    const auto ordinary = heights;
    if (!Expect(restore() && heights == ordinary && summary.cliffCells == 0,
            "maps without126 including legacy100 are exact no-ops")) return false;
    elevation[at(10, 11)] = 126;
    return Expect(!restore() && heights == ordinary,
        "invalid126 on non-Mountain is rejected without mutation");
}

bool TestPaintedRampCrossesNarrowCliffRidge() {
    constexpr int width = 40, height = 30;
    constexpr float threshold = 10.0f / 22.0f, step = 1.0f / 22.0f;
    std::vector<uint8_t> terrain(width * height, 3), land(width * height, 1), ramps(width * height, 0);
    std::vector<int8_t> elevation(width * height, 127);
    std::vector<float> heights(width * height, threshold + step);
    for (int y = 0; y < height; ++y) for (int x = 18; x <= 22; ++x) {
        const size_t i = edmw::RowMajorCellIndex(x, y, width);
        terrain[i] = 6; elevation[i] = 126;
    }
    ramps[edmw::RowMajorCellIndex(20, 15, width)] = 1;
    edmw::CliffHeightSummary summary;
    if (!Expect(edmw::RestorePaintedCliffHeights(land.data(), terrain.data(), elevation.data(),
            ramps.data(), width, height, threshold, step, 0.04f, 2, heights.data(), summary) &&
            summary.rampComponents == 1 && summary.steepBoundaryEdges > 0,
            "a narrow cliff ridge receives a manual passage while retaining its other faces")) return false;
    std::vector<uint8_t> visited(width * height, 0);
    std::queue<size_t> pending;
    pending.push(edmw::RowMajorCellIndex(10, 15, width));
    visited[pending.front()] = 1;
    bool crossed = false;
    while (!pending.empty()) {
        const size_t i = pending.front(); pending.pop();
        const int x = static_cast<int>(i % width), y = static_cast<int>(i / width);
        crossed = crossed || x == 30;
        for (int dy = -1; dy <= 1; ++dy) for (int dx = -1; dx <= 1; ++dx) {
            const int nx = x + dx, ny = y + dy;
            if (nx < 0 || nx >= width || ny < 5 || ny >= height - 5) continue;
            const size_t next = edmw::RowMajorCellIndex(nx, ny, width);
            if (visited[next] || std::fabs(heights[next] - heights[i]) > 0.04001f) continue;
            visited[next] = 1; pending.push(next);
        }
    }
    const auto beforeRepeat = heights;
    return Expect(crossed, "manual ramp crosses a thin ridge instead of ending in a disconnected groove") &&
        Expect(edmw::RestorePaintedCliffHeights(land.data(), terrain.data(), elevation.data(),
            ramps.data(), width, height, threshold, step, 0.04f, 2, heights.data(), summary) &&
            heights == beforeRepeat, "narrow ridge passage restoration is stable");
}

bool TestPaintedCliffCoastValidationAndEngineOrientation() {
    for (uint8_t terrain = 0; terrain <= 7; ++terrain) {
        if (!Expect(edmw::IsValidPaintedElevation(terrain, 126) == (terrain == 6),
                "126 is valid only on Mountain") ||
            !Expect(edmw::IsValidPaintedElevation(terrain, 127) &&
                edmw::IsValidPaintedElevation(terrain, 100) &&
                !edmw::IsValidPaintedElevation(terrain, 125),
                "Auto and numeric elevations retain their payload contract")) return false;
    }
    constexpr int stride = 10, active = 8;
    constexpr float threshold = 10.0f / 22.0f, step = 1.0f / 22.0f;
    std::vector<uint8_t> terrain(stride * stride, 0), land(stride * stride, 0), ramps(stride * stride, 0);
    std::vector<int8_t> elevation(stride * stride, 127);
    std::vector<float> heights(stride * stride);
    for (int y = 0; y < stride; ++y) for (int x = 0; x < stride; ++x)
        heights[edmw::RowMajorCellIndex(x, y, stride)] = static_cast<float>(y * stride + x) * 0.001f;
    const size_t logicalCliff = edmw::RowMajorCellIndex(2, 5, stride);
    terrain[logicalCliff] = 6; land[logicalCliff] = 1; elevation[logicalCliff] = 126;
    ramps[logicalCliff] = 1; // An isolated cliff island has no reachable land ramp endpoint.
    const auto coastBefore = terrain;
    edmw::GuaranteeBeachCoastline(terrain.data(), stride, stride, 0.60, elevation.data());
    if (!Expect(terrain == coastBefore, "beach promotion preserves reserved coastal cliffs")) return false;
    if (!Expect(edmw::EncodeHeightGridForEngine(heights.data(), stride, stride, active, active),
            "asymmetric padded height fixture encodes for engine")) return false;
    const auto encodedBefore = heights;
    edmw::CliffHeightSummary summary;
    if (!Expect(edmw::RestorePaintedCliffHeightGrid(land.data(), terrain.data(), elevation.data(),
            ramps.data(), stride, stride, active, active, true, threshold, step, 0.04f, 2,
            heights.data(), summary), "post-stock adapter restores in engine layout") ||
        !Expect(summary.unconnectedRamps == 1 && summary.rampCells == 0,
            "isolated coastal ramp cannot create an unconnected low hole")) return false;
    const size_t engineCliff = edmw::RowMajorCellIndex(5, 2, stride);
    for (size_t i = 0; i < heights.size(); ++i) {
        const float expected = i == engineCliff ? threshold + step * 8.0f : encodedBefore[i];
        if (!Expect(heights[i] == expected,
                "only correctly transposed cliff changes; logical mirror, water and padding remain exact")) return false;
    }
    const auto restored = heights;
    return Expect(edmw::RestorePaintedCliffHeightGrid(land.data(), terrain.data(), elevation.data(),
            ramps.data(), stride, stride, active, active, true, threshold, step, 0.04f, 2,
            heights.data(), summary) && heights == restored,
        "repeated engine-layout restoration never transposes the buffer again");
}

bool TestAuthoredMixedCoastRestoration() {
    constexpr int stride = 12, active = 10;
    constexpr float threshold = 10.0f / 22.0f, step = 1.0f / 22.0f;
    const auto at = [=](int x, int y) { return edmw::RowMajorCellIndex(x, y, stride); };
    std::vector<uint8_t> terrain(stride * stride, 0), land(stride * stride, 0);
    std::vector<uint8_t> playable(stride * stride, 0), ramps(stride * stride, 0);
    std::vector<int8_t> elevation(stride * stride, 127);
    std::vector<float> heights(stride * stride);
    for (int y = 0; y < stride; ++y) for (int x = 0; x < stride; ++x) {
        const size_t i = at(x, y);
        heights[i] = 0.2f + static_cast<float>(i) * 0.0001f;
        if (x >= active || y >= active) continue;
        playable[i] = 1;
        if (y >= 4) { land[i] = 1; terrain[i] = 3; }
    }
    terrain[at(2, 4)] = 4; // Forest directly on the coast.
    terrain[at(4, 4)] = 5;
    terrain[at(5, 4)] = 2;
    terrain[at(5, 3)] = 1;
    terrain[at(6, 4)] = 6; elevation[at(6, 4)] = 126;
    terrain[at(7, 4)] = 6;
    terrain[at(8, 4)] = 7;
    elevation[at(9, 4)] = 100;
    elevation[at(1, 3)] = -75;
    ramps[at(0, 4)] = 1;
    const auto original = heights;
    const auto originalTerrain = terrain;
    edmw::CoastHeightSummary coast;
    const auto restore = [&](uint32_t flags, bool engine) {
        return edmw::RestoreAuthoredCoastHeightGrid(flags, land.data(), terrain.data(),
            elevation.data(), ramps.data(), playable.data(), stride, stride, active, active,
            engine, threshold, step, heights.data(), coast);
    };
    if (!Expect(!edmw::PreservesAuthoredCoastline(0x0Fu) &&
            edmw::PreservesAuthoredCoastline(0x1Fu) && restore(0x0Fu, false) && heights == original,
            "existing payload flags preserve legacy heights exactly")) return false;
    if (!Expect(restore(edmw::kPreserveCoastlineFlag, false),
            "flagged mixed coast restores without requiring cliff markers")) return false;
    const auto near = [](float a, float b) { return std::fabs(a - b) < 0.00001f; };
    if (!Expect(near(heights[at(2, 4)], threshold + step) &&
            near(heights[at(3, 4)], threshold + step) &&
            near(heights[at(4, 4)], threshold + 2 * step) &&
            near(heights[at(7, 4)], threshold + 4 * step) &&
            near(heights[at(8, 4)], threshold + step) && terrain == originalTerrain,
            "forest grass hill mountain and desert shores retain authored type heights") ||
        !Expect(near(heights[at(2, 3)], threshold - 5 * step) &&
            near(heights[at(5, 3)], threshold - step) &&
            near(heights[at(5, 4)], threshold - step),
            "deep water meets land directly while authored beach and shallows retain level minus one") ||
        !Expect(near(heights[at(9, 4)], threshold + 4 * step) &&
            near(heights[at(1, 3)], threshold - 3 * step),
            "later numeric land and water elevation edits override automatic shore profiles") ||
        !Expect(heights[at(0, 4)] == original[at(0, 4)] &&
            heights[at(3, 7)] == original[at(3, 7)] &&
            heights[at(2, 0)] == original[at(2, 0)] &&
            heights[at(11, 11)] == original[at(11, 11)],
            "painted ramp cells unmarked interiors distant water and padding remain untouched")) return false;
    edmw::CliffHeightSummary cliffs;
    const auto restoreCliffs = [&](bool engine) {
        return edmw::RestorePaintedCliffHeightGrid(land.data(), terrain.data(), elevation.data(),
            ramps.data(), stride, stride, active, active, engine, threshold, step, 0.04f, 2,
            heights.data(), cliffs);
    };
    if (!Expect(restoreCliffs(false) && near(heights[at(6, 4)], threshold + 8 * step),
            "coastal cliffs restore last above the preserved deep water")) return false;
    auto expectedEncoded = heights;
    edmw::EncodeHeightGridForEngine(expectedEncoded.data(), stride, stride, active, active);
    heights = original;
    edmw::EncodeHeightGridForEngine(heights.data(), stride, stride, active, active);
    if (!Expect(restore(edmw::kPreserveCoastlineFlag, true) && restoreCliffs(true) &&
            heights == expectedEncoded,
            "post-stock shoreline and cliff adapters target the same asymmetric engine cells")) return false;
    for (int stage = 0; stage < 3; ++stage) {
        // Stand in for stock filter, ramps, and final river-fill mutations.
        heights[at(3, 2)] = threshold;
        heights[at(4, 2)] = threshold;
        heights[at(4, 6)] = threshold;
        if (!Expect(restore(edmw::kPreserveCoastlineFlag, true) && restoreCliffs(true) &&
                heights == expectedEncoded,
                "each late stage restores shores and cliffs without a second transpose")) return false;
    }
    elevation[at(6, 4)] = 127;
    heights = original;
    return Expect(restore(edmw::kPreserveCoastlineFlag, false) &&
        near(heights[at(2, 4)], threshold + step),
        "coastline-only maps restore normally without any cliff marker");
}

bool TestAuthoredCoastPlayableEdgesAndCorners() {
    constexpr int sourceSize = 5, active = 9, stride = 11;
    constexpr float threshold = 10.0f / 22.0f, step = 1.0f / 22.0f;
    std::vector<uint8_t> source(sourceSize * sourceSize, 3), playable(active * active, 0);
    source[0] = 4; source[sourceSize - 1] = 5;
    source[(sourceSize - 1) * sourceSize] = 7;
    source[sourceSize * sourceSize - 1] = 6;
    for (int y = 0; y < active; ++y) for (int x = 0; x < active; ++x)
        if (abs(x - 4) + abs(y - 4) <= 4) playable[edmw::RowMajorCellIndex(x, y, active)] = 1;
    std::vector<uint32_t> mapping;
    if (!Expect(edmw::BuildAuthoredWorldSourceIndexMap(sourceSize, sourceSize, active, active,
            playable.data(), mapping), "authored full-edge source mapping succeeds")) return false;
    std::vector<uint8_t> land(stride * stride, 0), terrain(stride * stride, 0);
    std::vector<uint8_t> paddedPlayable(stride * stride, 0), ramps(stride * stride, 0);
    std::vector<int8_t> elevation(stride * stride, 127);
    std::vector<float> heights(stride * stride, 0.123f);
    const auto at = [=](int x, int y) { return edmw::RowMajorCellIndex(x, y, stride); };
    for (int y = 0; y < active; ++y) for (int x = 0; x < active; ++x) {
        const uint32_t src = mapping[edmw::RowMajorCellIndex(x, y, active)];
        if (src == UINT32_MAX) continue;
        land[at(x, y)] = paddedPlayable[at(x, y)] = 1;
        terrain[at(x, y)] = source[src];
    }
    std::vector<uint16_t> coast;
    if (!Expect(edmw::BuildCoastalDistanceMap(land.data(), stride, stride, coast,
            paddedPlayable.data()) &&
            std::all_of(coast.begin(), coast.end(), [](uint16_t d) { return d == UINT16_MAX; }),
            "edge and corner land sees no invented shoreline against nonplayable padding")) return false;
    edmw::CoastHeightSummary summary;
    if (!Expect(edmw::RestoreAuthoredCoastHeightGrid(edmw::kPreserveCoastlineFlag,
            land.data(), terrain.data(), elevation.data(), ramps.data(), paddedPlayable.data(),
            stride, stride, active, active, false, threshold, step, heights.data(), summary) &&
            summary.shorelineCells == 0 && summary.boundaryLandCells > 0,
            "boundary land restores independently of genuine shoreline detection")) return false;
    return Expect(terrain[at(0, 4)] == 4 && heights[at(0, 4)] == threshold + step &&
        terrain[at(4, 8)] == 5 && heights[at(4, 8)] == threshold + 2 * step &&
        terrain[at(4, 0)] == 7 && heights[at(4, 0)] == threshold + step &&
        terrain[at(8, 4)] == 6 && heights[at(8, 4)] == threshold + 4 * step &&
        heights[at(4, 4)] == 0.123f && heights[at(10, 10)] == 0.123f,
        "four authored corners reach their correct diamond extremes without crop or interior mutation");
}

bool TestStockElevationLevels() {
    constexpr float threshold = 10.0f / 22.0f;
    constexpr float resourceCeiling = 12.0f / 22.0f;
    constexpr float epsilon = 0.00001f;
    const float step = edmw::StockElevationStep(threshold, resourceCeiling);
    if (!Expect(std::fabs(step - 1.0f / 22.0f) < epsilon,
                "stock elevation step is one integer level") ||
        !Expect(std::fabs(edmw::StockElevationForTerrain(
                    1, 1, threshold, resourceCeiling) - 9.0f / 22.0f) < epsilon,
                "shallows use stock integer level -1") ||
        !Expect(std::fabs(edmw::StockElevationForTerrain(
                    3, 4, threshold, resourceCeiling) - 11.0f / 22.0f) < epsilon,
                "flat land uses stock integer level 1") ||
        !Expect(std::fabs(edmw::StockElevationForTerrain(
                    5, 4, threshold, resourceCeiling) - resourceCeiling) < epsilon,
                "hills use stock integer level 2") ||
        !Expect(std::fabs(edmw::StockElevationForTerrain(
                    0, UINT16_MAX, threshold, resourceCeiling)) < epsilon,
                "deep water reaches stock minimum integer level -10"))
        return false;
    return true;
}

bool TestForcedNumericCliffsWinOverRampsAndLateSmoothing() {
    constexpr int size = 13;
    constexpr float threshold = 1.0f, step = 1.0f / 22.0f;
    const size_t count = size * size;
    const auto at = [=](int x, int y) { return edmw::RowMajorCellIndex(x, y, size); };
    std::vector<uint8_t> terrain(count, 3), land(count, 1), playable(count, 1);
    std::vector<uint8_t> ramps(count, 1), cliffs(count, 0);
    std::vector<int8_t> elevation(count, 0);
    std::vector<float> heights(count, threshold);
    for (int y = 4; y <= 8; ++y) for (int x = 4; x <= 8; ++x) {
        const size_t i = at(x, y);
        elevation[i] = 25; cliffs[i] = 1;
        heights[i] = threshold + step;
    }
    edmw::AccessRampSummary access;
    if (!Expect(edmw::CarveElevatedLandAccessCorridors(land.data(), terrain.data(),
            elevation.data(), ramps.data(), size, size, 0.04f, 0.16f, 2,
            heights.data(), access, cliffs.data()), "cliff-aware manual and automatic ramp pass succeeds") ||
        !Expect(heights[at(4, 6)] == threshold + step,
            "ramps cannot carve a forced cliff sample")) return false;
    // Stand in for the stock filter, coastline pass, explicit ramps and river fill.
    std::fill(heights.begin(), heights.end(), threshold + 0.02f);
    const auto distorted = heights;
    edmw::CliffHeightSummary summary;
    const auto restore = [&](bool engineLayout) {
        return edmw::RestoreForcedElevationCliffHeightGrid(terrain.data(), elevation.data(),
            cliffs.data(), playable.data(), size, size, size, size, engineLayout,
            threshold, step, heights.data(), summary);
    };
    if (!Expect(restore(false) && summary.cliffCells == 25 && summary.steepBoundaryEdges > 0,
            "flagged +25 becomes an engine cliff after all smoothing and ramp mutations") ||
        !Expect(heights[at(4, 6)] > threshold + 0.16f && heights[at(6, 6)] == heights[at(4, 6)],
            "small selected elevation expands only as much as needed and its plateau stays flat") ||
        !Expect(heights[at(3, 6)] == threshold && heights[at(0, 0)] == distorted[at(0, 0)],
            "ordinary boundary restores its own plane and distant terrain remains exact")) return false;
    const auto expected = heights;
    if (!Expect(restore(false) && heights == expected, "forced cliff restoration is idempotent")) return false;
    edmw::EncodeHeightGridForEngine(heights.data(), size, size, size, size);
    std::fill(heights.begin(), heights.end(), threshold + 0.02f);
    if (!Expect(restore(true), "late forced cliff restoration accepts engine layout")) return false;
    for (int y = 0; y < size; ++y) for (int x = 0; x < size; ++x)
        if (!Expect(heights[at(y, x)] == expected[at(x, y)],
                "late forced cliff restoration uses exactly one coordinate transpose")) return false;
    std::fill(cliffs.begin(), cliffs.end(), static_cast<uint8_t>(0));
    const auto unmarked = heights;
    return Expect(restore(true) && heights == unmarked && summary.cliffCells == 0,
        "ordinary numeric elevations without cliff flags remain exact no-ops");
}

bool TestForcedCliffPlateauOrderingAndMembership() {
    constexpr int size = 9;
    constexpr float threshold = 1.0f, step = 1.0f / 22.0f;
    const size_t count = size * size;
    const auto at = [=](int x, int y) { return edmw::RowMajorCellIndex(x, y, size); };
    std::vector<uint8_t> terrain(count, 0), playable(count, 1), cliffs(count, 0);
    std::vector<int8_t> elevation(count, -25);
    std::vector<float> heights(count, threshold - step);
    for (int y = 2; y <= 6; ++y) for (int x = 2; x <= 4; ++x) {
        const size_t i = at(x, y);
        elevation[i] = static_cast<int8_t>((x - 5) * 25);
        cliffs[i] = 1;
    }
    edmw::CliffHeightSummary summary;
    const auto restore = [&]() {
        return edmw::RestoreForcedElevationCliffHeightGrid(terrain.data(), elevation.data(),
            cliffs.data(), playable.data(), size, size, size, size, false,
            threshold, step, heights.data(), summary);
    };
    if (!Expect(restore(), "three neighboring negative cliff plateaus restore") ||
        !Expect(heights[at(3, 4)] - heights[at(2, 4)] > 0.16f &&
                heights[at(4, 4)] - heights[at(3, 4)] > 0.16f,
            "different forced negative plateaus retain slope direction and minimum cliff separation") ||
        !Expect(heights[at(4, 4)] == threshold - step && heights[at(2, 4)] < threshold,
            "negative chains spread downward while equal authored planes remain flat")) return false;
    const auto negative = heights;
    if (!Expect(restore() && heights == negative, "multiple negative plateaus have a deterministic bounded solution"))
        return false;
    std::fill(terrain.begin(), terrain.end(), static_cast<uint8_t>(3));
    std::fill(elevation.begin(), elevation.end(), static_cast<int8_t>(0));
    std::fill(cliffs.begin(), cliffs.end(), static_cast<uint8_t>(0));
    std::fill(heights.begin(), heights.end(), threshold);
    cliffs[at(4, 4)] = 1; elevation[at(4, 4)] = 25;
    elevation[at(5, 4)] = 50;
    if (!Expect(restore() && heights[at(4, 4)] >= threshold &&
            std::fabs(heights[at(4, 4)] - threshold) > 0.16f &&
            std::fabs(heights[at(4, 4)] - (threshold + 2 * step)) > 0.16f,
            "+25 between ordinary 0 and +50 keeps land membership while forcing both slopes")) return false;
    std::fill(elevation.begin(), elevation.end(), static_cast<int8_t>(0));
    std::fill(cliffs.begin(), cliffs.end(), static_cast<uint8_t>(0));
    for (int y = 2; y <= 6; ++y) for (int x = 2; x <= 4; ++x) {
        const size_t i = at(x, y);
        elevation[i] = static_cast<int8_t>((x - 1) * 25);
        cliffs[i] = 1;
    }
    if (!Expect(restore() && heights[at(3, 4)] - heights[at(2, 4)] > 0.16f &&
            heights[at(4, 4)] - heights[at(3, 4)] > 0.16f && heights[at(2, 4)] >= threshold,
            "adjacent positive forced plateaus spread upward without collapsing height tiers")) return false;
    std::fill(cliffs.begin(), cliffs.end(), static_cast<uint8_t>(0));
    std::fill(elevation.begin(), elevation.end(), static_cast<int8_t>(-100));
    cliffs[at(4, 4)] = 1; elevation[at(4, 4)] = 100;
    if (!Expect(restore() && heights[at(4, 4)] == threshold + 4 * step,
            "a selected height already steep enough remains exactly selected")) return false;
    std::fill(elevation.begin(), elevation.end(), static_cast<int8_t>(25));
    std::fill(cliffs.begin(), cliffs.end(), static_cast<uint8_t>(1));
    if (!Expect(restore() && summary.steepBoundaryEdges == 0,
            "equal selected heights do not invent cliffs")) return false;
    for (float value : heights)
        if (!Expect(value == threshold + step, "a uniform forced plane remains exactly selected")) return false;
    return true;
}

bool TestForcedCliffsProtectCoastsAndPadding() {
    constexpr int stride = 11, active = 9;
    constexpr float threshold = 1.0f, step = 1.0f / 22.0f;
    const size_t count = stride * stride;
    const auto at = [=](int x, int y) { return edmw::RowMajorCellIndex(x, y, stride); };
    std::vector<uint8_t> terrain(count, 0), playable(count, 0), cliffs(count, 0);
    std::vector<int8_t> elevation(count, -25);
    std::vector<float> heights(count, -999.0f);
    for (int y = 0; y < active; ++y) for (int x = 0; x < active; ++x) {
        playable[at(x, y)] = 1; heights[at(x, y)] = threshold - step;
    }
    terrain[at(2, 6)] = 3; elevation[at(2, 6)] = 25; cliffs[at(2, 6)] = 1;
    elevation[at(6, 2)] = -50; cliffs[at(6, 2)] = 1;
    const auto beforeTerrain = terrain;
    edmw::GuaranteeBeachCoastline(terrain.data(), stride, stride, 1.0,
        elevation.data(), cliffs.data());
    if (!Expect(terrain == beforeTerrain, "beach promotion cannot replace forced numeric coastal terrain")) return false;
    edmw::EncodeHeightGridForEngine(heights.data(), stride, stride, active, active);
    const auto before = heights;
    edmw::CliffHeightSummary summary;
    if (!Expect(edmw::RestoreForcedElevationCliffHeightGrid(terrain.data(), elevation.data(),
            cliffs.data(), playable.data(), stride, stride, active, active, true,
            threshold, step, heights.data(), summary), "asymmetric coast cliffs restore in padded engine layout") ||
        !Expect(heights[at(6, 2)] >= threshold && heights[at(2, 6)] < threshold &&
                summary.steepBoundaryEdges > 0,
            "forced numeric coast heights preserve land and water at the correct transposed coordinates")) return false;
    for (int y = 0; y < stride; ++y) for (int x = 0; x < stride; ++x)
        if ((x >= active || y >= active) &&
            !Expect(heights[at(x, y)] == before[at(x, y)], "cliff restoration preserves nonplayable padding exactly"))
            return false;
    cliffs[at(2, 6)] = 2;
    const auto restored = heights;
    return Expect(!edmw::RestoreForcedElevationCliffHeightGrid(terrain.data(), elevation.data(),
        cliffs.data(), playable.data(), stride, stride, active, active, true,
        threshold, step, heights.data(), summary) && heights == restored,
        "invalid cliff mask fails without mutating the height grid");
}

}  // namespace

int main() {
    if (!TestAsymmetricProjection() ||
        !TestHeightGridBoundaryEncodingIsExactlyOneTranspose() ||
        !TestAsymmetricSurfaceProjectionMirrorsEditorY() ||
        !TestRowMajorWorldSourceIndexLayout() ||
        !TestPlayableMaskProjection() ||
        !TestAsymmetricDiamondProjectionWithPadding() ||
        !TestDiamondAxesAndRecordSpecificOrientation() ||
        !TestResourcePointCoordinatesAreTransposedOnce() ||
        !TestFaunaRecordFormatsUseSeparateVerticalTransforms() ||
        !TestManualResourceKindMappings() ||
        !TestTransactionalFaunaPlanner() ||
        !TestManualFaunaRuntimeCellGates() ||
        !TestFinalWorldFaunaCellGuardUsesNoGeneratorHeightThreshold() ||
        !TestDeterminismAndValidation() ||
        !TestDeterministicInvalidNeutralRepair() ||
        !TestStockElevationLevels() ||
        !TestPaintedCliffHeightsAndRamps() ||
        !TestPaintedRampCrossesNarrowCliffRidge() ||
        !TestPaintedCliffCoastValidationAndEngineOrientation() ||
        !TestForcedNumericCliffsWinOverRampsAndLateSmoothing() ||
        !TestForcedCliffPlateauOrderingAndMembership() ||
        !TestForcedCliffsProtectCoastsAndPadding() ||
        !TestAuthoredMixedCoastRestoration() ||
        !TestAuthoredCoastPlayableEdgesAndCorners())
        return 1;
    puts("ResourceAlignment tests passed.");
    return 0;
}
