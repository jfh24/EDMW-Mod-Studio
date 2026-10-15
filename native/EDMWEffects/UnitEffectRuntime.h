#pragma once
#include <algorithm>
#include <array>
#include <cstdint>
#include <map>
#include <set>
#include <tuple>
#include <utility>
#include <vector>
#include "UnitEffectStats.h"

namespace edmw {
// Native Phoenix/tree values are zero-based rows; persisted effect IDs retain
// their existing one-based civilization identity (China 4, USA 12).
inline uint32_t NativeCivilizationToWireId(uint32_t nativeRow) {
    return nativeRow >= 3 && nativeRow <= 11 ? nativeRow + 1 : 0;
}
using UnitEffectId = std::array<uint8_t, 16>;
enum class UnitEffectTrigger : uint32_t { Proximity = 1, Activated = 2 };
enum class UnitEffectActivation : uint32_t { Toggle = 1, Timed = 2 };
struct UnitEffectOutput { UnitEffectStat stat; float amount; };

struct UnitEffectRule {
    UnitEffectId id{};
    uint32_t civilization = 0;
    uint32_t sourceObject = 0;
    uint32_t targetObject = 0;
    UnitEffectTrigger trigger = UnitEffectTrigger::Proximity;
    float amount = 0.0f;
    UnitEffectStat stat = UnitEffectStat::LineOfSight;
    std::vector<UnitEffectOutput> outputs;
    uint32_t radiusTiles = 5;
    UnitEffectActivation activation = UnitEffectActivation::Timed;
    uint32_t durationMilliseconds = 10000;
};

inline std::vector<UnitEffectOutput> UnitEffectOutputs(const UnitEffectRule& rule) {
    return rule.outputs.empty() ? std::vector<UnitEffectOutput>{ { rule.stat, rule.amount } } : rule.outputs;
}

inline bool ValidUnitEffectOutputs(const UnitEffectRule& rule) {
    const auto outputs = UnitEffectOutputs(rule);
    if (outputs.empty() || outputs.size() > 51) return false;
    std::set<UnitEffectStat> stats;
    for (const auto& output : outputs)
        if (!SupportedUnitEffectStat(output.stat) || !ValidUnitEffectAmount(output.stat, output.amount) ||
            !stats.insert(output.stat).second) return false;
    return true;
}

struct UnitEffectSnapshot {
    uint32_t id = 0;
    uint32_t player = 0;
    uint32_t civilization = 0;
    uint32_t object = 0;
    int32_t x = 0;
    int32_t y = 0;
    bool alive = true;
    uint64_t generation = 0;
};

struct UnitEffectTransition {
    uint32_t unit = 0;
    float previous = 0.0f;
    float next = 0.0f;
    UnitEffectStat stat = UnitEffectStat::LineOfSight;
};

// This model runs on simulation time, never wall-clock time. The adapter must
// remove a unit's old vision footprint before committing a changed sight bonus,
// then add the new footprint. Merely calculating proximity in the sight getter
// makes native movement/death remove the wrong footprint and leaks fog vision.
class UnitEffectRuntime {
public:
    explicit UnitEffectRuntime(std::vector<UnitEffectRule> rules = {}) : rules_(std::move(rules)) {
        for (size_t index = 0; index < rules_.size(); ++index) {
            if (rules_[index].outputs.empty()) rules_[index].outputs = UnitEffectOutputs(rules_[index]);
            targetRules_[rules_[index].targetObject].push_back(index);
            if (rules_[index].trigger == UnitEffectTrigger::Proximity) sourceObjects_.insert(rules_[index].sourceObject);
        }
    }

    void Reset() { active_.clear(); bonuses_.clear(); clockSet_ = false; }
    void ForgetUnit(uint32_t unit) {
        bonuses_.erase(unit);
        for (auto it = active_.begin(); it != active_.end();)
            if (it->first.first == unit) it = active_.erase(it);
            else ++it;
    }

    bool Activate(const UnitEffectId& effect, const UnitEffectSnapshot& actor, uint32_t simulationMilliseconds) {
        return ActivateGroup(effect, { actor }, actor.player, simulationMilliseconds) != 0;
    }

    size_t ActivateGroup(const UnitEffectId& effect, const std::vector<UnitEffectSnapshot>& actors,
        uint32_t expectedPlayer, uint32_t simulationMilliseconds) {
        ObserveClock(simulationMilliseconds);
        const auto rule = std::find_if(rules_.begin(), rules_.end(), [&](const auto& item) { return item.id == effect; });
        if (rule == rules_.end() || rule->trigger != UnitEffectTrigger::Activated) return 0;
        std::map<uint32_t, const UnitEffectSnapshot*> eligible;
        bool allActive = true;
        for (const auto& actor : actors) {
            if (actor.id == UINT32_MAX || !actor.alive || actor.player != expectedPlayer ||
                actor.object != rule->sourceObject || actor.civilization != rule->civilization ||
                !eligible.emplace(actor.id, &actor).second) continue;
            const auto key = std::make_pair(actor.id, effect);
            auto found = active_.find(key);
            if (found != active_.end() && (found->second.player != actor.player || found->second.object != actor.object ||
                found->second.civilization != actor.civilization || found->second.generation != actor.generation)) {
                active_.erase(found);
                found = active_.end();
            }
            if (found == active_.end()) allActive = false;
        }
        // A mixed selection turns on together; clicking an entirely active
        // toggle group turns it off together. Timed groups restart together.
        for (const auto& entry : eligible) {
            const auto& actor = *entry.second;
            const auto key = std::make_pair(actor.id, effect);
            if (rule->activation == UnitEffectActivation::Toggle && allActive) active_.erase(key);
            else active_[key] = { actor.player, actor.object, actor.civilization, actor.generation, simulationMilliseconds,
                rule->activation == UnitEffectActivation::Toggle ? 0u : rule->durationMilliseconds };
        }
        return eligible.size();
    }

    std::vector<UnitEffectTransition> Evaluate(const std::vector<UnitEffectSnapshot>& units, uint32_t simulationMilliseconds) {
        ObserveClock(simulationMilliseconds);
        std::map<uint32_t, const UnitEffectSnapshot*> live;
        for (const auto& unit : units) if (unit.alive) live.emplace(unit.id, &unit);
        std::map<SpatialKey, std::vector<const UnitEffectSnapshot*>> sources;
        for (const auto& entry : live) {
            const auto& unit = *entry.second;
            if (sourceObjects_.find(unit.object) != sourceObjects_.end())
                sources[{ unit.player, unit.civilization, unit.object, Cell(unit.x), Cell(unit.y) }].push_back(&unit);
        }
        for (auto it = active_.begin(); it != active_.end();) {
            const auto unit = live.find(it->first.first);
            const auto& state = it->second;
            if (unit == live.end() || unit->second->player != state.player || unit->second->object != state.object ||
                unit->second->civilization != state.civilization || unit->second->generation != state.generation ||
                (state.duration && simulationMilliseconds - state.started >= state.duration)) it = active_.erase(it);
            else ++it;
        }
        std::vector<UnitEffectTransition> changes;
        std::map<uint32_t, std::map<UnitEffectStat, float>> next;
        for (const auto& entry : live) {
            const auto& target = *entry.second;
            std::map<UnitEffectStat, double> totals;
            const auto matchingRules = targetRules_.find(target.object);
            if (matchingRules == targetRules_.end()) {
                AppendChanges(target.id, {}, changes);
                continue;
            }
            for (const auto index : matchingRules->second) {
                const auto& rule = rules_[index];
                if (target.civilization != rule.civilization) continue;
                bool applies = false;
                if (rule.trigger == UnitEffectTrigger::Activated) {
                    applies = active_.find(std::make_pair(target.id, rule.id)) != active_.end();
                } else {
                    const auto radius = int64_t(rule.radiusTiles);
                    for (auto x = Cell(int64_t(target.x) - radius); x <= Cell(int64_t(target.x) + radius) && !applies; ++x)
                        for (auto y = Cell(int64_t(target.y) - radius); y <= Cell(int64_t(target.y) + radius) && !applies; ++y) {
                            const auto nearby = sources.find({ target.player, rule.civilization, rule.sourceObject, x, y });
                            if (nearby == sources.end()) continue;
                            for (const auto source : nearby->second) {
                                if (source->id == target.id) continue; // A proximity emitter affects only other units.
                                const auto dx = int64_t(source->x) - target.x;
                                const auto dy = int64_t(source->y) - target.y;
                                // Bounding-box rejection also avoids squaring extreme coordinates.
                                if (dx < -radius || dx > radius || dy < -radius || dy > radius) continue;
                                if (dx * dx + dy * dy <= radius * radius) { applies = true; break; }
                            }
                        }
                }
                if (applies) for (const auto& output : rule.outputs) {
                    if (UnitEffectIsLimit(output.stat)) {
                        const auto previousLimit = totals.find(output.stat);
                        if (previousLimit == totals.end()) totals.emplace(output.stat, output.amount);
                        else previousLimit->second = (std::min)(previousLimit->second, static_cast<double>(output.amount));
                    } else totals[output.stat] += output.amount;
                }
            }
            std::map<UnitEffectStat, float> values;
            for (const auto& total : totals) {
                const auto limit = static_cast<double>(UnitEffectAmountLimit(total.first));
                const auto value = static_cast<float>((std::max)(-limit, (std::min)(limit, total.second)));
                if (value) values.emplace(total.first, value);
            }
            AppendChanges(target.id, values, changes);
            if (!values.empty()) next.emplace(target.id, std::move(values));
        }
        // Missing/dead units have already had their footprint removed by the
        // native game. Never dereference an old unit pointer from this cache.
        bonuses_.swap(next);
        return changes;
    }

    float Bonus(uint32_t unit, UnitEffectStat stat = UnitEffectStat::LineOfSight) const {
        const auto found = bonuses_.find(unit);
        if (found == bonuses_.end()) return 0.0f;
        const auto value = found->second.find(stat);
        return value == found->second.end() ? 0.0f : value->second;
    }

private:
    void AppendChanges(uint32_t unit, const std::map<UnitEffectStat, float>& values, std::vector<UnitEffectTransition>& changes) const {
        std::set<UnitEffectStat> stats;
        const auto previous = bonuses_.find(unit);
        if (previous != bonuses_.end()) for (const auto& value : previous->second) stats.insert(value.first);
        for (const auto& value : values) stats.insert(value.first);
        for (const auto stat : stats) {
            const auto before = Bonus(unit, stat);
            const auto found = values.find(stat);
            const auto after = found == values.end() ? 0.0f : found->second;
            if (before != after) changes.push_back({ unit, before, after, stat });
        }
    }
    using SpatialKey = std::tuple<uint32_t, uint32_t, uint32_t, int64_t, int64_t>;
    static int64_t Cell(int64_t coordinate) { return coordinate >= 0 ? coordinate / 8 : (coordinate - 7) / 8; }
    struct Active { uint32_t player; uint32_t object; uint32_t civilization; uint64_t generation; uint32_t started; uint32_t duration; };
    void ObserveClock(uint32_t now) {
        // A backwards simulation clock means a new/loaded match. Dynamic
        // activation state is intentionally discarded until save integration
        // exists. The native adapter clears its cached sight at world teardown.
        if (clockSet_ && now < clock_) { active_.clear(); bonuses_.clear(); }
        clock_ = now;
        clockSet_ = true;
    }
    std::vector<UnitEffectRule> rules_;
    std::map<uint32_t, std::vector<size_t>> targetRules_;
    std::set<uint32_t> sourceObjects_;
    std::map<std::pair<uint32_t, UnitEffectId>, Active> active_;
    std::map<uint32_t, std::map<UnitEffectStat, float>> bonuses_;
    uint32_t clock_ = 0;
    bool clockSet_ = false;
};
}
