#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include "UnitEffectModsPanel.h"
#include "UnitEffectNative.h"
#include "EffectTrace.h"

namespace {
constexpr uintptr_t selectedInformationAddress = 0x00770CC6;
constexpr uintptr_t selectionFrameAddress = 0x0076BB02;
constexpr uintptr_t selectionDestructorAddress = 0x0076BABA;
constexpr uintptr_t portraitHelpAddress = 0x0074FD9C;
constexpr uint8_t informationBytes[]{ 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x64 };
constexpr uint8_t frameBytes[]{ 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x14 };
constexpr uint8_t destructorBytes[]{ 0x56, 0x8B, 0xF1, 0x57, 0x8B, 0x7E, 0x08 };
constexpr uint8_t helpBytes[]{ 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x14 };
constexpr uint8_t portraitHelpBytes[]{ 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x5C };
constexpr uintptr_t mainGameVtable = 0x007FB8C0;
using Information = void(__thiscall*)(void*, void*);
using Frame = void(__thiscall*)(void*);
using Help = bool(__thiscall*)(void*, void*);
using ConstructString = void*(__thiscall*)(void*, const char*);
using DestroyString = void(__thiscall*)(void*);
using SetText = void(__thiscall*)(void*, const void*, void*);
using AppendText = void(__thiscall*)(void*, const void*, bool);
Information originalInformation = nullptr;
Frame originalFrame = nullptr;
Frame originalDestructor = nullptr;
Help originalHelp = nullptr;
Help originalPortraitHelp = nullptr;
uintptr_t helpAddress = 0;
ConstructString constructString = nullptr;
DestroyString destroyString = nullptr;
SetText setText = nullptr;
AppendText appendText = nullptr;
Frame resetHelp = nullptr;
std::atomic<uint32_t> selectedUnit{ UINT32_MAX };
std::atomic<void*> selectionOwner{ nullptr };
std::mutex publishedMutex;
uint32_t publishedUnit = UINT32_MAX;
std::string publishedText;
void* viewedController = nullptr;
void* viewedForm = nullptr;
uint32_t viewedUnit = UINT32_MAX;
std::string viewedText;

// SEH helpers contain no C++ objects requiring unwinding.
bool Copy(const void* source, void* destination, size_t size) {
    __try { if (!source) return false; memcpy(destination, source, size); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
template<class T> T Read(const void* pointer, size_t offset) {
    T value{};
    if (pointer) Copy(static_cast<const uint8_t*>(pointer) + offset, &value, sizeof(value));
    return value;
}
void* CurrentForm() {
    const auto manager = Read<void*>(reinterpret_cast<const void*>(0x0099FCD0), 0);
    const auto form = Read<void*>(manager, 0x188);
    return Read<uintptr_t>(form, 0) == mainGameVtable ? form : nullptr;
}
bool ControllerMatches(void* controller, void* form, void* helper) {
    return controller && form && helper && Read<void*>(form, 0x7D0) == controller &&
        Read<void*>(controller, 4) == form && Read<void*>(controller, 8) == helper && Read<void*>(helper, 0x94) == form;
}
bool TooltipTarget(void* control, void* destination, void* form, void* helper) {
    const auto count = Read<uint32_t>(form, 0x348);
    const auto table = Read<void*>(form, 0x2B0);
    const auto portrait = count > 42 && count <= 4096 ? Read<void*>(table, 42 * sizeof(void*)) : nullptr;
    if (!edmw::IsUnitEffectModsTooltipTarget(reinterpret_cast<uintptr_t>(control),
        reinterpret_cast<uintptr_t>(Read<void*>(helper, 0xA4)), reinterpret_cast<uintptr_t>(portrait),
        reinterpret_cast<uintptr_t>(form), reinterpret_cast<uintptr_t>(Read<void*>(control, 0x88)),
        reinterpret_cast<uintptr_t>(destination), reinterpret_cast<uintptr_t>(Read<void*>(form, 0x2D4)))) return false;
    const auto functions = Read<void*>(control, 0);
    if (control == portrait) return Read<void*>(helper, 0xA0) == portrait &&
        reinterpret_cast<uintptr_t>(functions) == 0x007FAABC && Read<uintptr_t>(functions, 0x24) == portraitHelpAddress;
    return Read<uintptr_t>(functions, 0x24) == helpAddress;
}
bool SelectionCurrent(void* controller, uint32_t id) {
    return edmw::IsUnitEffectModsSelectionCurrent(Read<uint8_t>(controller, 0x40) != 0,
        Read<uint32_t>(controller, 0x30), id);
}
bool WriteNativeTooltip(void* destination, const char* text, bool append) {
    __try {
        alignas(void*) uint8_t nativeString[0x14]{};
        constructString(nativeString, text);
        if (append) appendText(destination, nativeString, false);
        else setText(destination, nativeString, nullptr);
        destroyString(nativeString);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
void ResetNativeHelp(void* form) {
    __try { resetHelp(form); }
    __except (EXCEPTION_EXECUTE_HANDLER) { }
}
std::string EngineText(const std::string& utf8) {
    if (utf8.empty() || utf8.size() > 20000) return {};
    const auto count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
    if (count <= 0) return {};
    std::wstring wide(static_cast<size_t>(count), L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(), static_cast<int>(utf8.size()), wide.data(), count) != count) return {};
    // This engine has an MBCS UWideString. Match the Windows system code page;
    // unsupported glyphs use the platform's normal replacement character.
    const auto flags = GetACP() == CP_UTF8 ? 0u : WC_NO_BEST_FIT_CHARS;
    const auto bytes = WideCharToMultiByte(CP_ACP, flags, wide.data(), count, nullptr, 0, nullptr, nullptr);
    if (bytes <= 0 || bytes > 65536) return {};
    std::string result(static_cast<size_t>(bytes), '\0');
    if (WideCharToMultiByte(CP_ACP, flags, wide.data(), count, result.data(), bytes, nullptr, nullptr) != bytes) return {};
    return result;
}
bool AddSelectedUnitHelp(void* control, void* destination, bool native) {
    const auto form = CurrentForm();
    const auto controller = Read<void*>(form, 0x7D0);
    const auto helper = Read<void*>(controller, 8);
    if (!ControllerMatches(controller, form, helper) || selectionOwner.load() != controller ||
        !TooltipTarget(control, destination, form, helper)) return native;
    const auto id = selectedUnit.load();
    if (!SelectionCurrent(controller, id)) return native;
    std::string description;
    { std::lock_guard<std::mutex> lock(publishedMutex); if (publishedUnit == id) description = publishedText; }
    const auto addition = edmw::UnitEffectModsTooltipAddition(native, description);
    if (addition.empty()) return native;
    const auto encoded = EngineText(addition);
    return encoded.empty() ? native : WriteNativeTooltip(destination, encoded.c_str(), native) || native;
}
bool __fastcall SelectedUnitHelp(void* control, void*, void* destination) {
    return AddSelectedUnitHelp(control, destination, originalHelp(control, destination));
}
bool __fastcall SelectedPortraitHelp(void* control, void*, void* destination) {
    return AddSelectedUnitHelp(control, destination, originalPortraitHelp(control, destination));
}
void PumpTooltip(void* controller) {
    const auto form = CurrentForm();
    const auto helper = Read<void*>(controller, 8);
    if (!ControllerMatches(controller, form, helper)) return;
    if (!SelectionCurrent(controller, selectedUnit.load())) selectedUnit.store(UINT32_MAX);
    edmw::UpdateUnitEffectModsPanel();
    const auto id = selectedUnit.load();
    std::string text;
    { std::lock_guard<std::mutex> lock(publishedMutex); if (publishedUnit == id) text = publishedText; }
    if (viewedForm != form || viewedUnit != id || viewedText != text) {
        // Rebuild only this hovered information control's existing tooltip.
        // Native reset also removes stale mod text on unselection/range loss.
        const auto hovered = Read<void*>(form, 0x2D0);
        if (TooltipTarget(hovered, Read<void*>(form, 0x2D4), form, helper)) ResetNativeHelp(form);
    }
    viewedController = controller; viewedForm = form; viewedUnit = id; viewedText = text;
}
void __fastcall SelectedInformation(void* helper, void*, void* actor) {
    originalInformation(helper, actor);
    const auto form = CurrentForm();
    const auto controller = Read<void*>(form, 0x7D0);
    if (!ControllerMatches(controller, form, helper)) return;
    uint32_t id = UINT32_MAX;
    if (actor) Copy(static_cast<const uint8_t*>(actor) + 0x24, &id, sizeof(id));
    selectionOwner.store(controller);
    selectedUnit.store(id);
}
void __fastcall SelectionFrame(void* controller, void*) {
    originalFrame(controller);
    PumpTooltip(controller);
}
void __fastcall SelectionDestroyed(void* controller, void*) {
    if (selectionOwner.load() == controller) {
        selectedUnit.store(UINT32_MAX);
        selectionOwner.store(nullptr);
    }
    if (controller == viewedController) {
        viewedController = viewedForm = nullptr;
        viewedUnit = UINT32_MAX; viewedText.clear();
    }
    originalDestructor(controller);
}
void* Hook(uintptr_t address, const uint8_t* expected, size_t size, const void* callback, const char* role) {
    if (memcmp(reinterpret_cast<const void*>(address), expected, size)) {
        edmw::TraceEffect("mods-info-install reject=hook-signature role=%s", role); return nullptr;
    }
    auto trampoline = static_cast<uint8_t*>(VirtualAlloc(nullptr, size + 5, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    if (!trampoline) { edmw::TraceEffect("mods-info-install reject=trampoline-allocation role=%s win32=%lu", role, GetLastError()); return nullptr; }
    memcpy(trampoline, expected, size); trampoline[size] = 0xE9;
    const auto back = static_cast<int32_t>(address + size - reinterpret_cast<uintptr_t>(trampoline + size) - 5);
    memcpy(trampoline + size + 1, &back, sizeof(back));
    DWORD protection = 0;
    if (!VirtualProtect(trampoline, size + 5, PAGE_EXECUTE_READ, &protection)) {
        edmw::TraceEffect("mods-info-install reject=trampoline-protection role=%s win32=%lu", role, GetLastError());
        VirtualFree(trampoline, 0, MEM_RELEASE); return nullptr;
    }
    if (!FlushInstructionCache(GetCurrentProcess(), trampoline, size + 5)) {
        edmw::TraceEffect("mods-info-install reject=trampoline-cache role=%s win32=%lu", role, GetLastError());
        VirtualFree(trampoline, 0, MEM_RELEASE); return nullptr;
    }
    if (!VirtualProtect(reinterpret_cast<void*>(address), size, PAGE_EXECUTE_READWRITE, &protection)) {
        edmw::TraceEffect("mods-info-install reject=hook-protection role=%s win32=%lu", role, GetLastError()); return nullptr;
    }
    uint8_t patch[7]{ 0xE9, 0, 0, 0, 0, 0x90, 0x90 };
    const auto branch = static_cast<int32_t>(reinterpret_cast<uintptr_t>(callback) - address - 5);
    memcpy(patch + 1, &branch, sizeof(branch)); memcpy(reinterpret_cast<void*>(address), patch, size);
    DWORD ignored = 0;
    if (!VirtualProtect(reinterpret_cast<void*>(address), size, protection, &ignored)) {
        edmw::TraceEffect("mods-info-install reject=hook-restore-protection role=%s win32=%lu", role, GetLastError()); return nullptr;
    }
    if (!FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<void*>(address), size)) {
        edmw::TraceEffect("mods-info-install reject=hook-cache role=%s win32=%lu", role, GetLastError()); return nullptr;
    }
    edmw::TraceEffect("mods-info-install hook-ready role=%s", role);
    return trampoline;
}
template<class T> bool Resolve(HMODULE module, T& destination, const char* name) {
    destination = reinterpret_cast<T>(GetProcAddress(module, name));
    if (!destination) edmw::TraceEffect("mods-info-install reject=engine-export name=%s win32=%lu", name, GetLastError());
    return destination != nullptr;
}
}

bool edmw::InstallUnitEffectModsPanel() {
    TraceEffect("mods-info-install begin");
    if (originalInformation || originalFrame || originalDestructor || originalHelp || originalPortraitHelp) {
        TraceEffect("mods-info-install reject=already-installed"); return false;
    }
    const struct { uintptr_t address; const uint8_t* bytes; size_t size; const char* role; } signatures[]{
        { selectedInformationAddress, informationBytes, sizeof(informationBytes), "selection-information" },
        { selectionFrameAddress, frameBytes, sizeof(frameBytes), "selection-frame" },
        { selectionDestructorAddress, destructorBytes, sizeof(destructorBytes), "selection-destruction" },
        { portraitHelpAddress, portraitHelpBytes, sizeof(portraitHelpBytes), "portrait-help" }
    };
    for (const auto& signature : signatures)
        if (memcmp(reinterpret_cast<const void*>(signature.address), signature.bytes, signature.size)) {
            TraceEffect("mods-info-install reject=signature role=%s", signature.role); return false;
        }
    const auto engine = GetModuleHandleW(L"Low-Level Engine.dll");
    if (!engine) { TraceEffect("mods-info-install reject=engine-module win32=%lu", GetLastError()); return false; }
    Help help = nullptr;
    if (!Resolve(engine, help, "?SetHelpString@UIControl@@UBE_NPAVUITextLabel@@@Z") ||
        !Resolve(engine, constructString, "??0UWideString@@QAE@PBD@Z") ||
        !Resolve(engine, destroyString, "??1UWideString@@UAE@XZ") ||
        !Resolve(engine, setText, "?SetText@UITextLabel@@QAEXABVUWideString@@PAVUITOParser@@@Z") ||
        !Resolve(engine, appendText, "?AppendText@UITextLabel@@UAEXABVUWideString@@_N@Z") ||
        !Resolve(engine, resetHelp, "?ResetHelpText@UIForm@@QAEXXZ")) return false;
    helpAddress = reinterpret_cast<uintptr_t>(help);
    if (memcmp(reinterpret_cast<const void*>(helpAddress), helpBytes, sizeof(helpBytes))) {
        TraceEffect("mods-info-install reject=signature role=base-help"); return false;
    }
    originalInformation = reinterpret_cast<Information>(Hook(selectedInformationAddress, informationBytes, sizeof(informationBytes), &SelectedInformation, "selection-information"));
    originalFrame = reinterpret_cast<Frame>(Hook(selectionFrameAddress, frameBytes, sizeof(frameBytes), &SelectionFrame, "selection-frame"));
    originalDestructor = reinterpret_cast<Frame>(Hook(selectionDestructorAddress, destructorBytes, sizeof(destructorBytes), &SelectionDestroyed, "selection-destruction"));
    originalHelp = reinterpret_cast<Help>(Hook(helpAddress, helpBytes, sizeof(helpBytes), &SelectedUnitHelp, "base-help"));
    originalPortraitHelp = reinterpret_cast<Help>(Hook(portraitHelpAddress, portraitHelpBytes, sizeof(portraitHelpBytes), &SelectedPortraitHelp, "portrait-help"));
    const auto installed = originalInformation && originalFrame && originalDestructor && originalHelp && originalPortraitHelp;
    TraceEffect("mods-info-install result=%s", installed ? "ready" : "rejected");
    return installed;
}

void edmw::UpdateUnitEffectModsPanel() {
    if (!originalInformation) return;
    const auto id = selectedUnit.load();
    const auto text = id != UINT32_MAX ? DescribeUnitProximityEffects(id) : std::string{};
    std::lock_guard<std::mutex> lock(publishedMutex);
    if (selectedUnit.load() == id) { publishedUnit = id; publishedText = HasUnitEffectModDescription(text) ? text : std::string{}; }
}
