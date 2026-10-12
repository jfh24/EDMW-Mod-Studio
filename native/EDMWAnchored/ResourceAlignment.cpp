#include "ResourceAlignment.h"

#include <algorithm>
#include <cmath>
#include <functional>
#include <limits>
#include <map>
#include <queue>

namespace edmw {

bool EncodeHeightGridForEngine(
    float* heights,
    int width,
    int height,
    int activeWidth,
    int activeHeight) {
    if (!heights || width < 2 || height < 2 || activeWidth < 2 || activeHeight < 2 ||
        activeWidth > width || activeHeight > height || activeWidth != activeHeight)
        return false;
    for (int y = 0; y < activeHeight; ++y)
        for (int x = y + 1; x < activeWidth; ++x)
            std::swap(heights[RowMajorCellIndex(x, y, width)],
                      heights[RowMajorCellIndex(y, x, width)]);
    return true;
}
namespace {

constexpr uint8_t kShallows = 1;
constexpr uint8_t kEngineLand = 0;
constexpr uint8_t kEngineWater = 1;
constexpr uint8_t kInnerNeutralRole = 2;
constexpr uint8_t kOuterNeutralRole = 3;
constexpr uint8_t kPlayerRole = 4;
constexpr float kDeepWaterElevationOffset = -5.5f;
constexpr float kShallowWaterElevationOffset = -0.08f;
constexpr float kCoastalWaterElevationOffset = -0.12f;
constexpr float kCoastalWaterStep = 0.28f;
constexpr float kCoastalLandElevationOffset = 0.12f;
constexpr float kCoastalLandStep = 0.18f;

double Clamp(double value, double minimum, double maximum) {
    return (std::max)(minimum, (std::min)(maximum, value));
}

bool ValidGrid(const uint8_t* values, uint32_t width, uint32_t height, int worldWidth, int worldHeight) {
    return values && width >= 2 && height >= 2 && width <= 512 && height <= 512 &&
           worldWidth >= 2 && worldHeight >= 2;
}

uint32_t ScaleInclusive(int coordinate, int targetExtent, uint32_t sourceExtent) {
    if (targetExtent <= 1 || sourceExtent <= 1) return 0;
    return static_cast<uint32_t>((static_cast<uint64_t>(coordinate) * (sourceExtent - 1)) /
                                 static_cast<uint32_t>(targetExtent - 1));
}

struct WorldDomainMapping {
    bool projected = false;
    int minimumSum = 0;
    int maximumSum = 0;
    int minimumDifference = 0;
    int maximumDifference = 0;
};

bool BuildWorldDomainMapping(const uint8_t* playableCells, int worldWidth, int worldHeight,
                             WorldDomainMapping& mapping) {
    mapping = {};
    if (!playableCells || worldWidth < 2 || worldHeight < 2) return true;

    mapping.minimumSum = (std::numeric_limits<int>::max)();
    mapping.maximumSum = (std::numeric_limits<int>::min)();
    mapping.minimumDifference = (std::numeric_limits<int>::max)();
    mapping.maximumDifference = (std::numeric_limits<int>::min)();
    for (int y = 0; y < worldHeight; ++y) {
        for (int x = 0; x < worldWidth; ++x) {
            if (!playableCells[static_cast<size_t>(y) * worldWidth + x]) continue;
            mapping.minimumSum = (std::min)(mapping.minimumSum, x + y);
            mapping.maximumSum = (std::max)(mapping.maximumSum, x + y);
            mapping.minimumDifference = (std::min)(mapping.minimumDifference, y - x);
            mapping.maximumDifference = (std::max)(mapping.maximumDifference, y - x);
        }
    }
    mapping.projected = mapping.minimumSum < mapping.maximumSum &&
                        mapping.minimumDifference < mapping.maximumDifference;
    return mapping.projected;
}

uint32_t ScaleRangeInclusive(int coordinate, int minimum, int maximum, uint32_t sourceExtent) {
    if (sourceExtent <= 1 || minimum >= maximum) return 0;
    const int clamped = (std::max)(minimum, (std::min)(maximum, coordinate));
    return static_cast<uint32_t>(
        static_cast<uint64_t>(clamped - minimum) * (sourceExtent - 1) /
        static_cast<uint32_t>(maximum - minimum));
}

bool MapWorldCellToSource(const WorldDomainMapping& mapping, const uint8_t* playableCells,
                          int x, int y, uint32_t sourceWidth, uint32_t sourceHeight,
                          int worldWidth, int worldHeight,
                          uint32_t& sourceX, uint32_t& sourceY) {
    if (x < 0 || y < 0 || x >= worldWidth || y >= worldHeight) return false;
    if (playableCells && !playableCells[static_cast<size_t>(y) * worldWidth + x]) return false;
    if (mapping.projected) {
        const uint32_t logicalX = ScaleRangeInclusive(
            x + y, mapping.minimumSum, mapping.maximumSum, sourceWidth);
        const uint32_t logicalY = ScaleRangeInclusive(
            y - x, mapping.minimumDifference, mapping.maximumDifference, sourceHeight);
        // The engine's playable world is a diamond. Its resource-record horizontal
        // axis is x+y and its raw vertical axis is y-x. Keep this raw mapping stable:
        // resource and tree records apply their required transpose when emitted.
        // Terrain semantics and direct unit records use the authored wrapper below,
        // whose vertical axis runs opposite to this raw record axis.
        sourceX = logicalX;
        sourceY = logicalY;
        return true;
    }
    sourceX = ScaleInclusive(x, worldWidth, sourceWidth);
    sourceY = ScaleInclusive(y, worldHeight, sourceHeight);
    return true;
}

bool PaintedLand(uint8_t terrain) {
    return terrain > kShallows;
}

uint8_t PreferredRole(AlignmentTarget target, int32_t kind) {
    if (target == AlignmentTarget::Player) return kPlayerRole;
    if (kind == kInnerNeutralKind) return kInnerNeutralRole;
    if (kind == kOuterNeutralKind) return kOuterNeutralRole;
    return 0;
}

size_t SourceIndexForWorldCell(const WorldDomainMapping& mapping,
                               const uint8_t* playableCells,
                               int x, int y, uint32_t sourceWidth, uint32_t sourceHeight,
                               int worldWidth, int worldHeight) {
    uint32_t sourceX = 0;
    uint32_t sourceY = 0;
    MapWorldCellToSource(mapping, playableCells, x, y, sourceWidth, sourceHeight,
                         worldWidth, worldHeight, sourceX, sourceY);
    sourceY = sourceHeight - 1 - sourceY;
    return static_cast<size_t>(sourceY) * sourceWidth + sourceX;
}

bool BuildWorldTopology(const uint8_t* terrain, const uint8_t* roles,
                        uint32_t sourceWidth, uint32_t sourceHeight,
                        int worldWidth, int worldHeight,
                        const uint8_t* playableCells,
                        std::vector<uint8_t>& land,
                        std::vector<uint8_t>& worldRoles,
                        std::vector<uint16_t>& clearance) {
    if (!ValidGrid(terrain, sourceWidth, sourceHeight, worldWidth, worldHeight)) return false;
    const size_t count = static_cast<size_t>(worldWidth) * worldHeight;
    land.assign(count, 0);
    worldRoles.assign(count, 0);
    clearance.assign(count, 0);
    WorldDomainMapping mapping;
    if (!BuildWorldDomainMapping(playableCells, worldWidth, worldHeight, mapping)) return false;
    for (int y = 0; y < worldHeight; ++y) {
        for (int x = 0; x < worldWidth; ++x) {
            const size_t target = static_cast<size_t>(y) * worldWidth + x;
            const bool playable = !playableCells || playableCells[target] != 0;
            if (!playable) continue;
            const size_t source = SourceIndexForWorldCell(
                mapping, playableCells, x, y, sourceWidth, sourceHeight,
                worldWidth, worldHeight);
            land[target] = PaintedLand(terrain[source]) ? 1 : 0;
            if (roles) worldRoles[target] = roles[source];
        }
    }

    std::vector<uint16_t> distance(count, 0);
    const uint16_t infinity = static_cast<uint16_t>((std::min)(
        65534, worldWidth + worldHeight + 1));
    for (int y = 0; y < worldHeight; ++y) {
        for (int x = 0; x < worldWidth; ++x) {
            const size_t index = static_cast<size_t>(y) * worldWidth + x;
            if (!land[index]) continue;
            const int edge = (std::min)((std::min)(x + 1, worldWidth - x),
                                         (std::min)(y + 1, worldHeight - y));
            distance[index] = static_cast<uint16_t>((std::min)(edge, static_cast<int>(infinity)));
            if (x > 0)
                distance[index] = (std::min)(distance[index],
                    static_cast<uint16_t>(distance[index - 1] + 1));
            if (y > 0) {
                distance[index] = (std::min)(distance[index],
                    static_cast<uint16_t>(distance[index - worldWidth] + 1));
                if (x > 0)
                    distance[index] = (std::min)(distance[index],
                        static_cast<uint16_t>(distance[index - worldWidth - 1] + 1));
                if (x + 1 < worldWidth)
                    distance[index] = (std::min)(distance[index],
                        static_cast<uint16_t>(distance[index - worldWidth + 1] + 1));
            }
        }
    }

    for (int y = worldHeight; y-- > 0;) {
        for (int x = worldWidth; x-- > 0;) {
            const size_t index = static_cast<size_t>(y) * worldWidth + x;
            if (!land[index]) continue;
            if (x + 1 < worldWidth)
                distance[index] = (std::min)(distance[index], static_cast<uint16_t>(distance[index + 1] + 1));
            if (y + 1 < worldHeight) {
                distance[index] = (std::min)(distance[index],
                    static_cast<uint16_t>(distance[index + worldWidth] + 1));
                if (x > 0)
                    distance[index] = (std::min)(distance[index],
                        static_cast<uint16_t>(distance[index + worldWidth - 1] + 1));
                if (x + 1 < worldWidth)
                    distance[index] = (std::min)(distance[index],
                        static_cast<uint16_t>(distance[index + worldWidth + 1] + 1));
            }
        }
    }
    clearance.swap(distance);
    return true;
}

void BuildLandComponents(const std::vector<uint8_t>& land,
                         const std::vector<uint16_t>& clearance,
                         int width,
                         int height,
                         uint16_t minimumClearance,
                         std::vector<int>& labels,
                         std::vector<size_t>& eligibleCells) {
    labels.assign(land.size(), 0);
    eligibleCells.assign(1, 0);
    std::vector<size_t> pending;
    pending.reserve(land.size());

    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            const size_t start = static_cast<size_t>(y) * width + x;
            if (!land[start] || labels[start] != 0) continue;

            const int component = static_cast<int>(eligibleCells.size());
            eligibleCells.push_back(0);
            labels[start] = component;
            pending.push_back(start);
            while (!pending.empty()) {
                const size_t current = pending.back();
                pending.pop_back();
                if (clearance[current] >= minimumClearance)
                    ++eligibleCells[component];

                const int currentX = static_cast<int>(current % width);
                const int currentY = static_cast<int>(current / width);
                const int neighborX[] = {currentX - 1, currentX + 1, currentX, currentX};
                const int neighborY[] = {currentY, currentY, currentY - 1, currentY + 1};
                for (int direction = 0; direction < 4; ++direction) {
                    const int nextX = neighborX[direction];
                    const int nextY = neighborY[direction];
                    if (nextX < 0 || nextY < 0 || nextX >= width || nextY >= height)
                        continue;
                    const size_t next = static_cast<size_t>(nextY) * width + nextX;
                    if (!land[next] || labels[next] != 0) continue;
                    labels[next] = component;
                    pending.push_back(next);
                }
            }
        }
    }
}

template <typename T>
bool BuildTransformedTopologyGrid(
    const uint8_t* terrain,
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    int targetWidth,
    int targetHeight,
    int activeWidth,
    int activeHeight,
    const uint8_t* playableCells,
    T landValue,
    T waterValue,
    T* output) {
    if (!terrain || !output || sourceWidth < 2 || sourceHeight < 2 ||
        sourceWidth > 512 || sourceHeight > 512 || targetWidth < 2 || targetHeight < 2 ||
        targetWidth > 4096 || targetHeight > 4096 || activeWidth < 2 || activeHeight < 2 ||
        activeWidth > targetWidth || activeHeight > targetHeight)
        return false;

    std::fill(output, output + static_cast<size_t>(targetWidth) * targetHeight, waterValue);
    WorldDomainMapping mapping;
    if (!BuildWorldDomainMapping(playableCells, activeWidth, activeHeight, mapping)) return false;
    for (int y = 0; y < activeHeight; ++y) {
        for (int x = 0; x < activeWidth; ++x) {
            const size_t target = static_cast<size_t>(y) * targetWidth + x;
            uint32_t sourceX = 0;
            uint32_t sourceY = 0;
            if (!MapWorldCellToSource(mapping, playableCells, x, y,
                                      sourceWidth, sourceHeight,
                                      activeWidth, activeHeight, sourceX, sourceY))
                continue;
            sourceY = sourceHeight - 1 - sourceY;
            const size_t source = static_cast<size_t>(sourceY) * sourceWidth + sourceX;
            output[target] = PaintedLand(terrain[source]) ? landValue : waterValue;
        }
    }
    return true;
}

}  // namespace

bool ConstrainCoastalHeightGradient(
    const uint8_t* land,
    const uint16_t* coastalDistances,
    int width,
    int height,
    float landThreshold,
    float maximumLandWaterStep,
    float* heights,
    HeightGradientSummary& summary) {
    summary = {};
    if (!land || !coastalDistances || !heights || width < 2 || height < 2 ||
        !std::isfinite(landThreshold) || !std::isfinite(maximumLandWaterStep) ||
        maximumLandWaterStep <= 0.0f)
        return false;
    const size_t count = static_cast<size_t>(width) * height;
    for (size_t index = 0; index < count; ++index)
        if (!std::isfinite(heights[index])) return false;

    auto measureMaximum = [land, width, height](const float* values) {
        constexpr int directions[4][2] = {{1, 0}, {-1, 1}, {0, 1}, {1, 1}};
        float maximum = 0.0f;
        for (int y = 0; y < height; ++y) {
            for (int x = 0; x < width; ++x) {
                const size_t index = static_cast<size_t>(y) * width + x;
                for (const auto& direction : directions) {
                    const int neighborX = x + direction[0];
                    const int neighborY = y + direction[1];
                    if (neighborX < 0 || neighborY >= height || neighborX >= width) continue;
                    const size_t neighbor = static_cast<size_t>(neighborY) * width + neighborX;
                    if ((land[index] != 0) == (land[neighbor] != 0)) continue;
                    maximum = (std::max)(maximum,
                        std::fabs(values[index] - values[neighbor]));
                }
            }
        }
        return maximum;
    };

    summary.maximumBefore = measureMaximum(heights);
    const float landStep = maximumLandWaterStep * 0.25f;
    const float waterStep = maximumLandWaterStep - landStep;
    const float thresholdMargin = (std::min)(0.01f, landStep * 0.25f);
    constexpr float epsilon = 0.00001f;
    for (size_t index = 0; index < count; ++index) {
        if (coastalDistances[index] != 1) continue;
        const float original = heights[index];
        heights[index] = land[index]
            ? (std::max)(landThreshold + thresholdMargin,
                (std::min)(landThreshold + landStep, original))
            : (std::min)(landThreshold - thresholdMargin,
                (std::max)(landThreshold - waterStep, original));
        if (std::fabs(heights[index] - original) > epsilon) ++summary.adjustedCells;
    }
    summary.maximumAfter = measureMaximum(heights);
    return summary.maximumAfter <= maximumLandWaterStep + 0.0001f;
}

bool LimitLandHeightGradient(
    const uint8_t* land,
    int width,
    int height,
    float maximumLocalStep,
    float* heights,
    HeightGradientSummary& summary) {
    summary = {};
    if (!land || !heights || width < 2 || height < 2 ||
        !std::isfinite(maximumLocalStep) || maximumLocalStep <= 0.0f)
        return false;
    const size_t count = static_cast<size_t>(width) * height;
    std::vector<float> limited(heights, heights + count);
    using QueueEntry = std::pair<float, size_t>;
    std::priority_queue<QueueEntry, std::vector<QueueEntry>, std::greater<QueueEntry>> pending;
    for (size_t index = 0; index < count; ++index) {
        if (!land[index]) continue;
        if (!std::isfinite(limited[index])) return false;
        pending.emplace(limited[index], index);
    }
    if (pending.empty()) return false;

    constexpr float epsilon = 0.00001f;
    while (!pending.empty()) {
        const QueueEntry current = pending.top();
        pending.pop();
        if (current.first > limited[current.second] + epsilon) continue;
        const int x = static_cast<int>(current.second % static_cast<size_t>(width));
        const int y = static_cast<int>(current.second / static_cast<size_t>(width));
        for (int offsetY = -1; offsetY <= 1; ++offsetY) {
            for (int offsetX = -1; offsetX <= 1; ++offsetX) {
                if (offsetX == 0 && offsetY == 0) continue;
                const int neighborX = x + offsetX;
                const int neighborY = y + offsetY;
                if (neighborX < 0 || neighborY < 0 ||
                    neighborX >= width || neighborY >= height)
                    continue;
                const size_t neighbor = static_cast<size_t>(neighborY) * width + neighborX;
                if (!land[neighbor]) continue;
                // Each world cell is triangulated from local height samples. A diagonal
                // pair can therefore share the same movement surface even though its
                // centers are sqrt(2) farther apart. Giving diagonals a Euclidean-scale
                // allowance left corner spans above the engine's cliff threshold and
                // could form closed rings around otherwise connected painted land.
                const float candidate = current.first + maximumLocalStep;
                if (candidate + epsilon >= limited[neighbor]) continue;
                limited[neighbor] = candidate;
                pending.emplace(candidate, neighbor);
            }
        }
    }

    auto measureMaximum = [land, width, height](const float* values) {
        constexpr int directions[4][2] = {{1, 0}, {-1, 1}, {0, 1}, {1, 1}};
        float maximum = 0.0f;
        for (int y = 0; y < height; ++y) {
            for (int x = 0; x < width; ++x) {
                const size_t index = static_cast<size_t>(y) * width + x;
                if (!land[index]) continue;
                for (const auto& direction : directions) {
                    const int neighborX = x + direction[0];
                    const int neighborY = y + direction[1];
                    if (neighborX < 0 || neighborY >= height || neighborX >= width) continue;
                    const size_t neighbor = static_cast<size_t>(neighborY) * width + neighborX;
                    if (!land[neighbor]) continue;
                    maximum = (std::max)(maximum,
                        std::fabs(values[index] - values[neighbor]));
                }
            }
        }
        return maximum;
    };
    summary.maximumBefore = measureMaximum(heights);
    for (size_t index = 0; index < count; ++index) {
        if (!land[index]) continue;
        if (limited[index] + epsilon < heights[index]) ++summary.adjustedCells;
        heights[index] = limited[index];
    }
    summary.maximumAfter = measureMaximum(heights);
    return summary.maximumAfter <= maximumLocalStep + 0.0001f;
}

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
    const uint8_t* cliffMask) {
    summary = {};
    if (!land || !terrain || !heights || width < 2 || height < 2 ||
        !std::isfinite(maximumRampStep) || maximumRampStep <= 0.0f ||
        !std::isfinite(maximumLandStep) || maximumLandStep < maximumRampStep ||
        rampHalfWidth < 1 || rampHalfWidth > 16)
        return false;

    const size_t count = static_cast<size_t>(width) * height;
    // Cliff strokes win over both inferred access routes and explicit ramps.
    // Treat their samples as barriers throughout route discovery and carving.
    std::vector<uint8_t> traversableLand;
    if (cliffMask) {
        traversableLand.assign(land, land + count);
        for (size_t i = 0; i < count; ++i)
            if (cliffMask[i]) traversableLand[i] = 0;
        land = traversableLand.data();
    }
    bool hasAutomaticLowland = false;
    int minimumPositiveMask = 101;
    if (elevationMask) {
        for (size_t index = 0; index < count; ++index) {
            if (!land[index]) continue;
            const int value = elevationMask[index];
            if (value == 127 || value <= 0) hasAutomaticLowland = true;
            else minimumPositiveMask = (std::min)(minimumPositiveMask, value);
        }
    }
    const int explicitElevatedCutoff = hasAutomaticLowland || minimumPositiveMask > 100
        ? 25
        : (std::min)(100, (std::max)(25, minimumPositiveMask + 20));
    auto elevated = [land, terrain, elevationMask, explicitElevatedCutoff](size_t index) {
        // Hills and mountains are the only terrain kinds that retain their high
        // painted elevations. Resource-bearing desert is deliberately clamped to
        // the live lowland resource band and therefore is not an elevation barrier.
        // A mask is relative to the user's own lowest painted land plane. Requiring
        // a 20-point tier difference preserves access when an entire island is
        // raised uniformly and a still-higher ring or plateau is painted inside it.
        const bool explicitlyRaised = elevationMask && elevationMask[index] != 127 &&
            elevationMask[index] >= explicitElevatedCutoff;
        return land[index] &&
            (terrain[index] == 5 || terrain[index] == 6 || explicitlyRaised);
    };
    for (size_t index = 0; index < count; ++index) {
        if (land[index] && !std::isfinite(heights[index])) return false;
    }

    constexpr int orthogonal[4][2] = {
        {1, 0}, {-1, 0}, {0, 1}, {0, -1}
    };
    std::vector<int> lowlandLabels(count, -1);
    int lowlandComponentCount = 0;
    std::queue<size_t> pending;
    for (size_t start = 0; start < count; ++start) {
        if (!land[start] || elevated(start) || lowlandLabels[start] >= 0) continue;
        lowlandLabels[start] = lowlandComponentCount++;
        pending.push(start);
        while (!pending.empty()) {
            const size_t current = pending.front();
            pending.pop();
            const int x = static_cast<int>(current % static_cast<size_t>(width));
            const int y = static_cast<int>(current / static_cast<size_t>(width));
            for (const auto& direction : orthogonal) {
                const int nextX = x + direction[0];
                const int nextY = y + direction[1];
                if (nextX < 0 || nextY < 0 || nextX >= width || nextY >= height) continue;
                const size_t next = static_cast<size_t>(nextY) * width + nextX;
                if (!land[next] || elevated(next) || lowlandLabels[next] >= 0) continue;
                lowlandLabels[next] = lowlandLabels[start];
                pending.push(next);
            }
        }
    }

    struct BoundarySite {
        size_t high = 0;
        size_t low = 0;
    };
    std::vector<int> elevatedLabels(count, -1);
    std::vector<uint8_t> corridorMask(count, 0);
    if (manualRampMask) {
        for (size_t index = 0; index < count; ++index)
            if (land[index] && manualRampMask[index]) corridorMask[index] = 1;
    }
    std::vector<int> distance(count, -1);
    std::vector<size_t> parent(count, SIZE_MAX);
    std::vector<size_t> rootLow(count, SIZE_MAX);
    std::vector<size_t> touchedSearchCells;
    std::vector<size_t> elevatedCells;

    auto markCorridor = [&](const std::vector<size_t>& centerline) {
        for (const size_t center : centerline) {
            const int centerX = static_cast<int>(center % static_cast<size_t>(width));
            const int centerY = static_cast<int>(center / static_cast<size_t>(width));
            for (int offsetY = -rampHalfWidth; offsetY <= rampHalfWidth; ++offsetY) {
                for (int offsetX = -rampHalfWidth; offsetX <= rampHalfWidth; ++offsetX) {
                    if (offsetX * offsetX + offsetY * offsetY >
                        rampHalfWidth * rampHalfWidth)
                        continue;
                    const int x = centerX + offsetX;
                    const int y = centerY + offsetY;
                    if (x < 0 || y < 0 || x >= width || y >= height) continue;
                    const size_t index = static_cast<size_t>(y) * width + x;
                    if (land[index]) corridorMask[index] = 1;
                }
            }
        }
        ++summary.corridors;
    };

    auto reconstructCenterline = [&](size_t targetHigh, size_t targetLow,
                                     bool includeTargetLow) {
        std::vector<size_t> centerline;
        size_t current = targetHigh;
        while (current != SIZE_MAX) {
            centerline.push_back(current);
            current = parent[current];
        }
        if (centerline.empty()) return;
        const size_t rootEndpoint = rootLow[centerline.back()];
        if (rootEndpoint == SIZE_MAX) return;
        centerline.push_back(rootEndpoint);
        std::reverse(centerline.begin(), centerline.end());
        if (includeTargetLow && targetLow != SIZE_MAX) centerline.push_back(targetLow);
        markCorridor(centerline);
    };

    int elevatedComponent = 0;
    for (size_t start = 0; start < count; ++start) {
        if (!elevated(start) || elevatedLabels[start] >= 0) continue;
        elevatedCells.clear();
        elevatedLabels[start] = elevatedComponent;
        pending.push(start);
        while (!pending.empty()) {
            const size_t current = pending.front();
            pending.pop();
            elevatedCells.push_back(current);
            const int x = static_cast<int>(current % static_cast<size_t>(width));
            const int y = static_cast<int>(current / static_cast<size_t>(width));
            for (int offsetY = -1; offsetY <= 1; ++offsetY) {
                for (int offsetX = -1; offsetX <= 1; ++offsetX) {
                    if (offsetX == 0 && offsetY == 0) continue;
                    const int nextX = x + offsetX;
                    const int nextY = y + offsetY;
                    if (nextX < 0 || nextY < 0 || nextX >= width || nextY >= height)
                        continue;
                    const size_t next = static_cast<size_t>(nextY) * width + nextX;
                    if (!elevated(next) || elevatedLabels[next] >= 0) continue;
                    elevatedLabels[next] = elevatedComponent;
                    pending.push(next);
                }
            }
        }
        ++elevatedComponent;
        ++summary.elevatedComponents;

        std::map<int, std::vector<BoundarySite>> boundaries;
        for (const size_t high : elevatedCells) {
            const int x = static_cast<int>(high % static_cast<size_t>(width));
            const int y = static_cast<int>(high / static_cast<size_t>(width));
            for (const auto& direction : orthogonal) {
                const int lowX = x + direction[0];
                const int lowY = y + direction[1];
                if (lowX < 0 || lowY < 0 || lowX >= width || lowY >= height) continue;
                const size_t low = static_cast<size_t>(lowY) * width + lowX;
                const int label = lowlandLabels[low];
                if (label >= 0) boundaries[label].push_back({high, low});
            }
        }
        if (boundaries.empty()) continue;
        if (boundaries.size() > 1)
            summary.separatedLowlandRegions += boundaries.size() - 1;

        auto root = boundaries.begin();
        for (auto candidate = boundaries.begin(); candidate != boundaries.end(); ++candidate) {
            if (candidate->second.size() > root->second.size()) root = candidate;
        }

        touchedSearchCells.clear();
        for (const BoundarySite& site : root->second) {
            if (distance[site.high] >= 0) continue;
            distance[site.high] = 0;
            parent[site.high] = SIZE_MAX;
            rootLow[site.high] = site.low;
            touchedSearchCells.push_back(site.high);
            pending.push(site.high);
        }
        while (!pending.empty()) {
            const size_t current = pending.front();
            pending.pop();
            const int x = static_cast<int>(current % static_cast<size_t>(width));
            const int y = static_cast<int>(current / static_cast<size_t>(width));
            for (int offsetY = -1; offsetY <= 1; ++offsetY) {
                for (int offsetX = -1; offsetX <= 1; ++offsetX) {
                    if (offsetX == 0 && offsetY == 0) continue;
                    const int nextX = x + offsetX;
                    const int nextY = y + offsetY;
                    if (nextX < 0 || nextY < 0 || nextX >= width || nextY >= height)
                        continue;
                    const size_t next = static_cast<size_t>(nextY) * width + nextX;
                    if (elevatedLabels[next] != elevatedLabels[start] || distance[next] >= 0)
                        continue;
                    distance[next] = distance[current] + 1;
                    parent[next] = current;
                    rootLow[next] = rootLow[current];
                    touchedSearchCells.push_back(next);
                    pending.push(next);
                }
            }
        }

        if (boundaries.size() == 1) {
            size_t farthest = root->second.front().high;
            for (const size_t high : elevatedCells) {
                if (distance[high] > distance[farthest] ||
                    (distance[high] == distance[farthest] &&
                     heights[high] > heights[farthest]))
                    farthest = high;
            }
            reconstructCenterline(farthest, SIZE_MAX, false);
        } else {
            std::vector<BoundarySite> selectedRoutes;
            for (auto target = boundaries.begin(); target != boundaries.end(); ++target) {
                if (target == root) continue;
                const BoundarySite* nearest = nullptr;
                for (const BoundarySite& site : target->second) {
                    if (distance[site.high] < 0) continue;
                    if (!nearest || distance[site.high] < distance[nearest->high])
                        nearest = &site;
                }
                if (nearest) {
                    reconstructCenterline(nearest->high, nearest->low, true);
                    selectedRoutes.push_back(*nearest);
                }
            }

            // A single opening can still leave a raised ring effectively sealed if that
            // route is obstructed by placement or local geometry. Add well-separated
            // alternatives until every separating ring has three independent slopes.
            constexpr size_t requiredRingRoutes = 3;
            while (selectedRoutes.size() < requiredRingRoutes) {
                const BoundarySite* best = nullptr;
                long long bestSeparation = -1;
                int bestDistance = (std::numeric_limits<int>::max)();
                for (auto target = boundaries.begin(); target != boundaries.end(); ++target) {
                    if (target == root) continue;
                    for (const BoundarySite& site : target->second) {
                        if (distance[site.high] < 0) continue;
                        const bool alreadySelected = std::any_of(
                            selectedRoutes.begin(), selectedRoutes.end(),
                            [&](const BoundarySite& selected) {
                                return selected.high == site.high;
                            });
                        if (alreadySelected) continue;

                        const int siteX = static_cast<int>(site.high % static_cast<size_t>(width));
                        const int siteY = static_cast<int>(site.high / static_cast<size_t>(width));
                        long long separation = (std::numeric_limits<long long>::max)();
                        for (const BoundarySite& selected : selectedRoutes) {
                            const int selectedX = static_cast<int>(
                                selected.high % static_cast<size_t>(width));
                            const int selectedY = static_cast<int>(
                                selected.high / static_cast<size_t>(width));
                            const long long deltaX = siteX - selectedX;
                            const long long deltaY = siteY - selectedY;
                            separation = (std::min)(
                                separation, deltaX * deltaX + deltaY * deltaY);
                        }
                        if (separation > bestSeparation ||
                            (separation == bestSeparation &&
                             distance[site.high] < bestDistance)) {
                            best = &site;
                            bestSeparation = separation;
                            bestDistance = distance[site.high];
                        }
                    }
                }
                if (!best) break;
                reconstructCenterline(best->high, best->low, true);
                selectedRoutes.push_back(*best);
            }
        }

        for (const size_t index : touchedSearchCells) {
            distance[index] = -1;
            parent[index] = SIZE_MAX;
            rootLow[index] = SIZE_MAX;
        }
    }

    summary.corridorCells = static_cast<size_t>(std::count(
        corridorMask.begin(), corridorMask.end(), static_cast<uint8_t>(1)));
    if (summary.corridorCells == 0) return true;
    std::vector<float> limited(heights, heights + count);
    using QueueEntry = std::pair<float, size_t>;
    std::priority_queue<QueueEntry, std::vector<QueueEntry>, std::greater<QueueEntry>> limits;
    for (size_t index = 0; index < count; ++index) {
        if (land[index]) limits.emplace(limited[index], index);
    }
    constexpr float epsilon = 0.00001f;
    while (!limits.empty()) {
        const QueueEntry current = limits.top();
        limits.pop();
        if (current.first > limited[current.second] + epsilon) continue;
        const int x = static_cast<int>(current.second % static_cast<size_t>(width));
        const int y = static_cast<int>(current.second / static_cast<size_t>(width));
        for (int offsetY = -1; offsetY <= 1; ++offsetY) {
            for (int offsetX = -1; offsetX <= 1; ++offsetX) {
                if (offsetX == 0 && offsetY == 0) continue;
                const int nextX = x + offsetX;
                const int nextY = y + offsetY;
                if (nextX < 0 || nextY < 0 || nextX >= width || nextY >= height) continue;
                const size_t next = static_cast<size_t>(nextY) * width + nextX;
                if (!land[next]) continue;
                const float permittedStep = corridorMask[current.second] && corridorMask[next]
                    ? maximumRampStep
                    : maximumLandStep;
                const float candidate = current.first + permittedStep;
                if (candidate + epsilon >= limited[next]) continue;
                limited[next] = candidate;
                limits.emplace(candidate, next);
            }
        }
    }

    for (size_t index = 0; index < count; ++index) {
        if (!land[index]) continue;
        if (limited[index] + epsilon < heights[index]) ++summary.adjustedCells;
        heights[index] = limited[index];
    }
    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            const size_t index = static_cast<size_t>(y) * width + x;
            if (!land[index]) continue;
            for (int offsetY = -1; offsetY <= 1; ++offsetY) {
                for (int offsetX = -1; offsetX <= 1; ++offsetX) {
                    if (offsetX == 0 && offsetY == 0) continue;
                    const int nextX = x + offsetX;
                    const int nextY = y + offsetY;
                    if (nextX < 0 || nextY < 0 || nextX >= width || nextY >= height)
                        continue;
                    const size_t next = static_cast<size_t>(nextY) * width + nextX;
                    if (!land[next]) continue;
                    const float delta = std::fabs(heights[index] - heights[next]);
                    summary.maximumLandStepAfter = (std::max)(
                        summary.maximumLandStepAfter, delta);
                    if (corridorMask[index] && corridorMask[next])
                        summary.maximumCorridorStepAfter = (std::max)(
                            summary.maximumCorridorStepAfter, delta);
                }
            }
        }
    }
    return summary.maximumCorridorStepAfter <= maximumRampStep + 0.0001f &&
           summary.maximumLandStepAfter <= maximumLandStep + 0.0001f;
}

bool RestoreAuthoredCoastHeightGrid(
    uint32_t flags, const uint8_t* land, const uint8_t* terrain,
    const int8_t* elevationMask, const uint8_t* manualRampMask, const uint8_t* playableCells,
    int width, int height, int activeWidth, int activeHeight, bool engineLayout,
    float landThreshold, float stockElevationStep,
    float* heights, CoastHeightSummary& summary) {
    summary = {};
    if (!PreservesAuthoredCoastline(flags)) return true;
    if (!land || !terrain || !elevationMask || !manualRampMask || !playableCells || !heights ||
        width < 2 || height < 2 || width > 4096 || height > 4096 ||
        activeWidth < 2 || activeHeight < 2 || activeWidth > width || activeHeight > height ||
        (engineLayout && activeWidth != activeHeight) ||
        !std::isfinite(landThreshold) || !std::isfinite(stockElevationStep) ||
        stockElevationStep <= 0.0f) return false;
    std::vector<uint16_t> coast;
    if (!BuildCoastalDistanceMap(land, width, height, coast, playableCells)) return false;
    for (int y = 0; y < activeHeight; ++y) {
        for (int x = 0; x < activeWidth; ++x) {
            const size_t i = RowMajorCellIndex(x, y, width);
            if (!playableCells[i]) continue;
            bool boundaryLand = false;
            if (land[i]) {
                for (int dy = -1; dy <= 1 && !boundaryLand; ++dy) {
                    for (int dx = -1; dx <= 1; ++dx) {
                        const int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= activeWidth || ny >= activeHeight ||
                            !playableCells[RowMajorCellIndex(nx, ny, width)]) {
                            boundaryLand = true;
                            break;
                        }
                    }
                }
            }
            const bool shoreline = coast[i] == 1;
            // Explicit beach and shallows have an exact stock plane, including
            // wider painted bands. Do not restore unmarked inland terrain.
            if (!shoreline && !boundaryLand && terrain[i] != 1 && terrain[i] != 2) continue;
            if (IsPaintedCliff(terrain[i], elevationMask[i])) continue;
            // A painted land passage takes priority over its automatic shore
            // profile. Cliff passages are rebuilt by the following cliff pass.
            if (terrain[i] > 2 && manualRampMask[i]) continue;
            const size_t storage = engineLayout
                ? RowMajorCellIndex(y, x, width) : i;
            float levels = 1.0f;
            switch (terrain[i]) {
                case 0: {
                    // All stock terrain handles use WaterDeep by integer -5.
                    // Authored deep water can therefore meet land directly,
                    // without a synthetic coral/shallow shelf.
                    const int distance = coast[i] == UINT16_MAX ? 10 : coast[i];
                    levels = -static_cast<float>((std::max)(5, (std::min)(10, distance)));
                    break;
                }
                case 1:
                case 2: levels = -1.0f; break;
                case 5: levels = 2.0f; break;
                case 6: levels = 4.0f; break;
                default: break;
            }
            // Numeric edits made after generation take priority over Auto127.
            if (elevationMask[i] >= -100 && elevationMask[i] <= 100)
                levels = static_cast<float>(elevationMask[i]) / 25.0f;
            const float restored = landThreshold + levels * stockElevationStep;
            if (heights[storage] != restored) ++summary.adjustedCells;
            heights[storage] = restored;
            if (shoreline) ++summary.shorelineCells;
            if (boundaryLand) ++summary.boundaryLandCells;
        }
    }
    return true;
}

bool RestorePaintedCliffHeights(
    const uint8_t* land, const uint8_t* terrain, const int8_t* elevationMask,
    const uint8_t* manualRampMask, int width, int height, float landThreshold,
    float stockElevationStep, float maximumRampStep, int rampHalfWidth,
    float* heights, CliffHeightSummary& summary) {
    summary = {};
    if (!land || !terrain || !elevationMask || !manualRampMask || !heights ||
        width < 1 || height < 1 || width > 4096 || height > 4096)
        return false;
    const size_t count = static_cast<size_t>(width) * height;
    for (size_t i = 0; i < count; ++i) {
        if (elevationMask[i] != kPaintedCliffElevation) continue;
        if (!land[i] || !IsPaintedCliff(terrain[i], elevationMask[i])) return false;
        ++summary.cliffCells;
    }
    if (!summary.cliffCells) return true;
    if (!std::isfinite(landThreshold) || !std::isfinite(stockElevationStep) ||
        stockElevationStep <= 0.0f || !std::isfinite(maximumRampStep) ||
        maximumRampStep <= 0.0f || rampHalfWidth < 1 || rampHalfWidth > 16)
        return false;
    const float plateau = landThreshold + 8.0f * stockElevationStep;
    std::vector<uint8_t> cliffs(count, 0), corridor(count, 0), seen(count, 0);
    std::vector<int> distance(count, -1);
    std::vector<size_t> parent(count, count);
    std::queue<size_t> pending;
    float lowestOrdinary = plateau;
    for (size_t i = 0; i < count; ++i) {
        if (!std::isfinite(heights[i])) return false;
        cliffs[i] = IsPaintedCliff(terrain[i], elevationMask[i]) ? 1 : 0;
        // Beaches have an exact stock shoreline plane and cannot be excavated.
        if (land[i] && terrain[i] != 2 && !cliffs[i]) {
            distance[i] = 0;
            pending.push(i);
            lowestOrdinary = (std::min)(lowestOrdinary, heights[i]);
        }
    }
    const auto visitNeighbors = [width, height](size_t i, const auto& visit) {
        const int x = static_cast<int>(i % width);
        const int y = static_cast<int>(i / width);
        for (int dy = -1; dy <= 1; ++dy) {
            for (int dx = -1; dx <= 1; ++dx) {
                if (!dx && !dy) continue;
                const int nx = x + dx, ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx < width && ny < height)
                    visit(RowMajorCellIndex(nx, ny, width));
            }
        }
    };
    // This distance field supplies a deterministic route to ordinary land and
    // a conservative depth at which a 0.04-per-cell ramp can reach the plateau.
    while (!pending.empty()) {
        const size_t i = pending.front();
        pending.pop();
        visitNeighbors(i, [&](size_t next) {
            if (!land[next] || terrain[next] == 2 || distance[next] >= 0) return;
            distance[next] = distance[i] + 1;
            parent[next] = i;
            pending.push(next);
        });
    }
    const int plateauDepth = static_cast<int>(std::ceil(
        (plateau - lowestOrdinary) / maximumRampStep)) + rampHalfWidth * 2 + 1;
    std::vector<size_t> searchParent(count, count);
    std::vector<uint32_t> searchVisited(count, 0);
    uint32_t searchId = 0;
    for (size_t start = 0; start < count; ++start) {
        if (!manualRampMask[start] || !land[start] || terrain[start] == 2 || seen[start])
            continue;
        std::vector<size_t> component;
        size_t tip = count;
        pending.push(start);
        seen[start] = 1;
        while (!pending.empty()) {
            const size_t i = pending.front();
            pending.pop();
            component.push_back(i);
            if (cliffs[i] && distance[i] > 0 &&
                (tip == count || distance[i] > distance[tip])) tip = i;
            visitNeighbors(i, [&](size_t next) {
                if (seen[next] || !manualRampMask[next] || !land[next] || terrain[next] == 2)
                    return;
                seen[next] = 1;
                pending.push(next);
            });
        }
        bool hasCliff = false;
        for (size_t i : component) hasCliff = hasCliff || cliffs[i] != 0;
        if (!hasCliff) continue;
        if (tip == count) { ++summary.unconnectedRamps; continue; }
        std::vector<size_t> route = component;
        size_t base = tip;
        while (cliffs[base] && parent[base] != count) {
            route.push_back(base);
            base = parent[base];
        }
        route.push_back(base);
        const int directionX = static_cast<int>(tip % width) - static_cast<int>(base % width);
        const int directionY = static_cast<int>(tip / width) - static_cast<int>(base / width);
        const int tipProjection = directionX * directionX + directionY * directionY;
        // Continue uphill far enough to meet the plateau. A narrow ridge can
        // instead be traversed to ordinary land on its opposite side.
        ++searchId;
        pending.push(tip);
        searchVisited[tip] = searchId;
        searchParent[tip] = count;
        size_t end = count;
        size_t oppositeExit = count;
        while (!pending.empty()) {
            const size_t i = pending.front();
            pending.pop();
            if (distance[i] >= plateauDepth) { end = i; break; }
            visitNeighbors(i, [&](size_t next) {
                if (end != count || !land[next] || terrain[next] == 2 ||
                    searchVisited[next] == searchId) return;
                searchVisited[next] = searchId;
                searchParent[next] = i;
                if (cliffs[next]) {
                    pending.push(next);
                } else {
                    const int dx = static_cast<int>(next % width) - static_cast<int>(base % width);
                    const int dy = static_cast<int>(next / width) - static_cast<int>(base / width);
                    if (oppositeExit == count && dx * directionX + dy * directionY > tipProjection)
                        oppositeExit = next;
                }
            });
            if (end != count) break;
        }
        while (!pending.empty()) pending.pop();
        if (end == count) end = oppositeExit;
        if (end == count) { ++summary.unconnectedRamps; continue; }
        for (size_t i = end; i != count; i = searchParent[i]) route.push_back(i);
        ++summary.rampComponents;
        for (size_t i : route) {
            const int x = static_cast<int>(i % width), y = static_cast<int>(i / width);
            for (int dy = -rampHalfWidth; dy <= rampHalfWidth; ++dy) {
                for (int dx = -rampHalfWidth; dx <= rampHalfWidth; ++dx) {
                    const int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    const size_t next = RowMajorCellIndex(nx, ny, width);
                    if (land[next] && terrain[next] != 2) corridor[next] = 1;
                }
            }
        }
    }
    for (size_t i = 0; i < count; ++i)
        if (cliffs[i]) heights[i] = plateau;
    // The minimum height envelope is restricted to the intentional corridor;
    // it cannot soften an unmarked cliff face or change neighboring plains.
    using HeightCell = std::pair<float, size_t>;
    std::priority_queue<HeightCell, std::vector<HeightCell>, std::greater<HeightCell>> limits;
    for (size_t i = 0; i < count; ++i) {
        if (!corridor[i]) continue;
        ++summary.rampCells;
        limits.emplace(heights[i], i);
    }
    while (!limits.empty()) {
        const HeightCell current = limits.top();
        limits.pop();
        if (current.first != heights[current.second]) continue;
        visitNeighbors(current.second, [&](size_t next) {
            const float candidate = current.first + maximumRampStep;
            if (!corridor[next] || candidate >= heights[next]) return;
            heights[next] = candidate;
            limits.emplace(candidate, next);
        });
    }
    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            const size_t i = RowMajorCellIndex(x, y, width);
            if (!cliffs[i] || corridor[i]) continue;
            const int dx[4] = {-1, 1, 0, 0}, dy[4] = {0, 0, -1, 1};
            for (int n = 0; n < 4; ++n) {
                const int nx = x + dx[n], ny = y + dy[n];
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                const size_t next = RowMajorCellIndex(nx, ny, width);
                if (cliffs[next] || corridor[next]) continue;
                const float jump = std::fabs(heights[i] - heights[next]);
                summary.maximumBoundaryStep = (std::max)(summary.maximumBoundaryStep, jump);
                if (jump > 0.16f) ++summary.steepBoundaryEdges;
            }
        }
    }
    return true;
}

bool RestorePaintedCliffHeightGrid(
    const uint8_t* land, const uint8_t* terrain, const int8_t* elevationMask,
    const uint8_t* manualRampMask, int width, int height,
    int activeWidth, int activeHeight, bool engineLayout,
    float landThreshold, float stockElevationStep, float maximumRampStep,
    int rampHalfWidth, float* heights, CliffHeightSummary& summary) {
    summary = {};
    if (!heights || !elevationMask || width < 1 || height < 1 ||
        width > 4096 || height > 4096 || activeWidth < 1 || activeHeight < 1 ||
        activeWidth > width || activeHeight > height ||
        (engineLayout && activeWidth != activeHeight)) return false;
    const size_t count = static_cast<size_t>(width) * height;
    if (std::find(elevationMask, elevationMask + count, kPaintedCliffElevation) ==
        elevationMask + count) return true;
    std::vector<float> logical(count);
    const auto storage = [width, activeWidth, activeHeight, engineLayout](int x, int y) {
        return engineLayout && x < activeWidth && y < activeHeight
            ? RowMajorCellIndex(y, x, width) : RowMajorCellIndex(x, y, width);
    };
    for (int y = 0; y < height; ++y)
        for (int x = 0; x < width; ++x)
            logical[RowMajorCellIndex(x, y, width)] = heights[storage(x, y)];
    if (!RestorePaintedCliffHeights(land, terrain, elevationMask, manualRampMask,
            width, height, landThreshold, stockElevationStep, maximumRampStep,
            rampHalfWidth, logical.data(), summary)) return false;
    for (int y = 0; y < height; ++y)
        for (int x = 0; x < width; ++x)
            heights[storage(x, y)] = logical[RowMajorCellIndex(x, y, width)];
    return true;
}

bool RestoreForcedElevationCliffHeightGrid(
    const uint8_t* terrain, const int8_t* elevationMask,
    const uint8_t* cliffMask, const uint8_t* playableCells,
    int width, int height, int activeWidth, int activeHeight, bool engineLayout,
    float landThreshold, float stockElevationStep,
    float* heights, CliffHeightSummary& summary) {
    summary = {};
    if (!terrain || !elevationMask || !cliffMask || !playableCells || !heights ||
        width < 2 || height < 2 || width > 4096 || height > 4096 ||
        activeWidth < 2 || activeHeight < 2 || activeWidth > width || activeHeight > height ||
        (engineLayout && activeWidth != activeHeight)) return false;
    const size_t count = static_cast<size_t>(width) * height;
    for (size_t i = 0; i < count; ++i) {
        if (cliffMask[i] > 1) return false;
        if (cliffMask[i] && playableCells[i]) ++summary.cliffCells;
    }
    if (!summary.cliffCells) return true;
    if (!std::isfinite(landThreshold) || !std::isfinite(stockElevationStep) ||
        stockElevationStep <= 0.0f) return false;
    const auto storage = [width, activeWidth, activeHeight, engineLayout](int x, int y) {
        return engineLayout && x < activeWidth && y < activeHeight
            ? RowMajorCellIndex(y, x, width) : RowMajorCellIndex(x, y, width);
    };
    const auto neighbors = [width, activeWidth, activeHeight, playableCells](size_t i, const auto& visit) {
        const int x = static_cast<int>(i % width), y = static_cast<int>(i / width);
        for (int dy = -1; dy <= 1; ++dy) {
            for (int dx = -1; dx <= 1; ++dx) {
                if (!dx && !dy) continue;
                const int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= activeWidth || ny >= activeHeight) continue;
                const size_t next = RowMajorCellIndex(nx, ny, width);
                if (playableCells[next]) visit(next);
            }
        }
    };
    std::vector<uint8_t> land(count, 0);
    for (size_t i = 0; i < count; ++i) {
        if (!playableCells[i]) continue;
        if (!IsValidPaintedElevation(terrain[i], elevationMask[i])) return false;
        land[i] = terrain[i] > 1 ? 1 : 0;
    }
    std::vector<uint16_t> coast;
    if (!BuildCoastalDistanceMap(land.data(), width, height, coast, playableCells)) return false;
    std::vector<float> authored(count, 0.0f);
    for (int y = 0; y < activeHeight; ++y) {
        for (int x = 0; x < activeWidth; ++x) {
            const size_t i = RowMajorCellIndex(x, y, width);
            if (!playableCells[i]) continue;
            if (!std::isfinite(heights[storage(x, y)])) return false;
            if (elevationMask[i] >= -100 && elevationMask[i] <= 100)
                authored[i] = landThreshold + static_cast<float>(elevationMask[i]) / 25.0f * stockElevationStep;
            else if (IsPaintedCliff(terrain[i], elevationMask[i]))
                authored[i] = landThreshold + 8.0f * stockElevationStep;
            else
                authored[i] = StockElevationForTerrain(terrain[i], coast[i], landThreshold,
                    landThreshold + 2.0f * stockElevationStep);
            if (!cliffMask[i]) {
                // Legacy terrain repainting may leave a numeric height from its
                // old land/water kind. Keep ordinary boundary samples on their
                // present side of the shoreline. Beach retains its stock plane.
                if (terrain[i] > 2) authored[i] = (std::max)(authored[i], landThreshold);
                else if (!land[i]) authored[i] = (std::min)(authored[i], landThreshold - 0.0001f);
            }
        }
    }

    struct Plateau {
        float authored = 0.0f;
        float height = 0.0f;
        bool water = false;
        std::vector<size_t> cells;
    };
    std::vector<Plateau> plateaus;
    std::vector<size_t> labels(count, SIZE_MAX);
    std::queue<size_t> pending;
    for (int y = 0; y < activeHeight; ++y) {
        for (int x = 0; x < activeWidth; ++x) {
            const size_t start = RowMajorCellIndex(x, y, width);
            if (!playableCells[start] || !cliffMask[start] || labels[start] != SIZE_MAX) continue;
            const size_t label = plateaus.size();
            const bool water = !land[start];
            plateaus.push_back({authored[start], authored[start], water, {}});
            labels[start] = label;
            pending.push(start);
            while (!pending.empty()) {
                const size_t i = pending.front();
                pending.pop();
                plateaus[label].cells.push_back(i);
                neighbors(i, [&](size_t next) {
                    if (!cliffMask[next] || labels[next] != SIZE_MAX || authored[next] != authored[start] ||
                        land[next] != land[start]) return;
                    labels[next] = label;
                    pending.push(next);
                });
            }
        }
    }
    std::vector<size_t> ordered;
    for (size_t i = 0; i < plateaus.size(); ++i) ordered.push_back(i);
    std::stable_sort(ordered.begin(), ordered.end(), [&](size_t left, size_t right) {
        if (plateaus[left].water != plateaus[right].water) return plateaus[left].water;
        return plateaus[left].water
            ? plateaus[left].authored > plateaus[right].authored
            : plateaus[left].authored < plateaus[right].authored;
    });
    // Distinct authored planes form an acyclic ordering. Water spreads downward
    // from the sea; land spreads upward. Process each plateau once;
    // unlike repeated neighbor relaxation, a ring cannot continually rise.
    constexpr float minimumCliffStep = 0.17f;
    constexpr float epsilon = 0.000001f;
    for (size_t label : ordered) {
        Plateau& plateau = plateaus[label];
        float lowerBound = plateau.water ? -INFINITY : landThreshold;
        float upperBound = plateau.water ? landThreshold - 0.0001f : INFINITY;
        std::vector<float> ordinaryPlanes;
        for (size_t i : plateau.cells) {
            neighbors(i, [&](size_t next) {
                if (labels[next] == label || authored[next] == plateau.authored) return;
                if (!cliffMask[next]) {
                    ordinaryPlanes.push_back(authored[next]);
                } else if (plateau.water && !plateaus[labels[next]].water) {
                    upperBound = (std::min)(upperBound, landThreshold - minimumCliffStep);
                } else if (plateau.water && authored[next] > plateau.authored) {
                    upperBound = (std::min)(upperBound, plateaus[labels[next]].height - minimumCliffStep);
                } else if (!plateau.water && authored[next] < plateau.authored) {
                    lowerBound = (std::max)(lowerBound, plateaus[labels[next]].height + minimumCliffStep);
                }
            });
        }
        std::sort(ordinaryPlanes.begin(), ordinaryPlanes.end());
        ordinaryPlanes.erase(std::unique(ordinaryPlanes.begin(), ordinaryPlanes.end()), ordinaryPlanes.end());
        float above = (std::max)(lowerBound, (std::min)(plateau.authored, upperBound));
        for (float plane : ordinaryPlanes)
            if (above > plane - minimumCliffStep + epsilon && above < plane + minimumCliffStep - epsilon)
                above = plane + minimumCliffStep;
        float below = (std::min)(upperBound, (std::max)(plateau.authored, lowerBound));
        for (auto it = ordinaryPlanes.rbegin(); it != ordinaryPlanes.rend(); ++it)
            if (below > *it - minimumCliffStep + epsilon && below < *it + minimumCliffStep - epsilon)
                below = *it - minimumCliffStep;
        plateau.height = below >= lowerBound - epsilon &&
            (above > upperBound + epsilon ||
             std::fabs(below - plateau.authored) <= std::fabs(above - plateau.authored))
            ? below : above;
    }
    std::vector<uint8_t> ordinaryBoundary(count, 0);
    for (const Plateau& plateau : plateaus) {
        for (size_t i : plateau.cells) {
            const int x = static_cast<int>(i % width), y = static_cast<int>(i / width);
            heights[storage(x, y)] = plateau.height;
            neighbors(i, [&](size_t next) {
                if (!cliffMask[next]) ordinaryBoundary[next] = 1;
            });
        }
    }
    for (int y = 0; y < activeHeight; ++y) {
        for (int x = 0; x < activeWidth; ++x) {
            const size_t i = RowMajorCellIndex(x, y, width);
            if (ordinaryBoundary[i]) heights[storage(x, y)] = authored[i];
        }
    }
    for (int y = 0; y < activeHeight; ++y) {
        for (int x = 0; x < activeWidth; ++x) {
            const size_t i = RowMajorCellIndex(x, y, width);
            if (!cliffMask[i] || !playableCells[i]) continue;
            neighbors(i, [&](size_t next) {
                if (authored[i] == authored[next]) return;
                const int nx = static_cast<int>(next % width), ny = static_cast<int>(next / width);
                const float jump = std::fabs(heights[storage(x, y)] - heights[storage(nx, ny)]);
                summary.maximumBoundaryStep = (std::max)(summary.maximumBoundaryStep, jump);
                if (jump > 0.16f) ++summary.steepBoundaryEdges;
            });
        }
    }
    return true;
}

bool MapWorldCellToSource(
    int x,
    int y,
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    int worldWidth,
    int worldHeight,
    const uint8_t* playableCells,
    uint32_t& sourceX,
    uint32_t& sourceY) {
    if (sourceWidth < 2 || sourceHeight < 2 || sourceWidth > 512 || sourceHeight > 512 ||
        worldWidth < 2 || worldHeight < 2)
        return false;
    WorldDomainMapping mapping;
    if (!BuildWorldDomainMapping(playableCells, worldWidth, worldHeight, mapping)) return false;
    return MapWorldCellToSource(mapping, playableCells, x, y, sourceWidth, sourceHeight,
                                worldWidth, worldHeight, sourceX, sourceY);
}

bool BuildWorldSourceIndexMap(
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    int worldWidth,
    int worldHeight,
    const uint8_t* playableCells,
    std::vector<uint32_t>& sourceIndices) {
    sourceIndices.clear();
    if (sourceWidth < 2 || sourceHeight < 2 || sourceWidth > 512 || sourceHeight > 512 ||
        worldWidth < 2 || worldHeight < 2)
        return false;
    WorldDomainMapping mapping;
    if (!BuildWorldDomainMapping(playableCells, worldWidth, worldHeight, mapping)) return false;
    sourceIndices.assign(static_cast<size_t>(worldWidth) * worldHeight, UINT32_MAX);
    for (int y = 0; y < worldHeight; ++y) {
        for (int x = 0; x < worldWidth; ++x) {
            uint32_t sourceX = 0;
            uint32_t sourceY = 0;
            if (!MapWorldCellToSource(mapping, playableCells, x, y, sourceWidth, sourceHeight,
                                      worldWidth, worldHeight, sourceX, sourceY))
                continue;
            sourceIndices[RowMajorCellIndex(x, y, worldWidth)] =
                sourceY * sourceWidth + sourceX;
        }
    }
    return true;
}

bool BuildAuthoredWorldSourceIndexMap(
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    int worldWidth,
    int worldHeight,
    const uint8_t* playableCells,
    std::vector<uint32_t>& sourceIndices) {
    if (!BuildWorldSourceIndexMap(sourceWidth, sourceHeight, worldWidth, worldHeight,
                                  playableCells, sourceIndices))
        return false;
    for (uint32_t& sourceIndex : sourceIndices) {
        if (sourceIndex == UINT32_MAX) continue;
        const uint32_t sourceX = sourceIndex % sourceWidth;
        const uint32_t sourceY = sourceIndex / sourceWidth;
        sourceIndex = (sourceHeight - 1 - sourceY) * sourceWidth + sourceX;
    }
    return true;
}

bool FindNearestWorldCellForSourcePoint(
    const std::vector<uint32_t>& sourceIndices,
    uint32_t sourceWidth,
    int32_t sourceX,
    int32_t sourceY,
    size_t& worldIndex) {
    worldIndex = SIZE_MAX;
    if (sourceIndices.empty() || sourceWidth < 2 || sourceX < 0 || sourceY < 0)
        return false;
    uint64_t bestDistance = UINT64_MAX;
    for (size_t candidate = 0; candidate < sourceIndices.size(); ++candidate) {
        const uint32_t sourceIndex = sourceIndices[candidate];
        if (sourceIndex == UINT32_MAX) continue;
        const int32_t mappedX = static_cast<int32_t>(sourceIndex % sourceWidth);
        const int32_t mappedY = static_cast<int32_t>(sourceIndex / sourceWidth);
        const int64_t dx = static_cast<int64_t>(mappedX) - sourceX;
        const int64_t dy = static_cast<int64_t>(mappedY) - sourceY;
        const uint64_t distance = static_cast<uint64_t>(dx * dx + dy * dy);
        if (distance < bestDistance) {
            bestDistance = distance;
            worldIndex = candidate;
        }
    }
    return worldIndex != SIZE_MAX;
}

bool FindNearestWorldCellForAuthoredPoint(
    const std::vector<uint32_t>& sourceIndices,
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    int32_t sourceX,
    int32_t sourceY,
    size_t& worldIndex) {
    if (sourceHeight < 2 || sourceY < 0 ||
        sourceY >= static_cast<int32_t>(sourceHeight)) {
        worldIndex = SIZE_MAX;
        return false;
    }
    const int32_t engineSourceY =
        static_cast<int32_t>(sourceHeight - 1) - sourceY;
    return FindNearestWorldCellForSourcePoint(
        sourceIndices, sourceWidth, sourceX, engineSourceY, worldIndex);
}

void EncodeResourcePointCoordinates(
    float terrainWorldX,
    float terrainWorldY,
    float& recordX,
    float& recordY) {
    recordX = terrainWorldY;
    recordY = terrainWorldX;
}

int32_t QuantizeWorldCoordinate(float coordinate, int extent) {
    if (!std::isfinite(coordinate) || extent < 2) return 0;
    const double clamped = Clamp(coordinate, 0.0, static_cast<double>(extent - 1));
    return static_cast<int32_t>(std::floor(clamped + 0.5));
}

bool IsNamedFauna(uint8_t sourceKind) {
    switch (sourceKind) {
        case 16: // Deer.
        case 18: // Bighorn Sheep.
        case 20: // Bear.
        case 22: // Squirrel.
        case 23: // Sheep.
        case 27: // Chicken.
        case 29: // Dolphin.
        case 30: // Wild Pig.
        case 31: // Elk.
            return true;
        default:
            return false;
    }
}

bool IsWaterFauna(uint8_t sourceKind) {
    return sourceKind == 3 || sourceKind == 29;
}

bool IsLandFauna(uint8_t sourceKind) {
    return IsNamedFauna(sourceKind) && !IsWaterFauna(sourceKind);
}

bool IsLegacyUnsupportedFauna(uint8_t sourceKind) {
    // These stable payload values are removed placement choices. Most select
    // another species' graphics (Sheep for 17/19/25/26, Dolphin for 21, Elk
    // for 28); Blue Bird (24) is also deliberately excluded by product policy.
    // All stay readable but inert for old-project compatibility.
    return sourceKind == 0 || sourceKind == 17 || sourceKind == 19 ||
           sourceKind == 21 || sourceKind == 24 || sourceKind == 25 ||
           sourceKind == 26 || sourceKind == 28;
}

bool IsFauna(uint8_t sourceKind) {
    return IsLegacyUnsupportedFauna(sourceKind) || sourceKind == 3 ||
           IsNamedFauna(sourceKind);
}

bool IsUserPlaceable(uint8_t sourceKind) {
    return sourceKind == 1 || sourceKind == 4 || sourceKind == 5 ||
           IsWaterFauna(sourceKind) || IsLandFauna(sourceKind);
}

ManualFaunaCategory FaunaCategory(uint8_t sourceKind) {
    if (IsWaterFauna(sourceKind)) return ManualFaunaCategory::Water;
    if (IsLandFauna(sourceKind)) return ManualFaunaCategory::Land;
    return ManualFaunaCategory::None;
}

const char* NamedFaunaToken(uint8_t sourceKind) {
    static const char* const tokens[] = {
        "Res-Deer", "Res-Goat", "Res-BighornSheep", "Res-Tiger",
        "Res-Bear", "Res-Shark", "Res-Squirrel", "Res-Sheep",
        "Res-Eagle", "Res-Walrus", "Res-Wolf", "Res-Chicken",
        "Res-Horse", "Res-Dolphin", "Res-WildPig", "Res-Elk"
    };
    return IsNamedFauna(sourceKind) ? tokens[sourceKind - 16] : nullptr;
}

uint32_t NamedFaunaDescriptorIndex(uint8_t sourceKind) {
    // Zero-based ordinals among dbobjects.dat records whose byte +232 is nonzero.
    static const uint32_t descriptorIndices[] = {
        59, 66, 67, 70, 72, 76, 79, 80,
        81, 83, 92, 93, 94, 134, 510, 611
    };
    return IsNamedFauna(sourceKind)
        ? descriptorIndices[sourceKind - 16] : UINT32_MAX;
}

uint32_t NamedFaunaGlobalUnitId(uint8_t sourceKind) {
    // Descriptor +0x14 values for the exact installed EDMW v2.0.7 identities.
    static const uint32_t globalUnitIds[] = {
        261, 291, 292, 295, 301, 314, 325, 326,
        327, 349, 370, 372, 373, 661, 1501, 1618
    };
    return IsNamedFauna(sourceKind)
        ? globalUnitIds[sourceKind - 16] : UINT32_MAX;
}

bool IsManualFaunaPlacementEnabled(uint32_t flags) {
    return (flags & kManualFaunaPlacementFlag) != 0;
}

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
    float maximumLocalHeightVariation) {
    if (!heights || !liveLand || !playableCells || !resourceReservations ||
        width < 4 || height < 4 || x < 1 || y < 1 ||
        x + 2 >= width || y + 2 >= height ||
        !std::isfinite(landThreshold) ||
        !std::isfinite(maximumLocalHeightVariation) ||
        maximumLocalHeightVariation <= 0.0f)
        return false;
    const size_t center = RowMajorCellIndex(x, y, width);
    if (resourceReservations[center] != 0 || liveLand[center] != 0 ||
        !std::isfinite(heights[center]) ||
        !(heights[center] < landThreshold - 2.0f * maximumLocalHeightVariation))
        return false;

    // Stock Fish tests a radius-one corner envelope plus the forward 2x2
    // footprint. Requiring the complete 4x4 envelope to remain playable water
    // is conservative and independent of the land-only position mask.
    for (int offsetY = -1; offsetY <= 2; ++offsetY)
        for (int offsetX = -1; offsetX <= 2; ++offsetX) {
            const size_t index = RowMajorCellIndex(x + offsetX, y + offsetY, width);
            if (playableCells[index] == 0 || liveLand[index] != 0 ||
                !std::isfinite(heights[index]))
                return false;
        }
    return true;
}

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
    float maximumLocalHeightVariation) {
    if (!heights || !liveLand || !eligibility || !resourceReservations ||
        width < 2 || height < 2 || x < 0 || y < 0 ||
        x + 1 >= width || y + 1 >= height ||
        !std::isfinite(landThreshold) ||
        !std::isfinite(maximumAnimalElevation) ||
        !std::isfinite(maximumLocalHeightVariation) ||
        maximumAnimalElevation < landThreshold ||
        maximumLocalHeightVariation < 0.0f)
        return false;
    const size_t center = RowMajorCellIndex(x, y, width);
    if (resourceReservations[center] != 0 || liveLand[center] == 0 ||
        !std::isfinite(heights[center]) || heights[center] < landThreshold ||
        heights[center] > maximumAnimalElevation)
        return false;

    float minimumHeight = std::numeric_limits<float>::infinity();
    float maximumHeight = -std::numeric_limits<float>::infinity();
    for (int offsetY = 0; offsetY <= 1; ++offsetY)
        for (int offsetX = 0; offsetX <= 1; ++offsetX) {
            const size_t index = RowMajorCellIndex(x + offsetX, y + offsetY, width);
            if (eligibility[index] == 0 || liveLand[index] == 0 ||
                !std::isfinite(heights[index]))
                return false;
            minimumHeight = (std::min)(minimumHeight, heights[index]);
            maximumHeight = (std::max)(maximumHeight, heights[index]);
        }
    return maximumHeight - minimumHeight <= maximumLocalHeightVariation;
}

bool IsFinalWorldFaunaCellUsable(
        uint8_t sourceKind, uint32_t cellKind, float worldHeight) {
    if (!std::isfinite(worldHeight) || cellKind == 9) return false;
    const auto category = FaunaCategory(sourceKind);
    if (category == ManualFaunaCategory::Land) return true;
    if (category != ManualFaunaCategory::Water) return false;
    return sourceKind == 3 ? cellKind == 1 || cellKind == 10 : cellKind == 1;
}

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
    uint64_t& digest) {
    placements.clear();
    digest = 14695981039346656037ull;
    if (!sourceTerrain || sourceWidth < 2 || sourceHeight < 2 ||
        sourceWidth > 512 || sourceHeight > 512 || worldWidth < 2 ||
        worldHeight < 2 || worldWidth > 4096 || worldHeight > 4096 ||
        (category != ManualFaunaCategory::Water &&
         category != ManualFaunaCategory::Land))
        return false;
    const size_t worldCount = static_cast<size_t>(worldWidth) * worldHeight;
    if (resourceSourceIndices.size() != worldCount ||
        directUnitSourceIndices.size() != worldCount ||
        reservedCells.size() != worldCount ||
        requests.size() > 4096)
        return false;

    std::vector<ManualFaunaRequest> ordered = requests;
    std::stable_sort(ordered.begin(), ordered.end(),
        [](const ManualFaunaRequest& left, const ManualFaunaRequest& right) {
            const auto leftCategory = FaunaCategory(left.kind);
            const auto rightCategory = FaunaCategory(right.kind);
            if (leftCategory != rightCategory) return leftCategory < rightCategory;
            if (left.kind != right.kind) return left.kind < right.kind;
            if (left.sourceY != right.sourceY) return left.sourceY < right.sourceY;
            if (left.sourceX != right.sourceX) return left.sourceX < right.sourceX;
            return left.sequence < right.sequence;
        });
    for (const auto& request : ordered)
        if (FaunaCategory(request.kind) != category ||
            request.sourceX < 0 || request.sourceY < 0 ||
            request.sourceX >= static_cast<int32_t>(sourceWidth) ||
            request.sourceY >= static_cast<int32_t>(sourceHeight))
            return false;

    std::vector<uint8_t> workingReservations = reservedCells;
    placements.reserve(ordered.size());
    const uint16_t requiredShoreDistance = category == ManualFaunaCategory::Water
        ? constraints.waterShoreDistance : constraints.landShoreDistance;
    const uint64_t separationSquared =
        static_cast<uint64_t>(constraints.minimumSeparation) *
        constraints.minimumSeparation;
    const auto hashBytes = [&digest](const void* bytes, size_t count) {
        const auto* values = static_cast<const uint8_t*>(bytes);
        for (size_t index = 0; index < count; ++index)
            digest = (digest ^ values[index]) * 1099511628211ull;
    };
    const uint8_t categoryByte = static_cast<uint8_t>(category);
    hashBytes(&categoryByte, sizeof(categoryByte));

    for (const auto& request : ordered) {
        const bool resourcePoint = request.kind == 3;
        const auto& projectionSourceIndices = resourcePoint
            ? resourceSourceIndices
            : directUnitSourceIndices;
        size_t projectedIndex = SIZE_MAX;
        if (!FindNearestWorldCellForSourcePoint(
                projectionSourceIndices, sourceWidth, request.sourceX, request.sourceY,
                projectedIndex))
            return false;
        int projectedX = static_cast<int>(projectedIndex % worldWidth);
        int projectedY = static_cast<int>(projectedIndex / worldWidth);
        // Fish is emitted through the resource vector, whose X/Y fields are the
        // one-time transpose of the raw inverse projection. Plan and validate in
        // the final consumer coordinates while preserving the same emitted pair.
        if (resourcePoint) std::swap(projectedX, projectedY);
        if (projectedX < 0 || projectedY < 0 ||
            projectedX >= worldWidth || projectedY >= worldHeight)
            return false;
        size_t bestIndex = SIZE_MAX;
        uint64_t bestDistance = UINT64_MAX;

        for (int y = 0; y < worldHeight; ++y) {
            for (int x = 0; x < worldWidth; ++x) {
                const size_t worldIndex = RowMajorCellIndex(x, y, worldWidth);
                const uint32_t sourceIndex = directUnitSourceIndices[worldIndex];
                if ((playableCells && playableCells[worldIndex] == 0) ||
                    sourceIndex == UINT32_MAX ||
                    sourceIndex >= static_cast<uint64_t>(sourceWidth) * sourceHeight ||
                    (blockedCells && blockedCells[worldIndex] != 0) ||
                    workingReservations[worldIndex] != 0)
                    continue;
                const bool paintedLand = sourceTerrain[sourceIndex] > 1;
                const bool expectedLand = category == ManualFaunaCategory::Land;
                if (paintedLand != expectedLand ||
                    (liveLand && (liveLand[worldIndex] != 0) != expectedLand))
                    continue;
                const int edge = (std::min)((std::min)(x, y),
                    (std::min)(worldWidth - 1 - x, worldHeight - 1 - y));
                if (edge < constraints.edgeDistance ||
                    (shoreDistances && shoreDistances[worldIndex] < requiredShoreDistance))
                    continue;

                bool separated = true;
                if (separationSquared != 0) {
                    const int radius = constraints.minimumSeparation;
                    for (int offsetY = -radius; offsetY <= radius && separated; ++offsetY) {
                        const int reservedY = y + offsetY;
                        if (reservedY < 0 || reservedY >= worldHeight) continue;
                        for (int offsetX = -radius; offsetX <= radius; ++offsetX) {
                            const int reservedX = x + offsetX;
                            if (reservedX < 0 || reservedX >= worldWidth) continue;
                            const int64_t dx = offsetX;
                            const int64_t dy = offsetY;
                            if (static_cast<uint64_t>(dx * dx + dy * dy) <
                                    separationSquared &&
                                workingReservations[RowMajorCellIndex(
                                    reservedX, reservedY, worldWidth)] != 0) {
                                separated = false;
                                break;
                            }
                        }
                    }
                }
                if (!separated) continue;

                const int64_t dx = static_cast<int64_t>(x) - projectedX;
                const int64_t dy = static_cast<int64_t>(y) - projectedY;
                const uint64_t distance = static_cast<uint64_t>(dx * dx + dy * dy);
                if (distance < bestDistance ||
                    (distance == bestDistance &&
                     (bestIndex == SIZE_MAX || y < static_cast<int>(bestIndex / worldWidth) ||
                      (y == static_cast<int>(bestIndex / worldWidth) &&
                       x < static_cast<int>(bestIndex % worldWidth))))) {
                    bestDistance = distance;
                    bestIndex = worldIndex;
                }
            }
        }
        if (bestIndex == SIZE_MAX) {
            placements.clear();
            return false;
        }

        workingReservations[bestIndex] = 1;
        PlannedFaunaPlacement placement;
        placement.kind = request.kind;
        placement.worldX = static_cast<int32_t>(bestIndex % worldWidth);
        placement.worldY = static_cast<int32_t>(bestIndex / worldWidth);
        placement.sequence = request.sequence;
        placement.unitDescriptorIndexRaw = request.unitDescriptorIndexRaw;
        placements.push_back(placement);
        hashBytes(&placement.kind, sizeof(placement.kind));
        hashBytes(&placement.worldX, sizeof(placement.worldX));
        hashBytes(&placement.worldY, sizeof(placement.worldY));
        hashBytes(&placement.sequence, sizeof(placement.sequence));
        hashBytes(&placement.unitDescriptorIndexRaw,
                  sizeof(placement.unitDescriptorIndexRaw));
    }

    reservedCells.swap(workingReservations);
    return true;
}

int32_t ManualGeneratedResourceKind(uint8_t sourceKind) {
    // Default.dll categories: Stone=0, Gold=1, Tree=2, Berry=3,
    // Animal=4, Fish=5. The latter two were confirmed by comparing the stock
    // Large Islands symbol values with the live resource-quantity arrays.
    // Tree placement remains driven by the painted forest mask.
    // Fish is handled as kind 5 only after the complete water-category plan commits.
    // Generic Wildlife has no exact identity; named fauna use parser-resolved unit
    // descriptors and therefore are not represented in this category mapping.
    switch (sourceKind) {
        case 1: return 3;  // Berry bush.
        case 4: return 1;  // Gold.
        case 5: return 0;  // Stone.
        default: return -1;
    }
}

float RecommendedAlignmentClearance(AlignmentTarget target, int worldWidth, int worldHeight) {
    if (worldWidth < 2 || worldHeight < 2) return 0.0f;
    const float activeExtent = static_cast<float>((std::min)(worldWidth, worldHeight) - 1);
    const float scaledClearance = activeExtent *
        (target == AlignmentTarget::Player ? 0.08125f : 0.06f);
    const float minimumClearance = target == AlignmentTarget::Player ? 16.0f : 12.0f;
    const float maximumClearance = target == AlignmentTarget::Player ? 26.0f : 24.0f;
    return std::ceil((std::max)(minimumClearance,
        (std::min)(maximumClearance, scaledClearance)));
}

bool BuildCoastalDistanceMap(const uint8_t* land, int width, int height,
                             std::vector<uint16_t>& distances,
                             const uint8_t* playableCells) {
    distances.clear();
    if (!land || width < 2 || height < 2 || width > 4096 || height > 4096) return false;
    const size_t count = static_cast<size_t>(width) * height;
    distances.assign(count, UINT16_MAX);
    std::vector<size_t> pending;
    pending.reserve(count);

    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            const size_t index = static_cast<size_t>(y) * width + x;
            if (playableCells && playableCells[index] == 0) continue;
            const bool cellLand = land[index] != 0;
            bool coastal = false;
            for (int offsetY = -1; offsetY <= 1 && !coastal; ++offsetY) {
                for (int offsetX = -1; offsetX <= 1; ++offsetX) {
                    if (offsetX == 0 && offsetY == 0) continue;
                    const int neighborX = x + offsetX;
                    const int neighborY = y + offsetY;
                    if (neighborX < 0 || neighborY < 0 ||
                        neighborX >= width || neighborY >= height)
                        continue;
                    const size_t neighbor = static_cast<size_t>(neighborY) * width + neighborX;
                    if (playableCells && playableCells[neighbor] == 0) continue;
                    if ((land[neighbor] != 0) != cellLand) {
                        coastal = true;
                        break;
                    }
                }
            }
            if (coastal) {
                distances[index] = 1;
                pending.push_back(index);
            }
        }
    }

    size_t head = 0;
    while (head < pending.size()) {
        const size_t current = pending[head++];
        const int currentX = static_cast<int>(current % width);
        const int currentY = static_cast<int>(current / width);
        const bool currentLand = land[current] != 0;
        const uint16_t nextDistance = distances[current] == UINT16_MAX
            ? UINT16_MAX
            : static_cast<uint16_t>((std::min)(
                static_cast<unsigned>(UINT16_MAX),
                static_cast<unsigned>(distances[current]) + 1));
        for (int offsetY = -1; offsetY <= 1; ++offsetY) {
            for (int offsetX = -1; offsetX <= 1; ++offsetX) {
                if (offsetX == 0 && offsetY == 0) continue;
                const int neighborX = currentX + offsetX;
                const int neighborY = currentY + offsetY;
                if (neighborX < 0 || neighborY < 0 ||
                    neighborX >= width || neighborY >= height)
                    continue;
                const size_t neighbor = static_cast<size_t>(neighborY) * width + neighborX;
                if ((playableCells && playableCells[neighbor] == 0) ||
                    (land[neighbor] != 0) != currentLand ||
                    distances[neighbor] <= nextDistance)
                    continue;
                distances[neighbor] = nextDistance;
                pending.push_back(neighbor);
            }
        }
    }
    return true;
}

bool BuildPlayableEdgeDistanceMap(const uint8_t* playableCells, int width,
                                  int height,
                                  std::vector<uint16_t>& distances) {
    distances.clear();
    if (!playableCells || width < 2 || height < 2 ||
        width > 4096 || height > 4096)
        return false;
    const size_t count = static_cast<size_t>(width) * height;
    distances.assign(count, UINT16_MAX);
    std::vector<size_t> pending;
    pending.reserve(count);

    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            const size_t index = RowMajorCellIndex(x, y, width);
            if (playableCells[index] == 0) continue;
            bool boundary = x == 0 || y == 0 || x == width - 1 || y == height - 1;
            for (int offsetY = -1; offsetY <= 1 && !boundary; ++offsetY) {
                for (int offsetX = -1; offsetX <= 1; ++offsetX) {
                    if (offsetX == 0 && offsetY == 0) continue;
                    const int neighborX = x + offsetX;
                    const int neighborY = y + offsetY;
                    if (neighborX < 0 || neighborY < 0 ||
                        neighborX >= width || neighborY >= height ||
                        playableCells[RowMajorCellIndex(
                            neighborX, neighborY, width)] == 0) {
                        boundary = true;
                        break;
                    }
                }
            }
            if (boundary) {
                distances[index] = 0;
                pending.push_back(index);
            }
        }
    }

    size_t head = 0;
    while (head < pending.size()) {
        const size_t current = pending[head++];
        const int currentX = static_cast<int>(current % width);
        const int currentY = static_cast<int>(current / width);
        const uint16_t nextDistance = distances[current] == UINT16_MAX
            ? UINT16_MAX
            : static_cast<uint16_t>((std::min)(
                static_cast<unsigned>(UINT16_MAX),
                static_cast<unsigned>(distances[current]) + 1));
        for (int offsetY = -1; offsetY <= 1; ++offsetY) {
            for (int offsetX = -1; offsetX <= 1; ++offsetX) {
                if (offsetX == 0 && offsetY == 0) continue;
                const int neighborX = currentX + offsetX;
                const int neighborY = currentY + offsetY;
                if (neighborX < 0 || neighborY < 0 ||
                    neighborX >= width || neighborY >= height)
                    continue;
                const size_t neighbor = RowMajorCellIndex(
                    neighborX, neighborY, width);
                if (playableCells[neighbor] == 0 ||
                    distances[neighbor] <= nextDistance)
                    continue;
                distances[neighbor] = nextDistance;
                pending.push_back(neighbor);
            }
        }
    }
    return !pending.empty();
}

float CoastalElevationForTerrain(uint8_t terrain, uint16_t coastalDistance,
                                  float landThreshold) {
    if (terrain == 0) {
        const float coastalOffset = kCoastalWaterElevationOffset -
            kCoastalWaterStep * static_cast<float>(
                coastalDistance > 0 ? coastalDistance - 1 : 0);
        return landThreshold + (std::max)(kDeepWaterElevationOffset, coastalOffset);
    }
    if (terrain == 1) return landThreshold + kShallowWaterElevationOffset;

    float paintedOffset = 0.85f;
    switch (terrain) {
        // Beaches are dockable shoreline: keep them on the adjacent shallow-water
        // plane while the authoritative masks continue to classify them as land.
        case 2: paintedOffset = kShallowWaterElevationOffset; break;
        case 3: paintedOffset = 0.85f; break;
        case 4: paintedOffset = 1.55f; break;
        case 5: paintedOffset = 2.35f; break;
        case 6: paintedOffset = 3.35f; break;
        case 7: paintedOffset = 4.35f; break;
        default: break;
    }
    if (coastalDistance != UINT16_MAX && coastalDistance > 0) {
        const float coastalRamp = kCoastalLandElevationOffset +
            kCoastalLandStep * static_cast<float>(coastalDistance - 1);
        paintedOffset = (std::min)(paintedOffset, coastalRamp);
    }
    return landThreshold + paintedOffset;
}

float StockElevationStep(float landThreshold, float maximumResourceElevation) {
    if (!std::isfinite(landThreshold) || !std::isfinite(maximumResourceElevation) ||
        maximumResourceElevation <= landThreshold)
        return 1.0f / 22.0f;
    // Exact-location RMVs pin MaxResourceElevation to stock integer level 2.
    return (maximumResourceElevation - landThreshold) * 0.5f;
}

float StockElevationForTerrain(
    uint8_t terrain, uint16_t coastalDistance, float landThreshold,
    float maximumResourceElevation) {
    const float step = StockElevationStep(landThreshold, maximumResourceElevation);
    switch (terrain) {
        case 0: {
            // Large Islands uses MinIntElevation -10. Move progressively toward that
            // stock seabed level as deep water gets farther from the shoreline.
            const int distance = coastalDistance == UINT16_MAX
                ? 10 : static_cast<int>(coastalDistance);
            const int levels = (std::max)(2, (std::min)(10, distance));
            return landThreshold - static_cast<float>(levels) * step;
        }
        case 1: // ShallowsElevation -1
        case 2: // Dockable beach shares the shallows plane.
            return landThreshold - step;
        case 3: // Grassland
        case 4: // Forest
        case 7: // Desert
            return landThreshold + step; // FlatElevation 1
        case 5:
            return landThreshold + 2.0f * step;
        case 6:
            return landThreshold + 4.0f * step;
        default:
            return landThreshold + step;
    }
}

float ResourceCompatibleElevationForTerrain(
    uint8_t terrain,
    uint16_t coastalDistance,
    float landThreshold,
    float maximumResourceElevation,
    float detailOffset) {
    const float elevation = StockElevationForTerrain(
        terrain, coastalDistance, landThreshold, maximumResourceElevation) + detailOffset;
    const bool resourceBearingTerrain = terrain == 3 || terrain == 4 || terrain == 7;
    if (!resourceBearingTerrain || !std::isfinite(maximumResourceElevation) ||
        maximumResourceElevation <= landThreshold)
        return elevation;

    const float resourceSpan = maximumResourceElevation - landThreshold;
    const float margin = (std::min)(resourceSpan * 0.20f,
        (std::max)(0.005f, resourceSpan * 0.01f));
    const float safeMinimum = landThreshold +
        (std::min)(0.01f, resourceSpan * 0.10f);
    const float safeMaximum = maximumResourceElevation - margin;
    const float preferredLowland = landThreshold + resourceSpan * 0.50f;
    if (safeMaximum <= safeMinimum)
        return landThreshold + resourceSpan * 0.50f;
    return (std::max)(safeMinimum,
        (std::min)(safeMaximum, (std::min)(elevation, preferredLowland)));
}

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
    const uint8_t* playableCells) {
    AlignmentSummary summary;
    if (!ValidGrid(terrain, width, height, worldWidth, worldHeight) || !roles || positions.empty())
        return summary;

    std::vector<uint8_t> land;
    std::vector<uint8_t> worldRoles;
    std::vector<uint16_t> clearance;
    if (!BuildWorldTopology(terrain, roles, width, height, worldWidth, worldHeight,
                            playableCells, land, worldRoles, clearance))
        return summary;

    bool rolePresent[5] = {};
    bool nonPlayerLandPresent = false;
    for (size_t index = 0; index < clearance.size(); ++index) {
        if (clearance[index] == 0) continue;
        const uint8_t role = worldRoles[index];
        if (role < 5) rolePresent[role] = true;
        if (role != kPlayerRole) nonPlayerLandPresent = true;
    }

    std::vector<ResourcePosition> occupied;
    occupied.reserve(fixedPositions.size() + positions.size());
    for (const ResourcePosition& fixed : fixedPositions)
        if (std::isfinite(fixed.x) && std::isfinite(fixed.y))
            occupied.push_back({
                static_cast<float>(QuantizeWorldCoordinate(fixed.x, worldWidth)),
                static_cast<float>(QuantizeWorldCoordinate(fixed.y, worldHeight)),
                fixed.kind
            });

    const uint16_t minimumClearance = static_cast<uint16_t>((std::max)(
        1.0, std::ceil(static_cast<double>(requiredClearance))));
    summary.requestedClearance = minimumClearance;
    const double preferredSeparation = (std::max)(2.0,
        static_cast<double>(requiredClearance) * 1.5);
    const double minimumSeparation = (std::max)(2.0,
        static_cast<double>(requiredClearance));
    const double inverseWidth = 1.0 / static_cast<double>(worldWidth - 1);
    const double inverseHeight = 1.0 / static_cast<double>(worldHeight - 1);
    const double inverseExtent = 1.0 / static_cast<double>((std::max)(worldWidth, worldHeight));

    std::vector<int> componentLabels;
    std::vector<size_t> componentEligibleCells;
    std::vector<size_t> componentPreferredCells;
    std::vector<size_t> componentOccupancy;
    if (target == AlignmentTarget::Player) {
        BuildLandComponents(land, clearance, worldWidth, worldHeight, minimumClearance,
                            componentLabels, componentEligibleCells);
        componentPreferredCells.assign(componentEligibleCells.size(), 0);
        for (size_t index = 0; index < componentLabels.size(); ++index) {
            const int component = componentLabels[index];
            if (component > 0 && clearance[index] >= minimumClearance &&
                worldRoles[index] == kPlayerRole)
                ++componentPreferredCells[component];
        }
        componentOccupancy.assign(componentEligibleCells.size(), 0);
        for (size_t component = 1; component < componentEligibleCells.size(); ++component)
            if (componentEligibleCells[component] > 0) ++summary.viableComponents;
    }

    uint16_t alignedMinimumClearance = (std::numeric_limits<uint16_t>::max)();
    double alignedMinimumSeparation = (std::numeric_limits<double>::max)();

    for (ResourcePosition& position : positions) {
        if (!std::isfinite(position.x) || !std::isfinite(position.y)) continue;
        const double desiredX = Clamp(position.x, 0.0, static_cast<double>(worldWidth - 1));
        const double desiredY = Clamp(position.y, 0.0, static_cast<double>(worldHeight - 1));
        const uint8_t preferredRole = PreferredRole(target, position.kind);
        const bool preferredPresent = preferredRole < 5 && rolePresent[preferredRole];
        double bestScore = (std::numeric_limits<double>::max)();
        int bestX = 0;
        int bestY = 0;
        uint16_t bestClearance = 0;
        double bestSeparation = (std::numeric_limits<double>::max)();
        size_t bestComponent = 0;
        size_t bestComponentOccupancy = (std::numeric_limits<size_t>::max)();
        bool found = false;

        const int rolePassCount = target == AlignmentTarget::Player
            ? 1 : (preferredPresent ? 2 : 1);
        for (int rolePass = 0; rolePass < rolePassCount && !found; ++rolePass) {
            const bool requirePreferredRole = target == AlignmentTarget::Neutral &&
                                              preferredPresent && rolePass == 0;
            const int separationPassCount = target == AlignmentTarget::Player ? 1 : 3;
            for (int separationPass = 0; separationPass < separationPassCount && !found;
                 ++separationPass) {
                const double requiredSeparation = target == AlignmentTarget::Player
                    ? minimumSeparation
                    : (separationPass == 0 ? preferredSeparation :
                       (separationPass == 1 ? minimumSeparation :
                        (std::max)(2.0, minimumSeparation * 0.75)));
                bestComponentOccupancy = (std::numeric_limits<size_t>::max)();
                for (int y = 0; y < worldHeight; ++y) {
                    for (int x = 0; x < worldWidth; ++x) {
                        const size_t index = static_cast<size_t>(y) * worldWidth + x;
                        if (!land[index] || clearance[index] < minimumClearance) continue;
                        const uint8_t role = worldRoles[index];
                        if (requirePreferredRole && role != preferredRole) continue;
                        if (!requirePreferredRole && target == AlignmentTarget::Neutral &&
                            nonPlayerLandPresent && role == kPlayerRole)
                            continue;

                        size_t component = 0;
                        size_t componentUse = 0;
                        if (target == AlignmentTarget::Player) {
                            const int label = componentLabels[index];
                            if (label <= 0 || componentEligibleCells[label] == 0) continue;
                            component = static_cast<size_t>(label);
                            componentUse = componentOccupancy[component];
                            if (componentUse > bestComponentOccupancy) continue;
                        }

                        double candidateSeparation = (std::numeric_limits<double>::max)();
                        double componentSeparation = (std::numeric_limits<double>::max)();
                        bool separated = true;
                        for (const ResourcePosition& used : occupied) {
                            const double usedX = static_cast<double>(x) - used.x;
                            const double usedY = static_cast<double>(y) - used.y;
                            const double usedDistance = std::sqrt(usedX * usedX + usedY * usedY);
                            candidateSeparation = (std::min)(candidateSeparation, usedDistance);
                            if (target == AlignmentTarget::Player) {
                                const int occupiedX = QuantizeWorldCoordinate(used.x, worldWidth);
                                const int occupiedY = QuantizeWorldCoordinate(used.y, worldHeight);
                                const size_t occupiedIndex =
                                    static_cast<size_t>(occupiedY) * worldWidth + occupiedX;
                                if (componentLabels[occupiedIndex] == static_cast<int>(component))
                                    componentSeparation = (std::min)(componentSeparation,
                                                                     usedDistance);
                            }
                            if (usedDistance < requiredSeparation) {
                                separated = false;
                                break;
                            }
                        }
                        if (!separated) continue;

                        const double dx = (static_cast<double>(x) - desiredX) * inverseWidth;
                        const double dy = (static_cast<double>(y) - desiredY) * inverseHeight;
                        double score = (dx * dx + dy * dy) * 0.20;
                        score -= clearance[index] * inverseExtent;
                        if (target == AlignmentTarget::Player) {
                            if (preferredPresent && componentPreferredCells[component] == 0)
                                score += 0.50;
                            if (role == kInnerNeutralRole || role == kOuterNeutralRole)
                                score += 0.75;
                            if (componentSeparation != (std::numeric_limits<double>::max)())
                                score -= componentSeparation * inverseExtent * 0.75;
                            if (componentUse < bestComponentOccupancy) {
                                bestComponentOccupancy = componentUse;
                                bestScore = (std::numeric_limits<double>::max)();
                                found = false;
                            }
                        } else {
                            score -= (std::min)(2.0,
                                clearance[index] / static_cast<double>(minimumClearance)) * 0.02;
                        }
                        if (score < bestScore) {
                            bestScore = score;
                            bestX = x;
                            bestY = y;
                            bestClearance = clearance[index];
                            bestSeparation = candidateSeparation;
                            bestComponent = component;
                            found = true;
                        }
                    }
                }
            }
        }

        if (!found) continue;
        const ResourcePosition aligned = {
            static_cast<float>(bestX), static_cast<float>(bestY), position.kind
        };
        if (std::fabs(aligned.x - position.x) > 0.01f || std::fabs(aligned.y - position.y) > 0.01f)
            ++summary.moved;
        position = aligned;
        occupied.push_back(aligned);
        if (target == AlignmentTarget::Player && bestComponent > 0)
            ++componentOccupancy[bestComponent];
        alignedMinimumClearance = (std::min)(alignedMinimumClearance, bestClearance);
        if (bestSeparation != (std::numeric_limits<double>::max)())
            alignedMinimumSeparation = (std::min)(alignedMinimumSeparation, bestSeparation);
        ++summary.aligned;
    }
    if (summary.aligned > 0) {
        summary.minimumClearance = alignedMinimumClearance;
        if (alignedMinimumSeparation != (std::numeric_limits<double>::max)())
            summary.minimumSeparation = static_cast<float>(alignedMinimumSeparation);
    }
    if (target == AlignmentTarget::Player) {
        for (size_t component = 1; component < componentOccupancy.size(); ++component) {
            if (componentOccupancy[component] > 0) ++summary.componentsUsed;
            summary.maximumComponentOccupancy = (std::max)(
                summary.maximumComponentOccupancy, componentOccupancy[component]);
        }
    }
    return summary;
}

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
    const uint8_t* playableCells) {
    AlignmentSummary best;
    if (positions.empty()) return best;

    const uint16_t preferred = static_cast<uint16_t>((std::max)(
        1.0, std::ceil(static_cast<double>(preferredClearance))));
    const uint16_t minimum = static_cast<uint16_t>((std::max)(
        1.0, (std::min)(static_cast<double>(preferred),
                        std::ceil(static_cast<double>(minimumClearance)))));
    const std::vector<ResourcePosition> requested = positions;
    for (uint16_t clearance = preferred;; --clearance) {
        std::vector<ResourcePosition> candidate = requested;
        AlignmentSummary attempt = AlignResourcePositions(
            terrain, roles, width, height, worldWidth, worldHeight, target,
            static_cast<float>(clearance), fixedPositions, candidate, playableCells);
        if (attempt.aligned > best.aligned ||
            (attempt.aligned == best.aligned &&
             attempt.requestedClearance > best.requestedClearance))
            best = attempt;
        if (attempt.aligned == candidate.size()) {
            positions.swap(candidate);
            return attempt;
        }
        if (clearance == minimum) break;
    }
    return best;
}

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
    const uint8_t* playableCells) {
    domains.clear();
    if (positions.empty()) return true;

    std::vector<uint8_t> land;
    std::vector<uint8_t> worldRoles;
    std::vector<uint16_t> clearance;
    if (!BuildWorldTopology(terrain, roles, width, height, worldWidth, worldHeight,
                            playableCells, land, worldRoles, clearance))
        return false;

    std::vector<int> componentLabels;
    std::vector<size_t> componentCells;
    BuildLandComponents(land, clearance, worldWidth, worldHeight, 1,
                        componentLabels, componentCells);

    std::vector<int> positionComponents(positions.size(), 0);
    std::vector<int> positionX(positions.size(), 0);
    std::vector<int> positionY(positions.size(), 0);
    for (size_t index = 0; index < positions.size(); ++index) {
        positionX[index] = QuantizeWorldCoordinate(positions[index].x, worldWidth);
        positionY[index] = QuantizeWorldCoordinate(positions[index].y, worldHeight);
        const size_t cell = static_cast<size_t>(positionY[index]) * worldWidth +
                            positionX[index];
        if (!land[cell] || componentLabels[cell] <= 0) return false;
        positionComponents[index] = componentLabels[cell];
    }

    std::vector<std::vector<ResourcePosition>> allDomains(positions.size());
    std::vector<std::vector<ResourcePosition>> preferredDomains(positions.size());
    std::vector<uint8_t> reservedCells(land.size(), 0);
    const auto reservePosition = [&](const ResourcePosition& position) {
        if (!std::isfinite(position.x) || !std::isfinite(position.y)) return;
        const int x = QuantizeWorldCoordinate(position.x, worldWidth);
        const int y = QuantizeWorldCoordinate(position.y, worldHeight);
        reservedCells[static_cast<size_t>(y) * worldWidth + x] = 1;
    };
    for (const ResourcePosition& position : positions) reservePosition(position);
    for (const ResourcePosition& position : reservedPositions) reservePosition(position);

    const uint16_t requiredClearance = (std::max)(static_cast<uint16_t>(1),
                                                   minimumClearance);
    for (int y = 0; y < worldHeight; ++y) {
        for (int x = 0; x < worldWidth; ++x) {
            const size_t cell = static_cast<size_t>(y) * worldWidth + x;
            if (!land[cell] || clearance[cell] < requiredClearance || reservedCells[cell])
                continue;

            size_t owner = positions.size();
            uint64_t nearestDistance = UINT64_MAX;
            for (size_t position = 0; position < positions.size(); ++position) {
                if (positionComponents[position] != componentLabels[cell]) continue;
                const int64_t dx = static_cast<int64_t>(x) - positionX[position];
                const int64_t dy = static_cast<int64_t>(y) - positionY[position];
                const uint64_t distance = static_cast<uint64_t>(dx * dx + dy * dy);
                if (distance < nearestDistance) {
                    nearestDistance = distance;
                    owner = position;
                }
            }
            if (owner == positions.size()) continue;

            const ResourcePosition member = {
                static_cast<float>(x), static_cast<float>(y), positions[owner].kind
            };
            allDomains[owner].push_back(member);
            const uint8_t preferredRole = PreferredRole(target, positions[owner].kind);
            if (roles && preferredRole < 5 && worldRoles[cell] == preferredRole)
                preferredDomains[owner].push_back(member);
        }
    }

    domains.resize(positions.size());
    for (size_t index = 0; index < positions.size(); ++index) {
        domains[index] = preferredDomains[index].empty()
            ? std::move(allDomains[index])
            : std::move(preferredDomains[index]);
        if (domains[index].empty()) {
            domains.clear();
            return false;
        }
    }
    return true;
}

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
    const uint8_t* playableCells) {
    const std::vector<ResourcePosition> noAdditionalReservations;
    return BuildPositionLandDomains(
        terrain, roles, width, height, worldWidth, worldHeight, target,
        minimumClearance, positions, noAdditionalReservations, domains, playableCells);
}

bool PositionMapsToPaintedLand(
    const uint8_t* terrain,
    uint32_t width,
    uint32_t height,
    int worldWidth,
    int worldHeight,
    const ResourcePosition& position,
    const uint8_t* playableCells) {
    if (!ValidGrid(terrain, width, height, worldWidth, worldHeight) ||
        !std::isfinite(position.x) || !std::isfinite(position.y))
        return false;
    const int x = QuantizeWorldCoordinate(position.x, worldWidth);
    const int y = QuantizeWorldCoordinate(position.y, worldHeight);
    WorldDomainMapping mapping;
    if (!BuildWorldDomainMapping(playableCells, worldWidth, worldHeight, mapping) ||
        (playableCells && !playableCells[static_cast<size_t>(y) * worldWidth + x]))
        return false;
    return PaintedLand(terrain[SourceIndexForWorldCell(
        mapping, playableCells, x, y, width, height, worldWidth, worldHeight)]);
}

uint8_t RoleAtPosition(
    const uint8_t* roles,
    uint32_t width,
    uint32_t height,
    int worldWidth,
    int worldHeight,
    const ResourcePosition& position,
    const uint8_t* playableCells) {
    if (!ValidGrid(roles, width, height, worldWidth, worldHeight) ||
        !std::isfinite(position.x) || !std::isfinite(position.y))
        return 0;
    const int x = QuantizeWorldCoordinate(position.x, worldWidth);
    const int y = QuantizeWorldCoordinate(position.y, worldHeight);
    WorldDomainMapping mapping;
    if (!BuildWorldDomainMapping(playableCells, worldWidth, worldHeight, mapping) ||
        (playableCells && !playableCells[static_cast<size_t>(y) * worldWidth + x]))
        return 0;
    return roles[SourceIndexForWorldCell(
        mapping, playableCells, x, y, width, height, worldWidth, worldHeight)];
}

bool BuildAuthoritativeLandMask(
    const uint8_t* terrain,
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    int targetWidth,
    int targetHeight,
    uint8_t* output) {
    return BuildTransformedTopologyGrid(terrain, sourceWidth, sourceHeight,
                                        targetWidth, targetHeight, targetWidth, targetHeight,
                                        nullptr,
                                        kEngineLand, kEngineWater, output);
}

bool BuildAuthoritativeLandMask(
    const uint8_t* terrain,
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    const TopologyGridLayout& layout,
    uint8_t* output) {
    return BuildTransformedTopologyGrid(terrain, sourceWidth, sourceHeight,
                                        layout.width, layout.height,
                                        layout.activeWidth, layout.activeHeight,
                                        layout.playableCells,
                                        kEngineLand, kEngineWater, output);
}

bool BuildAuthoritativeEligibilityMask(
    const uint8_t* terrain,
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    int targetWidth,
    int targetHeight,
    uint8_t* output) {
    return BuildTransformedTopologyGrid(terrain, sourceWidth, sourceHeight,
                                        targetWidth, targetHeight, targetWidth, targetHeight,
                                        nullptr,
                                        static_cast<uint8_t>(1), static_cast<uint8_t>(0), output);
}

bool BuildAuthoritativeEligibilityMask(
    const uint8_t* terrain,
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    const TopologyGridLayout& layout,
    uint8_t* output) {
    return BuildTransformedTopologyGrid(terrain, sourceWidth, sourceHeight,
                                        layout.width, layout.height,
                                        layout.activeWidth, layout.activeHeight,
                                        layout.playableCells,
                                        static_cast<uint8_t>(1), static_cast<uint8_t>(0), output);
}

bool ConstrainEligibilityToPaintedLand(
    const uint8_t* paintedEligibility,
    size_t count,
    uint8_t* stockEligibility) {
    if (!paintedEligibility || !stockEligibility || count == 0) return false;
    for (size_t index = 0; index < count; ++index)
        if (paintedEligibility[index] == 0) stockEligibility[index] = 0;
    return true;
}

bool BuildAuthoritativeSurfaceGrid(
    const uint8_t* terrain,
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    int targetWidth,
    int targetHeight,
    uint32_t landValue,
    uint32_t* output) {
    if (landValue == 0) return false;
    const TopologyGridLayout layout = {
        targetWidth, targetHeight, targetWidth, targetHeight, nullptr
    };
    return BuildAuthoritativeSurfaceGrid(
        terrain, sourceWidth, sourceHeight, layout, landValue, output);
}

bool BuildAuthoritativeSurfaceGrid(
    const uint8_t* terrain,
    uint32_t sourceWidth,
    uint32_t sourceHeight,
    const TopologyGridLayout& layout,
    uint32_t landValue,
    uint32_t* output) {
    if (landValue == 0) return false;
    if (!terrain || !output || layout.width < 2 || layout.height < 2 ||
        layout.activeWidth < 2 || layout.activeHeight < 2 ||
        layout.activeWidth > layout.width || layout.activeHeight > layout.height)
        return false;
    std::fill(output, output + static_cast<size_t>(layout.width) * layout.height, 0u);
    WorldDomainMapping mapping;
    if (!BuildWorldDomainMapping(
            layout.playableCells, layout.activeWidth, layout.activeHeight, mapping))
        return false;
    for (int y = 0; y < layout.activeHeight; ++y) {
        for (int x = 0; x < layout.activeWidth; ++x) {
            uint32_t sourceX = 0, sourceY = 0;
            if (!MapWorldCellToSource(mapping, layout.playableCells, x, y,
                                      sourceWidth, sourceHeight, layout.activeWidth,
                                      layout.activeHeight, sourceX, sourceY))
                continue;
            sourceY = sourceHeight - 1 - sourceY;
            const uint8_t kind = terrain[static_cast<size_t>(sourceY) * sourceWidth + sourceX];
            if (!PaintedLand(kind)) continue;
            // Live surface histograms establish 1024 as Beach and 1025 as Grassland.
            // The common terrain declarations and TerrainKind use the same order.
            output[static_cast<size_t>(y) * layout.width + x] =
                landValue + static_cast<int32_t>(kind) - 3;
        }
    }
    return true;
}

size_t GuaranteeBeachCoastline(uint8_t* terrain, int width, int height,
                               double minimumBeachShare, const int8_t* elevationMask,
                               const uint8_t* cliffMask) {
    if (!terrain || width < 2 || height < 2 || minimumBeachShare < 0.0 ||
        minimumBeachShare > 1.0) return 0;
    const size_t count = static_cast<size_t>(width) * height;
    std::vector<uint8_t> coast(count, 0), seen(count, 0);
    constexpr int directions[4][2] = {{1,0},{-1,0},{0,1},{0,-1}};
    for (int y = 0; y < height; ++y) for (int x = 0; x < width; ++x) {
        const size_t index = static_cast<size_t>(y) * width + x;
        if (!PaintedLand(terrain[index])) continue;
        for (const auto& d : directions) {
            const int nx=x+d[0], ny=y+d[1];
            if (nx >= 0 && ny >= 0 && nx < width && ny < height &&
                !PaintedLand(terrain[static_cast<size_t>(ny)*width+nx])) { coast[index]=1; break; }
        }
    }
    size_t promoted = 0;
    for (size_t start = 0; start < count; ++start) {
        if (!coast[start] || seen[start]) continue;
        std::vector<size_t> component;
        std::queue<size_t> pending; pending.push(start); seen[start]=1;
        while (!pending.empty()) {
            const size_t current=pending.front(); pending.pop(); component.push_back(current);
            const int x=static_cast<int>(current%width), y=static_cast<int>(current/width);
            for (const auto& d : directions) {
                const int nx=x+d[0], ny=y+d[1];
                if(nx<0||ny<0||nx>=width||ny>=height) continue;
                const size_t next=static_cast<size_t>(ny)*width+nx;
                if(coast[next]&&!seen[next]) { seen[next]=1; pending.push(next); }
            }
        }
        const size_t required=static_cast<size_t>(std::ceil(component.size()*minimumBeachShare));
        size_t beaches=static_cast<size_t>(std::count_if(component.begin(),component.end(),
            [terrain](size_t i){return terrain[i]==2;}));
        for(size_t i=0; beaches<required && i<component.size(); ++i) {
            const size_t candidate=component[(i*component.size()/required)%component.size()];
            if(terrain[candidate]!=2 &&
                !(cliffMask && cliffMask[candidate]) &&
                !(elevationMask && IsPaintedCliff(terrain[candidate], elevationMask[candidate]))) {
                terrain[candidate]=2; ++beaches; ++promoted;
            }
        }
        for(size_t i=0; beaches<required && i<component.size(); ++i)
            if(terrain[component[i]]!=2 && !(cliffMask && cliffMask[component[i]]) && !(elevationMask &&
                IsPaintedCliff(terrain[component[i]], elevationMask[component[i]]))) {
                terrain[component[i]]=2; ++beaches; ++promoted;
            }
    }
    return promoted;
}

}  // namespace edmw
