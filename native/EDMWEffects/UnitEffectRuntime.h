#pragma once
#include <algorithm>
#include <array>
#include <cstdint>
#include <map>
#include <set>
#include <string>
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
enum class UnitEffectTrigger : uint32_t { Proximity = 1, Activated = 2, ProximityAbility = 3 };
enum class UnitEffectActivation : uint32_t { Toggle = 1, Timed = 2, Instant = 3 };
struct UnitEffectOutput { UnitEffectStat stat; float amount; };
enum class UnitEffectActionKind : uint32_t { OwnershipTransfer = 1, Transform = 2, Spawn = 3 };
enum class UnitEffectOwner : uint32_t { SourcePlayer = 0, AffectedPlayer = 1, SpecificPlayer = 2 };
enum class UnitEffectTargetPlayers : uint32_t { Own = 0, Other = 1, Any = 2 };
struct UnitEffectAction {
    UnitEffectActionKind kind = UnitEffectActionKind::OwnershipTransfer;
    uint32_t object = UINT32_MAX;
    uint32_t count = 1;
    UnitEffectOwner owner = UnitEffectOwner::SourcePlayer;
    uint32_t playerSlot = 1;
    std::string objectName;
};
inline bool SameUnitEffectAction(const UnitEffectAction& left, const UnitEffectAction& right) {
    return left.kind == right.kind && left.object == right.object && left.count == right.count &&
        left.owner == right.owner && left.playerSlot == right.playerSlot;
}

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
    uint32_t grantFamilyTechnology = 0;
    std::string name;
    std::string targetName;
    std::vector<UnitEffectAction> actions;
    UnitEffectTargetPlayers targetPlayers = UnitEffectTargetPlayers::Own;
    UnitEffectId familyId{};
    uint32_t delayMilliseconds = 0;
    uint32_t maxAffectedTargets = 0;
    uint32_t maxConcurrentTargets = 0;
    std::array<uint32_t, 4> resourceCosts{}; // Food, wood, gold, stone.
};

struct UnitEffectInfo {
    UnitEffectRule rule;
    bool active;
    bool provides = false;
    bool receives = true;
};

inline std::vector<UnitEffectOutput> UnitEffectOutputs(const UnitEffectRule& rule) {
    return rule.outputs.empty() && rule.actions.empty() ? std::vector<UnitEffectOutput>{ { rule.stat, rule.amount } } : rule.outputs;
}

inline bool ValidUnitEffectOutputs(const UnitEffectRule& rule) {
    const auto outputs = UnitEffectOutputs(rule);
    if (outputs.size() > 51 || outputs.size() + rule.actions.size() > 64 || (outputs.empty() && rule.actions.empty())) return false;
    std::set<UnitEffectStat> stats;
    for (const auto& output : outputs)
        if (!SupportedUnitEffectStat(output.stat) || !ValidUnitEffectAmount(output.stat, output.amount) ||
            !stats.insert(output.stat).second) return false;
    std::set<UnitEffectActionKind> actionKinds;
    uint32_t spawned = 0;
    for (const auto& action : rule.actions) {
        const auto kind = static_cast<uint32_t>(action.kind);
        if (kind < 1 || kind > 3 || static_cast<uint32_t>(action.owner) > 2 || action.playerSlot < 1 || action.playerSlot > 8 ||
            !action.count || action.count > 64 || (action.kind == UnitEffectActionKind::OwnershipTransfer ? action.object != UINT32_MAX : action.object >= 100000) ||
            (action.kind != UnitEffectActionKind::Spawn && (action.count != 1 || !actionKinds.insert(action.kind).second))) return false;
        if (action.kind == UnitEffectActionKind::Spawn) { spawned += action.count; if (spawned > 64) return false; }
    }
    if (static_cast<uint32_t>(rule.targetPlayers) > 2 || (rule.trigger != UnitEffectTrigger::Proximity && rule.targetPlayers != UnitEffectTargetPlayers::Own) ||
        (rule.activation == UnitEffectActivation::Instant && (!outputs.empty() || rule.actions.empty()))) return false;
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
// Native lifecycle results can acquire their world ID on a later tick. Only a
// retained pointer's generation, owner and type identify it across that queue.
inline bool SamePendingUnitEffectActor(const UnitEffectSnapshot& previous, const UnitEffectSnapshot& current) {
    return previous.generation != 0 && previous.generation == current.generation && current.alive &&
        previous.player == current.player && previous.civilization == current.civilization && previous.object == current.object;
}
struct UnitEffectActionEvent {
    UnitEffectId effect{};
    UnitEffectSnapshot source, target;
    std::vector<UnitEffectAction> actions;
    std::array<uint32_t, 4> resourceCosts{};
    bool hasStatOutputs = false;
};

// This model runs on simulation time, never wall-clock time. The adapter must
// remove a unit's old vision footprint before committing a changed sight bonus,
// then add the new footprint. Merely calculating proximity in the sight getter
// makes native movement/death remove the wrong footprint and leaks fog vision.
class UnitEffectRuntime {
public:
    explicit UnitEffectRuntime(std::vector<UnitEffectRule> rules = {}) : rules_(std::move(rules)) {
        for (size_t index = 0; index < rules_.size(); ++index) {
            if (rules_[index].familyId == UnitEffectId{}) rules_[index].familyId = rules_[index].id;
            if (rules_[index].outputs.empty()) rules_[index].outputs = UnitEffectOutputs(rules_[index]);
            targetRules_[rules_[index].targetObject].push_back(index);
            if (rules_[index].trigger != UnitEffectTrigger::Activated) sourceObjects_.insert(rules_[index].sourceObject);
        }
    }

    void Reset() { active_.clear(); grants_.clear(); grantProviders_.clear(); training_.clear(); ledgers_.clear(); proximityMatches_.clear(); observedActors_.clear(); bonuses_.clear(); entries_.clear(); pendingActions_.clear(); clockSet_ = false; }
    std::vector<UnitEffectActionEvent> TakeActions() { std::vector<UnitEffectActionEvent> actions; actions.swap(pendingActions_); return actions; }
    // Native dispatch confirms the outcome after the engine has completed the
    // permanent operation. A failed action releases only its pending quota.
    void CommitAction(const UnitEffectActionEvent& event, bool success, bool permanentSucceeded = true) {
        const auto rule = FindRule(event.effect);
        if (!rule) return;
        auto& ledger = ledgers_[Family(*rule, event.source)];
        const auto target = Identity(event.target);
        ledger.pending.erase(target);
        if (success) {
            ledger.successful.insert(target);
            if (permanentSucceeded && !event.actions.empty()) ledger.permanent.insert(target);
        }
    }
    void RetainActionEntry(const UnitEffectId& effect, const UnitEffectSnapshot& actor) {
        const auto original = std::find_if(rules_.begin(), rules_.end(), [&](const auto& rule) { return rule.id == effect; });
        if (original == rules_.end() || original->trigger != UnitEffectTrigger::Proximity) return;
        // Native replacement and creation inside an aura do not cross its
        // boundary. Inherit that entry until the actor actually leaves, also
        // preventing an aura from recursively spawning its own target type.
        for (const auto& rule : rules_)
            if (rule.trigger == UnitEffectTrigger::Proximity && rule.targetObject == actor.object &&
                rule.sourceObject == original->sourceObject && rule.civilization == original->civilization && rule.name == original->name &&
                rule.targetPlayers == original->targetPlayers && rule.actions.size() == original->actions.size() &&
                std::equal(rule.actions.begin(), rule.actions.end(), original->actions.begin(), SameUnitEffectAction))
                entries_[{ actor.id, rule.id }] = actor;
    }
    void ForgetUnit(uint32_t unit) {
        bonuses_.erase(unit);
        proximityMatches_.erase(unit);
        observedActors_.erase(unit);
        for (auto it = training_.begin(); it != training_.end();)
            if (it->first.first == unit || it->second.source.id == unit) it = training_.erase(it); else ++it;
        for (auto it = grantProviders_.begin(); it != grantProviders_.end();)
            if (it->first.first == unit || it->second.id == unit) {
                grants_.erase(it->first); it = grantProviders_.erase(it);
            } else ++it;
        for (auto it = entries_.begin(); it != entries_.end();) if (it->first.first == unit) it = entries_.erase(it); else ++it;
        for (auto it = grants_.begin(); it != grants_.end();)
            if (it->first.first == unit) it = grants_.erase(it);
            else ++it;
        for (auto it = active_.begin(); it != active_.end();)
            if (it->first.first == unit) it = active_.erase(it);
            else ++it;
    }

    bool Activate(const UnitEffectId& effect, const UnitEffectSnapshot& actor, uint32_t simulationMilliseconds) {
        return ActivateGroup(effect, { actor }, actor.player, simulationMilliseconds) != 0;
    }

    size_t ActivateGroup(const UnitEffectId& effect, const std::vector<UnitEffectSnapshot>& actors,
        uint32_t expectedPlayer, uint32_t simulationMilliseconds) {
        return ActivateGroups({ effect }, actors, expectedPlayer, simulationMilliseconds);
    }

    size_t ActivateGroups(const std::vector<UnitEffectId>& effects, const std::vector<UnitEffectSnapshot>& actors,
        uint32_t expectedPlayer, uint32_t simulationMilliseconds) {
        ObserveClock(simulationMilliseconds);
        bool allActive = true;
        const auto eligible = ActivationTargets(effects, actors, expectedPlayer, simulationMilliseconds, allActive);
        // A mixed selection turns on together; clicking an entirely active
        // toggle group turns it off together. Timed groups restart together.
        for (const auto& entry : eligible) {
            const auto& actor = *entry.second.first;
            const auto& rule = *entry.second.second;
            if (rule.activation == UnitEffectActivation::Toggle && allActive) active_.erase(entry.first);
            else if (rule.activation == UnitEffectActivation::Toggle && ActiveFor(rule, actor, simulationMilliseconds)) {
                // Joining an already-on recipient to a mixed selection does
                // not restart its actions, charge it again or consume quota.
                continue;
            }
            else {
                const auto source = ActivationProvider(rule, actor);
                if (!rule.actions.empty()) QueueAction(rule, source, actor);
                else {
                    // Stat-only activation has already been accepted by the
                    // caller's resource preflight and needs no native action.
                    auto& ledger = ledgers_[Family(rule, source)];
                    ledger.successful.insert(Identity(actor));
                }
                if (rule.activation != UnitEffectActivation::Instant)
                    active_[entry.first] = { actor.player, actor.object, actor.civilization, actor.generation, simulationMilliseconds,
                        rule.activation == UnitEffectActivation::Timed ? rule.durationMilliseconds : 0u,
                        rule.trigger == UnitEffectTrigger::ProximityAbility };
            }
        }
        return eligible.size();
    }

    size_t CountActivationTargets(const std::vector<UnitEffectId>& effects, const std::vector<UnitEffectSnapshot>& actors,
        uint32_t expectedPlayer, uint32_t simulationMilliseconds) {
        ObserveClock(simulationMilliseconds);
        bool allActive = true;
        const auto eligible = ActivationTargets(effects, actors, expectedPlayer, simulationMilliseconds, allActive);
        size_t starts = 0;
        // Resource preflight counts new starts only. Turning a complete toggle
        // group off is free; already-on members of a mixed group remain on.
        for (const auto& entry : eligible) {
            const auto& actor = *entry.second.first;
            const auto& rule = *entry.second.second;
            if (rule.activation != UnitEffectActivation::Toggle || (!allActive && !ActiveFor(rule, actor, simulationMilliseconds))) ++starts;
        }
        return starts;
    }

    std::vector<UnitEffectTransition> Evaluate(const std::vector<UnitEffectSnapshot>& units, uint32_t simulationMilliseconds) {
        ObserveClock(simulationMilliseconds);
        std::map<uint32_t, const UnitEffectSnapshot*> live;
        for (const auto& unit : units) if (unit.alive) live.emplace(unit.id, &unit);
        std::map<uint32_t, UnitEffectSnapshot> observedActors;
        for (const auto& entry : live) {
            const auto& actor = *entry.second;
            if (sourceObjects_.find(actor.object) != sourceObjects_.end() || targetRules_.find(actor.object) != targetRules_.end())
                observedActors.emplace(actor.id, actor);
        }
        std::map<SpatialKey, std::vector<const UnitEffectSnapshot*>> sources;
        for (const auto& entry : live) {
            const auto& unit = *entry.second;
            if (sourceObjects_.find(unit.object) != sourceObjects_.end())
                sources[{ unit.player, unit.civilization, unit.object, Cell(unit.x), Cell(unit.y) }].push_back(&unit);
        }
        const auto readyProximity = PrepareProximity(live, sources, simulationMilliseconds);
        for (auto it = active_.begin(); it != active_.end();) {
            const auto unit = live.find(it->first.first);
            const auto& state = it->second;
            if (unit == live.end() || unit->second->player != state.player || unit->second->object != state.object ||
                unit->second->civilization != state.civilization || unit->second->generation != state.generation ||
                (state.proximityGranted && !Granted(it->first.second, *unit->second)) ||
                (state.duration && simulationMilliseconds - state.started >= state.duration)) it = active_.erase(it);
            else ++it;
        }
        std::vector<UnitEffectTransition> changes;
        std::map<uint32_t, std::map<UnitEffectStat, float>> next;
        std::map<uint32_t, MatchedProximity> proximityMatches;
        std::map<std::pair<uint32_t, UnitEffectId>, UnitEffectSnapshot> entries;
        for (const auto& entry : live) {
            const auto& target = *entry.second;
            MatchedProximity matched{ target, {} };
            std::map<UnitEffectStat, double> totals;
            const auto matchingRules = targetRules_.find(target.object);
            if (matchingRules == targetRules_.end()) {
                AppendChanges(target.id, {}, changes);
                continue;
            }
            for (const auto index : matchingRules->second) {
                const auto& rule = rules_[index];
                if (rule.trigger != UnitEffectTrigger::Proximity && target.civilization != rule.civilization) continue;
                bool applies = false;
                if (rule.trigger != UnitEffectTrigger::Proximity) {
                    applies = active_.find(std::make_pair(target.id, rule.id)) != active_.end();
                } else {
                    const auto key = std::make_pair(target.id, rule.id);
                    const auto inherited = entries_.find(key);
                    if (training_.count(key) && inherited != entries_.end() && SameIdentity(inherited->second, target))
                        entries.emplace(key, target);
                    const auto ready = readyProximity.find(key);
                    const auto provider = ready == readyProximity.end() ? nullptr : &ready->second;
                    applies = provider != nullptr;
                    if (applies && !rule.actions.empty()) {
                        entries.emplace(key, target);
                        const auto previous = entries_.find(key);
                        if (previous == entries_.end() || !SameIdentity(previous->second, target))
                            applies = QueueAction(rule, *provider, target);
                    }
                    if (applies) matched.rules.push_back(index);
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
            if (!matched.rules.empty()) proximityMatches.emplace(target.id, std::move(matched));
        }
        // Missing/dead units have already had their footprint removed by the
        // native game. Never dereference an old unit pointer from this cache.
        bonuses_.swap(next);
        proximityMatches_.swap(proximityMatches);
        entries_.swap(entries);
        observedActors_.swap(observedActors);
        return changes;
    }

    // Describe the last evaluated proximity state, rather than inferring rule
    // membership from summed bonuses that can cancel or clamp to the same value.
    std::vector<UnitEffectInfo> ProximityEffects(const UnitEffectSnapshot& actor) const {
        std::vector<UnitEffectInfo> result;
        if (!actor.alive || actor.id == UINT32_MAX) return result;
        const auto matching = targetRules_.find(actor.object);
        if (matching == targetRules_.end()) return result;
        const auto proximity = proximityMatches_.find(actor.id);
        if (proximity != proximityMatches_.end() && SameIdentity(actor, proximity->second.actor))
            for (const auto index : proximity->second.rules) result.push_back({ rules_[index], true });
        for (const auto index : matching->second) {
            const auto& rule = rules_[index];
            const auto key = std::make_pair(actor.id, rule.id);
            if (rule.trigger == UnitEffectTrigger::ProximityAbility && Granted(rule.id, actor)) {
                const auto state = active_.find(key);
                const bool active = state != active_.end() && state->second.player == actor.player &&
                    state->second.object == actor.object && state->second.civilization == actor.civilization &&
                    state->second.generation == actor.generation;
                result.push_back({ rule, active });
            }
        }
        std::sort(result.begin(), result.end(), [](const auto& left, const auto& right) { return left.rule.id < right.rule.id; });
        return result;
    }

    // Describe an observed emitter's capabilities independently of whether it
    // currently has any recipients or receives another emitter's effect.
    std::vector<UnitEffectInfo> ProvidedProximityEffects(const UnitEffectSnapshot& actor) const {
        std::vector<UnitEffectInfo> result;
        if (!actor.alive || actor.id == UINT32_MAX) return result;
        const auto observed = observedActors_.find(actor.id);
        if (observed == observedActors_.end() || !SameIdentity(actor, observed->second)) return result;
        for (const auto& rule : rules_)
            if ((rule.trigger == UnitEffectTrigger::Proximity || rule.trigger == UnitEffectTrigger::ProximityAbility) &&
                rule.sourceObject == actor.object && rule.civilization == actor.civilization)
                result.push_back({ rule, false, true, false });
        std::sort(result.begin(), result.end(), [](const auto& left, const auto& right) { return left.rule.id < right.rule.id; });
        return result;
    }

    float Bonus(uint32_t unit, UnitEffectStat stat = UnitEffectStat::LineOfSight) const {
        const auto found = bonuses_.find(unit);
        if (found == bonuses_.end()) return 0.0f;
        const auto value = found->second.find(stat);
        return value == found->second.end() ? 0.0f : value->second;
    }

    bool Granted(const UnitEffectId& effect, const UnitEffectSnapshot& actor) const {
        const auto found = grants_.find(std::make_pair(actor.id, effect));
        return actor.alive && found != grants_.end() && found->second.player == actor.player &&
            found->second.object == actor.object && found->second.civilization == actor.civilization &&
            found->second.generation == actor.generation;
    }

private:
    using ActionKey = std::pair<uint32_t, UnitEffectId>;
    using ActorIdentity = std::tuple<uint32_t, uint32_t, uint32_t, uint32_t, uint64_t>;
    using FamilyKey = std::pair<ActorIdentity, UnitEffectId>;
    struct Ledger { std::set<ActorIdentity> successful, pending, permanent; };
    struct Training { UnitEffectSnapshot target, source; uint32_t started; };
    using ActivationPlan = std::map<ActionKey, std::pair<const UnitEffectSnapshot*, const UnitEffectRule*>>;
    static ActorIdentity Identity(const UnitEffectSnapshot& actor) {
        return { actor.id, actor.player, actor.civilization, actor.object, actor.generation };
    }
    static FamilyKey Family(const UnitEffectRule& rule, const UnitEffectSnapshot& source) {
        return { Identity(source), rule.familyId };
    }
    const UnitEffectRule* FindRule(const UnitEffectId& effect) const {
        const auto found = std::find_if(rules_.begin(), rules_.end(), [&](const auto& rule) { return rule.id == effect; });
        return found == rules_.end() ? nullptr : &*found;
    }
    const Ledger* FindLedger(const FamilyKey& key) const {
        const auto found = ledgers_.find(key);
        return found == ledgers_.end() ? nullptr : &found->second;
    }
    bool Completed(const UnitEffectRule& rule, const UnitEffectSnapshot& source, const UnitEffectSnapshot& target) const {
        const auto ledger = FindLedger(Family(rule, source));
        return ledger && ledger->successful.count(Identity(target)) != 0;
    }
    bool CompletedPermanentAction(const UnitEffectRule& rule, const UnitEffectSnapshot& source, const UnitEffectSnapshot& target) const {
        const auto ledger = FindLedger(Family(rule, source));
        return ledger && ledger->permanent.count(Identity(target)) != 0;
    }
    bool HasTotalRoom(const UnitEffectRule& rule, const UnitEffectSnapshot& source, const UnitEffectSnapshot& target,
        const std::map<FamilyKey, std::set<ActorIdentity>>& planned = {}) const {
        if (!rule.maxAffectedTargets) return true;
        const auto family = Family(rule, source);
        const auto targetId = Identity(target);
        const auto ledger = FindLedger(family);
        if (ledger && (ledger->successful.count(targetId) || ledger->pending.count(targetId))) return true;
        const auto reserved = planned.find(family);
        if (reserved != planned.end() && reserved->second.count(targetId)) return true;
        const size_t count = (ledger ? ledger->successful.size() + ledger->pending.size() : 0) +
            (reserved == planned.end() ? 0 : reserved->second.size());
        return count < rule.maxAffectedTargets;
    }
    bool ActiveFor(const UnitEffectRule& rule, const UnitEffectSnapshot& actor, uint32_t now) const {
        const auto found = active_.find({ actor.id, rule.id });
        if (found == active_.end()) return false;
        const auto& state = found->second;
        return state.player == actor.player && state.object == actor.object && state.civilization == actor.civilization &&
            state.generation == actor.generation && (!state.duration || now - state.started < state.duration);
    }
    UnitEffectSnapshot ActivationProvider(const UnitEffectRule& rule, const UnitEffectSnapshot& actor) const {
        if (rule.trigger == UnitEffectTrigger::ProximityAbility) {
            const auto found = grantProviders_.find({ actor.id, rule.id });
            if (found != grantProviders_.end()) return found->second;
        }
        return actor;
    }
    ActivationPlan ActivationTargets(const std::vector<UnitEffectId>& effects, const std::vector<UnitEffectSnapshot>& actors,
        uint32_t expectedPlayer, uint32_t now, bool& allActive) const {
        ActivationPlan candidates;
        allActive = true;
        for (const auto& rule : rules_) {
            if (rule.trigger == UnitEffectTrigger::Proximity || std::find(effects.begin(), effects.end(), rule.id) == effects.end()) continue;
            for (const auto& actor : actors) {
                if (actor.id == UINT32_MAX || !actor.alive || actor.player != expectedPlayer || actor.object != rule.targetObject ||
                    actor.civilization != rule.civilization || (rule.trigger == UnitEffectTrigger::ProximityAbility && !Granted(rule.id, actor))) continue;
                candidates.emplace(std::make_pair(actor.id, rule.id), std::make_pair(&actor, &rule));
                if (!ActiveFor(rule, actor, now)) allActive = false;
            }
        }
        ActivationPlan result;
        std::map<FamilyKey, std::set<ActorIdentity>> planned;
        for (const auto& candidate : candidates) {
            const auto& actor = *candidate.second.first;
            const auto& rule = *candidate.second.second;
            if (rule.activation == UnitEffectActivation::Toggle && ActiveFor(rule, actor, now)) { result.insert(candidate); continue; }
            const auto source = ActivationProvider(rule, actor);
            const auto family = Family(rule, source);
            const auto ledger = FindLedger(family);
            if (rule.maxAffectedTargets && ledger && ledger->pending.count(Identity(actor))) continue;
            if (!HasTotalRoom(rule, source, actor, planned)) continue;
            result.insert(candidate);
            if (!Completed(rule, source, actor)) planned[family].insert(Identity(actor));
        }
        return result;
    }
    bool QueueAction(const UnitEffectRule& rule, const UnitEffectSnapshot& source, const UnitEffectSnapshot& target) {
        if (!HasTotalRoom(rule, source, target)) return false;
        auto& ledger = ledgers_[Family(rule, source)];
        const auto identity = Identity(target);
        if (rule.maxAffectedTargets && ledger.pending.count(identity)) return false;
        if (rule.maxAffectedTargets && !ledger.successful.count(identity)) ledger.pending.insert(identity);
        pendingActions_.push_back({ rule.id, source, target, rule.actions, rule.resourceCosts, !rule.outputs.empty() });
        return true;
    }
    std::map<ActionKey, UnitEffectSnapshot> PrepareProximity(
        const std::map<uint32_t, const UnitEffectSnapshot*>& live,
        const std::map<std::tuple<uint32_t, uint32_t, uint32_t, int64_t, int64_t>, std::vector<const UnitEffectSnapshot*>>& sources,
        uint32_t now) {
        std::map<ActionKey, Training> training;
        std::map<ActionKey, UnitEffectSnapshot> ready, grants, grantProviders;
        std::map<FamilyKey, std::set<ActorIdentity>> concurrent;
        using Candidate = std::pair<const UnitEffectRule*, const UnitEffectSnapshot*>;
        std::map<ActionKey, Candidate> candidates;
        for (const auto& entry : live) {
            const auto& target = *entry.second;
            const auto matching = targetRules_.find(target.object);
            if (matching == targetRules_.end()) continue;
            for (const auto index : matching->second) {
                const auto& rule = rules_[index];
                if (rule.trigger == UnitEffectTrigger::Activated ||
                    (rule.targetPlayers == UnitEffectTargetPlayers::Own && target.civilization != rule.civilization)) continue;
                candidates.emplace(std::make_pair(target.id, rule.id), std::make_pair(&rule, &target));
            }
        }
        const auto admit = [&](const ActionKey& key, const UnitEffectRule& rule, const UnitEffectSnapshot& target,
            const UnitEffectSnapshot& provider, uint32_t started) {
            if (!HasTotalRoom(rule, provider, target)) return false;
            auto& admitted = concurrent[Family(rule, provider)];
            const auto identity = Identity(target);
            // Successful temporary/stat-only activation still occupies a live
            // recipient slot. Only finishing a permanent native action frees
            // training capacity while that recipient remains in the radius.
            const bool finished = CompletedPermanentAction(rule, provider, target);
            if (!finished && rule.maxConcurrentTargets && !admitted.count(identity) && admitted.size() >= rule.maxConcurrentTargets) return false;
            if (!finished) admitted.insert(identity);
            training.emplace(key, Training{ target, provider, started });
            if (now - started >= rule.delayMilliseconds) {
                ready.emplace(key, provider);
                if (rule.trigger == UnitEffectTrigger::ProximityAbility) {
                    grants.emplace(key, target);
                    grantProviders.emplace(key, provider);
                }
            }
            return true;
        };
        // Preserve every valid warming/ready reservation before admitting any
        // new arrival. A lower-ID unit or provider entering nearby must not
        // steal an existing slot or restart somebody else's countdown.
        for (const auto& previous : training_) {
            const auto candidate = candidates.find(previous.first);
            if (candidate == candidates.end()) continue;
            const auto& rule = *candidate->second.first;
            const auto& target = *candidate->second.second;
            if (!SameIdentity(previous.second.target, target)) continue;
            const auto providers = NearbyProviders(rule, target, sources);
            const auto provider = std::find_if(providers.begin(), providers.end(), [&](const auto source) {
                return SameIdentity(previous.second.source, *source);
            });
            if (provider != providers.end()) admit(previous.first, rule, target, **provider, previous.second.started);
        }
        // New recipients and unavoidable provider handovers use a deterministic
        // ID order. Multiple affected-type rules share one source/family cap.
        for (const auto& candidate : candidates) {
            if (training.count(candidate.first)) continue;
            const auto& rule = *candidate.second.first;
            const auto& target = *candidate.second.second;
            for (const auto provider : NearbyProviders(rule, target, sources))
                if (admit(candidate.first, rule, target, *provider, now)) break;
        }
        training_.swap(training);
        grants_.swap(grants);
        grantProviders_.swap(grantProviders);
        return ready;
    }
    struct MatchedProximity { UnitEffectSnapshot actor; std::vector<size_t> rules; };
    static bool SameIdentity(const UnitEffectSnapshot& left, const UnitEffectSnapshot& right) {
        return left.alive && right.alive && left.id == right.id && left.player == right.player &&
            left.object == right.object && left.civilization == right.civilization && left.generation == right.generation;
    }
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
    static std::vector<const UnitEffectSnapshot*> NearbyProviders(const UnitEffectRule& rule, const UnitEffectSnapshot& target,
        const std::map<SpatialKey, std::vector<const UnitEffectSnapshot*>>& sources) {
        const auto radius = int64_t(rule.radiusTiles);
        std::vector<const UnitEffectSnapshot*> found;
        for (uint32_t player = 0; player < 16; ++player) {
            if ((rule.targetPlayers == UnitEffectTargetPlayers::Own && player != target.player) ||
                (rule.targetPlayers == UnitEffectTargetPlayers::Other && player == target.player)) continue;
        for (auto x = Cell(int64_t(target.x) - radius); x <= Cell(int64_t(target.x) + radius); ++x)
            for (auto y = Cell(int64_t(target.y) - radius); y <= Cell(int64_t(target.y) + radius); ++y) {
                const auto nearby = sources.find({ player, rule.civilization, rule.sourceObject, x, y });
                if (nearby == sources.end()) continue;
                for (const auto source : nearby->second) {
                    if (source->id == target.id) continue;
                    const auto dx = int64_t(source->x) - target.x;
                    const auto dy = int64_t(source->y) - target.y;
                    if (dx < -radius || dx > radius || dy < -radius || dy > radius) continue;
                    if (dx * dx + dy * dy <= radius * radius) found.push_back(source);
                }
            }
        }
        std::sort(found.begin(), found.end(), [](const auto left, const auto right) {
            return std::tie(left->player, left->id) < std::tie(right->player, right->id);
        });
        return found;
    }
    struct Active { uint32_t player; uint32_t object; uint32_t civilization; uint64_t generation; uint32_t started; uint32_t duration; bool proximityGranted; };
    void ObserveClock(uint32_t now) {
        // A backwards simulation clock means a new/loaded match. Dynamic
        // activation state is intentionally discarded until save integration
        // exists. The native adapter clears its cached sight at world teardown.
        if (clockSet_ && now < clock_) Reset();
        clock_ = now;
        clockSet_ = true;
    }
    std::vector<UnitEffectRule> rules_;
    std::map<uint32_t, std::vector<size_t>> targetRules_;
    std::set<uint32_t> sourceObjects_;
    std::map<std::pair<uint32_t, UnitEffectId>, Active> active_;
    std::map<std::pair<uint32_t, UnitEffectId>, UnitEffectSnapshot> grants_;
    std::map<ActionKey, UnitEffectSnapshot> grantProviders_;
    std::map<ActionKey, Training> training_;
    std::map<FamilyKey, Ledger> ledgers_;
    std::map<uint32_t, MatchedProximity> proximityMatches_;
    std::map<uint32_t, UnitEffectSnapshot> observedActors_;
    std::map<uint32_t, std::map<UnitEffectStat, float>> bonuses_;
    std::map<std::pair<uint32_t, UnitEffectId>, UnitEffectSnapshot> entries_;
    std::vector<UnitEffectActionEvent> pendingActions_;
    uint32_t clock_ = 0;
    bool clockSet_ = false;
};
}
