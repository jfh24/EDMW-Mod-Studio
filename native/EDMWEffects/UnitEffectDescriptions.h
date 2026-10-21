#pragma once
#include "UnitEffectRuntime.h"
#include <string>

namespace edmw {
inline std::string UnitEffectSecondsText(uint32_t milliseconds) {
    auto seconds = std::to_string(milliseconds / 1000);
    if (milliseconds % 1000) {
        auto fraction = std::to_string(1000 + milliseconds % 1000).substr(1);
        while (fraction.back() == '0') fraction.pop_back();
        seconds += "." + fraction;
    }
    return seconds;
}

inline std::string UnitEffectCostDescription(const std::array<uint32_t, 4>& costs) {
    const char* names[] = { "food", "wood", "gold", "stone" };
    std::string text;
    for (size_t index = 0; index < costs.size(); ++index) if (costs[index]) {
        if (!text.empty()) text += ", ";
        text += std::to_string(costs[index]) + " " + names[index];
    }
    return text.empty() ? "" : " Costs " + text + " per activation.";
}

inline std::string UnitEffectActionDescription(const UnitEffectAction& action) {
    const auto object = action.objectName.empty() ? std::string("the chosen unit or building") : action.objectName;
    const auto owner = action.owner == UnitEffectOwner::SpecificPlayer ? "Player " + std::to_string(action.playerSlot) :
        action.owner == UnitEffectOwner::AffectedPlayer ? std::string("the affected player") : std::string("the source player");
    switch (action.kind) {
    case UnitEffectActionKind::OwnershipTransfer: return "Transfer ownership to " + owner;
    case UnitEffectActionKind::Transform: return "Change into " + object;
    case UnitEffectActionKind::Spawn: return "Spawn " + std::to_string(action.count) + " x " + object + " for " + owner;
    default: return "";
    }
}

inline std::string UnitEffectDescriptionHeading(const UnitEffectInfo& effect,
    const std::map<UnitEffectId, std::string>& captions) {
    const auto caption = captions.find(effect.rule.id);
    const auto heading = !effect.rule.name.empty() ? effect.rule.name : caption == captions.end() ? "" : caption->second;
    const auto first = heading.find_first_not_of(" \t\r\n");
    return first != std::string::npos ? heading.substr(first, heading.find_last_not_of(" \t\r\n") - first + 1) :
        effect.rule.trigger == UnitEffectTrigger::ProximityAbility ? "Granted ability" : "Proximity effect";
}

inline bool SameProximityDescription(const UnitEffectInfo& left, const UnitEffectInfo& right,
    const std::map<UnitEffectId, std::string>& captions) {
    const bool granted = left.rule.trigger == UnitEffectTrigger::ProximityAbility;
    if (left.provides && right.provides) {
        if (granted && left.receives && right.receives && left.active != right.active) return false;
    } else if (left.provides != right.provides || left.receives != right.receives ||
        (granted && left.active != right.active)) return false;
    if (left.rule.civilization != right.rule.civilization || left.rule.sourceObject != right.rule.sourceObject ||
        left.rule.radiusTiles != right.rule.radiusTiles || left.rule.trigger != right.rule.trigger ||
        left.rule.activation != right.rule.activation || left.rule.targetPlayers != right.rule.targetPlayers ||
        left.rule.familyId != right.rule.familyId || left.rule.delayMilliseconds != right.rule.delayMilliseconds ||
        left.rule.maxAffectedTargets != right.rule.maxAffectedTargets || left.rule.maxConcurrentTargets != right.rule.maxConcurrentTargets ||
        left.rule.resourceCosts != right.rule.resourceCosts ||
        (left.rule.trigger == UnitEffectTrigger::ProximityAbility && left.rule.grantFamilyTechnology != right.rule.grantFamilyTechnology) ||
        UnitEffectDescriptionHeading(left, captions) != UnitEffectDescriptionHeading(right, captions)) return false;
    const auto leftOutputs = UnitEffectOutputs(left.rule), rightOutputs = UnitEffectOutputs(right.rule);
    return left.rule.actions.size() == right.rule.actions.size() &&
        std::equal(left.rule.actions.begin(), left.rule.actions.end(), right.rule.actions.begin(), SameUnitEffectAction) &&
        leftOutputs.size() == rightOutputs.size() && std::equal(leftOutputs.begin(), leftOutputs.end(), rightOutputs.begin(),
        [](const auto& first, const auto& second) { return first.stat == second.stat && first.amount == second.amount; });
}

// Gameplay text only. Ordinary proximity rules in older exports have no caption;
// their actual outputs still describe the effect without exposing internal IDs.
inline std::string DescribeProximityEffects(const std::vector<UnitEffectInfo>& effects,
    const std::map<UnitEffectId, std::string>& captions) {
    if (effects.empty()) return "";
    struct Group { UnitEffectInfo effect; std::vector<const UnitEffectInfo*> members; };
    std::vector<Group> groups;
    for (const auto& effect : effects) {
        const auto group = std::find_if(groups.begin(), groups.end(), [&](const auto& item) {
            return SameProximityDescription(item.effect, effect, captions);
        });
        if (group == groups.end()) groups.push_back({ effect, { &effect } });
        else {
            // A provider-only row has no On/Off state. Preserve the first real
            // recipient state, then compare future rows against that merged state.
            if (!group->effect.receives && effect.receives) group->effect.active = effect.active;
            group->effect.receives |= effect.receives;
            group->effect.provides |= effect.provides;
            group->members.push_back(&effect);
        }
    }
    std::string text;
    for (const auto& group : groups) {
        const auto& effect = group.effect;
        std::string row = UnitEffectDescriptionHeading(effect, captions);
        const bool granted = effect.rule.trigger == UnitEffectTrigger::ProximityAbility;
        const bool instant = effect.rule.activation == UnitEffectActivation::Instant;
        row += granted && effect.receives && !instant ? (effect.active ? " - On\n" : " - Off\n") : "\n";
        std::set<std::string> targets;
        for (const auto* item : group.members) if (!item->rule.targetName.empty()) targets.insert(item->rule.targetName);
        if (!targets.empty()) {
            row += "Targets: ";
            for (const auto& target : targets) row += target + ", ";
            row.resize(row.size() - 2);
            row += ".\n";
        }
        if (effect.provides && !effect.receives) row += granted ? instant ? "Grants an ability: " : "Grants a toggle: " : "Aura: ";
        std::string changes;
        for (const auto& output : UnitEffectOutputs(effect.rule)) {
            if (!changes.empty()) changes += "; ";
            changes += UnitEffectIsLimit(output.stat) ? "Maximum " + std::to_string(static_cast<uint32_t>(output.amount)) :
                UnitEffectAmountText(output.amount);
            changes += " " + std::string(UnitEffectStatName(output.stat));
        }
        for (const auto& action : effect.rule.actions) {
            if (!changes.empty()) changes += "; ";
            changes += UnitEffectActionDescription(action);
        }
        row += changes;
        row += granted && effect.receives && !effect.active ? instant ? ". Click the ability to apply.\n" : ". Toggle to apply.\n" : ".\n";
        row += effect.receives ? "Within " + std::to_string(effect.rule.radiusTiles) + " tiles of a source." :
            "Affects other matching units within " + std::to_string(effect.rule.radiusTiles) + " tiles.";
        if (effect.rule.delayMilliseconds) row += " Stay nearby for " + UnitEffectSecondsText(effect.rule.delayMilliseconds) +
            " continuous seconds; leaving range or changing sources restarts the wait.";
        if (effect.rule.maxConcurrentTargets) row += " Each source affects up to " + std::to_string(effect.rule.maxConcurrentTargets) + " recipients at once.";
        if (effect.rule.maxAffectedTargets) row += " Each source has a total recipient limit of " + std::to_string(effect.rule.maxAffectedTargets) + ".";
        row += UnitEffectCostDescription(effect.rule.resourceCosts);
        if (effect.rule.targetPlayers == UnitEffectTargetPlayers::Other) row += " Other players' targets only.";
        else if (effect.rule.targetPlayers == UnitEffectTargetPlayers::Any) row += " Targets from any player.";
        if (granted) row += UnitEffectOutputs(effect.rule).empty() ? " Leaving range removes the ability." :
            " Leaving range removes the ability and its bonuses.";
        if (!effect.rule.actions.empty()) row += granted ? " Actions are permanent." :
            " Actions happen once on entering range and are permanent.";
        if (effect.provides && effect.receives) row += " Also provides this effect to other nearby units.";
        // Bound native text allocation; do not cut a description mid-value.
        if (text.size() + row.size() > 16000) { text += "\n\nMore nearby effects are active."; break; }
        if (!text.empty()) text += "\n\n";
        text += row;
    }
    return text;
}
}
