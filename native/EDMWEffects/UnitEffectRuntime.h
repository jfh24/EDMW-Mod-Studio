#pragma once
#include <algorithm>
#include <array>
#include <cstdint>
#include <map>
#include <set>
#include <tuple>
#include <utility>
#include <vector>

namespace edmw {
using UnitEffectId = std::array<uint8_t, 16>;
enum class UnitEffectTrigger : uint32_t { Proximity = 1, Activated = 2 };
enum class UnitEffectActivation : uint32_t { Toggle = 1, Timed = 2 };

struct UnitEffectRule {
    UnitEffectId id{};
    uint32_t civilization = 0;
    uint32_t sourceObject = 0;
    uint32_t targetObject = 0;
    UnitEffectTrigger trigger = UnitEffectTrigger::Proximity;
    int32_t sightAmount = 0;
    uint32_t radiusTiles = 5;
    UnitEffectActivation activation = UnitEffectActivation::Timed;
    uint32_t durationMilliseconds = 10000;
};

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
    int32_t previous = 0;
    int32_t next = 0;
};

// This model runs on simulation time, never wall-clock time. The adapter must
// remove a unit's old vision footprint before committing a changed sight bonus,
// then add the new footprint. Merely calculating proximity in the sight getter
// makes native movement/death remove the wrong footprint and leaks fog vision.
class UnitEffectRuntime {
public:
    explicit UnitEffectRuntime(std::vector<UnitEffectRule> rules = {}) : rules_(std::move(rules)) {
        for (size_t index = 0; index < rules_.size(); ++index) {
            targetRules_[rules_[index].targetObject].push_back(index);
            if (rules_[index].trigger == UnitEffectTrigger::Proximity) sourceObjects_.insert(rules_[index].sourceObject);
        }
    }

    void Reset() { active_.clear(); bonuses_.clear(); clockSet_ = false; }

    bool Activate(const UnitEffectId& effect, const UnitEffectSnapshot& actor, uint32_t simulationMilliseconds) {
        ObserveClock(simulationMilliseconds);
        const auto rule = std::find_if(rules_.begin(), rules_.end(), [&](const auto& item) { return item.id == effect; });
        if (rule == rules_.end() || rule->trigger != UnitEffectTrigger::Activated || !actor.alive ||
            actor.object != rule->sourceObject || actor.civilization != rule->civilization) return false;
        const auto key = std::make_pair(actor.id, effect);
        auto found = active_.find(key);
        if (found != active_.end() && (found->second.player != actor.player || found->second.object != actor.object ||
            found->second.civilization != actor.civilization || found->second.generation != actor.generation)) {
            active_.erase(found);
            found = active_.end();
        }
        if (rule->activation == UnitEffectActivation::Toggle && found != active_.end()) {
            active_.erase(found);
        } else {
            active_[key] = { actor.player, actor.object, actor.civilization, actor.generation, simulationMilliseconds,
                rule->activation == UnitEffectActivation::Toggle ? 0u : rule->durationMilliseconds };
        }
        return true;
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
        std::map<uint32_t, int32_t> next;
        for (const auto& entry : live) {
            const auto& target = *entry.second;
            int32_t bonus = 0;
            const auto matchingRules = targetRules_.find(target.object);
            if (matchingRules == targetRules_.end()) {
                const auto previous = Bonus(target.id);
                if (previous) changes.push_back({ target.id, previous, 0 });
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
                                const auto dx = int64_t(source->x) - target.x;
                                const auto dy = int64_t(source->y) - target.y;
                                // Bounding-box rejection also avoids squaring extreme coordinates.
                                if (dx < -radius || dx > radius || dy < -radius || dy > radius) continue;
                                if (dx * dx + dy * dy <= radius * radius) { applies = true; break; }
                            }
                        }
                }
                if (applies) bonus += rule.sightAmount;
            }
            bonus = (std::max)(-32, (std::min)(32, bonus));
            const auto previous = Bonus(target.id);
            if (previous != bonus) changes.push_back({ target.id, previous, bonus });
            if (bonus) next.emplace(target.id, bonus);
        }
        // Missing/dead units have already had their footprint removed by the
        // native game. Never dereference an old unit pointer from this cache.
        bonuses_.swap(next);
        return changes;
    }

    int32_t Bonus(uint32_t unit) const {
        const auto found = bonuses_.find(unit);
        return found == bonuses_.end() ? 0 : found->second;
    }

private:
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
    std::map<uint32_t, int32_t> bonuses_;
    uint32_t clock_ = 0;
    bool clockSet_ = false;
};
}
