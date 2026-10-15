#pragma once
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>
#include "UnitEffectRuntime.h"
#include "NativeAbilityBindings.h"

namespace edmw {
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
    const bool version3 = !memcmp(bytes.data(), "EDMWEF03", 8);
    if (!version3 && memcmp(bytes.data(), "EDMWEF02", 8)) return false;
    uint32_t researchCount = 0, unitCount = 0;
    memcpy(&researchCount, bytes.data() + 8, 4);
    memcpy(&unitCount, bytes.data() + 12, 4);
    if (researchCount > 1024 || unitCount > 1024 || researchCount + unitCount > 1024 ||
        bytes.size() < 16u + researchCount * 16u + unitCount * (version3 ? 56u : 36u) ||
        bytes.size() > 16u + researchCount * 16u + unitCount * (version3 ? 96u : 36u)) return false;
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
    size_t offset = 16u + researchCount * 16u;
    for (uint32_t i = 0; i < unitCount; ++i) {
        const size_t minimum = version3 ? 56u : 36u;
        if (offset > bytes.size() || bytes.size() - offset < minimum) return false;
        const auto* record = bytes.data() + offset;
        UnitEffectRule rule;
        memcpy(rule.id.data(), record, 16);
        memcpy(&rule.civilization, record + 16, 4);
        memcpy(&rule.sourceObject, record + 20, 4);
        memcpy(&rule.targetObject, record + 24, 4);
        memcpy(&rule.sightAmount, record + 28, 4);
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
            if (!enabled && rule.trigger != UnitEffectTrigger::Activated) return false;
            rule.activation = static_cast<UnitEffectActivation>(mode);
        }
        if (std::all_of(rule.id.begin(), rule.id.end(), [](uint8_t value) { return value == 0; }) ||
            rule.civilization < 4 || rule.civilization > 12 || rule.sourceObject >= 100000 || rule.targetObject >= 100000 ||
            rule.sightAmount < -32 || rule.sightAmount > 32 || !rule.sightAmount ||
            !identities.insert(rule.id).second) return false;
        if (rule.trigger == UnitEffectTrigger::Proximity) {
            if (!rule.radiusTiles || rule.radiusTiles > 64 || rule.activation != UnitEffectActivation::Toggle ||
                rule.durationMilliseconds || technology || captionLength) return false;
        } else if (rule.trigger == UnitEffectTrigger::Activated) {
            if (rule.targetObject != rule.sourceObject || rule.radiusTiles || technology < 10000 || technology > 14999 ||
                !captionLength || !technologyIds.insert(technology).second ||
                std::any_of(parsedResearch.begin(), parsedResearch.end(), [&](const auto& item) { return item.technology == technology; })) return false;
            if (rule.activation == UnitEffectActivation::Toggle) {
                if (rule.durationMilliseconds) return false;
            } else if (rule.activation == UnitEffectActivation::Timed) {
                if (rule.durationMilliseconds < 1000 || rule.durationMilliseconds > 3600000) return false;
            } else return false;
            const std::string caption(reinterpret_cast<const char*>(record + minimum), captionLength);
            if (std::any_of(caption.begin(), caption.end(), [](unsigned char character) { return character < 32 || character > 126; }) ||
                std::all_of(caption.begin(), caption.end(), [](char character) { return character == ' '; })) return false;
            const std::string amount = (rule.sightAmount > 0 ? "+" : "") + std::to_string(rule.sightAmount);
            const std::string mode = rule.activation == UnitEffectActivation::Toggle ? "Toggle on/off." :
                "Duration: " + std::to_string(rule.durationMilliseconds) + " ms.";
            parsedAbilities.push_back({ rule.id, technology, rule.civilization, rule.sourceObject, caption,
                (enabled ? "" : "Disabled: ") + caption + ": " + amount + " line of sight. " + mode + " Session only.", enabled });
        } else return false;
        if (enabled) parsedUnits.push_back(rule);
        offset += minimum + captionLength;
    }
    if (offset != bytes.size()) return false;
    research.swap(parsedResearch); units.swap(parsedUnits); abilities.swap(parsedAbilities);
    return true;
}
inline bool ReadRuntimeRules(const std::vector<uint8_t>& bytes, std::vector<EffectRule>& research,
    std::vector<UnitEffectRule>& units) {
    std::vector<NativeAbilityBinding> abilities;
    return ReadRuntimeRules(bytes, research, units, abilities);
}
}
