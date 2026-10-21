#include "UnitEffectDescriptions.h"
#include <cassert>
#include <cstdio>

int main() {
    using namespace edmw;
    assert(DescribeProximityEffects({}, {}).empty());
    UnitEffectRule aura;
    aura.id[0] = 1; aura.radiusTiles = 5;
    aura.outputs = { { UnitEffectStat::Health, 100 }, { UnitEffectStat::LineOfSight, 3 } };
    auto grant = aura; grant.id[0] = 2; grant.trigger = UnitEffectTrigger::ProximityAbility;
    const std::map<UnitEffectId, std::string> captions{ { grant.id, "Battle focus" } };
    const auto passive = DescribeProximityEffects({ {aura, true} }, captions);
    assert(passive.find("Proximity effect") != std::string::npos && passive.find("+100 health") != std::string::npos &&
        passive.find("+3 line of sight") != std::string::npos && passive.find("5 tiles") != std::string::npos);
    const std::map<UnitEffectId, std::string> authoredNames{ { aura.id, "Sherman Support" }, { grant.id, "Battle focus" } };
    assert(DescribeProximityEffects({}, authoredNames).empty()); // Named packages still hide the panel when this actor has no effects.
    const auto namedPassive = DescribeProximityEffects({ {aura, true} }, authoredNames);
    assert(namedPassive.find("Sherman Support\n") == 0 && namedPassive.find("Proximity effect") == std::string::npos);
    const auto namedProvider = DescribeProximityEffects({ {aura, false, true, false} }, authoredNames);
    assert(namedProvider.find("Sherman Support\n") == 0 && namedProvider.find("Aura: +100 health") != std::string::npos);
    assert(DescribeProximityEffects({ {aura, true} }, { { aura.id, " \tSherman Support\r\n" } }).find("Sherman Support\n") == 0);
    assert(DescribeProximityEffects({ {aura, true} }, { { aura.id, " \t\r\n" } }).find("Proximity effect\n") == 0);
    const auto off = DescribeProximityEffects({ {grant, false} }, captions);
    const auto on = DescribeProximityEffects({ {grant, true} }, captions);
    assert(off.find("Battle focus - Off") != std::string::npos && off.find("Toggle to apply.") != std::string::npos &&
        off.find("Leaving range removes the ability and its bonuses.") != std::string::npos);
    assert(on.find("Battle focus - On") != std::string::npos && on.find("Toggle to apply.") == std::string::npos);
    const auto provides = DescribeProximityEffects({ {grant, false, true, false} }, captions);
    assert(provides.find("Battle focus\n") == 0 && provides.find("Grants a toggle:") != std::string::npos &&
        provides.find("5 tiles") != std::string::npos && provides.find(" - Off") == std::string::npos);
    const auto mutual = DescribeProximityEffects({ {aura, true, true, true} }, captions);
    assert(mutual.find("Also provides this effect") != std::string::npos);
    auto namedAura = aura; namedAura.name = "Sherman guard";
    auto namedGrant = grant; namedGrant.name = "Forward support";
    assert(DescribeProximityEffects({ {namedAura, true} }, authoredNames).find("Sherman guard\n") == 0);
    assert(DescribeProximityEffects({ {namedGrant, false} }, captions).find("Forward support - Off\n") == 0);
    assert(DescribeProximityEffects({ {namedGrant, true} }, {}).find("Forward support - On\n") == 0);
    namedAura.targetName = "Sherman V";
    auto otherTarget = namedAura; otherTarget.id[0] = 3; otherTarget.targetObject = 878; otherTarget.targetName = "Citizen";
    const auto shared = DescribeProximityEffects({ {namedAura, false, true, false}, {otherTarget, false, true, false},
        {otherTarget, false, true, false} }, {});
    assert(shared.find("Sherman guard\n") == 0 && shared.find("Sherman guard", 1) == std::string::npos);
    assert(shared.find("+100 health") != std::string::npos && shared.find("+100 health", shared.find("+100 health") + 1) == std::string::npos);
    assert(shared.find("Targets: Citizen, Sherman V.\n") != std::string::npos && shared.find("878") == std::string::npos && shared.find("777") == std::string::npos);
    assert(shared.find("Citizen", shared.find("Citizen") + 1) == std::string::npos);
    const auto mutualShared = DescribeProximityEffects({ {otherTarget, false, true, false}, {namedAura, true, true, true} }, {});
    assert(mutualShared.find("Sherman guard\n") == 0 && mutualShared.find("Targets: Citizen, Sherman V.\n") != std::string::npos);
    assert(mutualShared.find("+100 health") != std::string::npos &&
        mutualShared.find("+100 health", mutualShared.find("+100 health") + 1) == std::string::npos);
    assert(mutualShared.find("Within 5 tiles") != std::string::npos && mutualShared.find("Also provides this effect") != std::string::npos &&
        mutualShared.find("Affects other matching units") == std::string::npos);
    const auto separateState = DescribeProximityEffects({ {namedGrant, false}, {namedGrant, true} }, {});
    assert(separateState.find("Forward support - Off") != std::string::npos && separateState.find("Forward support - On") != std::string::npos);
    auto changedOutput = otherTarget; changedOutput.outputs[0].amount = 200;
    assert(!SameProximityDescription({namedAura, false, true, false}, {changedOutput, false, true, false}, {}));
    auto changedSource = otherTarget; ++changedSource.sourceObject;
    assert(!SameProximityDescription({namedAura, false, true, false}, {changedSource, false, true, false}, {}));
    auto familyGrant = namedGrant; familyGrant.grantFamilyTechnology = 10000;
    auto secondFamily = familyGrant; secondFamily.grantFamilyTechnology = 10001;
    assert(!SameProximityDescription({familyGrant, false}, {secondFamily, false}, {}));
    familyGrant.targetName = "Sherman V";
    auto grantOtherTarget = familyGrant; grantOtherTarget.id[0] = 4; grantOtherTarget.targetObject = 878; grantOtherTarget.targetName = "Citizen";
    const auto mergedGrant = DescribeProximityEffects({ {grantOtherTarget, false, true, false}, {familyGrant, true, true, true} }, {});
    assert(mergedGrant.find("Forward support - On\n") == 0 && mergedGrant.find("Targets: Citizen, Sherman V.\n") != std::string::npos &&
        mergedGrant.find("Forward support", 1) == std::string::npos && mergedGrant.find(" - Off") == std::string::npos);
    const auto conflictingGrantStates = DescribeProximityEffects({ {grantOtherTarget, false, true, false},
        {familyGrant, false, true, true}, {familyGrant, true, true, true} }, {});
    assert(conflictingGrantStates.find("Forward support - Off") != std::string::npos &&
        conflictingGrantStates.find("Forward support - On") != std::string::npos);
    for (uint32_t id = 0; id <= 50; ++id) {
        auto stat = aura; stat.outputs = { {static_cast<UnitEffectStat>(id), 1} };
        const auto description = DescribeProximityEffects({ {stat, true} }, {});
        assert(description.find(UnitEffectStatName(static_cast<UnitEffectStat>(id))) != std::string::npos);
        assert(description.find("Session") == std::string::npos && description.find("simulation") == std::string::npos &&
            description.find("object ID") == std::string::npos);
    }
    UnitEffectRule actions;
    actions.id[0] = 9; actions.name = "Reinforcements"; actions.targetName = "Town Center";
    actions.radiusTiles = 4; actions.activation = UnitEffectActivation::Toggle; actions.durationMilliseconds = 0;
    actions.targetPlayers = UnitEffectTargetPlayers::Any;
    actions.actions = {
        { UnitEffectActionKind::OwnershipTransfer, UINT32_MAX, 1, UnitEffectOwner::SourcePlayer, 1, "" },
        { UnitEffectActionKind::Transform, 777, 1, UnitEffectOwner::SourcePlayer, 1, "Sherman V" },
        { UnitEffectActionKind::Spawn, 878, 3, UnitEffectOwner::SpecificPlayer, 4, "Citizen" }
    };
    const auto actionText = DescribeProximityEffects({ {actions, false, true, false} }, {});
    assert(actionText.find("Reinforcements\n") == 0 && actionText.find("Targets: Town Center.") != std::string::npos &&
        actionText.find("Aura: Transfer ownership to the source player; Change into Sherman V; Spawn 3 x Citizen for Player 4.") != std::string::npos &&
        actionText.find("once on entering range") != std::string::npos && actionText.find("are permanent") != std::string::npos &&
        actionText.find("Targets from any player.") != std::string::npos && actionText.find("line of sight") == std::string::npos &&
        actionText.find("777") == std::string::npos && actionText.find("878") == std::string::npos);
    auto otherActionTarget = actions; otherActionTarget.id[0] = 10; otherActionTarget.targetName = "Citizen";
    const auto sharedActions = DescribeProximityEffects({ {actions, false, true, false}, {otherActionTarget, false, true, false} }, {});
    assert(sharedActions.find("Reinforcements", 1) == std::string::npos &&
        sharedActions.find("Spawn 3 x Citizen", sharedActions.find("Spawn 3 x Citizen") + 1) == std::string::npos);
    auto changedAction = otherActionTarget; changedAction.actions[2].count = 4;
    assert(!SameProximityDescription({actions, false, true, false}, {changedAction, false, true, false}, {}));
    changedAction = otherActionTarget; changedAction.targetPlayers = UnitEffectTargetPlayers::Other;
    assert(!SameProximityDescription({actions, false, true, false}, {changedAction, false, true, false}, {}));
    assert(DescribeProximityEffects({ {changedAction, false, true, false} }, {}).find("Other players' targets only.") != std::string::npos);
    actions.trigger = UnitEffectTrigger::ProximityAbility; actions.targetPlayers = UnitEffectTargetPlayers::Own;
    actions.activation = UnitEffectActivation::Instant;
    const auto instant = DescribeProximityEffects({ {actions, false} }, {});
    assert(instant.find("Reinforcements\n") == 0 && instant.find(" - Off") == std::string::npos &&
        instant.find("Click the ability to apply.") != std::string::npos &&
        instant.find("Leaving range removes the ability.") != std::string::npos &&
        instant.find("Actions are permanent.") != std::string::npos && instant.find("bonuses") == std::string::npos);
    const auto instantProvider = DescribeProximityEffects({ {actions, false, true, false} }, {});
    assert(instantProvider.find("Grants an ability: Transfer ownership") != std::string::npos);
    auto limited = actions;
    limited.delayMilliseconds = 60000; limited.maxAffectedTargets = 5; limited.maxConcurrentTargets = 2;
    limited.resourceCosts[2] = 50;
    const auto limitedText = DescribeProximityEffects({ {limited, false, true, false} }, {});
    assert(limitedText.find("60 continuous seconds") != std::string::npos &&
        limitedText.find("changing sources restarts the wait") != std::string::npos &&
        limitedText.find("2 recipients at once") != std::string::npos && limitedText.find("total recipient limit of 5") != std::string::npos &&
        limitedText.find("Costs 50 gold per activation.") != std::string::npos);
    assert(!SameProximityDescription({actions, false, true, false}, {limited, false, true, false}, {}));
    limited = actions; limited.delayMilliseconds = 1500;
    assert(DescribeProximityEffects({ {limited, false} }, {}).find("1.5 continuous seconds") != std::string::npos);
    assert(UnitEffectSecondsText(1) == "0.001" && UnitEffectSecondsText(1000) == "1");
    assert(UnitEffectCostDescription({ 10,20,30,40 }) == " Costs 10 food, 20 wood, 30 gold, 40 stone per activation.");
    assert(UnitEffectCostDescription({}).empty());
    auto unnamed = actions.actions[2]; unnamed.objectName.clear();
    assert(UnitEffectActionDescription(unnamed).find("the chosen unit or building") != std::string::npos);
    unnamed.owner = UnitEffectOwner::AffectedPlayer;
    assert(UnitEffectActionDescription(unnamed).find("the affected player") != std::string::npos);
    puts("MODS DESCRIPTIONS PASS: empty-state hiding, authored headings, grouped targets, stat and permanent action outputs, toggle/instant state, radius and gameplay-only text.");
}
