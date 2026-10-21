#include "UnitEffectRuntime.h"
#include "UnitEffectAbilityCosts.h"
#include <cassert>
#include <cstdio>

namespace {
edmw::UnitEffectRule TrainingRule(uint8_t id = 100) {
    using namespace edmw;
    UnitEffectRule rule;
    rule.id[0] = id; rule.familyId[0] = 200; rule.civilization = 12;
    rule.sourceObject = 749; rule.targetObject = 777;
    rule.trigger = UnitEffectTrigger::ProximityAbility;
    rule.radiusTiles = 5; rule.activation = UnitEffectActivation::Instant;
    rule.durationMilliseconds = 0; rule.delayMilliseconds = 60000;
    rule.maxAffectedTargets = 3; rule.maxConcurrentTargets = 1;
    rule.resourceCosts = { 0, 0, 50, 0 };
    rule.actions = { { UnitEffectActionKind::Transform, 878, 1, UnitEffectOwner::AffectedPlayer, 1, "Trainee" } };
    return rule;
}
edmw::UnitEffectSnapshot Teacher(uint32_t id = 10) { return { id, 1, 12, 749, 20, 20, true, 1 }; }
edmw::UnitEffectSnapshot Student(uint32_t id = 20) { return { id, 1, 12, 777, 23, 24, true, id }; }

void VerifyContinuousTraining() {
    using namespace edmw;
    const auto rule = TrainingRule();
    auto source = Teacher(); auto target = Student();
    UnitEffectRuntime runtime({ rule });
    runtime.Evaluate({ source, target }, 0);
    assert(!runtime.Granted(rule.id, target));
    runtime.Evaluate({ target, source }, 59999);
    assert(!runtime.Granted(rule.id, target) && runtime.CountActivationTargets({rule.id}, {target}, 1, 59999) == 0);
    runtime.Evaluate({ source, target }, 60000);
    assert(runtime.Granted(rule.id, target));
    assert(runtime.CountActivationTargets({rule.id}, {target, target}, 1, 60000) == 1);
    assert(runtime.CountActivationTargets({rule.id}, {target}, 1, 60000) == 1); // Preflight does not consume a slot.
    assert(runtime.ActivateGroup(rule.id, {target}, 1, 60000) == 1);
    auto events = runtime.TakeActions();
    assert(events.size() == 1 && events[0].source.id == source.id && events[0].target.id == target.id);
    assert(events[0].resourceCosts[2] == 50 && !events[0].hasStatOutputs);
    assert(runtime.CountActivationTargets({rule.id}, {target}, 1, 60000) == 0); // Pending action reserves quota.
    runtime.CommitAction(events[0], false);
    assert(runtime.CountActivationTargets({rule.id}, {target}, 1, 60000) == 1);
    assert(runtime.Activate(rule.id, target, 60000));
    events = runtime.TakeActions(); runtime.CommitAction(events[0], true);
    // Finishing a trainee releases the concurrent slot even before the next
    // snapshot replaces its native actor with the trained unit.
    auto second = Student(21);
    runtime.Evaluate({ target, second, source }, 60001);
    assert(runtime.Granted(rule.id, target) && !runtime.Granted(rule.id, second));
    runtime.Evaluate({ second, source }, 120000);
    assert(!runtime.Granted(rule.id, second));
    runtime.Evaluate({ source, second }, 120001);
    assert(runtime.Granted(rule.id, second));

    UnitEffectRuntime leave({ rule });
    leave.Evaluate({ source, target }, 0);
    target.x = 26; target.y = 20;
    leave.Evaluate({ source, target }, 50000);
    target.x = 23; target.y = 24;
    leave.Evaluate({ source, target }, 50001);
    leave.Evaluate({ source, target }, 110000); assert(!leave.Granted(rule.id, target));
    leave.Evaluate({ source, target }, 110001); assert(leave.Granted(rule.id, target));
    // A different original source starts its own uninterrupted countdown.
    auto otherSource = Teacher(11); otherSource.generation = 2;
    UnitEffectRuntime handover({ rule });
    handover.Evaluate({ source, otherSource, target }, 0);
    handover.Evaluate({ otherSource, target }, 50000);
    handover.Evaluate({ otherSource, target }, 109999); assert(!handover.Granted(rule.id, target));
    handover.Evaluate({ otherSource, target }, 110000); assert(handover.Granted(rule.id, target));
    assert(handover.Activate(rule.id, target, 110000));
    assert(handover.TakeActions()[0].source.id == otherSource.id);
    handover.Evaluate({ otherSource, target }, 5); assert(!handover.Granted(rule.id, target));
    handover.Evaluate({ otherSource, target }, 60005); assert(handover.Granted(rule.id, target));
}

void VerifyTrainingBudgets() {
    using namespace edmw;
    auto rule = TrainingRule(); rule.delayMilliseconds = 0; rule.maxAffectedTargets = 2;
    auto source = Teacher(); auto first = Student(); auto second = Student(21); auto third = Student(22);
    UnitEffectRuntime runtime({ rule });
    runtime.Evaluate({ third, second, first, source }, 0);
    assert(runtime.Granted(rule.id, first) && !runtime.Granted(rule.id, second) && !runtime.Granted(rule.id, third));
    assert(runtime.Activate(rule.id, first, 0));
    auto events = runtime.TakeActions(); runtime.CommitAction(events[0], false);
    runtime.Evaluate({ source, first, second }, 1);
    assert(runtime.Granted(rule.id, first) && !runtime.Granted(rule.id, second)); // Failure keeps training reservation.
    assert(runtime.Activate(rule.id, first, 1)); events = runtime.TakeActions(); runtime.CommitAction(events[0], true);
    runtime.Evaluate({ source, second, third }, 2);
    assert(runtime.Granted(rule.id, second) && !runtime.Granted(rule.id, third));
    assert(runtime.Activate(rule.id, second, 2)); events = runtime.TakeActions(); runtime.CommitAction(events[0], true);
    runtime.Evaluate({ source, third }, 3); assert(!runtime.Granted(rule.id, third)); // Total two reached independently of free concurrency.
    auto otherSource = Teacher(11); otherSource.generation = 2;
    runtime.Evaluate({ source, otherSource, third }, 4);
    assert(runtime.Granted(rule.id, third));
    assert(runtime.Activate(rule.id, third, 4)); events = runtime.TakeActions();
    assert(events[0].source.id == otherSource.id); runtime.CommitAction(events[0], true);
    // Reusing an ID or changing its owner never inherits another source's quota.
    ++source.generation;
    auto fourth = Student(23);
    runtime.Evaluate({ source, fourth }, 5); assert(runtime.Granted(rule.id, fourth));
    assert(runtime.Activate(rule.id, fourth, 5)); events = runtime.TakeActions(); runtime.CommitAction(events[0], true);
    source.player = 2; fourth.player = 2;
    runtime.Evaluate({ source, fourth }, 6); assert(runtime.CountActivationTargets({rule.id}, {fourth}, 2, 6) == 1);

    auto multi = rule; multi.id[0] = 101; multi.targetObject = 878;
    rule.maxConcurrentTargets = multi.maxConcurrentTargets = 3;
    rule.maxAffectedTargets = multi.maxAffectedTargets = 1;
    source = Teacher(); first = Student(); second = Student(21); second.object = 878;
    UnitEffectRuntime family({ rule, multi });
    family.Evaluate({ second, first, source }, 10);
    assert(family.Granted(rule.id, first) && family.Granted(multi.id, second));
    assert(family.CountActivationTargets({multi.id,rule.id}, {second,first,second}, 1, 10) == 1);
    assert(family.ActivateGroups({multi.id,rule.id}, {second,first,second}, 1, 10) == 1);
    events = family.TakeActions(); assert(events.size() == 1 && events[0].target.id == first.id);
    assert(family.CountActivationTargets({multi.id}, {second}, 1, 10) == 0);
    family.CommitAction(events[0], false);
    assert(family.CountActivationTargets({multi.id}, {second}, 1, 10) == 1);
    assert(family.Activate(multi.id, second, 10)); events = family.TakeActions(); family.CommitAction(events[0], true);
    family.Evaluate({ first, source }, 11); assert(!family.Granted(rule.id, first));
    // Concurrent admission is shared across target types even with no total cap.
    rule.maxAffectedTargets = multi.maxAffectedTargets = 0;
    rule.maxConcurrentTargets = multi.maxConcurrentTargets = 1;
    UnitEffectRuntime shared({ rule, multi });
    shared.Evaluate({ source, first, second }, 20);
    assert(shared.Granted(rule.id, first) && !shared.Granted(multi.id, second));
    first.alive = false; shared.Evaluate({ source, first, second }, 21);
    assert(shared.Granted(multi.id, second));
}

void VerifyDelayedAuraAndStatOnlyActivation() {
    using namespace edmw;
    auto rule = TrainingRule(); rule.trigger = UnitEffectTrigger::Proximity;
    rule.activation = UnitEffectActivation::Timed; rule.delayMilliseconds = 1000;
    rule.maxAffectedTargets = rule.maxConcurrentTargets = 0;
    rule.outputs = { { UnitEffectStat::Health, 100 } };
    const auto source = Teacher(); auto target = Student();
    UnitEffectRuntime runtime({ rule });
    runtime.Evaluate({ source, target }, 0); assert(runtime.Bonus(target.id, UnitEffectStat::Health) == 0 && runtime.TakeActions().empty());
    runtime.Evaluate({ source, target }, 999); assert(runtime.TakeActions().empty());
    runtime.Evaluate({ source, target }, 1000);
    auto events = runtime.TakeActions(); assert(events.size() == 1 && events[0].hasStatOutputs && events[0].resourceCosts[2] == 50);
    assert(runtime.Bonus(target.id, UnitEffectStat::Health) == 100);
    runtime.Evaluate({ source, target }, 1001); assert(runtime.TakeActions().empty());
    // A native-created target already inside the aura must not start a spawn
    // chain when its delay finishes.
    auto created = Student(21); runtime.RetainActionEntry(rule.id, created);
    runtime.Evaluate({ source, target, created }, 1002);
    runtime.Evaluate({ source, target, created }, 2002); assert(runtime.TakeActions().empty());
    rule.trigger = UnitEffectTrigger::ProximityAbility; rule.activation = UnitEffectActivation::Toggle;
    rule.actions.clear(); rule.delayMilliseconds = 0; rule.maxAffectedTargets = rule.maxConcurrentTargets = 1;
    UnitEffectRuntime stats({ rule });
    stats.Evaluate({ source, target, created }, 0);
    assert(stats.Granted(rule.id, target) && !stats.Granted(rule.id, created));
    assert(stats.Activate(rule.id, target, 0) && stats.TakeActions().empty());
    stats.Evaluate({ source, created }, 1);
    assert(!stats.Granted(rule.id, created)); // Stat-only accepted activation consumes the total quota.
}

void VerifyChargeableToggleStarts() {
    using namespace edmw;
    auto rule = TrainingRule(); rule.activation = UnitEffectActivation::Toggle;
    rule.delayMilliseconds = 0; rule.maxConcurrentTargets = 0; rule.maxAffectedTargets = 2;
    rule.outputs = { { UnitEffectStat::Health, 100 } };
    const auto source = Teacher(); const auto first = Student(); const auto second = Student(21);
    UnitEffectRuntime runtime({ rule });
    runtime.Evaluate({ source, first, second }, 0);
    assert(runtime.CountActivationTargets({rule.id}, {first}, 1, 0) == 1);
    assert(runtime.Activate(rule.id, first, 0));
    auto events = runtime.TakeActions(); assert(events.size() == 1); runtime.CommitAction(events[0], true);
    runtime.Evaluate({ source, first, second }, 1);
    assert(runtime.Bonus(first.id, UnitEffectStat::Health) == 100);
    assert(runtime.CountActivationTargets({rule.id}, {second,first}, 1, 1) == 1);
    assert(runtime.ActivateGroup(rule.id, {second,first}, 1, 1) == 2);
    events = runtime.TakeActions(); assert(events.size() == 1 && events[0].target.id == second.id);
    runtime.CommitAction(events[0], true);
    runtime.Evaluate({ source, first, second }, 2);
    assert(runtime.Bonus(first.id, UnitEffectStat::Health) == 100 && runtime.Bonus(second.id, UnitEffectStat::Health) == 100);
    assert(runtime.CountActivationTargets({rule.id}, {first,second}, 1, 2) == 0); // Turning off is free at the total cap.
    assert(runtime.ActivateGroup(rule.id, {first,second}, 1, 2) == 2 && runtime.TakeActions().empty());
    runtime.Evaluate({ source, first, second }, 3);
    assert(runtime.Bonus(first.id, UnitEffectStat::Health) == 0 && runtime.Bonus(second.id, UnitEffectStat::Health) == 0);
    assert(runtime.CountActivationTargets({rule.id}, {first,second}, 1, 3) == 2); // Existing distinct trainees can start again.
}

void VerifyStableTrainingReservations() {
    using namespace edmw;
    auto rule = TrainingRule();
    auto source = Teacher(20); auto target = Student(30);
    UnitEffectRuntime providers({ rule });
    providers.Evaluate({ source, target }, 0);
    const auto lowerProvider = Teacher(10);
    providers.Evaluate({ lowerProvider, source, target }, 50000);
    providers.Evaluate({ target, lowerProvider, source }, 60000);
    assert(providers.Granted(rule.id, target));
    assert(providers.Activate(rule.id, target, 60000));
    auto events = providers.TakeActions();
    assert(events.size() == 1 && events[0].source.id == source.id); // Existing provider keeps the sixty-second progress.

    source = Teacher(); target = Student(30); auto lowerTarget = Student(20);
    UnitEffectRuntime recipients({ rule });
    recipients.Evaluate({ source, target }, 0);
    recipients.Evaluate({ lowerTarget, source, target }, 50000);
    recipients.Evaluate({ target, lowerTarget, source }, 60000);
    assert(recipients.Granted(rule.id, target) && !recipients.Granted(rule.id, lowerTarget));
    for (uint32_t tick = 60001; tick < 60100; ++tick) {
        recipients.Evaluate({ lowerTarget, target, source }, tick);
        assert(recipients.Granted(rule.id, target) && !recipients.Granted(rule.id, lowerTarget));
    }
    recipients.Evaluate({ source, lowerTarget }, 61000);
    recipients.Evaluate({ source, lowerTarget }, 120999); assert(!recipients.Granted(rule.id, lowerTarget));
    recipients.Evaluate({ source, lowerTarget }, 121000); assert(recipients.Granted(rule.id, lowerTarget));
}

void VerifyContinuousStatConcurrency() {
    using namespace edmw;
    auto rule = TrainingRule(); rule.trigger = UnitEffectTrigger::Proximity;
    rule.activation = UnitEffectActivation::Timed; rule.delayMilliseconds = 0;
    rule.actions.clear(); rule.outputs = { { UnitEffectStat::Health, 100 } };
    rule.maxAffectedTargets = 0; rule.maxConcurrentTargets = 2;
    const auto source = Teacher(); const auto first = Student(30); const auto second = Student(31);
    const auto third = Student(20); const auto fourth = Student(21);
    UnitEffectRuntime continuous({ rule });
    continuous.Evaluate({ source, first, second }, 0);
    // Native stat-only application can consume lifetime quota but must never
    // classify a temporary health bonus as completed permanent training.
    continuous.CommitAction({rule.id, source, first, {}, {}, true}, true);
    continuous.CommitAction({rule.id, source, second, {}, {}, true}, true);
    for (uint32_t tick = 1; tick <= 100; ++tick) {
        continuous.Evaluate({ fourth, third, source, second, first }, tick);
        assert(continuous.Bonus(first.id, UnitEffectStat::Health) == 100 && continuous.Bonus(second.id, UnitEffectStat::Health) == 100);
        assert(continuous.Bonus(third.id, UnitEffectStat::Health) == 0 && continuous.Bonus(fourth.id, UnitEffectStat::Health) == 0);
    }
    rule.trigger = UnitEffectTrigger::ProximityAbility; rule.activation = UnitEffectActivation::Toggle;
    UnitEffectRuntime granted({ rule });
    granted.Evaluate({ source, first, second }, 0);
    assert(granted.ActivateGroup(rule.id, {first,second}, 1, 0) == 2);
    for (uint32_t tick = 1; tick <= 100; ++tick) {
        granted.Evaluate({ source, fourth, third, second, first }, tick);
        assert(granted.Granted(rule.id, first) && granted.Granted(rule.id, second));
        assert(!granted.Granted(rule.id, third) && !granted.Granted(rule.id, fourth));
    }
    rule.actions = TrainingRule().actions; rule.maxConcurrentTargets = 1;
    UnitEffectRuntime mixed({ rule });
    mixed.Evaluate({ source, first }, 0); assert(mixed.Activate(rule.id, first, 0));
    const auto events = mixed.TakeActions(); assert(events.size() == 1);
    mixed.CommitAction(events[0], true, false); // Stats applied, permanent native operation failed.
    mixed.Evaluate({ third, source, first }, 1);
    assert(mixed.Granted(rule.id, first) && !mixed.Granted(rule.id, third));
    mixed.CommitAction(events[0], true, true);
    mixed.Evaluate({ third, source, first }, 2);
    assert(mixed.Granted(rule.id, third)); // Actual permanent completion releases the training slot.
}

void VerifyPaidTransformationPendingRegistration() {
    using namespace edmw;
    auto rule = TrainingRule(); rule.delayMilliseconds = 0;
    rule.maxAffectedTargets = 2; rule.maxConcurrentTargets = 1;
    const auto source = Teacher(); const auto first = Student(); const auto second = Student(21);
    UnitEffectRuntime runtime({ rule });
    EffectAbilityCostTotals balances{ 0, 0, 100, 0 }, paid{};
    runtime.Evaluate({ source, first, second }, 0);
    const auto chargeable = runtime.CountActivationTargets({rule.id}, {first}, 1, 0);
    assert(chargeable == 1 && ComputeEffectAbilityCostTotals(rule.resourceCosts, static_cast<uint32_t>(chargeable), paid));
    assert(CanAffordEffectAbilityCost(balances, paid));
    for (size_t index = 0; index < balances.size(); ++index) balances[index] -= paid[index];
    assert(balances[2] == 50 && runtime.ActivateGroups({rule.id}, {first}, 1, 0) == 1);
    const auto events = runtime.TakeActions();
    assert(events.size() == 1 && events[0].target.id == first.id && events[0].resourceCosts[2] == 50 && !events[0].hasStatOutputs);
    assert(!runtime.Granted(rule.id, second) && runtime.CountActivationTargets({rule.id}, {first}, 1, 0) == 0);

    // A successful factory call returns its new actor before the world queue
    // assigns its ID. The old target remains the payment/quota event identity.
    UnitEffectSnapshot pending{ UINT32_MAX, first.player, first.civilization, rule.actions[0].object, first.x, first.y, true, 1000 };
    auto registered = pending; registered.id = 100;
    assert(SamePendingUnitEffectActor(pending, pending) && SamePendingUnitEffectActor(pending, registered));
    auto wrong = registered; ++wrong.generation; assert(!SamePendingUnitEffectActor(pending, wrong));
    wrong = registered; ++wrong.player; assert(!SamePendingUnitEffectActor(pending, wrong));
    wrong = registered; ++wrong.civilization; assert(!SamePendingUnitEffectActor(pending, wrong));
    wrong = registered; ++wrong.object; assert(!SamePendingUnitEffectActor(pending, wrong));
    wrong = registered; wrong.alive = false; assert(!SamePendingUnitEffectActor(pending, wrong));
    wrong = pending; wrong.generation = 0; assert(!SamePendingUnitEffectActor(wrong, registered));

    runtime.CommitAction(events[0], true);
    runtime.Evaluate({ source, first, second }, 1);
    assert(runtime.Granted(rule.id, second)); // Success frees concurrency even before actor registration.
    assert(runtime.CountActivationTargets({rule.id}, {second}, 1, 1) == 1 && balances[2] == 50);
    runtime.Evaluate({ source, registered, second }, 2);
    assert(runtime.TakeActions().empty() && balances[2] == 50);
    assert(runtime.CountActivationTargets({rule.id}, {registered}, 1, 2) == 0);
    assert(runtime.CountActivationTargets({rule.id}, {second}, 1, 2) == 1); // The source has one remaining lifetime recipient.
    assert(runtime.Activate(rule.id, second, 2));
    const auto next = runtime.TakeActions(); assert(next.size() == 1);
    runtime.CommitAction(next[0], true);
    const auto third = Student(22);
    runtime.Evaluate({ source, registered, third }, 3);
    assert(!runtime.Granted(rule.id, third)); // Commit used the old recipients, so the total limit is still exact.

    // A real factory failure releases pending quota and refunds this one paid
    // recipient. Retrying reserves it again instead of consuming the limit.
    rule.maxAffectedTargets = 1;
    UnitEffectRuntime failed({ rule });
    balances = { 0, 0, 100, 0 };
    failed.Evaluate({ source, first, second }, 0);
    assert(failed.CountActivationTargets({rule.id}, {first}, 1, 0) == 1);
    assert(ComputeEffectAbilityCostTotals(rule.resourceCosts, 1, paid) && CanAffordEffectAbilityCost(balances, paid));
    for (size_t index = 0; index < balances.size(); ++index) balances[index] -= paid[index];
    assert(failed.Activate(rule.id, first, 0));
    const auto rejected = failed.TakeActions(); assert(rejected.size() == 1);
    assert(failed.CountActivationTargets({rule.id}, {first}, 1, 0) == 0 && balances[2] == 50);
    failed.CommitAction(rejected[0], false);
    EffectAbilityCostTotals refundTotals{};
    assert(ComputeEffectAbilityCostTotals(rejected[0].resourceCosts, 1, refundTotals));
    const auto refund = ClampEffectAbilityCostRefund(balances, refundTotals);
    assert(refund[2] == 50);
    for (size_t index = 0; index < balances.size(); ++index) balances[index] += refund[index];
    assert(balances[2] == 100 && failed.CountActivationTargets({rule.id}, {first}, 1, 0) == 1);
    failed.Evaluate({ source, first, second }, 1);
    assert(failed.Granted(rule.id, first) && !failed.Granted(rule.id, second));
    assert(CanAffordEffectAbilityCost(balances, paid));
    for (size_t index = 0; index < balances.size(); ++index) balances[index] -= paid[index];
    assert(failed.Activate(rule.id, first, 1));
    const auto retried = failed.TakeActions(); assert(retried.size() == 1);
    failed.CommitAction(retried[0], true);
    failed.Evaluate({ source, registered, second }, 2);
    assert(!failed.Granted(rule.id, second) && failed.TakeActions().empty() && balances[2] == 50);
}
}

int main() {
    using namespace edmw;
    UnitEffectRule rule;
    rule.id[0] = 91; rule.civilization = 12; rule.sourceObject = 749; rule.targetObject = 777;
    rule.radiusTiles = 5; rule.activation = UnitEffectActivation::Toggle; rule.durationMilliseconds = 0;
    rule.actions = { { UnitEffectActionKind::OwnershipTransfer, UINT32_MAX, 1, UnitEffectOwner::SourcePlayer, 1, "" },
        { UnitEffectActionKind::Transform, 878, 1, UnitEffectOwner::SourcePlayer, 1, "Commoner" },
        { UnitEffectActionKind::Spawn, 777, 3, UnitEffectOwner::SourcePlayer, 1, "Sherman Tank" } };
    assert(ValidUnitEffectOutputs(rule) && UnitEffectOutputs(rule).empty());
    UnitEffectSnapshot source{ 10, 1, 12, 749, 0, 0, true, 1 };
    UnitEffectSnapshot target{ 20, 1, 12, 777, 3, 4, true, 2 };
    auto second = source; second.id = 11; second.generation = 3;
    UnitEffectRuntime aura({ rule });
    aura.Evaluate({ source, target, second }, 100);
    auto events = aura.TakeActions();
    assert(events.size() == 1 && events[0].source.id == source.id && events[0].target.id == target.id && events[0].actions.size() == 3);
    assert(aura.TakeActions().empty());
    for (uint32_t tick = 101; tick < 110; ++tick) { aura.Evaluate({ target, second, source }, tick); assert(aura.TakeActions().empty()); }
    aura.Evaluate({ second, target }, 110); assert(aura.TakeActions().empty()); // Provider handover does not retrigger.
    target.x = 6;
    aura.Evaluate({ second, target }, 111); assert(aura.TakeActions().empty());
    target.x = 3;
    aura.Evaluate({ second, target }, 112); assert(aura.TakeActions().size() == 1);
    ++target.generation;
    aura.Evaluate({ second, target }, 113); assert(aura.TakeActions().size() == 1);
    aura.Evaluate({ target }, 114); assert(aura.TakeActions().empty());
    aura.Evaluate({ second, target }, 115); assert(aura.TakeActions().size() == 1);
    target.player = 2; target.civilization = 4;
    aura.Evaluate({ source, target }, 116); assert(aura.TakeActions().empty());
    rule.targetPlayers = UnitEffectTargetPlayers::Other;
    UnitEffectRuntime convert({ rule });
    convert.Evaluate({ target, source }, 200);
    events = convert.TakeActions();
    assert(events.size() == 1 && events[0].source.player == 1 && events[0].target.player == 2);
    convert.Evaluate({ source, target }, 201); assert(convert.TakeActions().empty());
    convert.Evaluate({ source, target }, 100); assert(convert.TakeActions().size() == 1); // New simulation.
    convert.Reset(); assert(convert.TakeActions().empty());
    rule.trigger = UnitEffectTrigger::Activated; rule.targetPlayers = UnitEffectTargetPlayers::Own;
    rule.sourceObject = rule.targetObject; rule.radiusTiles = 0; rule.activation = UnitEffectActivation::Instant;
    assert(ValidUnitEffectOutputs(rule));
    target.player = 1; target.civilization = 12;
    UnitEffectRuntime click({ rule });
    assert(click.Activate(rule.id, target, 1));
    assert(click.TakeActions().size() == 1);
    assert(click.Activate(rule.id, target, 2));
    assert(click.TakeActions().size() == 1);
    click.Evaluate({ target }, 3); assert(click.TakeActions().empty());
    auto other = target; other.id = 21;
    assert(click.ActivateGroup(rule.id, { target, other, target }, 1, 4) == 2 && click.TakeActions().size() == 2);
    other.player = 2;
    assert(click.ActivateGroup(rule.id, { target, other }, 1, 5) == 1 && click.TakeActions().size() == 1);
    rule.activation = UnitEffectActivation::Toggle;
    rule.outputs = { { UnitEffectStat::Health, 100 } };
    UnitEffectRuntime mixed({ rule });
    assert(mixed.Activate(rule.id, target, 10) && mixed.TakeActions().size() == 1);
    mixed.Evaluate({ target }, 11); assert(mixed.Bonus(target.id, UnitEffectStat::Health) == 100);
    assert(mixed.Activate(rule.id, target, 12) && mixed.TakeActions().empty());
    mixed.Evaluate({ target }, 13); assert(mixed.Bonus(target.id, UnitEffectStat::Health) == 0);
    assert(mixed.Activate(rule.id, target, 14) && mixed.TakeActions().size() == 1);
    auto invalid = rule; invalid.activation = UnitEffectActivation::Instant; assert(!ValidUnitEffectOutputs(invalid));
    invalid = rule; invalid.actions[2].count = 65; assert(!ValidUnitEffectOutputs(invalid));
    invalid = rule; invalid.actions.push_back(invalid.actions[0]); assert(!ValidUnitEffectOutputs(invalid));
    VerifyContinuousTraining();
    VerifyTrainingBudgets();
    VerifyDelayedAuraAndStatOnlyActivation();
    VerifyChargeableToggleStarts();
    VerifyStableTrainingReservations();
    VerifyContinuousStatConcurrency();
    VerifyPaidTransformationPendingRegistration();
    puts("UNIT ACTIONS PASS: permanent outputs, entry actions, continuous proximity training, provider/family quotas, concurrent admission, success-only totals, paid transformation registration/refund and group activation preflight.");
}
