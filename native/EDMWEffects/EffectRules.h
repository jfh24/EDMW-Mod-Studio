#pragma once
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>
#include "UnitEffectRuntime.h"
#include "NativeAbilityBindings.h"
#include "UnitEffectDescriptions.h"

namespace edmw {
// V7 names use strict UTF-8. The limit matches the editor's 100 UTF-16 code
// units; controls and leading/trailing Unicode whitespace are not headings.
inline bool ValidUnitEffectName(const std::string& name) {
    if (name.size() > 400) return false;
    uint32_t characters = 0, first = 0, last = 0;
    for (size_t offset = 0; offset < name.size();) {
        const auto byte = static_cast<uint8_t>(name[offset++]);
        uint32_t point = byte, remaining = 0, minimum = 0;
        if (byte >= 0xC2 && byte <= 0xDF) { point = byte & 0x1Fu; remaining = 1; minimum = 0x80; }
        else if (byte >= 0xE0 && byte <= 0xEF) { point = byte & 0x0Fu; remaining = 2; minimum = 0x800; }
        else if (byte >= 0xF0 && byte <= 0xF4) { point = byte & 7u; remaining = 3; minimum = 0x10000; }
        else if (byte >= 0x80) return false;
        if (name.size() - offset < remaining) return false;
        for (uint32_t index = 0; index < remaining; ++index) {
            const auto continuation = static_cast<uint8_t>(name[offset++]);
            if ((continuation & 0xC0u) != 0x80u) return false;
            point = (point << 6) | (continuation & 0x3Fu);
        }
        if ((remaining && point < minimum) || point > 0x10FFFF || (point >= 0xD800 && point <= 0xDFFF) ||
            point < 32 || (point >= 0x7F && point <= 0x9F) || point == 0x2028 || point == 0x2029) return false;
        characters += point > 0xFFFF ? 2u : 1u;
        if (characters > 100) return false;
        if (!first) first = point;
        last = point;
    }
    const auto whitespace = [](uint32_t point) {
        return point == 0x20 || point == 0xA0 || point == 0x1680 || (point >= 0x2000 && point <= 0x200A) ||
            point == 0x202F || point == 0x205F || point == 0x3000;
    };
    return !whitespace(first) && !whitespace(last);
}

struct EffectRule { uint32_t technology, civilization, resource, amount; };
inline bool ReadEffectRules(const std::vector<uint8_t>& bytes, std::vector<EffectRule>& output) {
    output.clear();
    if (bytes.size() < 12 || memcmp(bytes.data(), "EDMWEF01", 8)) return false;
    uint32_t count = 0;
    memcpy(&count, bytes.data() + 8, 4);
    if (count > 1024 || bytes.size() != 12u + count * 16u) return false;
    std::vector<EffectRule> parsed(count);
    if (count) memcpy(parsed.data(), bytes.data() + 12, count * 16u);
    for (const auto& r : parsed)
        if (r.technology < 1000 || r.technology >= 15000 || r.civilization < 4 || r.civilization > 12 ||
            (r.resource != 0 && r.resource != 1 && r.resource != 2 && r.resource != 4) || !r.amount || r.amount > 100000) return false;
    output.swap(parsed);
    return true;
}
inline bool Matches(const EffectRule& r, uint32_t technology, uint32_t civilization, uint32_t kind, bool restoring) {
    return !restoring && (kind == 0 || kind == 7) && r.technology == technology && r.civilization == civilization;
}
inline bool ReadRuntimeRules(const std::vector<uint8_t>& bytes, std::vector<EffectRule>& research,
    std::vector<UnitEffectRule>& units, std::vector<NativeAbilityBinding>& abilities) {
    research.clear(); units.clear(); abilities.clear();
    if (bytes.size() >= 8 && !memcmp(bytes.data(), "EDMWEF01", 8)) return ReadEffectRules(bytes, research);
    if (bytes.size() < 16) return false;
    const bool version8 = !memcmp(bytes.data(), "EDMWEF08", 8);
    const bool version7 = version8 || !memcmp(bytes.data(), "EDMWEF07", 8);
    const bool version6 = version7 || !memcmp(bytes.data(), "EDMWEF06", 8);
    const bool version5 = version6 || !memcmp(bytes.data(), "EDMWEF05", 8);
    const bool version4 = version5 || !memcmp(bytes.data(), "EDMWEF04", 8);
    const bool version3 = version4 || !memcmp(bytes.data(), "EDMWEF03", 8);
    if (!version3 && memcmp(bytes.data(), "EDMWEF02", 8)) return false;
    uint32_t researchCount = 0, unitCount = 0;
    memcpy(&researchCount, bytes.data() + 8, 4);
    memcpy(&unitCount, bytes.data() + 12, 4);
    if (researchCount > 1024 || unitCount > 1024 || researchCount + unitCount > 1024 ||
        bytes.size() < 16u + researchCount * 16u + unitCount * (version7 ? 76u : version5 ? 68u : version4 ? 60u : version3 ? 56u : 36u) ||
        bytes.size() > 16u + researchCount * 16u + unitCount * (version8 ? 1316u + 8u + 64u * 424u + 44u : version7 ? 1316u : version5 ? 508u : version4 ? 100u : version3 ? 96u : 36u)) return false;
    std::vector<uint8_t> legacy(12u + researchCount * 16u);
    memcpy(legacy.data(), "EDMWEF01", 8);
    memcpy(legacy.data() + 8, &researchCount, 4);
    if (researchCount) memcpy(legacy.data() + 12, bytes.data() + 16, researchCount * 16u);
    std::vector<EffectRule> parsedResearch;
    if (!ReadEffectRules(legacy, parsedResearch)) return false;
    std::vector<UnitEffectRule> parsedUnits;
    std::vector<NativeAbilityBinding> parsedAbilities;
    std::set<uint32_t> technologyIds;
    std::set<UnitEffectId> identities;
    std::map<uint32_t, std::pair<UnitEffectRule, bool>> grantRules;
    size_t offset = 16u + researchCount * 16u;
    for (uint32_t i = 0; i < unitCount; ++i) {
        size_t minimum = version4 ? 60u : version3 ? 56u : 36u;
        if (offset > bytes.size() || bytes.size() - offset < minimum) return false;
        const auto* record = bytes.data() + offset;
        UnitEffectRule rule;
        memcpy(rule.id.data(), record, 16);
        memcpy(&rule.civilization, record + 16, 4);
        memcpy(&rule.sourceObject, record + 20, 4);
        memcpy(&rule.targetObject, record + 24, 4);
        if (version5) {
            uint32_t reserved = 0, outputCount = 0;
            memcpy(&reserved, record + 28, 4);
            memcpy(&outputCount, record + 56, 4);
            if ((!version6 && reserved) || (!version8 && !outputCount) || outputCount > 51 || bytes.size() - offset - minimum < outputCount * 8u) return false;
            rule.grantFamilyTechnology = reserved;
            for (uint32_t outputIndex = 0; outputIndex < outputCount; ++outputIndex) {
                UnitEffectOutput output{};
                memcpy(&output.stat, record + minimum + outputIndex * 8u, 4);
                memcpy(&output.amount, record + minimum + outputIndex * 8u + 4u, 4);
                rule.outputs.push_back(output);
            }
            if (!rule.outputs.empty()) {
                rule.stat = rule.outputs.front().stat;
                rule.amount = rule.outputs.front().amount;
            }
            minimum += outputCount * 8u;
        } else if (version4) {
            memcpy(&rule.amount, record + 28, 4);
            memcpy(&rule.stat, record + 56, 4);
        } else {
            int32_t legacyAmount = 0;
            memcpy(&legacyAmount, record + 28, 4);
            rule.amount = static_cast<float>(legacyAmount);
        }
        memcpy(&rule.radiusTiles, record + 32, 4);
        rule.activation = UnitEffectActivation::Toggle;
        rule.durationMilliseconds = 0;
        uint32_t technology = 0, captionLength = 0;
        bool enabled = true;
        if (version3) {
            uint32_t trigger = 0, mode = 0;
            memcpy(&trigger, record + 36, 4);
            memcpy(&mode, record + 40, 4);
            memcpy(&rule.durationMilliseconds, record + 44, 4);
            memcpy(&technology, record + 48, 4);
            memcpy(&captionLength, record + 52, 4);
            if (captionLength > 40 || bytes.size() - offset - minimum < captionLength) return false;
            enabled = (trigger & 0x80000000u) == 0;
            rule.trigger = static_cast<UnitEffectTrigger>(trigger & 0x7FFFFFFFu);
            if ((!enabled && rule.trigger == UnitEffectTrigger::Proximity) ||
                (!version6 && rule.trigger == UnitEffectTrigger::ProximityAbility)) return false;
            rule.activation = static_cast<UnitEffectActivation>(mode);
        }
        size_t recordSize = minimum + captionLength;
        if (version7) {
            for (auto* name : { &rule.name, &rule.targetName }) {
                if (bytes.size() - offset - recordSize < 4) return false;
                uint32_t nameLength = 0;
                memcpy(&nameLength, record + recordSize, 4);
                if (nameLength > 400 || bytes.size() - offset - recordSize - 4 < nameLength) return false;
                name->assign(reinterpret_cast<const char*>(record + recordSize + 4), nameLength);
                if (!ValidUnitEffectName(*name)) return false;
                recordSize += 4u + nameLength;
            }
        }
        if (version8) {
            if (bytes.size() - offset - recordSize < 8) return false;
            uint32_t actionCount = 0;
            memcpy(&rule.targetPlayers, record + recordSize, 4);
            memcpy(&actionCount, record + recordSize + 4, 4);
            recordSize += 8;
            if (actionCount > 64 || rule.outputs.size() + actionCount > 64) return false;
            for (uint32_t actionIndex = 0; actionIndex < actionCount; ++actionIndex) {
                if (bytes.size() - offset - recordSize < 24) return false;
                UnitEffectAction action;
                uint32_t nameLength = 0;
                memcpy(&action.kind, record + recordSize, 4);
                memcpy(&action.object, record + recordSize + 4, 4);
                memcpy(&action.count, record + recordSize + 8, 4);
                memcpy(&action.owner, record + recordSize + 12, 4);
                memcpy(&action.playerSlot, record + recordSize + 16, 4);
                memcpy(&nameLength, record + recordSize + 20, 4);
                recordSize += 24;
                if (nameLength > 400 || bytes.size() - offset - recordSize < nameLength) return false;
                action.objectName.assign(reinterpret_cast<const char*>(record + recordSize), nameLength);
                if (!ValidUnitEffectName(action.objectName)) return false;
                recordSize += nameLength;
                rule.actions.push_back(std::move(action));
            }
            if (bytes.size() - offset - recordSize < 44) return false;
            memcpy(rule.familyId.data(), record + recordSize, 16);
            memcpy(&rule.delayMilliseconds, record + recordSize + 16, 4);
            memcpy(&rule.maxAffectedTargets, record + recordSize + 20, 4);
            memcpy(&rule.maxConcurrentTargets, record + recordSize + 24, 4);
            memcpy(rule.resourceCosts.data(), record + recordSize + 28, 16);
            recordSize += 44;
            if (std::all_of(rule.familyId.begin(), rule.familyId.end(), [](uint8_t value) { return !value; })) rule.familyId = rule.id;
            if (rule.delayMilliseconds > 3600000 || rule.maxAffectedTargets > 100000 || rule.maxConcurrentTargets > 100000 ||
                std::any_of(rule.resourceCosts.begin(), rule.resourceCosts.end(), [](uint32_t value) { return value > 100000; }) ||
                (rule.trigger == UnitEffectTrigger::Activated && (rule.delayMilliseconds || rule.maxAffectedTargets || rule.maxConcurrentTargets)) ||
                (rule.trigger == UnitEffectTrigger::Proximity &&
                    std::any_of(rule.resourceCosts.begin(), rule.resourceCosts.end(), [](uint32_t value) { return value != 0; }))) return false;
        }
        if (std::all_of(rule.id.begin(), rule.id.end(), [](uint8_t value) { return value == 0; }) ||
            rule.civilization < 4 || rule.civilization > 12 || rule.sourceObject >= 100000 || rule.targetObject >= 100000 ||
            !ValidUnitEffectOutputs(rule) ||
            !identities.insert(rule.id).second) return false;
        if (rule.trigger != UnitEffectTrigger::ProximityAbility && rule.grantFamilyTechnology) return false;
        if (rule.trigger == UnitEffectTrigger::Proximity) {
            if (!rule.radiusTiles || rule.radiusTiles > 64 || rule.activation != UnitEffectActivation::Toggle ||
                rule.durationMilliseconds || technology || captionLength) return false;
        } else if (rule.trigger == UnitEffectTrigger::Activated || rule.trigger == UnitEffectTrigger::ProximityAbility) {
            const bool granted = rule.trigger == UnitEffectTrigger::ProximityAbility;
            if ((granted ? (!rule.radiusTiles || rule.radiusTiles > 64 || rule.activation == UnitEffectActivation::Timed ||
                rule.grantFamilyTechnology < 10000 || rule.grantFamilyTechnology > 14999) :
                (rule.targetObject != rule.sourceObject || rule.radiusTiles)) || technology < 10000 || technology > 14999 ||
                !captionLength || !technologyIds.insert(technology).second ||
                std::any_of(parsedResearch.begin(), parsedResearch.end(), [&](const auto& item) { return item.technology == technology; })) return false;
            if (rule.activation == UnitEffectActivation::Toggle || (version8 && rule.activation == UnitEffectActivation::Instant)) {
                if (rule.durationMilliseconds) return false;
            } else if (rule.activation == UnitEffectActivation::Timed) {
                if (rule.durationMilliseconds < 1000 || rule.durationMilliseconds > 3600000) return false;
            } else return false;
            const std::string caption(reinterpret_cast<const char*>(record + minimum), captionLength);
            if (std::any_of(caption.begin(), caption.end(), [](unsigned char character) { return character < 32 || character > 126; }) ||
                std::all_of(caption.begin(), caption.end(), [](char character) { return character == ' '; })) return false;
            std::string outputText;
            for (const auto& output : UnitEffectOutputs(rule)) {
                if (!outputText.empty()) outputText += "; ";
                outputText += (UnitEffectIsLimit(output.stat) ? "maximum " + std::to_string(static_cast<uint32_t>(output.amount)) :
                    UnitEffectAmountText(output.amount)) + " " + UnitEffectStatName(output.stat);
            }
            for (const auto& action : rule.actions) {
                if (!outputText.empty()) outputText += "; ";
                outputText += UnitEffectActionDescription(action);
            }
            const auto seconds = UnitEffectSecondsText(rule.durationMilliseconds);
            const std::string mode = rule.activation == UnitEffectActivation::Instant ? "Applies once per click." :
                rule.activation == UnitEffectActivation::Toggle ? "Click again to turn off." :
                "Lasts " + seconds + (rule.durationMilliseconds == 1000 ? " second." : " seconds.");
            const std::string range = !granted ? "" : rule.actions.empty() ?
                " Available while in range of its source. Leaving range ends its effects." :
                " Available while in range of its source. Leaving range removes the ability" +
                    std::string(UnitEffectOutputs(rule).empty() ? "." : " and stat bonuses.");
            parsedAbilities.push_back({ rule.id, technology, rule.civilization, granted ? rule.targetObject : rule.sourceObject, caption,
                (enabled ? "" : "Disabled: ") + caption + ": " + outputText + ". " + mode +
                range + (rule.actions.empty() ? "" : " Actions are permanent.") + UnitEffectCostDescription(rule.resourceCosts), enabled, granted,
                rule.grantFamilyTechnology });
            parsedAbilities.back().resourceCosts = rule.resourceCosts;
            if (granted) grantRules.emplace(technology, std::make_pair(rule, enabled));
        } else return false;
        if (enabled) parsedUnits.push_back(rule);
        offset += recordSize;
    }
    if (offset != bytes.size()) return false;
    std::map<uint32_t, std::set<uint32_t>> familyTargets;
    for (const auto& entry : grantRules) {
        const auto& rule = entry.second.first;
        const auto family = grantRules.find(rule.grantFamilyTechnology);
        if (family == grantRules.end() || family->second.first.grantFamilyTechnology != rule.grantFamilyTechnology ||
            entry.first < rule.grantFamilyTechnology || !familyTargets[rule.grantFamilyTechnology].insert(rule.targetObject).second) return false;
        const auto& anchor = family->second.first;
        if (anchor.civilization != rule.civilization || anchor.sourceObject != rule.sourceObject || anchor.radiusTiles != rule.radiusTiles ||
            family->second.second != entry.second.second || anchor.name != rule.name || anchor.activation != rule.activation ||
            anchor.familyId != rule.familyId || anchor.delayMilliseconds != rule.delayMilliseconds ||
            anchor.maxAffectedTargets != rule.maxAffectedTargets || anchor.maxConcurrentTargets != rule.maxConcurrentTargets ||
            anchor.resourceCosts != rule.resourceCosts ||
            anchor.targetPlayers != rule.targetPlayers || anchor.actions.size() != rule.actions.size() ||
            !std::equal(anchor.actions.begin(), anchor.actions.end(), rule.actions.begin(), SameUnitEffectAction) ||
            anchor.outputs.size() != rule.outputs.size() ||
            !std::equal(anchor.outputs.begin(), anchor.outputs.end(), rule.outputs.begin(), [](const auto& left, const auto& right) {
                return left.stat == right.stat && left.amount == right.amount;
            })) return false;
        const auto binding = std::find_if(parsedAbilities.begin(), parsedAbilities.end(), [&](const auto& item) { return item.technology == entry.first; });
        const auto familyBinding = std::find_if(parsedAbilities.begin(), parsedAbilities.end(), [&](const auto& item) { return item.technology == rule.grantFamilyTechnology; });
        if (binding->caption != familyBinding->caption) return false;
    }
    research.swap(parsedResearch); units.swap(parsedUnits); abilities.swap(parsedAbilities);
    return true;
}
inline bool ReadRuntimeRules(const std::vector<uint8_t>& bytes, std::vector<EffectRule>& research,
    std::vector<UnitEffectRule>& units) {
    std::vector<NativeAbilityBinding> abilities;
    return ReadRuntimeRules(bytes, research, units, abilities);
}
}
