#include "EffectRules.h"
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <iterator>
void RequireAt(bool value, int line) { if (!value) { fprintf(stderr, "Effect rule test failed at line %d\n", line); exit(1); } }
#define Require(value) RequireAt((value), __LINE__)
void Write32(std::vector<uint8_t>& bytes, size_t offset, uint32_t value) { memcpy(bytes.data() + offset, &value, 4); }
std::vector<uint8_t> Version3(bool timed = false, bool proximity = false) {
    const std::string caption = proximity ? "" : "Scout vision";
    std::vector<uint8_t> bytes(16 + 56 + caption.size());
    memcpy(bytes.data(), "EDMWEF03", 8);
    Write32(bytes, 12, 1);
    bytes[16] = 1;
    Write32(bytes, 32, 12);
    Write32(bytes, 36, 749);
    Write32(bytes, 40, proximity ? 777 : 749);
    Write32(bytes, 44, 3);
    Write32(bytes, 48, proximity ? 5 : 0);
    Write32(bytes, 52, proximity ? 1 : 2);
    Write32(bytes, 56, timed ? 2 : 1);
    Write32(bytes, 60, timed ? 10000 : 0);
    Write32(bytes, 64, proximity ? 0 : 10000);
    Write32(bytes, 68, static_cast<uint32_t>(caption.size()));
    if (!caption.empty()) memcpy(bytes.data() + 72, caption.data(), caption.size());
    return bytes;
}
int main(int argc, char** argv) {
    std::vector<uint8_t> bytes(28);
    memcpy(bytes.data(), "EDMWEF01", 8);
    bytes[8] = 1;
    edmw::EffectRule rule{9404, 10, 4, 123};
    memcpy(bytes.data() + 12, &rule, sizeof(rule));
    std::vector<edmw::EffectRule> rules;
    Require(edmw::ReadEffectRules(bytes, rules) && rules.size() == 1);
    Require(edmw::Matches(rules[0], 9404, 10, 0, false));
    Require(!edmw::Matches(rules[0], 9404, 10, 0, true));
    Require(!edmw::Matches(rules[0], 9404, 11, 0, false));
    Require(!edmw::Matches(rules[0], 9405, 10, 0, false));
    Require(!edmw::Matches(rules[0], 9404, 10, 3, false));
    bytes[8] = 255; Require(!edmw::ReadEffectRules(bytes, rules) && rules.empty());
    bytes[8] = 1; bytes[20] = 3; Require(!edmw::ReadEffectRules(bytes, rules));
    bytes[20] = 4; bytes.pop_back(); Require(!edmw::ReadEffectRules(bytes, rules));
    std::vector<edmw::UnitEffectRule> units;
    bytes.assign(52, 0);
    memcpy(bytes.data(), "EDMWEF02", 8); bytes[12] = 1;
    bytes[16] = 1; bytes[32] = 12;
    const uint32_t source = 749, target = 777, radius = 5;
    const int32_t amount = 3;
    memcpy(bytes.data() + 36, &source, 4); memcpy(bytes.data() + 40, &target, 4);
    memcpy(bytes.data() + 44, &amount, 4); memcpy(bytes.data() + 48, &radius, 4);
    Require(edmw::ReadRuntimeRules(bytes, rules, units) && rules.empty() && units.size() == 1);
    Require(units[0].sourceObject == 749 && units[0].targetObject == 777 && units[0].sightAmount == 3);
    bytes[48] = 65; Require(!edmw::ReadRuntimeRules(bytes, rules, units) && units.empty());
    bytes[48] = 5; bytes[16] = 0; Require(!edmw::ReadRuntimeRules(bytes, rules, units));
    bytes[16] = 1; bytes.push_back(0); Require(!edmw::ReadRuntimeRules(bytes, rules, units));
    std::vector<edmw::NativeAbilityBinding> abilities;
    bytes = Version3();
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) && rules.empty() && units.size() == 1 && abilities.size() == 1);
    Require(units[0].trigger == edmw::UnitEffectTrigger::Activated && units[0].activation == edmw::UnitEffectActivation::Toggle &&
        units[0].sourceObject == units[0].targetObject && units[0].durationMilliseconds == 0);
    Require(abilities[0].technology == 10000 && abilities[0].caption == "Scout vision" &&
        abilities[0].tooltip.find("+3 line of sight") != std::string::npos);
    const auto toggleBytes = bytes;
    for (size_t length = 0; length < toggleBytes.size(); ++length) {
        bytes.assign(toggleBytes.begin(), toggleBytes.begin() + length);
        Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities) && rules.empty() && units.empty() && abilities.empty());
    }
    bytes = toggleBytes; bytes.push_back(0);
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = Version3(true);
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) && units[0].activation == edmw::UnitEffectActivation::Timed &&
        units[0].durationMilliseconds == 10000 && abilities[0].tooltip.find("10000 ms") != std::string::npos);
    Write32(bytes, 60, 1000); Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    Write32(bytes, 60, 3600000); Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    Write32(bytes, 60, 999); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    Write32(bytes, 60, 3600001); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; Write32(bytes, 60, 1); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; Write32(bytes, 56, 3); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; Write32(bytes, 52, 3); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; Write32(bytes, 40, 777); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; Write32(bytes, 48, 5); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; Write32(bytes, 64, 9999); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; Write32(bytes, 64, 15000); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; Write32(bytes, 68, 0); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; Write32(bytes, 68, 41); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; bytes[72] = 0; Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; bytes[72] = 128; Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; std::fill(bytes.begin() + 72, bytes.end(), uint8_t(' '));
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = Version3(false, true);
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) && units.size() == 1 && abilities.empty());
    Write32(bytes, 64, 10000); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes;
    bytes.insert(bytes.end(), toggleBytes.begin() + 16, toggleBytes.end());
    Write32(bytes, 12, 2);
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities)); // Duplicate effect identity.
    bytes[toggleBytes.size()] = 2;
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities)); // Distinct effects cannot share a command ID.
    Write32(bytes, toggleBytes.size() + 48, 10001);
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) && abilities.size() == 2);
    bytes = toggleBytes;
    bytes.insert(bytes.begin() + 16, 16, 0);
    Write32(bytes, 8, 1);
    Write32(bytes, 16, 10000); Write32(bytes, 20, 11); Write32(bytes, 24, 4); Write32(bytes, 28, 123);
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities)); // Native command IDs are global, across civilizations.
    Write32(bytes, 16, 9404);
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) && rules.size() == 1 && units.size() == 1 && abilities.size() == 1);
    bytes = toggleBytes;
    Write32(bytes, 52, 0x80000002u);
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) && units.empty() && abilities.size() == 1 &&
        !abilities[0].enabled && abilities[0].caption == "Scout vision" && abilities[0].tooltip.find("Disabled:") == 0);
    bytes.insert(bytes.end(), toggleBytes.begin() + 16, toggleBytes.end());
    Write32(bytes, 12, 2);
    Write32(bytes, toggleBytes.size() + 48, 10001);
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities)); // Disabled identities remain reserved.
    bytes[toggleBytes.size()] = 2;
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) && units.size() == 1 && abilities.size() == 2);
    bytes = Version3(false, true);
    Write32(bytes, 52, 0x80000001u);
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities)); // Only command bindings can use the disabled flag.
    for (int argument = 1; argument < argc; ++argument) {
        std::ifstream file(argv[argument], std::ios::binary);
        Require(file.good());
        const std::vector<uint8_t> fixture((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
        Require(edmw::ReadRuntimeRules(fixture, rules, units, abilities));
        const bool disabled = !abilities.empty() && std::all_of(abilities.begin(), abilities.end(), [](const auto& binding) { return !binding.enabled; });
        if (disabled) Require(rules.empty() && units.empty() && abilities.size() == 2);
        else {
            Require(rules.size() == 1 && rules[0].resource == 4 && rules[0].amount == 123 && rules[0].civilization == 11);
            Require(units.size() == 1 + abilities.size());
            const auto aura = std::find_if(units.begin(), units.end(), [](const auto& item) { return item.trigger == edmw::UnitEffectTrigger::Proximity; });
            Require(aura != units.end() && aura->sourceObject == 749 && aura->targetObject == 777 && aura->civilization == 12 &&
                aura->radiusTiles == 5 && aura->sightAmount == 3);
        }
        if (!abilities.empty()) {
            Require(abilities.size() == 2);
            std::set<uint32_t> technologyIds;
            std::set<std::string> captions;
            for (const auto& binding : abilities) {
                Require(binding.civilization == 12 && binding.sourceObject == 749 &&
                    (binding.technology == 10000 || binding.technology == 10001));
                technologyIds.insert(binding.technology);
                captions.insert(binding.caption);
                Require(binding.caption == "Recon toggle" || binding.caption == "Recon burst");
                if (!disabled) {
                    const auto effect = std::find_if(units.begin(), units.end(), [&](const auto& item) { return item.id == binding.effect; });
                    Require(binding.enabled && effect != units.end() && effect->trigger == edmw::UnitEffectTrigger::Activated &&
                        effect->sourceObject == 749 && effect->targetObject == 749 && effect->sightAmount == 3 && !effect->radiusTiles);
                    Require(binding.caption == "Recon toggle" ?
                        effect->activation == edmw::UnitEffectActivation::Toggle && effect->durationMilliseconds == 0 :
                        effect->activation == edmw::UnitEffectActivation::Timed && effect->durationMilliseconds == 15000);
                } else Require(binding.tooltip.find("Disabled:") == 0);
            }
            Require(technologyIds.size() == 2 && captions.size() == 2);
        }
        printf("Managed-to-native fixture passed: %s\n", argv[argument]);
    }
    puts("Native formats 01/02/03, activation bounds, duplicate IDs, malformed payloads and research/load filtering passed.");
}
