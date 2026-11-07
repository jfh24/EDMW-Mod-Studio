#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <iphlpapi.h>
#include <tlhelp32.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <deque>
#include <cwctype>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <vector>

namespace {
constexpr uint16_t RelayPortBase = 40000;
constexpr uint32_t VirtualNetwork = 0x0AFEFF00; // 10.254.255.0/24
constexpr size_t PipeFrameLimit = 2048;
constexpr size_t MaximumQueuedPipeBytes = 512 * 1024;
constexpr size_t TunnelHeaderSize = 14;
constexpr UINT MapStudioMessage = WM_APP + 0x342;
constexpr uint32_t GameAppGlobalRva = 0x59FCD0;
constexpr uint32_t GameSessionGlobalRva = 0x59FD90;
constexpr uint32_t GameNetworkGlobalRva = 0x59FD8C;
constexpr uint32_t EdmwUiManagerVtableRva = 0x3FC49C;
constexpr uint32_t EdmwJoinOrHostEventConstructorRva = 0x32C9;
constexpr uint32_t EdmwJoinOrHostEventDeletingDestructorRva = 0x3331;
constexpr uint32_t EdmwJoinOrHostEventHandlerRva = 0x337C;
constexpr uint32_t EdmwMultiplayerRuntimeConstructorRva = 0xEB193;
constexpr uint32_t MultiplayerSetupFormId = 26;
constexpr uint32_t MultiplayerConnectionFormId = 42;
constexpr uint32_t MultiplayerConnectionTextId = 1951;
constexpr size_t EdmwJoinOrHostEventSize = 0x50;
constexpr size_t EdmwWideStringSize = 0x14;
constexpr uint32_t EdmwNetworkClientInitializerRva = 0x3A5D;
constexpr uint32_t EdmwOperatorNewRva = 0x3D49B6;
constexpr uint32_t EdmwOperatorDeleteRva = 0x3D4950;
constexpr uint32_t EdmwCreateMutexAIatRva = 0x3E3070;
constexpr char EdmwSingleInstanceMutexName[] = "StainlessSteelStudiosPresentsEpicEngine";
using SocketFn = SOCKET(WSAAPI*)(int, int, int);
using BindFn = int(WSAAPI*)(SOCKET, const sockaddr*, int);
using ListenFn = int(WSAAPI*)(SOCKET, int);
using AcceptFn = SOCKET(WSAAPI*)(SOCKET, sockaddr*, int*);
using ConnectFn = int(WSAAPI*)(SOCKET, const sockaddr*, int);
using CloseFn = int(WSAAPI*)(SOCKET);
using SendFn = int(WSAAPI*)(SOCKET, const char*, int, int);
using SendToFn = int(WSAAPI*)(SOCKET, const char*, int, int, const sockaddr*, int);
using RecvFn = int(WSAAPI*)(SOCKET, char*, int, int);
using RecvFromFn = int(WSAAPI*)(SOCKET, char*, int, int, sockaddr*, int*);
using GetAdaptersInfoFn = ULONG(WINAPI*)(PIP_ADAPTER_INFO, PULONG);
using GetIpForwardTableFn = DWORD(WINAPI*)(PMIB_IPFORWARDTABLE, PULONG, BOOL);
using CreateMutexAFn = HANDLE(WINAPI*)(LPSECURITY_ATTRIBUTES, BOOL, LPCSTR);

SocketFn realSocket = ::socket;
BindFn realBind = ::bind;
ListenFn realListen = ::listen;
AcceptFn realAccept = ::accept;
ConnectFn realConnect = ::connect;
CloseFn realClose = ::closesocket;
SendFn realSend = ::send;
SendToFn realSendTo = ::sendto;
RecvFn realRecv = ::recv;
RecvFromFn realRecvFrom = ::recvfrom;
GetAdaptersInfoFn realGetAdaptersInfo = nullptr;
GetIpForwardTableFn realGetIpForwardTable = nullptr;
HANDLE pipeHandle = INVALID_HANDLE_VALUE;
HANDLE eventPipeHandle = INVALID_HANDLE_VALUE;
HANDLE statusPipeHandle = INVALID_HANDLE_VALUE;
HANDLE pipeWriteEvent = nullptr;
SRWLOCK pipeFrameWriteLock = SRWLOCK_INIT;
SRWLOCK statusPipeWriteLock = SRWLOCK_INIT;
std::mutex stateLock, pipeQueueLock;
std::deque<std::vector<uint8_t>> pipeQueue;
size_t pipeQueueBytes = 0;
std::atomic<bool> pipeConnected{ false };
std::atomic<bool> statusPipeConnected{ false };
std::unordered_map<SOCKET, sockaddr_in> virtualPeers;
std::unordered_map<uint32_t, sockaddr_in> tcpPeerAddresses;
std::atomic<uint16_t> ownRelayPort{ 0 };
std::atomic<uint16_t> tcpProxyPort{ 0 };
std::atomic<uint32_t> selectedPhysicalIpv4{ 0 };
std::atomic<bool> routeTableHookInstalled{ false };
std::atomic<bool> globalAddressMappingDetoursInstalled{ false };
std::atomic<int> routeTableMappingReported{ 0 };
thread_local bool networkMappingSelfCheckCall = false;
thread_local uint32_t selfCheckRouteFieldsRewritten = 0;
thread_local uint32_t selfCheckAdapterApiCalls = 0;
thread_local uint32_t selfCheckRouteApiCalls = 0;
std::atomic<bool> gameSetupWorkerRequested{ false };
std::atomic<bool> gameSetupWorkerEntryReported{ false };
std::atomic<bool> gameSetupWorkerStartFailureReported{ false };
std::atomic<uint64_t> gameSetupWorkerStartedAt{ 0 };
std::atomic<uint32_t> gameSetupWorkerStage{ 0 };
std::atomic<uint64_t> gameSetupWorkerStageSince{ 0 };
std::once_flag broadcastAddressInitialization;
std::vector<uint32_t> directedBroadcastAddresses;
HWND gameWindow = nullptr;
WNDPROC originalGameWindowProc = nullptr;
HMODULE engineModule = nullptr;
void* uiManager = nullptr;
std::string uiAutomationError;

void WriteNativeDiagnostic(const char* message) {
    wchar_t path[1024]{};
    if (!GetEnvironmentVariableW(L"EDMW_RELAY_DIAGNOSTICS_PATH", path, static_cast<DWORD>(_countof(path))) || !path[0]) return;
    HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
        OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    char line[4096]{};
    const int length = sprintf_s(line, sizeof(line), "[%lu:%lu] %s\r\n",
        GetCurrentProcessId(), GetCurrentThreadId(), message ? message : "(no message)");
    if (length > 0) {
        DWORD written = 0;
        WriteFile(file, line, static_cast<DWORD>(length), &written, nullptr);
        FlushFileBuffers(file);
    }
    CloseHandle(file);
}

struct MapStudioRequest {
    uint32_t operation;
    uint32_t formId;
    uint32_t controlId;
    uint32_t value;
    uint32_t playerCount;
    int32_t result;
    wchar_t text[256];
    wchar_t secondary[256];
    wchar_t third[64];
};

using GetFormByIdFn = void* (__thiscall*)(void*, uint32_t);
using IsFormVisibleFn = BOOL (__thiscall*)(void*, void*);
using GetControlByIdFn = void* (__thiscall*)(void*, uint32_t);
using GetCurrentFormFn = void* (__thiscall*)(void*);
using GetLabelGeTextFn = void* (__thiscall*)(void*);
using GetGeTextValueFn = const void* (__thiscall*)(void*);
using WideStringToAnsiFn = const char* (__thiscall*)(const void*);
using WideStringConstructorFn = void (__thiscall*)(void*, const char*);
using WideStringDestructorFn = void (__thiscall*)(void*);
using EdmwJoinOrHostEventConstructorFn = void* (__thiscall*)(void*, uint32_t, const void*, const void*, const void*, uint8_t);
using EdmwJoinOrHostEventDeletingDestructorFn = void* (__thiscall*)(void*, uint32_t);
using EdmwJoinOrHostEventFn = void (__thiscall*)(void*);
using EdmwMultiplayerRuntimeConstructorFn = void (__thiscall*)(void*, uint8_t);
using EdmwNetworkClientInitializerFn = void (__cdecl*)();
using EdmwAllocateFn = void* (__cdecl*)(size_t);
using EdmwFreeFn = void (__cdecl*)(void*);

GetFormByIdFn getFormById = nullptr;
IsFormVisibleFn isFormVisible = nullptr;
GetControlByIdFn getControlById = nullptr;
GetCurrentFormFn getCurrentForm = nullptr;
GetLabelGeTextFn getLabelGeText = nullptr;
GetGeTextValueFn getGeTextValue = nullptr;
WideStringToAnsiFn wideStringToAnsi = nullptr;
WideStringConstructorFn wideStringConstructor = nullptr;
WideStringDestructorFn wideStringDestructor = nullptr;
EdmwJoinOrHostEventConstructorFn edmwJoinOrHostEventConstructor = nullptr;
EdmwJoinOrHostEventDeletingDestructorFn edmwJoinOrHostEventDeletingDestructor = nullptr;
EdmwJoinOrHostEventFn edmwJoinOrHostEventHandler = nullptr;
EdmwMultiplayerRuntimeConstructorFn edmwMultiplayerRuntimeConstructor = nullptr;
EdmwNetworkClientInitializerFn edmwNetworkClientInitializer = nullptr;
EdmwAllocateFn edmwAllocate = nullptr;
EdmwFreeFn edmwFree = nullptr;
CreateMutexAFn realCreateMutexA = nullptr;

bool IsReadableAddress(const void* address, size_t bytes) {
    if (!address || bytes == 0) return false;
    MEMORY_BASIC_INFORMATION region{};
    if (VirtualQuery(address, &region, sizeof(region)) != sizeof(region) || region.State != MEM_COMMIT ||
        (region.Protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0) return false;
    const auto start = reinterpret_cast<uintptr_t>(address);
    const auto regionStart = reinterpret_cast<uintptr_t>(region.BaseAddress);
    const auto regionEnd = regionStart + region.RegionSize;
    return start >= regionStart && start <= regionEnd && bytes <= regionEnd - start;
}
HANDLE WINAPI HookCreateMutexA(LPSECURITY_ATTRIBUTES attributes, BOOL initialOwner, LPCSTR name) {
    if (realCreateMutexA && name && strcmp(name, EdmwSingleInstanceMutexName) == 0) {
        char uniqueName[128]{};
        if (sprintf_s(uniqueName, sizeof(uniqueName), "%s_EDMWVerify_%lu",
                EdmwSingleInstanceMutexName, GetCurrentProcessId()) > 0)
            return realCreateMutexA(attributes, initialOwner, uniqueName);
    }
    return realCreateMutexA ? realCreateMutexA(attributes, initialOwner, name) : nullptr;
}
void InstallVerificationMultiInstanceHook() {
    wchar_t enabled[8]{};
    if (!GetEnvironmentVariableW(L"EDMW_RELAY_TEST_MULTI_INSTANCE", enabled, _countof(enabled)) ||
        wcscmp(enabled, L"1") != 0) return;
    auto* executable = reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
    auto** importSlot = executable
        ? reinterpret_cast<FARPROC*>(executable + EdmwCreateMutexAIatRva) : nullptr;
    if (!importSlot || !IsReadableAddress(importSlot, sizeof(*importSlot)) || !*importSlot) {
        WriteNativeDiagnostic("test-only mutex hook could not resolve the supported game CreateMutexA import");
        return;
    }
    realCreateMutexA = reinterpret_cast<CreateMutexAFn>(*importSlot);
    DWORD oldProtection = 0;
    if (!VirtualProtect(importSlot, sizeof(*importSlot), PAGE_READWRITE, &oldProtection)) {
        WriteNativeDiagnostic("test-only mutex hook could not make the supported game import writable");
        realCreateMutexA = nullptr;
        return;
    }
    *importSlot = reinterpret_cast<FARPROC>(HookCreateMutexA);
    DWORD ignoredProtection = 0;
    VirtualProtect(importSlot, sizeof(*importSlot), oldProtection, &ignoredProtection);
    FlushInstructionCache(GetCurrentProcess(), importSlot, sizeof(*importSlot));
    WriteNativeDiagnostic("test-only hook gives each verification process a unique name for EDMW's single-instance mutex");
}
bool IsReadableProtection(DWORD protection) {
    if ((protection & (PAGE_GUARD | PAGE_NOACCESS)) != 0) return false;
    switch (protection & 0xFF) {
    case PAGE_READONLY:
    case PAGE_READWRITE:
    case PAGE_WRITECOPY:
    case PAGE_EXECUTE_READ:
    case PAGE_EXECUTE_READWRITE:
    case PAGE_EXECUTE_WRITECOPY:
        return true;
    default:
        return false;
    }
}
bool TryGetCurrentFormSafely(void* candidate, void** currentForm) {
    __try {
        *currentForm = getCurrentForm(candidate);
        return *currentForm != nullptr;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *currentForm = nullptr;
        return false;
    }
}
void* FindUiManagerByVtable(const void* uiManagerVtable, const void* edmwUiManagerVtable) {
    SYSTEM_INFO systemInfo{};
    GetSystemInfo(&systemInfo);
    uintptr_t cursor = reinterpret_cast<uintptr_t>(systemInfo.lpMinimumApplicationAddress);
    const uintptr_t limit = reinterpret_cast<uintptr_t>(systemInfo.lpMaximumApplicationAddress);
    while (cursor < limit) {
        MEMORY_BASIC_INFORMATION region{};
        if (VirtualQuery(reinterpret_cast<const void*>(cursor), &region, sizeof(region)) != sizeof(region) ||
            region.RegionSize == 0) break;
        const uintptr_t start = reinterpret_cast<uintptr_t>(region.BaseAddress);
        const uintptr_t end = start + region.RegionSize;
        if (region.State == MEM_COMMIT && region.RegionSize >= sizeof(void*) && IsReadableProtection(region.Protect) &&
            (region.Type == MEM_PRIVATE || region.Type == MEM_IMAGE)) {
            uintptr_t object = (start + sizeof(void*) - 1) & ~(static_cast<uintptr_t>(sizeof(void*)) - 1);
            for (; object <= end - sizeof(void*); object += sizeof(void*)) {
                const void* vtable = *reinterpret_cast<const void* const*>(object);
                if (vtable != uiManagerVtable && vtable != edmwUiManagerVtable) continue;
                void* currentForm = nullptr;
                auto* candidate = reinterpret_cast<void*>(object);
                if (TryGetCurrentFormSafely(candidate, &currentForm) && IsReadableAddress(currentForm, sizeof(void*))) {
                    char detail[128]{};
                    sprintf_s(detail, sizeof(detail), "%s manager found at %p with current form %p",
                        vtable == edmwUiManagerVtable ? "EDMW EEUIManager" : "base UIManager", candidate, currentForm);
                    WriteNativeDiagnostic(detail);
                    return candidate;
                }
            }
        }
        cursor = end > cursor ? end : cursor + 0x1000;
    }
    return nullptr;
}

uint16_t ReadU16(const uint8_t* p) { return static_cast<uint16_t>((p[0] << 8) | p[1]); }
void WriteU16(uint8_t* p, uint16_t value) { p[0] = static_cast<uint8_t>(value >> 8); p[1] = static_cast<uint8_t>(value); }
uint32_t TcpPeerKey(uint16_t listenerPort, uint16_t localProxyPort) {
    return (static_cast<uint32_t>(listenerPort) << 16) | localProxyPort;
}
bool ReadExact(HANDLE pipe, void* buffer, DWORD bytes) {
    auto* next = static_cast<uint8_t*>(buffer);
    while (bytes) {
        DWORD count = 0;
        if (!ReadFile(pipe, next, bytes, &count, nullptr)) {
            char detail[192]{};
            sprintf_s(detail, sizeof(detail), "ReadFile failed on control pipe handle=%p requested=%lu error=%lu",
                pipe, bytes, GetLastError());
            WriteNativeDiagnostic(detail);
            return false;
        }
        if (count == 0) {
            WriteNativeDiagnostic("ReadFile returned zero bytes on a connected control pipe");
            return false;
        }
        next += count; bytes -= count;
    }
    return true;
}
bool WriteExact(HANDLE pipe, const void* buffer, DWORD bytes) {
    auto* next = static_cast<const uint8_t*>(buffer);
    while (bytes) {
        DWORD count = 0;
        if (!WriteFile(pipe, next, bytes, &count, nullptr)) {
            char detail[192]{};
            sprintf_s(detail, sizeof(detail), "WriteFile failed on pipe handle=%p requested=%lu error=%lu",
                pipe, bytes, GetLastError());
            WriteNativeDiagnostic(detail);
            return false;
        }
        if (count == 0) {
            WriteNativeDiagnostic("WriteFile returned zero bytes on a connected pipe");
            return false;
        }
        next += count; bytes -= count;
    }
    return true;
}
bool WritePipeFrame(const std::vector<uint8_t>& body) {
    if (!pipeConnected.load() || pipeWriteEvent == nullptr || body.empty() || body.size() > PipeFrameLimit) return false;
    const size_t frameBytes = sizeof(uint32_t) + body.size();
    {
        std::lock_guard<std::mutex> lock(pipeQueueLock);
        if (!pipeConnected.load() || pipeQueueBytes + frameBytes > MaximumQueuedPipeBytes) return false;
        pipeQueue.push_back(body);
        pipeQueueBytes += frameBytes;
    }
    return SetEvent(pipeWriteEvent) != FALSE;
}
bool WritePipeFrameImmediately(const std::vector<uint8_t>& body) {
    if (!statusPipeConnected.load() || body.empty() || body.size() > PipeFrameLimit) return false;
    AcquireSRWLockExclusive(&statusPipeWriteLock);
    if (!statusPipeConnected.load()) {
        ReleaseSRWLockExclusive(&statusPipeWriteLock);
        return false;
    }
    const uint32_t length = static_cast<uint32_t>(body.size());
    const bool written = WriteExact(statusPipeHandle, &length, sizeof(length)) &&
        WriteExact(statusPipeHandle, body.data(), length);
    if (!written) statusPipeConnected.store(false);
    ReleaseSRWLockExclusive(&statusPipeWriteLock);
    WriteNativeDiagnostic(written ? "status frame written on its dedicated control pipe" : "status pipe write failed");
    return written;
}
void ReportMapAutomation(uint8_t state, const std::string& message) {
    WriteNativeDiagnostic(message.c_str());
    const size_t count = (std::min)(message.size(), static_cast<size_t>(2000));
    std::vector<uint8_t> body(2 + count);
    body[0] = 9;
    body[1] = state;
    if (count) memcpy(body.data() + 2, message.data(), count);
    WritePipeFrameImmediately(body);
}
void SetGameSetupStage(uint32_t stage) {
    gameSetupWorkerStage.store(stage);
    gameSetupWorkerStageSince.store(GetTickCount64());
}
const char* GameSetupStageName(uint32_t stage) {
    switch (stage) {
    case 1: return "worker entered native code";
    case 2: return "launch configuration read";
    case 3: return "waiting for the game window and UI manager";
    case 4: return "game window and UI manager attached";
    case 5: return "native multiplayer callback invoked";
    case 6: return "waiting for multiplayer form 26";
    case 7: return "multiplayer form 26 reached";
    default: return "unknown setup stage";
    }
}
bool ResolveUiAutomation() {
    uiAutomationError.clear();
    if (!engineModule) engineModule = GetModuleHandleW(L"Low-Level Engine.dll");
    if (!engineModule) { uiAutomationError = "Low-Level Engine.dll is not loaded."; return false; }
    getFormById = reinterpret_cast<GetFormByIdFn>(GetProcAddress(engineModule, "?GetFormByID@UIManager@@QBEPAVUIForm@@K@Z"));
    isFormVisible = reinterpret_cast<IsFormVisibleFn>(GetProcAddress(engineModule, "?IsFormVisible@UIManager@@QAE_NPAVUIForm@@@Z"));
    getControlById = reinterpret_cast<GetControlByIdFn>(GetProcAddress(engineModule, "?GetControlById@UIForm@@QAEPAVUIControl@@K@Z"));
    getCurrentForm = reinterpret_cast<GetCurrentFormFn>(GetProcAddress(engineModule, "?GetCurrentForm@UIManager@@QBEPAVUIForm@@XZ"));
    getLabelGeText = reinterpret_cast<GetLabelGeTextFn>(GetProcAddress(engineModule, "?GetGEText@UITextLabel@@QBEPAVGEText@@XZ"));
    getGeTextValue = reinterpret_cast<GetGeTextValueFn>(GetProcAddress(engineModule, "?GetText@GEText@@QBEABVUWideString@@XZ"));
    wideStringToAnsi = reinterpret_cast<WideStringToAnsiFn>(GetProcAddress(engineModule, "??BUWideString@@QBEPBDXZ"));
    wideStringConstructor = reinterpret_cast<WideStringConstructorFn>(GetProcAddress(engineModule, "??0UWideString@@QAE@PBD@Z"));
    wideStringDestructor = reinterpret_cast<WideStringDestructorFn>(GetProcAddress(engineModule, "??1UWideString@@UAE@XZ"));
    auto* executable = reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
    if (!executable) { uiAutomationError = "The game executable module is unavailable."; return false; }
    edmwJoinOrHostEventConstructor = reinterpret_cast<EdmwJoinOrHostEventConstructorFn>(executable + EdmwJoinOrHostEventConstructorRva);
    edmwJoinOrHostEventDeletingDestructor = reinterpret_cast<EdmwJoinOrHostEventDeletingDestructorFn>(executable + EdmwJoinOrHostEventDeletingDestructorRva);
    edmwJoinOrHostEventHandler = reinterpret_cast<EdmwJoinOrHostEventFn>(executable + EdmwJoinOrHostEventHandlerRva);
    edmwMultiplayerRuntimeConstructor = reinterpret_cast<EdmwMultiplayerRuntimeConstructorFn>(executable + EdmwMultiplayerRuntimeConstructorRva);
    edmwNetworkClientInitializer = reinterpret_cast<EdmwNetworkClientInitializerFn>(executable + EdmwNetworkClientInitializerRva);
    edmwAllocate = reinterpret_cast<EdmwAllocateFn>(executable + EdmwOperatorNewRva);
    edmwFree = reinterpret_cast<EdmwFreeFn>(executable + EdmwOperatorDeleteRva);
    if (!getFormById) { uiAutomationError = "EDMW export UIManager::GetFormByID is unavailable."; return false; }
    if (!isFormVisible) { uiAutomationError = "EDMW export UIManager::IsFormVisible is unavailable."; return false; }
    if (!getControlById) { uiAutomationError = "EDMW export UIForm::GetControlById is unavailable."; return false; }
    if (!getCurrentForm) { uiAutomationError = "EDMW export UIManager::GetCurrentForm is unavailable."; return false; }
    if (!getLabelGeText) { uiAutomationError = "EDMW export UITextLabel::GetGEText is unavailable."; return false; }
    if (!getGeTextValue) { uiAutomationError = "EDMW export GEText::GetText is unavailable."; return false; }
    if (!wideStringToAnsi) { uiAutomationError = "EDMW export UWideString conversion is unavailable."; return false; }
    if (!wideStringConstructor) { uiAutomationError = "EDMW export UWideString constructor is unavailable."; return false; }
    if (!wideStringDestructor) { uiAutomationError = "EDMW export UWideString destructor is unavailable."; return false; }
    if (!edmwJoinOrHostEventConstructor || !edmwJoinOrHostEventDeletingDestructor || !edmwJoinOrHostEventHandler ||
        !edmwMultiplayerRuntimeConstructor ||
        !edmwNetworkClientInitializer || !edmwAllocate || !edmwFree ||
        !IsReadableAddress(edmwJoinOrHostEventConstructor, 64) || !IsReadableAddress(edmwJoinOrHostEventDeletingDestructor, 32) ||
        !IsReadableAddress(edmwJoinOrHostEventHandler, 64) || !IsReadableAddress(edmwMultiplayerRuntimeConstructor, 32) ||
        !IsReadableAddress(edmwNetworkClientInitializer, 32) ||
        !IsReadableAddress(edmwAllocate, 16) || !IsReadableAddress(edmwFree, 16)) {
        uiAutomationError = "The supported Empires_DMW.exe native multiplayer callback mappings are unavailable.";
        return false;
    }
    const void* uiManagerVtable = GetProcAddress(engineModule, "??_7UIManager@@6B@");
    if (!uiManagerVtable) { uiAutomationError = "EDMW's UIManager vtable export is unavailable."; return false; }
    const void* edmwUiManagerVtable = executable + EdmwUiManagerVtableRva;
    if (!IsReadableAddress(edmwUiManagerVtable, sizeof(void*))) {
        uiAutomationError = "The supported EDMW EEUIManager vtable mapping is outside the loaded executable.";
        return false;
    }
    void* application = IsReadableAddress(executable + GameAppGlobalRva, sizeof(void*))
        ? *reinterpret_cast<void**>(executable + GameAppGlobalRva) : nullptr;
    uiManager = nullptr;
    if (application && IsReadableAddress(application, sizeof(void*))) {
        if (*reinterpret_cast<void**>(application) == uiManagerVtable ||
            *reinterpret_cast<void**>(application) == edmwUiManagerVtable) uiManager = application;
        for (size_t offset = 0; !uiManager && offset < 0x1000; offset += sizeof(void*)) {
            auto* member = static_cast<uint8_t*>(application) + offset;
            if (!IsReadableAddress(member, sizeof(void*))) break;
            void* candidate = *reinterpret_cast<void**>(member);
            if (candidate == uiManagerVtable || candidate == edmwUiManagerVtable) uiManager = member;
            else if (IsReadableAddress(candidate, sizeof(void*)) &&
                (*reinterpret_cast<void**>(candidate) == uiManagerVtable ||
                    *reinterpret_cast<void**>(candidate) == edmwUiManagerVtable)) uiManager = candidate;
        }
    }
    static uint64_t lastManagerScanAt = 0;
    const uint64_t now = GetTickCount64();
    if (!uiManager && now - lastManagerScanAt >= 1000) {
        lastManagerScanAt = now;
        WriteNativeDiagnostic("UIManager was not found inside the application object; scanning committed game memory for its exported vtable");
        uiManager = FindUiManagerByVtable(uiManagerVtable, edmwUiManagerVtable);
    }
    if (!uiManager) {
        uiAutomationError = application
            ? "Could not locate the Low-Level Engine UIManager inside the initialized EDMW application object or game memory."
            : "The EDMW application pointer was unavailable and no initialized UIManager instance was found in game memory.";
        return false;
    }
    void* currentForm = nullptr;
    if (!TryGetCurrentFormSafely(uiManager, &currentForm)) {
        uiAutomationError = "EDMW's resolved UIManager has no current form yet.";
        return false;
    }
    return uiManager != nullptr;
}
struct ScopedEdmwWideString {
    alignas(void*) uint8_t storage[EdmwWideStringSize]{};
    bool constructed = false;
    ScopedEdmwWideString(const char* value) {
        wideStringConstructor(storage, value);
        constructed = true;
    }
    ~ScopedEdmwWideString() {
        if (constructed) wideStringDestructor(storage);
    }
    ScopedEdmwWideString(const ScopedEdmwWideString&) = delete;
    ScopedEdmwWideString& operator=(const ScopedEdmwWideString&) = delete;
};
bool ConvertGameString(const std::wstring& source, std::string& target) {
    BOOL usedDefaultCharacter = FALSE;
    const int required = WideCharToMultiByte(CP_ACP, WC_NO_BEST_FIT_CHARS, source.c_str(), -1,
        nullptr, 0, nullptr, &usedDefaultCharacter);
    if (required <= 0 || usedDefaultCharacter || required > 1024) return false;
    target.assign(static_cast<size_t>(required), '\0');
    return WideCharToMultiByte(CP_ACP, WC_NO_BEST_FIT_CHARS, source.c_str(), -1,
        target.data(), required, nullptr, &usedDefaultCharacter) > 0 && !usedDefaultCharacter;
}
bool InvokeNativeMultiplayerCallbackProtected(void* event, const void* playerString,
    const void* connectionString, const void* emptyString, bool host) {
    bool constructed = false;
    bool completed = false;
    __try {
        edmwJoinOrHostEventConstructor(event, host ? 0u : 2u, playerString,
            connectionString, emptyString, 1u);
        constructed = true;
        WriteNativeDiagnostic(host
            ? "calling EDMW's native host callback (event=0, player name, lobby name, action flag=1)"
            : "calling EDMW's native Join by IP callback (event=2, player name, virtual host IP, Connect flag=1)");
        edmwJoinOrHostEventHandler(event);
        completed = true;
    } __finally {
        if (constructed) edmwJoinOrHostEventDeletingDestructor(event, 1);
        else edmwFree(event);
    }
    return completed;
}
bool ConstructNativeMultiplayerRuntimeProtected(void* runtimeContext) {
    __try {
        edmwMultiplayerRuntimeConstructor(runtimeContext, 0);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool CallNpimAllowNewConnectionsProtected(void* function, void** networkObject, uint32_t allow) {
    __try {
        using NpimAllowNewConnectionsFn = void(__cdecl*)(void**, uint32_t);
        reinterpret_cast<NpimAllowNewConnectionsFn>(function)(networkObject, allow);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool EnableHostNetworkAdmission(void** networkObject, std::wstring& error) {
    error.clear();
    if (!networkObject || !IsReadableAddress(networkObject, sizeof(*networkObject)) ||
        !*networkObject || !IsReadableAddress(*networkObject, 0x6C)) {
        error = L"EDMW's host network session is unavailable after its native callback.";
        return false;
    }
    HMODULE provider = GetModuleHandleW(L"TCP Protocol.dll");
    if (!provider) {
        error = L"EDMW's TCP/IP provider is not loaded; the host cannot enable new Join packets.";
        return false;
    }
    auto* allowFunction = reinterpret_cast<uint8_t*>(provider) + 0x10F0;
    static constexpr uint8_t supportedSignature[] = {
        0x8B, 0x44, 0x24, 0x04, 0x8B, 0x00, 0x8A, 0x4C, 0x24, 0x08, 0x88, 0x48, 0x68, 0xC3
    };
    if (!IsReadableAddress(allowFunction, sizeof(supportedSignature)) ||
        memcmp(allowFunction, supportedSignature, sizeof(supportedSignature)) != 0) {
        error = L"EDMW's TCP/IP provider does not match the supported NPIMAllowNewConnections mapping.";
        return false;
    }
    auto* session = static_cast<uint8_t*>(*networkObject);
    const uint32_t transportFlags = *reinterpret_cast<uint32_t*>(session + 0x38);
    const uint8_t allowBefore = session[0x68];
    if (!CallNpimAllowNewConnectionsProtected(allowFunction, networkObject, 1)) {
        error = L"EDMW's native provider failed while enabling new UDP peer admission.";
        return false;
    }
    const uint8_t allowAfter = session[0x68];
    char detail[192]{};
    sprintf_s(detail, sizeof(detail),
        "NPIMAllowNewConnections(true) session=%p transportFlags=%08lX allowBefore=%u allowAfter=%u",
        session, transportFlags, static_cast<unsigned>(allowBefore), static_cast<unsigned>(allowAfter));
    WriteNativeDiagnostic(detail);
    if ((transportFlags & 0x2) == 0) {
        error = L"EDMW reached form 26, but its TCP/IP provider did not finish publishing the UDP host endpoint.";
        return false;
    }
    if (allowAfter == 0) {
        error = L"EDMW's TCP/IP provider did not enable new UDP peer admission for the host.";
        return false;
    }
    ReportMapAutomation(5, "EDMW's UDP host endpoint is published and its provider is accepting new players.");
    return true;
}
bool InvokeNativeMultiplayerCallback(bool host, const std::wstring& playerName,
    const std::wstring& roomName, const std::wstring& hostAddress, uint32_t playerCount,
    std::wstring& error) {
    error.clear();
    if (!uiManager || !playerName.size() ||
        (host && (roomName.empty() || playerCount < 2 || playerCount > 8)) ||
        (!host && hostAddress.empty())) {
        error = L"The direct multiplayer launch configuration is incomplete.";
        return false;
    }
    auto* executable = reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
    if (!executable || !IsReadableAddress(executable + GameSessionGlobalRva, sizeof(void*)) ||
        !IsReadableAddress(executable + GameNetworkGlobalRva, sizeof(void*))) {
        error = L"Empires_DMW.exe runtime globals are outside the supported executable mapping.";
        return false;
    }
    void* runtimeContext = *reinterpret_cast<void**>(executable + GameSessionGlobalRva);
    void** networkObject = reinterpret_cast<void**>(executable + GameNetworkGlobalRva);
    if (!runtimeContext) {
        WriteNativeDiagnostic("constructing EDMW's native multiplayer runtime with the game's standard 0xA64-byte allocation and 0x4EB193 constructor");
        runtimeContext = edmwAllocate(0xA64);
        if (runtimeContext) {
            if (ConstructNativeMultiplayerRuntimeProtected(runtimeContext)) {
                *reinterpret_cast<void**>(executable + GameSessionGlobalRva) = runtimeContext;
            } else {
                edmwFree(runtimeContext);
                runtimeContext = nullptr;
            }
        }
        if (!runtimeContext) {
            error = L"EDMW could not construct its native multiplayer runtime object.";
            return false;
        }
    }
    if (!runtimeContext || !IsReadableAddress(runtimeContext, 0xA64)) {
        error = L"EDMW's runtime/session object is not initialized; the native callback was not invoked.";
        return false;
    }
    void* form26 = getFormById(uiManager, MultiplayerSetupFormId);
    if (!form26 || !IsReadableAddress(form26, 0x35C)) {
        error = L"EDMW's initialized UI manager does not contain multiplayer form 26.";
        return false;
    }
    if (*reinterpret_cast<uint32_t*>(static_cast<uint8_t*>(form26) + 0x358) != MultiplayerSetupFormId) {
        error = L"EDMW's form table returned an object other than multiplayer form 26.";
        return false;
    }
    std::string playerAnsi, roomAnsi, addressAnsi;
    if (!ConvertGameString(playerName, playerAnsi) ||
        (host && !ConvertGameString(roomName, roomAnsi)) ||
        (!host && !ConvertGameString(hostAddress, addressAnsi))) {
        error = L"A multiplayer name or address contains characters unsupported by EDMW's ANSI string format.";
        return false;
    }
    // The multiplayer runtime constructor alone leaves the game with a default
    // network object. The native Join by IP path replaces it through the
    // game's initializer at RVA 0x3A5D; the host path needs the same initialized
    // TCP/IP client object before its callback can publish a lobby endpoint.
    WriteNativeDiagnostic(*networkObject
        ? (host
            ? "replacing EDMW's runtime network object with its standard TCP/IP multiplayer object before host setup"
            : "replacing EDMW's runtime network object with its standard TCP/IP multiplayer object for Join by IP")
        : (host
            ? "initializing EDMW's standard TCP/IP multiplayer object before host setup"
            : "initializing EDMW's standard TCP/IP multiplayer object for Join by IP"));
    edmwNetworkClientInitializer();
    if (!*networkObject || !IsReadableAddress(*networkObject, sizeof(void*))) {
        error = host
            ? L"EDMW's native TCP/IP host object did not initialize; host setup was not invoked."
            : L"EDMW's native TCP/IP client object did not initialize; Join by IP was not invoked.";
        return false;
    }
    const std::string empty;
    ScopedEdmwWideString playerString(playerAnsi.c_str());
    ScopedEdmwWideString connectionString(host ? roomAnsi.c_str() : addressAnsi.c_str());
    ScopedEdmwWideString emptyString(empty.c_str());
    void* event = edmwAllocate(EdmwJoinOrHostEventSize);
    if (!event) {
        error = L"Empires_DMW.exe could not allocate its native multiplayer callback object.";
        return false;
    }
    if (!InvokeNativeMultiplayerCallbackProtected(event, playerString.storage,
        connectionString.storage, emptyString.storage, host)) {
        error = L"EDMW's native multiplayer callback did not complete.";
        return false;
    }
    if (host) {
        std::wstring admissionError;
        if (!EnableHostNetworkAdmission(networkObject, admissionError)) {
            error = admissionError;
            return false;
        }
    }
    char detail[192]{};
    sprintf_s(detail, sizeof(detail), "EDMW native multiplayer callback returned; runtime=%p network=%p form26=%p",
        runtimeContext, *networkObject, form26);
    WriteNativeDiagnostic(detail);
    return true;
}
LRESULT DispatchMapStudioRequest(MapStudioRequest* request) {
        WriteNativeDiagnostic("game window received a Mod Studio UI request");
        if (request->operation == 2) {
            request->value = 1;
            void* form = getFormById(uiManager, request->formId);
            if (!form) { request->result = -1; wcscpy_s(request->text, L"The requested EDMW form is not loaded."); return request->result; }
            request->value = 2;
            void* control = form ? getControlById(form, request->controlId) : nullptr;
            if (!control) { request->result = -2; wcscpy_s(request->text, L"The requested EDMW label control is missing."); return request->result; }
            request->value = 3;
            void* geText = control ? getLabelGeText(control) : nullptr;
            request->value = 4;
            const void* wideText = geText ? getGeTextValue(geText) : nullptr;
            request->value = 5;
            const char* text = wideText ? wideStringToAnsi(wideText) : nullptr;
            if (text) request->result = MultiByteToWideChar(CP_ACP, 0, text, -1,
                request->text, static_cast<int>(_countof(request->text))) > 0 ? 1 : 0;
            else { request->result = -3; wcscpy_s(request->text, L"EDMW did not expose text for the requested label."); }
        } else if (request->operation == 3) {
            void* form = getFormById(uiManager, request->formId);
            void* currentForm = getCurrentForm(uiManager);
            const BOOL visible = form ? isFormVisible(uiManager, form) : FALSE;
            request->result = form && currentForm == form && visible ? 1 : 0;
            if (request->formId == MultiplayerSetupFormId) {
                static std::atomic<uint32_t> form26PollTrace{ 0 };
                const uint32_t trace = form26PollTrace.fetch_add(1);
                if (trace < 6 || trace % 100 == 0) {
                    uint32_t currentFormId = 0xFFFFFFFFu;
                    if (currentForm && IsReadableAddress(static_cast<uint8_t*>(currentForm) + 0x358, sizeof(currentFormId)))
                        currentFormId = *reinterpret_cast<uint32_t*>(static_cast<uint8_t*>(currentForm) + 0x358);
                    char detail[224]{};
                    sprintf_s(detail, sizeof(detail), "Form 26 poll manager=%p form=%p current=%p currentId=%u visible=%u result=%d",
                        uiManager, form, currentForm, currentFormId, visible ? 1u : 0u, request->result);
                    WriteNativeDiagnostic(detail);
                }
            }
        } else if (request->operation == 4) {
            void* currentForm = getCurrentForm(uiManager);
            if (!currentForm || !IsReadableAddress(static_cast<uint8_t*>(currentForm) + 0x358, sizeof(uint32_t))) {
                request->result = -1;
                wcscpy_s(request->text, L"EDMW has no readable current form ID yet.");
            } else {
                request->value = *reinterpret_cast<uint32_t*>(static_cast<uint8_t*>(currentForm) + 0x358);
                request->result = 1;
            }
        } else if (request->operation == 10) {
            WriteNativeDiagnostic("dispatching EDMW's native host/join callback without opening Form 30");
            const std::wstring playerName(request->text);
            const std::wstring roomName(request->secondary);
            const std::wstring hostAddress(request->third);
            const bool host = request->value == 1;
            const bool join = request->value == 2;
            if ((!host && !join) || playerName.empty()) {
                request->result = -1;
                wcscpy_s(request->text, L"The direct multiplayer launch role or player name is invalid.");
                return request->result;
            }
            std::wstring error;
            if (!InvokeNativeMultiplayerCallback(host, playerName, roomName, hostAddress,
                request->playerCount, error)) {
                request->result = -2;
                wcscpy_s(request->text, error.c_str());
                return request->result;
            }
            wcscpy_s(request->text, host
                ? L"EDMW's native host callback completed; waiting for its Form 26 transition."
                : L"EDMW's native Join by IP callback completed; waiting for its Form 26 transition.");
            request->result = 1;
        }
        return request->result;
}
LRESULT CALLBACK MapStudioWindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    if (message == MapStudioMessage && lParam != 0 && uiManager) {
        auto* request = reinterpret_cast<MapStudioRequest*>(lParam);
        __try {
            return DispatchMapStudioRequest(request);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            const DWORD exceptionCode = GetExceptionCode();
            request->result = -100;
            swprintf_s(request->text, _countof(request->text), L"EDMW native game callback raised exception 0x%08lX.", exceptionCode);
            char detail[256]{};
            sprintf_s(detail, sizeof(detail), "EDMW native UI request operation=%lu form=%lu control=%lu stage=%lu raised exception 0x%08lX",
                request->operation, request->formId, request->controlId, request->value, exceptionCode);
            WriteNativeDiagnostic(detail);
            return request->result;
        }
    }
    return originalGameWindowProc ? CallWindowProcW(originalGameWindowProc, window, message, wParam, lParam)
                                  : DefWindowProcW(window, message, wParam, lParam);
}
BOOL CALLBACK FindGameWindow(HWND window, LPARAM parameter) {
    DWORD processId = 0;
    GetWindowThreadProcessId(window, &processId);
    if (processId != static_cast<DWORD>(parameter) || !IsWindow(window)) return TRUE;
    wchar_t className[128]{};
    wchar_t title[256]{};
    GetClassNameW(window, className, static_cast<int>(_countof(className)));
    GetWindowTextW(window, title, static_cast<int>(_countof(title)));
    char classAnsi[256]{};
    char titleAnsi[512]{};
    WideCharToMultiByte(CP_ACP, 0, className, -1, classAnsi, static_cast<int>(_countof(classAnsi)), nullptr, nullptr);
    WideCharToMultiByte(CP_ACP, 0, title, -1, titleAnsi, static_cast<int>(_countof(titleAnsi)), nullptr, nullptr);
    char detail[1024]{};
    sprintf_s(detail, sizeof(detail), "EDMW window candidate %p owner=%p visible=%u style=%08lX class='%s' title='%s'",
        window, GetWindow(window, GW_OWNER), IsWindowVisible(window) ? 1u : 0u,
        static_cast<DWORD>(GetWindowLongPtrW(window, GWL_STYLE)), classAnsi, titleAnsi);
    WriteNativeDiagnostic(detail);
    if (GetWindow(window, GW_OWNER) != nullptr || _wcsicmp(className, L"D3DProxyWindow") == 0) return TRUE;
    if (!gameWindow || _wcsicmp(className, L"Loading Game Window") == 0) gameWindow = window;
    return TRUE;
}
bool InstallMapStudioWindowProc() {
    if (gameWindow && originalGameWindowProc && IsWindow(gameWindow)) return true;
    gameWindow = nullptr;
    originalGameWindowProc = nullptr;
    EnumWindows(FindGameWindow, static_cast<LPARAM>(GetCurrentProcessId()));
    if (!gameWindow) { uiAutomationError = "The EDMW main window was not found."; return false; }
    char selected[128]{};
    sprintf_s(selected, sizeof(selected), "selected EDMW game window %p", gameWindow);
    WriteNativeDiagnostic(selected);
    SetLastError(ERROR_SUCCESS);
    const LONG_PTR prior = SetWindowLongPtrW(gameWindow, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(MapStudioWindowProc));
    if (prior == 0 && GetLastError() != ERROR_SUCCESS) {
        uiAutomationError = "Could not attach the EDMW window event hook (Windows error " + std::to_string(GetLastError()) + ").";
        return false;
    }
    originalGameWindowProc = reinterpret_cast<WNDPROC>(prior);
    if (!originalGameWindowProc) uiAutomationError = "EDMW returned an empty original window procedure.";
    if (originalGameWindowProc && IsWindowVisible(gameWindow)) {
        ShowWindow(gameWindow, SW_HIDE);
        WriteNativeDiagnostic("hid the EDMW game window while preparing form 26");
    }
    return originalGameWindowProc != nullptr;
}
bool SendMapStudioRequest(MapStudioRequest& request) {
    if ((!gameWindow || !IsWindow(gameWindow)) && !InstallMapStudioWindowProc()) {
        uiAutomationError = "The EDMW main window is unavailable.";
        return false;
    }
    if (!gameWindow || !IsWindow(gameWindow)) { uiAutomationError = "The EDMW main window was destroyed before its control request."; return false; }
    DWORD_PTR result = 0;
    const UINT timeout = request.operation == 10 ? 30000u : 5000u;
    if (!SendMessageTimeoutW(gameWindow, MapStudioMessage, 0, reinterpret_cast<LPARAM>(&request),
        SMTO_ABORTIFHUNG | SMTO_BLOCK, timeout, &result)) {
        uiAutomationError = "EDMW's game thread did not process the native multiplayer request (Windows error " +
            std::to_string(GetLastError()) + ").";
        return false;
    }
    if (request.result < 0) {
        char message[256]{};
        WideCharToMultiByte(CP_ACP, 0, request.text, -1, message, static_cast<int>(_countof(message)), nullptr, nullptr);
        uiAutomationError = message;
        return false;
    }
    return request.result > 0;
}
bool IsMapStudioFormVisible(uint32_t formId) {
    if (!ResolveUiAutomation()) return false;
    MapStudioRequest request{};
    request.operation = 3;
    request.formId = formId;
    return SendMapStudioRequest(request);
}
uint32_t ReadMapStudioCurrentFormId() {
    if (!ResolveUiAutomation()) return 0xFFFFFFFFu;
    MapStudioRequest request{};
    request.operation = 4;
    if (!SendMapStudioRequest(request)) return 0xFFFFFFFFu;
    return request.value;
}
bool WaitForMapStudioForm(uint32_t formId, DWORD timeoutMs) {
    for (DWORD elapsed = 0; elapsed <= timeoutMs; elapsed += 100) {
        if (IsMapStudioFormVisible(formId)) return true;
        Sleep(100);
    }
    return false;
}
bool ReadMapStudioLabel(uint32_t formId, uint32_t controlId, std::wstring& text) {
    uiAutomationError.clear();
    MapStudioRequest request{};
    request.operation = 2;
    request.formId = formId;
    request.controlId = controlId;
    if (!SendMapStudioRequest(request)) return false;
    text.assign(request.text);
    while (!text.empty() && iswspace(text.back())) text.pop_back();
    size_t first = 0;
    while (first < text.size() && iswspace(text[first])) ++first;
    if (first) text.erase(0, first);
    return !text.empty();
}
bool JoinMultiplayerLobby(const std::wstring& role, const std::wstring& playerName,
    const std::wstring& roomName, const std::wstring& hostAddress, uint32_t playerCount) {
    uiAutomationError.clear();
    if (playerName.empty() || playerName.size() >= 256 || roomName.size() >= 256 || hostAddress.size() >= 64 ||
        (role != L"host" && role != L"join") || (role == L"host" && (roomName.empty() || playerCount < 2 || playerCount > 8)) ||
        (role == L"join" && hostAddress.empty())) {
        uiAutomationError = "The direct form-26 launch configuration is incomplete or outside the game limits.";
        return false;
    }
    MapStudioRequest request{};
    request.operation = 10;
    request.value = role == L"host" ? 1u : 2u;
    request.playerCount = playerCount;
    wcscpy_s(request.text, playerName.c_str());
    wcscpy_s(request.secondary, roomName.c_str());
    wcscpy_s(request.third, hostAddress.c_str());
    if (!SendMapStudioRequest(request)) return false;
    ReportMapAutomation(5, role == L"host"
        ? "EDMW's native host callback returned; waiting for multiplayer lobby form 26."
        : "EDMW's native Join by IP callback returned; waiting for multiplayer lobby form 26.");
    SetGameSetupStage(6);
    if (!WaitForMapStudioForm(MultiplayerSetupFormId, 45000)) {
        std::wstring connectionText;
        const auto currentFormId = ReadMapStudioCurrentFormId();
        const std::string currentFormError = currentFormId == 0xFFFFFFFFu ? uiAutomationError : std::string{};
        if (ReadMapStudioLabel(MultiplayerConnectionFormId, MultiplayerConnectionTextId, connectionText)) {
            char message[512]{};
            const int required = WideCharToMultiByte(CP_ACP, 0, connectionText.c_str(), -1, message,
                static_cast<int>(_countof(message)), nullptr, nullptr);
            uiAutomationError = required > 0
                ? "EDMW did not reach the multiplayer lobby: " + std::string(message)
                : "EDMW did not reach multiplayer lobby form 26 within 45 seconds; current form ID is " +
                    std::to_string(currentFormId) + ".";
        } else {
            const std::string labelReadError = uiAutomationError;
            uiAutomationError = "EDMW did not reach multiplayer lobby form 26 within 45 seconds; current form ID is " +
                std::to_string(currentFormId) + ".";
            if (!currentFormError.empty()) uiAutomationError += " Current form read failed: " + currentFormError;
            if (!labelReadError.empty()) uiAutomationError += " Connection status read failed: " + labelReadError;
        }
        return false;
    }
    return true;
}
bool AttachEdmwUiAutomation() {
    bool windowProcInstalled = false;
    for (DWORD elapsed = 0; elapsed < 60000; elapsed += 100) {
        if (!windowProcInstalled) {
            windowProcInstalled = InstallMapStudioWindowProc();
            if (windowProcInstalled)
                ReportMapAutomation(5, "EDMW game window found; resolving its initialized UI manager.");
        }
        if (windowProcInstalled && ResolveUiAutomation()) return true;
        if (elapsed % 5000 == 0) {
            const std::string detail = uiAutomationError.empty()
                ? "waiting for the EDMW main window and UI manager" : uiAutomationError;
            ReportMapAutomation(5, "EDMW UI attach stage: " + detail);
        }
        Sleep(100);
    }
    ReportMapAutomation(9, uiAutomationError.empty()
        ? "EDMW's UI event bridge did not attach to its main window and initialized UI manager within 60 seconds."
        : "EDMW UI attachment failed: " + uiAutomationError);
    return false;
}
[[maybe_unused]] DWORD WINAPI MapAutomationWorker(void*) {
    gameSetupWorkerEntryReported.store(true);
    SetGameSetupStage(1);
    WriteNativeDiagnostic("host setup worker entered");
    ReportMapAutomation(5, "Native host callback worker started.");
    wchar_t playerName[256]{};
    wchar_t roomName[256]{};
    wchar_t countText[16]{};
    if (!GetEnvironmentVariableW(L"EDMW_RELAY_PLAYER_NAME", playerName, _countof(playerName)) || playerName[0] == L'\0' ||
        !GetEnvironmentVariableW(L"EDMW_RELAY_ROOM_NAME", roomName, _countof(roomName)) || roomName[0] == L'\0' ||
        !GetEnvironmentVariableW(L"EDMW_RELAY_PLAYER_COUNT", countText, _countof(countText)) || countText[0] == L'\0') {
        ReportMapAutomation(9, "The host launch is missing its player name, matchmaking room name, or room member count.");
        return 1;
    }
    SetGameSetupStage(2);
    wchar_t* countEnd = nullptr;
    const unsigned long playerCount = wcstoul(countText, &countEnd, 10);
    if (countEnd == countText || *countEnd != L'\0' || playerCount < 2 || playerCount > 8) {
        ReportMapAutomation(9, "The host launch has an invalid matchmaking room member count.");
        return 2;
    }
    SetGameSetupStage(3);
    if (!AttachEdmwUiAutomation()) return 5;
    SetGameSetupStage(4);
    ReportMapAutomation(5, "EDMW's initialized UI manager is attached; invoking its native host callback without opening game menus.");
    SetGameSetupStage(5);
    if (!JoinMultiplayerLobby(L"host", playerName, roomName, L"", playerCount)) {
        ReportMapAutomation(9, uiAutomationError.empty() ? "EDMW could not enter multiplayer lobby form 26 as host." : uiAutomationError);
        return 6;
    }
    SetGameSetupStage(7);
    ReportMapAutomation(8, "EDMW loaded the host directly into multiplayer lobby form 26.");
    return 0;
}
[[maybe_unused]] DWORD WINAPI ClientConnectionWorker(void*) {
    gameSetupWorkerEntryReported.store(true);
    SetGameSetupStage(1);
    WriteNativeDiagnostic("join setup worker entered");
    ReportMapAutomation(5, "Native Join by IP callback worker started.");
    wchar_t playerName[256]{};
    wchar_t hostAddress[64]{};
    if (!GetEnvironmentVariableW(L"EDMW_RELAY_PLAYER_NAME", playerName, _countof(playerName)) || playerName[0] == L'\0' ||
        !GetEnvironmentVariableW(L"EDMW_RELAY_HOST_ADDRESS", hostAddress, _countof(hostAddress)) || hostAddress[0] == L'\0') {
        ReportMapAutomation(9, "The join launch is missing its player name or host virtual IP address.");
        return 1;
    }
    SetGameSetupStage(2);
    SetGameSetupStage(3);
    if (!AttachEdmwUiAutomation()) return 2;
    SetGameSetupStage(4);
    ReportMapAutomation(5, "EDMW's initialized UI manager is attached; invoking native Join by IP without opening game menus.");
    SetGameSetupStage(5);
    if (!JoinMultiplayerLobby(L"join", playerName, L"", hostAddress, 0)) {
        ReportMapAutomation(9, uiAutomationError.empty()
            ? "EDMW could not join the host's TCP/IP lobby using the Join by IP control."
            : uiAutomationError);
        return 3;
    }
    SetGameSetupStage(7);
    ReportMapAutomation(8, "EDMW loaded the joining player directly into multiplayer lobby form 26.");
    return 0;
}
bool IsDatagram(SOCKET socket) {
    int type = 0, size = sizeof(type);
    return ::getsockopt(socket, SOL_SOCKET, SO_TYPE, reinterpret_cast<char*>(&type), &size) == 0 && type == SOCK_DGRAM;
}
bool IsSocketType(SOCKET socket, int expected) {
    int type = 0, size = sizeof(type);
    return ::getsockopt(socket, SOL_SOCKET, SO_TYPE, reinterpret_cast<char*>(&type), &size) == 0 && type == expected;
}
void InitializeDirectedBroadcastAddresses() {
    ULONG bufferSize = 16 * 1024;
    std::vector<uint8_t> buffer(bufferSize);
    constexpr ULONG flags = GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER;
    auto* adapters = reinterpret_cast<PIP_ADAPTER_ADDRESSES>(buffer.data());
    ULONG result = GetAdaptersAddresses(AF_INET, flags, nullptr, adapters, &bufferSize);
    if (result == ERROR_BUFFER_OVERFLOW) {
        buffer.resize(bufferSize);
        adapters = reinterpret_cast<PIP_ADAPTER_ADDRESSES>(buffer.data());
        result = GetAdaptersAddresses(AF_INET, flags, nullptr, adapters, &bufferSize);
    }
    if (result != NO_ERROR) return;
    for (auto* adapter = adapters; adapter; adapter = adapter->Next) {
        for (auto* address = adapter->FirstUnicastAddress; address; address = address->Next) {
            if (!address->Address.lpSockaddr || address->Address.lpSockaddr->sa_family != AF_INET) continue;
            const auto prefix = address->OnLinkPrefixLength;
            // /31 and /32 links have no directed broadcast address.
            if (prefix >= 31) continue;
            const auto* ipv4 = reinterpret_cast<const sockaddr_in*>(address->Address.lpSockaddr);
            const uint32_t local = ntohl(ipv4->sin_addr.s_addr);
            const uint32_t mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
            directedBroadcastAddresses.push_back((local & mask) | ~mask);
        }
    }
}
bool IsDirectedBroadcast(uint32_t address) {
    std::call_once(broadcastAddressInitialization, InitializeDirectedBroadcastAddresses);
    return std::find(directedBroadcastAddresses.begin(), directedBroadcastAddresses.end(), address) != directedBroadcastAddresses.end();
}
ULONG WINAPI HookGetAdaptersInfo(PIP_ADAPTER_INFO adapters, PULONG bufferLength) {
    static std::atomic<int> adapterMappingReported{ 0 };
    if (networkMappingSelfCheckCall) ++selfCheckAdapterApiCalls;
    if (!realGetAdaptersInfo) return ERROR_NOT_SUPPORTED;
    const ULONG result = realGetAdaptersInfo(adapters, bufferLength);
    if (result != NO_ERROR || !adapters) return result;

    const uint16_t relayPort = ownRelayPort.load();
    const uint32_t physicalIpv4 = selectedPhysicalIpv4.load();
    if (relayPort < RelayPortBase || relayPort > RelayPortBase + 7 || physicalIpv4 == 0) return result;
    const uint8_t localOctet = static_cast<uint8_t>(relayPort - RelayPortBase + 1);
    IN_ADDR virtualAddress{};
    virtualAddress.s_addr = htonl(VirtualNetwork | localOctet);
    char virtualIp[INET_ADDRSTRLEN]{};
    IN_ADDR physicalAddress{};
    physicalAddress.s_addr = physicalIpv4;
    char physicalIp[INET_ADDRSTRLEN]{};
    if (!InetNtopA(AF_INET, &virtualAddress, virtualIp, sizeof(virtualIp)) ||
        !InetNtopA(AF_INET, &physicalAddress, physicalIp, sizeof(physicalIp))) return result;

    PIP_ADAPTER_INFO selectedAdapter = nullptr;
    IP_ADDR_STRING* selectedAddress = nullptr;
    for (auto* adapter = adapters; adapter; adapter = adapter->Next) {
        for (auto* address = &adapter->IpAddressList; address; address = address->Next) {
            IN_ADDR parsed{};
            if (InetPtonA(AF_INET, address->IpAddress.String, &parsed) == 1 &&
                parsed.s_addr == physicalIpv4) {
                selectedAdapter = adapter;
                selectedAddress = address;
                break;
            }
        }
        if (selectedAddress) break;
    }
    if (!selectedAdapter || !selectedAddress) {
        char detail[192]{};
        sprintf_s(detail, sizeof(detail),
            "GameRanger-style adapter mapping skipped: ICE local IPv4 %s was not present in GetAdaptersInfo",
            physicalIp);
        WriteNativeDiagnostic(detail);
        if (!networkMappingSelfCheckCall && adapterMappingReported.exchange(1) != 1)
            ReportMapAutomation(5, std::string("Network mapping did not find ICE's local IPv4 ") + physicalIp +
                " in the game's adapter list; the route is inactive.");
        return result;
    }

    strcpy_s(selectedAddress->IpAddress.String, sizeof(selectedAddress->IpAddress.String), virtualIp);
    // GameRanger's encrypted mask literal is a 16-byte C string consistent with
    // 255.255.255.255; retain the same host route mask.
    strcpy_s(selectedAddress->IpMask.String, sizeof(selectedAddress->IpMask.String), "255.255.255.255");
    selectedAdapter->DhcpEnabled = 0;
    strcpy_s(selectedAdapter->DhcpServer.IpAddress.String,
        sizeof(selectedAdapter->DhcpServer.IpAddress.String), "0.0.0.0");
    strcpy_s(selectedAdapter->DhcpServer.IpMask.String,
        sizeof(selectedAdapter->DhcpServer.IpMask.String), "0.0.0.0");
    selectedAdapter->HaveWins = FALSE;
    strcpy_s(selectedAdapter->PrimaryWinsServer.IpAddress.String,
        sizeof(selectedAdapter->PrimaryWinsServer.IpAddress.String), "0.0.0.0");
    strcpy_s(selectedAdapter->PrimaryWinsServer.IpMask.String,
        sizeof(selectedAdapter->PrimaryWinsServer.IpMask.String), "0.0.0.0");
    strcpy_s(selectedAdapter->SecondaryWinsServer.IpAddress.String,
        sizeof(selectedAdapter->SecondaryWinsServer.IpAddress.String), "0.0.0.0");
    strcpy_s(selectedAdapter->SecondaryWinsServer.IpMask.String,
        sizeof(selectedAdapter->SecondaryWinsServer.IpMask.String), "0.0.0.0");

    char detail[256]{};
    sprintf_s(detail, sizeof(detail),
        "GameRanger-style TCP/IP adapter mapping physical=%s virtual=%s mask=255.255.255.255",
        physicalIp, virtualIp);
    WriteNativeDiagnostic(detail);
    if (!networkMappingSelfCheckCall && adapterMappingReported.exchange(2) != 2)
        ReportMapAutomation(5, std::string("Network mapping active: ") + physicalIp + " maps to " + virtualIp + ".");
    return result;
}
DWORD WINAPI HookGetIpForwardTable(PMIB_IPFORWARDTABLE table, PULONG bufferLength, BOOL order) {
    if (networkMappingSelfCheckCall) ++selfCheckRouteApiCalls;
    if (!realGetIpForwardTable) return ERROR_NOT_SUPPORTED;
    const DWORD result = realGetIpForwardTable(table, bufferLength, order);
    const uint32_t physicalIpv4 = selectedPhysicalIpv4.load();
    const uint16_t relayPort = ownRelayPort.load();
    if (result != NO_ERROR || !table || physicalIpv4 == 0 ||
        relayPort < RelayPortBase || relayPort > RelayPortBase + 7) return result;

    const uint32_t virtualIpv4 = htonl(VirtualNetwork | static_cast<uint8_t>(relayPort - RelayPortBase + 1));
    uint32_t rewritten = 0;
    for (DWORD index = 0; index < table->dwNumEntries; ++index) {
        auto& row = table->table[index];
        if (row.dwForwardDest == physicalIpv4) {
            row.dwForwardDest = virtualIpv4;
            ++rewritten;
        }
        if (row.dwForwardNextHop == physicalIpv4) {
            row.dwForwardNextHop = virtualIpv4;
            ++rewritten;
        }
    }
    if (rewritten != 0) {
        IN_ADDR physicalAddress{}; physicalAddress.s_addr = physicalIpv4;
        IN_ADDR virtualAddress{}; virtualAddress.s_addr = virtualIpv4;
        char physicalIp[INET_ADDRSTRLEN]{}, virtualIp[INET_ADDRSTRLEN]{};
        InetNtopA(AF_INET, &physicalAddress, physicalIp, sizeof(physicalIp));
        InetNtopA(AF_INET, &virtualAddress, virtualIp, sizeof(virtualIp));
        char detail[224]{};
        sprintf_s(detail, sizeof(detail), "GameRanger-style route table mapping physical=%s virtual=%s fields=%lu",
            physicalIp, virtualIp, rewritten);
        WriteNativeDiagnostic(detail);
        if (networkMappingSelfCheckCall) {
            selfCheckRouteFieldsRewritten += rewritten;
        } else if (routeTableMappingReported.exchange(2) != 2) {
            ReportMapAutomation(5, "Route table mapping active: matching physical-address routes were rewritten to the room IP.");
        }
    } else if (networkMappingSelfCheckCall) {
        selfCheckRouteFieldsRewritten = 0;
    } else if (routeTableMappingReported.exchange(1) == 0) {
        ReportMapAutomation(5, "Route table hook ran, but no route row matched the selected physical address.");
    }
    return result;
}
bool RunNetworkMappingSelfCheck() {
    struct SelfCheckScope {
        SelfCheckScope() {
            networkMappingSelfCheckCall = true;
            selfCheckRouteFieldsRewritten = 0;
            selfCheckAdapterApiCalls = 0;
            selfCheckRouteApiCalls = 0;
        }
        ~SelfCheckScope() { networkMappingSelfCheckCall = false; }
    } selfCheckScope;

    if (!globalAddressMappingDetoursInstalled.load()) {
        ReportMapAutomation(5, "Network mapping self-check failed: global IP Helper entry detours are not installed.");
        return false;
    }
    HMODULE iphlpapi = GetModuleHandleW(L"IPHLPAPI.dll");
    auto mappedGetAdaptersInfo = iphlpapi
        ? reinterpret_cast<GetAdaptersInfoFn>(GetProcAddress(iphlpapi, "GetAdaptersInfo")) : nullptr;
    auto mappedGetIpForwardTable = iphlpapi
        ? reinterpret_cast<GetIpForwardTableFn>(GetProcAddress(iphlpapi, "GetIpForwardTable")) : nullptr;
    if (!mappedGetAdaptersInfo || !mappedGetIpForwardTable) {
        ReportMapAutomation(5, "Network mapping self-check failed: hooked IP Helper exports could not be resolved.");
        return false;
    }

    ULONG adapterBytes = 0;
    const ULONG adapterSizeResult = mappedGetAdaptersInfo(nullptr, &adapterBytes);
    if (adapterSizeResult != ERROR_BUFFER_OVERFLOW || adapterBytes == 0 || adapterBytes > 1024 * 1024) {
        char detail[160]{};
        sprintf_s(detail, sizeof(detail), "Network mapping self-check failed: GetAdaptersInfo size result=%lu.",
            adapterSizeResult);
        ReportMapAutomation(5, detail);
        return false;
    }
    std::vector<uint8_t> adapters(adapterBytes);
    ULONG adapterCapacity = adapterBytes;
    const ULONG adapterResult = mappedGetAdaptersInfo(
        reinterpret_cast<PIP_ADAPTER_INFO>(adapters.data()), &adapterCapacity);
    if (adapterResult != NO_ERROR) {
        char detail[160]{};
        sprintf_s(detail, sizeof(detail), "Network mapping self-check failed: GetAdaptersInfo result=%lu.",
            adapterResult);
        ReportMapAutomation(5, detail);
        return false;
    }

    const uint16_t relayPort = ownRelayPort.load();
    const uint32_t expectedVirtualIpv4 = htonl(VirtualNetwork | static_cast<uint8_t>(relayPort - RelayPortBase + 1));
    bool virtualAddressFound = false;
    bool virtualMaskFound = false;
    for (auto* adapter = reinterpret_cast<PIP_ADAPTER_INFO>(adapters.data()); adapter; adapter = adapter->Next) {
        for (auto* address = &adapter->IpAddressList; address; address = address->Next) {
            IN_ADDR parsed{};
            if (InetPtonA(AF_INET, address->IpAddress.String, &parsed) == 1 &&
                parsed.s_addr == expectedVirtualIpv4) {
                virtualAddressFound = true;
                virtualMaskFound = strcmp(address->IpMask.String, "255.255.255.255") == 0;
                break;
            }
        }
        if (virtualAddressFound) break;
    }
    if (!virtualAddressFound) {
        ReportMapAutomation(5, "Network mapping self-check failed: the adapter list did not contain the expected room virtual IPv4.");
        return false;
    }
    if (!virtualMaskFound) {
        ReportMapAutomation(5, "Network mapping self-check failed: the room virtual IPv4 did not use the expected /32 adapter mask.");
        return false;
    }

    ULONG routeBytes = 0;
    const DWORD routeSizeResult = mappedGetIpForwardTable(nullptr, &routeBytes, FALSE);
    if (routeSizeResult != ERROR_INSUFFICIENT_BUFFER || routeBytes == 0 || routeBytes > 1024 * 1024) {
        char detail[160]{};
        sprintf_s(detail, sizeof(detail), "Network mapping self-check failed: GetIpForwardTable size result=%lu.",
            routeSizeResult);
        ReportMapAutomation(5, detail);
        return false;
    }

    std::vector<uint8_t> routes(routeBytes);
    ULONG routeCapacity = routeBytes;
    const DWORD routeResult = mappedGetIpForwardTable(reinterpret_cast<PMIB_IPFORWARDTABLE>(routes.data()),
        &routeCapacity, FALSE);
    if (routeResult != NO_ERROR) {
        char detail[160]{};
        sprintf_s(detail, sizeof(detail), "Network mapping self-check failed: GetIpForwardTable result=%lu.",
            routeResult);
        ReportMapAutomation(5, detail);
        return false;
    }
    if (selfCheckAdapterApiCalls < 2 || selfCheckRouteApiCalls < 2) {
        ReportMapAutomation(5, "Network mapping self-check failed: one or more Windows callers bypassed the installed IP Helper entry detour.");
        return false;
    }

    IN_ADDR physicalAddress{}; physicalAddress.s_addr = selectedPhysicalIpv4.load();
    IN_ADDR virtualAddress{}; virtualAddress.s_addr = expectedVirtualIpv4;
    char physicalIp[INET_ADDRSTRLEN]{}, virtualIp[INET_ADDRSTRLEN]{};
    InetNtopA(AF_INET, &physicalAddress, physicalIp, sizeof(physicalIp));
    InetNtopA(AF_INET, &virtualAddress, virtualIp, sizeof(virtualIp));
    ReportMapAutomation(5, std::string("Network mapping self-check passed: global IP Helper detours mapped adapter ") +
        physicalIp + " to " + virtualIp + ".");
    if (selfCheckRouteFieldsRewritten != 0) {
        char detail[160]{};
        sprintf_s(detail, sizeof(detail), "Route table self-check passed: %lu matching fields were rewritten.",
            selfCheckRouteFieldsRewritten);
        ReportMapAutomation(5, detail);
    } else {
        ReportMapAutomation(5, "Route table self-check completed; no route row matched the selected physical IPv4.");
    }
    return true;
}
bool IsBroadcastOrMulticast(const sockaddr_in& address) {
    const uint32_t ip = ntohl(address.sin_addr.s_addr);
    return ip == INADDR_BROADCAST || (ip >> 28) == 0xE || IsDirectedBroadcast(ip);
}
uint16_t RouteFor(const sockaddr_in& address) {
    const uint32_t ip = ntohl(address.sin_addr.s_addr);
    const uint8_t last = static_cast<uint8_t>(ip);
    if (ip == INADDR_BROADCAST || (ip >> 28) == 0xE || IsDirectedBroadcast(ip) ||
        ip == (VirtualNetwork | 0xFF)) return 0;
    if ((ip & 0xFFFFFF00u) == VirtualNetwork && last >= 1 && last <= 8)
        return static_cast<uint16_t>(RelayPortBase + last - 1);
    return UINT16_MAX;
}
bool LocalEndpoint(SOCKET socket, sockaddr_in& local) {
    int size = sizeof(local);
    ZeroMemory(&local, sizeof(local));
    local.sin_family = AF_INET;
    return ::getsockname(socket, reinterpret_cast<sockaddr*>(&local), &size) == 0 && local.sin_port != 0;
}
bool NotifyBoundSocket(SOCKET socket) {
    sockaddr_in local{};
    if (!LocalEndpoint(socket, local)) return false;
    std::vector<uint8_t> body(7);
    body[0] = 4; WriteU16(body.data() + 1, ntohs(local.sin_port));
    memcpy(body.data() + 3, &local.sin_addr.s_addr, sizeof(local.sin_addr.s_addr));
    return WritePipeFrame(body);
}
bool NotifyTcpListener(SOCKET socket) {
    sockaddr_in local{};
    if (!LocalEndpoint(socket, local)) return false;
    std::vector<uint8_t> body(7);
    body[0] = 8; WriteU16(body.data() + 1, ntohs(local.sin_port));
    memcpy(body.data() + 3, &local.sin_addr.s_addr, sizeof(local.sin_addr.s_addr));
    const bool written = WritePipeFrame(body);
    char detail[160]{};
    sprintf_s(detail, sizeof(detail), "EDMW TCP listener registration port=%u address=%08lX sent=%u",
        ntohs(local.sin_port), ntohl(local.sin_addr.s_addr), written ? 1u : 0u);
    WriteNativeDiagnostic(detail);
    return written;
}
int RelaySend(SOCKET socket, const char* bytes, int length, const sockaddr_in& target) {
    const uint16_t route = RouteFor(target);
    if (route == UINT16_MAX) { WSASetLastError(WSAEHOSTUNREACH); return SOCKET_ERROR; }
    if (length <= 0 || length > 1200 || target.sin_port == 0) { WSASetLastError(WSAEMSGSIZE); return SOCKET_ERROR; }
    sockaddr_in local{};
    if (!LocalEndpoint(socket, local)) {
        // The real UDP send/sendto APIs implicitly bind an unbound socket to an
        // ephemeral port. This hook replaces that send, so recreate that OS step.
        sockaddr_in automaticBind{};
        automaticBind.sin_family = AF_INET;
        automaticBind.sin_addr.s_addr = htonl(INADDR_ANY);
        if (realBind(socket, reinterpret_cast<const sockaddr*>(&automaticBind), sizeof(automaticBind)) == SOCKET_ERROR) return SOCKET_ERROR;
        if (!LocalEndpoint(socket, local)) { WSASetLastError(WSAEINVAL); return SOCKET_ERROR; }
        if (!NotifyBoundSocket(socket)) { WSASetLastError(WSAENOBUFS); return SOCKET_ERROR; }
    }
    std::vector<uint8_t> body(9 + static_cast<size_t>(length));
    body[0] = 1;
    WriteU16(body.data() + 1, route);
    WriteU16(body.data() + 3, ntohs(local.sin_port));
    WriteU16(body.data() + 5, ntohs(target.sin_port));
    WriteU16(body.data() + 7, static_cast<uint16_t>(length));
    memcpy(body.data() + 9, bytes, static_cast<size_t>(length));
    const bool queued = WritePipeFrame(body);
    static std::atomic<uint32_t> tracedUdpFrames{ 0 };
    const uint32_t trace = tracedUdpFrames.fetch_add(1);
    if (trace < 16) {
        char detail[160]{};
        sprintf_s(detail, sizeof(detail), "native UDP frame queued=%u route=%u source=%u target=%u bytes=%d",
            queued ? 1u : 0u, ReadU16(body.data() + 1), ReadU16(body.data() + 3),
            ReadU16(body.data() + 5), length);
        WriteNativeDiagnostic(detail);
    }
    if (!queued) { WSASetLastError(WSAENETUNREACH); return SOCKET_ERROR; }
    return length;
}
uint16_t RouteFromVirtualAddress(const sockaddr_in& address) {
    const uint32_t ip = ntohl(address.sin_addr.s_addr);
    const uint8_t last = static_cast<uint8_t>(ip);
    return (ip & 0xFFFFFF00u) == VirtualNetwork && last >= 1 && last <= 8
        ? static_cast<uint16_t>(RelayPortBase + last - 1) : 0;
}
int Unwrap(const char* frame, int frameLength, char* output, int outputLength, sockaddr* from, int* fromLength,
    uint16_t expectedRoute, bool filterRoute) {
    if (frameLength >= static_cast<int>(TunnelHeaderSize) && memcmp(frame, "EDMWRLY1", 8) == 0) {
        const auto* data = reinterpret_cast<const uint8_t*>(frame);
        const uint16_t sourceRoute = ReadU16(data + 8), sourcePort = ReadU16(data + 10), payloadLength = ReadU16(data + 12);
        if (sourceRoute < RelayPortBase || sourceRoute > RelayPortBase + 7 || sourcePort == 0 ||
            payloadLength != frameLength - TunnelHeaderSize || (filterRoute && sourceRoute != expectedRoute))
            return -2;
        if (payloadLength > outputLength) { WSASetLastError(WSAEMSGSIZE); return SOCKET_ERROR; }
        memcpy(output, frame + TunnelHeaderSize, payloadLength);
        if (from && fromLength && *fromLength >= static_cast<int>(sizeof(sockaddr_in))) {
            sockaddr_in virtualAddress{};
            virtualAddress.sin_family = AF_INET;
            virtualAddress.sin_addr.s_addr = htonl(VirtualNetwork | (sourceRoute - RelayPortBase + 1));
            virtualAddress.sin_port = htons(sourcePort);
            memcpy(from, &virtualAddress, sizeof(virtualAddress));
            *fromLength = sizeof(virtualAddress);
        }
        return payloadLength;
    }
    if (frameLength > outputLength) { WSASetLastError(WSAEMSGSIZE); return SOCKET_ERROR; }
    if (frameLength > 0) memcpy(output, frame, static_cast<size_t>(frameLength));
    return frameLength;
}
bool TryDecodeProviderDatagram(const char* bytes, int length, char* decoded, size_t decodedCapacity) {
    // TCP Protocol.dll exports no public decrypt API. This address is its
    // verified packet transform routine in the installed EDMW build. Decode a
    // copy after recvfrom has unwrapped the ICE envelope, leaving the game's
    // original packet untouched for NPIMReceiveFrom.
    constexpr uintptr_t ProviderDatagramTransformRva = 0x247E;
    if (!bytes || !decoded || length < 2 || static_cast<size_t>(length) > decodedCapacity) return false;
    HMODULE provider = GetModuleHandleW(L"TCP Protocol.dll");
    if (!provider) return false;
    memcpy(decoded, bytes, static_cast<size_t>(length));
    using ProviderDatagramTransformFn = void(__cdecl*)(char*, int, int);
    auto transform = reinterpret_cast<ProviderDatagramTransformFn>(
        reinterpret_cast<uint8_t*>(provider) + ProviderDatagramTransformRva);
    __try {
        transform(decoded + 1, length - 1, static_cast<unsigned char>(decoded[0]));
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

SOCKET WSAAPI HookSocket(int af, int type, int protocol) {
    const SOCKET socket = realSocket(af, type, protocol);
    return socket;
}
int WSAAPI HookBind(SOCKET socket, const sockaddr* address, int length) {
    sockaddr_in localVirtualAddress{};
    const sockaddr* bindAddress = address;
    if (address && address->sa_family == AF_INET && length >= static_cast<int>(sizeof(sockaddr_in))) {
        memcpy(&localVirtualAddress, address, sizeof(localVirtualAddress));
        const uint16_t relayPort = ownRelayPort.load();
        if (relayPort >= RelayPortBase && relayPort <= RelayPortBase + 7 &&
            ntohl(localVirtualAddress.sin_addr.s_addr) ==
                (VirtualNetwork | static_cast<uint8_t>(relayPort - RelayPortBase + 1))) {
            localVirtualAddress.sin_addr.s_addr = htonl(INADDR_ANY);
            bindAddress = reinterpret_cast<const sockaddr*>(&localVirtualAddress);
            WriteNativeDiagnostic("TCP/IP provider bind to the room virtual IP mapped to the local relay socket");
        }
    }
    const int result = realBind(socket, bindAddress, length);
    if (result == 0 && IsDatagram(socket) && !NotifyBoundSocket(socket)) WSASetLastError(WSAENOBUFS);
    return result;
}
int WSAAPI HookListen(SOCKET socket, int backlog) {
    const int result = realListen(socket, backlog);
    if (result == 0 && IsSocketType(socket, SOCK_STREAM) && !NotifyTcpListener(socket)) WSASetLastError(WSAENOBUFS);
    return result;
}
SOCKET WSAAPI HookAccept(SOCKET socket, sockaddr* address, int* addressLength) {
    const SOCKET accepted = realAccept(socket, address, addressLength);
    if (accepted == INVALID_SOCKET || !address || !addressLength || *addressLength < static_cast<int>(sizeof(sockaddr_in)) ||
        address->sa_family != AF_INET) return accepted;
    auto* remote = reinterpret_cast<sockaddr_in*>(address);
    const uint16_t localProxyPort = ntohs(remote->sin_port);
    sockaddr_in local{};
    if (!LocalEndpoint(socket, local)) return accepted;
    std::lock_guard<std::mutex> lock(stateLock);
    const auto mapping = tcpPeerAddresses.find(TcpPeerKey(ntohs(local.sin_port), localProxyPort));
    if (mapping != tcpPeerAddresses.end()) {
        *remote = mapping->second;
        tcpPeerAddresses.erase(mapping);
    }
    return accepted;
}
int WSAAPI HookConnect(SOCKET socket, const sockaddr* address, int length) {
    if (address && address->sa_family == AF_INET && length >= static_cast<int>(sizeof(sockaddr_in))) {
        sockaddr_in target{}; memcpy(&target, address, sizeof(target));
        int socketType = -1, socketTypeLength = sizeof(socketType);
        const int socketTypeResult = ::getsockopt(socket, SOL_SOCKET, SO_TYPE,
            reinterpret_cast<char*>(&socketType), &socketTypeLength);
        char ipText[INET_ADDRSTRLEN]{};
        InetNtopA(AF_INET, &target.sin_addr, ipText, sizeof(ipText));
        char detail[256]{};
        sprintf_s(detail, sizeof(detail), "Winsock connect entry socket=%llu type=%d typeQuery=%d target=%s:%u tcpProxy=%u",
            static_cast<unsigned long long>(socket), socketType, socketTypeResult, ipText,
            ntohs(target.sin_port), tcpProxyPort.load());
        WriteNativeDiagnostic(detail);
    }
    if (address && address->sa_family == AF_INET && length >= static_cast<int>(sizeof(sockaddr_in)) && IsDatagram(socket)) {
        sockaddr_in peer{}; memcpy(&peer, address, sizeof(peer));
        if (RouteFor(peer) != UINT16_MAX) {
            std::lock_guard<std::mutex> lock(stateLock); virtualPeers[socket] = peer;
            return 0;
        }
        { std::lock_guard<std::mutex> lock(stateLock); virtualPeers.erase(socket); }
        return realConnect(socket, address, length);
    }
    if (address && address->sa_family == AF_INET && length >= static_cast<int>(sizeof(sockaddr_in)) &&
        IsSocketType(socket, SOCK_STREAM) && tcpProxyPort.load() != 0) {
        sockaddr_in target{}; memcpy(&target, address, sizeof(target));
        const uint16_t relayPort = RouteFor(target);
        char ipText[INET_ADDRSTRLEN]{};
        InetNtopA(AF_INET, const_cast<IN_ADDR*>(&target.sin_addr), ipText, sizeof(ipText));
        char detail[256]{};
        sprintf_s(detail, sizeof(detail), "EDMW TCP connect intercepted target=%s:%u relay=%u proxy=%u socket=%llu",
            ipText, ntohs(target.sin_port), relayPort, tcpProxyPort.load(), static_cast<unsigned long long>(socket));
        WriteNativeDiagnostic(detail);
        if (relayPort == 0 || relayPort == UINT16_MAX) {
            WSASetLastError(WSAEHOSTUNREACH);
            WriteNativeDiagnostic("EDMW TCP connect rejected because the target has no matchmaking route");
            return SOCKET_ERROR;
        }
        sockaddr_in proxy{};
        proxy.sin_family = AF_INET;
        proxy.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        proxy.sin_port = htons(tcpProxyPort.load());
        const int result = realConnect(socket, reinterpret_cast<const sockaddr*>(&proxy), sizeof(proxy));
        if (result == SOCKET_ERROR) {
            const int error = WSAGetLastError();
            sprintf_s(detail, sizeof(detail), "EDMW TCP proxy connect failed result=%d WSA=%d", result, error);
            WriteNativeDiagnostic(detail);
            WSASetLastError(error);
            return result;
        }
        sockaddr_in local{};
        if (!LocalEndpoint(socket, local)) { WSASetLastError(WSAEINVAL); return SOCKET_ERROR; }
        uint8_t preamble[12]{};
        memcpy(preamble, "EDWTCP", 6);
        WriteU16(preamble + 6, relayPort);
        WriteU16(preamble + 8, ntohs(local.sin_port));
        WriteU16(preamble + 10, ntohs(target.sin_port));
        int sent = 0;
        while (sent < static_cast<int>(sizeof(preamble))) {
            const int count = realSend(socket, reinterpret_cast<const char*>(preamble + sent),
                static_cast<int>(sizeof(preamble)) - sent, 0);
            if (count == SOCKET_ERROR || count == 0) {
                const int error = WSAGetLastError();
                sprintf_s(detail, sizeof(detail), "EDMW TCP proxy preamble send failed after %d bytes; WSA=%d", sent, error);
                WriteNativeDiagnostic(detail);
                WSASetLastError(error);
                return SOCKET_ERROR;
            }
            sent += count;
        }
        WriteNativeDiagnostic("EDMW TCP proxy connection established and its route preamble was sent");
        return 0;
    }
    return realConnect(socket, address, length);
}
int WSAAPI HookClose(SOCKET socket) {
    { std::lock_guard<std::mutex> lock(stateLock); virtualPeers.erase(socket); }
    return realClose(socket);
}
int WSAAPI HookSendTo(SOCKET socket, const char* bytes, int length, int flags, const sockaddr* address, int addressLength) {
    if (address && address->sa_family == AF_INET && addressLength >= static_cast<int>(sizeof(sockaddr_in)) && IsDatagram(socket)) {
        sockaddr_in target{}; memcpy(&target, address, sizeof(target));
        if (RouteFor(target) == UINT16_MAX)
            return realSendTo(socket, bytes, length, flags, address, addressLength);
        static std::atomic<uint32_t> udpSendTrace{ 0 };
        const uint32_t trace = udpSendTrace.fetch_add(1);
        if (trace < 16) {
            char ipText[INET_ADDRSTRLEN]{};
            InetNtopA(AF_INET, &target.sin_addr, ipText, sizeof(ipText));
            char head[49]{};
            for (int index = 0; index < (std::min)(length, 24); ++index)
                sprintf_s(head + index * 2, sizeof(head) - index * 2, "%02X", static_cast<unsigned char>(bytes[index]));
            char detail[256]{};
            sprintf_s(detail, sizeof(detail), "EDMW UDP sendto target=%s:%u bytes=%d route=%u head=%s",
                ipText, ntohs(target.sin_port), length, RouteFor(target), head);
            WriteNativeDiagnostic(detail);
        }
        return RelaySend(socket, bytes, length, target);
    }
    return realSendTo(socket, bytes, length, flags, address, addressLength);
}
int WSAAPI HookSend(SOCKET socket, const char* bytes, int length, int flags) {
    if (IsDatagram(socket)) {
        sockaddr_in target{};
        { std::lock_guard<std::mutex> lock(stateLock); const auto found = virtualPeers.find(socket); if (found != virtualPeers.end()) target = found->second; }
        if (target.sin_family == AF_INET) {
            if (RouteFor(target) == UINT16_MAX)
                return realSend(socket, bytes, length, flags);
            static std::atomic<uint32_t> udpConnectedSendTrace{ 0 };
            const uint32_t trace = udpConnectedSendTrace.fetch_add(1);
            if (trace < 16) {
                char ipText[INET_ADDRSTRLEN]{};
                InetNtopA(AF_INET, &target.sin_addr, ipText, sizeof(ipText));
                char head[49]{};
                for (int index = 0; index < (std::min)(length, 24); ++index)
                    sprintf_s(head + index * 2, sizeof(head) - index * 2, "%02X", static_cast<unsigned char>(bytes[index]));
                char detail[256]{};
                sprintf_s(detail, sizeof(detail), "EDMW UDP send target=%s:%u bytes=%d route=%u head=%s",
                    ipText, ntohs(target.sin_port), length, RouteFor(target), head);
                WriteNativeDiagnostic(detail);
            }
            return RelaySend(socket, bytes, length, target);
        }
    }
    return realSend(socket, bytes, length, flags);
}
int WSAAPI HookRecvFrom(SOCKET socket, char* bytes, int length, int flags, sockaddr* from, int* fromLength) {
    if (!IsDatagram(socket) || length < 0) return realRecvFrom(socket, bytes, length, flags, from, fromLength);
    std::vector<char> frame(static_cast<size_t>(length) + 2048);
    for (;;) {
        sockaddr_in source{}; int sourceLength = sizeof(source);
        const int received = realRecvFrom(socket, frame.data(), static_cast<int>(frame.size()), flags,
            reinterpret_cast<sockaddr*>(&source), &sourceLength);
        if (received == SOCKET_ERROR) return received;
        const bool tunnelFrame = received >= static_cast<int>(TunnelHeaderSize) && memcmp(frame.data(), "EDMWRLY1", 8) == 0;
        const int unwrapped = Unwrap(frame.data(), received, bytes, length, from, fromLength, 0, false);
        if (unwrapped == -2) continue;
        static std::atomic<uint32_t> udpReceiveTrace{ 0 };
        const uint32_t trace = udpReceiveTrace.fetch_add(1);
        if (trace < 24) {
            sockaddr_in local{};
            const bool hasLocal = LocalEndpoint(socket, local);
            char sourceText[INET_ADDRSTRLEN]{};
            InetNtopA(AF_INET, &source.sin_addr, sourceText, sizeof(sourceText));
            char senderText[INET_ADDRSTRLEN] = "?";
            uint16_t senderPort = 0;
            if (from && fromLength && *fromLength >= static_cast<int>(sizeof(sockaddr_in)) &&
                from->sa_family == AF_INET) {
                const auto* sender = reinterpret_cast<const sockaddr_in*>(from);
                InetNtopA(AF_INET, const_cast<IN_ADDR*>(&sender->sin_addr), senderText, sizeof(senderText));
                senderPort = ntohs(sender->sin_port);
            }
            char head[49]{};
            for (int index = 0; index < (std::min)(unwrapped, 24); ++index)
                sprintf_s(head + index * 2, sizeof(head) - index * 2, "%02X", static_cast<unsigned char>(bytes[index]));
            char detail[256]{};
            sprintf_s(detail, sizeof(detail), "EDMW UDP recvfrom socket=%llu gamePort=%u injector=%s:%u tunnel=%u result=%d sender=%s:%u head=%s",
                static_cast<unsigned long long>(socket), hasLocal ? ntohs(local.sin_port) : 0, sourceText, ntohs(source.sin_port),
                tunnelFrame ? 1u : 0u, unwrapped, senderText, senderPort, head);
            WriteNativeDiagnostic(detail);
            wchar_t decodeTrace[8]{};
            wchar_t decodePackets[8]{};
            const bool decodeTraceEnabled = GetEnvironmentVariableW(
                L"EDMW_RELAY_TRACE_PROVIDER_PACKETS", decodeTrace, _countof(decodeTrace)) != 0 &&
                wcscmp(decodeTrace, L"1") == 0;
            const bool decodePacketsEnabled = GetEnvironmentVariableW(
                L"EDMW_RELAY_DECODE_PROVIDER_PACKETS", decodePackets, _countof(decodePackets)) != 0 &&
                wcscmp(decodePackets, L"1") == 0;
            if ((decodePacketsEnabled || decodeTraceEnabled) && tunnelFrame && unwrapped > 1) {
                char decoded[1200]{};
                if (TryDecodeProviderDatagram(bytes, unwrapped, decoded, sizeof(decoded))) {
                    if (decodeTraceEnabled) {
                        char decodedHead[49]{};
                        for (int index = 0; index < (std::min)(unwrapped, 24); ++index)
                            sprintf_s(decodedHead + index * 2, sizeof(decodedHead) - index * 2, "%02X",
                                static_cast<unsigned char>(decoded[index]));
                        char decodedDetail[192]{};
                        sprintf_s(decodedDetail, sizeof(decodedDetail),
                            "EDMW UDP packet decoded gamePort=%u bytes=%d opcode=%02X head=%s",
                            hasLocal ? ntohs(local.sin_port) : 0, unwrapped,
                            static_cast<unsigned char>(decoded[1]), decodedHead);
                        WriteNativeDiagnostic(decodedDetail);
                    }
                } else if (decodeTraceEnabled) {
                    WriteNativeDiagnostic("EDMW UDP diagnostic could not decode tunneled packet with the installed TCP Protocol.dll transform");
                }
            }
        }
        if (!tunnelFrame && from && fromLength && *fromLength >= sourceLength) {
            memcpy(from, &source, sourceLength);
            *fromLength = sourceLength;
        }
        wchar_t delayProviderPacket[8]{};
        if (tunnelFrame && GetEnvironmentVariableW(L"EDMW_RELAY_DELAY_PROVIDER_PACKETS",
                delayProviderPacket, _countof(delayProviderPacket)) != 0 &&
            wcscmp(delayProviderPacket, L"1") == 0)
            Sleep(1);
        return unwrapped;
    }
}
int WSAAPI HookRecv(SOCKET socket, char* bytes, int length, int flags) {
    if (!IsDatagram(socket) || length < 0) return realRecv(socket, bytes, length, flags);
    sockaddr_in peer{}; bool connected = false;
    { std::lock_guard<std::mutex> lock(stateLock); const auto found = virtualPeers.find(socket); if (found != virtualPeers.end()) { peer = found->second; connected = true; } }
    if (!connected) return realRecv(socket, bytes, length, flags);
    std::vector<char> frame(static_cast<size_t>(length) + 2048);
    for (;;) {
        sockaddr_in source{}; int sourceLength = sizeof(source);
        const int received = realRecvFrom(socket, frame.data(), static_cast<int>(frame.size()), flags,
            reinterpret_cast<sockaddr*>(&source), &sourceLength);
        if (received == SOCKET_ERROR) return received;
        const uint16_t expectedRoute = RouteFromVirtualAddress(peer);
        const int unwrapped = Unwrap(frame.data(), received, bytes, length, nullptr, nullptr, expectedRoute, expectedRoute != 0);
        if (unwrapped != -2) return unwrapped;
    }
}

struct ImportHook {
    const char* name;
    WORD ordinal;
    FARPROC hook;
    uint16_t bit;
};
const ImportHook ProviderHooks[] = {
    { "socket", 23, reinterpret_cast<FARPROC>(HookSocket), 0x0200 },
    { "bind", 2, reinterpret_cast<FARPROC>(HookBind), 0x0001 },
    { "listen", 13, reinterpret_cast<FARPROC>(HookListen), 0x0080 },
    { "accept", 1, reinterpret_cast<FARPROC>(HookAccept), 0x0100 },
    { "connect", 4, reinterpret_cast<FARPROC>(HookConnect), 0x0002 },
    { "closesocket", 3, reinterpret_cast<FARPROC>(HookClose), 0x0004 },
    { "send", 19, reinterpret_cast<FARPROC>(HookSend), 0x0008 },
    { "sendto", 20, reinterpret_cast<FARPROC>(HookSendTo), 0x0010 },
    { "recv", 16, reinterpret_cast<FARPROC>(HookRecv), 0x0020 },
    { "recvfrom", 17, reinterpret_cast<FARPROC>(HookRecvFrom), 0x0040 }
};
const ImportHook AdapterHooks[] = {
    { "GetAdaptersInfo", 14, reinterpret_cast<FARPROC>(HookGetAdaptersInfo), 0x0001 },
    { "GetIpForwardTable", 0, reinterpret_cast<FARPROC>(HookGetIpForwardTable), 0x0002 }
};
bool ImageRange(uint32_t imageSize, uint32_t rva, size_t length) {
    return rva <= imageSize && length <= static_cast<size_t>(imageSize - rva);
}
const ImportHook* FindImportHook(const ImportHook* hooks, size_t hookCount, const char* name,
    WORD ordinal, bool byOrdinal, bool lookupNamesAvailable, DWORD resolvedAddress, HMODULE importedModule) {
    for (size_t index = 0; index < hookCount; ++index) {
        const auto& candidate = hooks[index];
        if (lookupNamesAvailable) {
            if ((byOrdinal && candidate.ordinal == ordinal) ||
                (!byOrdinal && name && _stricmp(candidate.name, name) == 0)) return &candidate;
            continue;
        }
        // OriginalFirstThunk can be absent in a valid PE image. In that case
        // FirstThunk contains resolved function pointers, not import-name RVAs.
        // Match those pointers against the loaded module's exports instead of
        // interpreting them as RVAs and crashing the patch worker.
        FARPROC original = GetProcAddress(importedModule, candidate.name);
        if (original && reinterpret_cast<DWORD>(original) == resolvedAddress) return &candidate;
        if (reinterpret_cast<DWORD>(candidate.hook) == resolvedAddress) return &candidate;
        original = GetProcAddress(importedModule, MAKEINTRESOURCEA(candidate.ordinal));
        if (original && reinterpret_cast<DWORD>(original) == resolvedAddress) return &candidate;
    }
    return nullptr;
}
bool PatchThunk(const ImportHook& imported, IMAGE_THUNK_DATA32* address, uint16_t& patched) {
    const DWORD replacement = reinterpret_cast<DWORD>(imported.hook);
    const DWORD previous = address->u1.Function;
    if (address->u1.Function != replacement) {
        DWORD prior = 0;
        if (!VirtualProtect(&address->u1.Function, sizeof(address->u1.Function), PAGE_READWRITE, &prior)) return false;
        address->u1.Function = replacement;
        DWORD ignored = 0;
        const bool restored = VirtualProtect(&address->u1.Function, sizeof(address->u1.Function), prior, &ignored) != FALSE;
        if (!restored) return false;
    }
    patched |= imported.bit;
    if (_stricmp(imported.name, "GetIpForwardTable") == 0) routeTableHookInstalled.store(true);
    if (_stricmp(imported.name, "connect") == 0) {
        char detail[192]{};
        sprintf_s(detail, sizeof(detail), "TCP Protocol.dll connect IAT slot=%p previous=%08lX hook=%08lX",
            address, previous, replacement);
        WriteNativeDiagnostic(detail);
    }
    return true;
}
bool PatchProvider(HMODULE provider) {
    auto* base = reinterpret_cast<uint8_t*>(provider);
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0) return false;
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS32*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR32_MAGIC) return false;
    const uint32_t imageSize = nt->OptionalHeader.SizeOfImage;
    const auto directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!directory.VirtualAddress || directory.Size < sizeof(IMAGE_IMPORT_DESCRIPTOR) ||
        !ImageRange(imageSize, directory.VirtualAddress, directory.Size)) return false;
    HMODULE winsock = GetModuleHandleW(L"WS2_32.dll");
    HMODULE iphlpapi = GetModuleHandleW(L"IPHLPAPI.dll");
    if (!winsock || !iphlpapi) return false;
    if (!realGetAdaptersInfo)
        realGetAdaptersInfo = reinterpret_cast<GetAdaptersInfoFn>(GetProcAddress(iphlpapi, "GetAdaptersInfo"));
    if (!realGetIpForwardTable)
        realGetIpForwardTable = reinterpret_cast<GetIpForwardTableFn>(GetProcAddress(iphlpapi, "GetIpForwardTable"));
    if (!realGetAdaptersInfo || !realGetIpForwardTable) return false;
    auto* imports = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + directory.VirtualAddress);
    uint16_t patched = 0;
    bool adapterInfoPatched = false;
    const size_t descriptorCount = directory.Size / sizeof(IMAGE_IMPORT_DESCRIPTOR);
    for (size_t descriptorIndex = 0; descriptorIndex < descriptorCount && imports[descriptorIndex].Name; ++descriptorIndex) {
        auto& descriptor = imports[descriptorIndex];
        if (!ImageRange(imageSize, descriptor.Name, 1)) return false;
        const char* dll = reinterpret_cast<const char*>(base + descriptor.Name);
        if (!memchr(dll, '\0', imageSize - descriptor.Name)) return false;
        const ImportHook* hooks = nullptr;
        size_t hookCount = 0;
        HMODULE importedModule = nullptr;
        uint16_t* patchedBits = nullptr;
        if (_stricmp(dll, "WS2_32.dll") == 0) {
            hooks = ProviderHooks;
            hookCount = _countof(ProviderHooks);
            importedModule = winsock;
            patchedBits = &patched;
        } else if (_stricmp(dll, "IPHLPAPI.dll") == 0) {
            hooks = AdapterHooks;
            hookCount = _countof(AdapterHooks);
            importedModule = iphlpapi;
        } else {
            continue;
        }
        if (!ImageRange(imageSize, descriptor.FirstThunk, sizeof(IMAGE_THUNK_DATA32))) return false;
        const bool lookupNamesAvailable = descriptor.OriginalFirstThunk != 0;
        const uint32_t lookupRva = lookupNamesAvailable ? descriptor.OriginalFirstThunk : descriptor.FirstThunk;
        if (!ImageRange(imageSize, lookupRva, sizeof(IMAGE_THUNK_DATA32))) return false;
        auto* lookup = reinterpret_cast<IMAGE_THUNK_DATA32*>(base + lookupRva);
        auto* address = reinterpret_cast<IMAGE_THUNK_DATA32*>(base + descriptor.FirstThunk);
        const size_t lookupCount = (imageSize - lookupRva) / sizeof(IMAGE_THUNK_DATA32);
        const size_t addressCount = (imageSize - descriptor.FirstThunk) / sizeof(IMAGE_THUNK_DATA32);
        const size_t thunkCount = (std::min)(lookupCount, addressCount);
        bool terminated = false;
        for (size_t thunkIndex = 0; thunkIndex < thunkCount; ++thunkIndex) {
            const DWORD lookupValue = lookup[thunkIndex].u1.AddressOfData;
            if (lookupValue == 0) { terminated = true; break; }
            bool byOrdinal = false;
            WORD ordinal = 0;
            const char* name = nullptr;
            if (lookupNamesAvailable) {
                byOrdinal = IMAGE_SNAP_BY_ORDINAL32(lookup[thunkIndex].u1.Ordinal) != FALSE;
                ordinal = byOrdinal ? IMAGE_ORDINAL32(lookup[thunkIndex].u1.Ordinal) : 0;
                if (!byOrdinal) {
                    const uint32_t nameRva = lookup[thunkIndex].u1.AddressOfData;
                    if (!ImageRange(imageSize, nameRva, sizeof(WORD) + 1)) return false;
                    name = reinterpret_cast<const char*>(base + nameRva + sizeof(WORD));
                    if (!memchr(name, '\0', imageSize - nameRva - sizeof(WORD))) return false;
                }
            }
            const auto* imported = FindImportHook(hooks, hookCount, name, ordinal, byOrdinal,
                lookupNamesAvailable, address[thunkIndex].u1.Function, importedModule);
            if (imported) {
                uint16_t scratch = 0;
                if (!PatchThunk(*imported, &address[thunkIndex], patchedBits ? *patchedBits : scratch)) return false;
                if (_stricmp(imported->name, "GetAdaptersInfo") == 0) {
                    adapterInfoPatched = true;
                    WriteNativeDiagnostic("GameRanger-style GetAdaptersInfo interception installed for TCP Protocol.dll");
                }
            }
        }
        if (!terminated) return false;
    }
    constexpr uint16_t required = 0x187;
    const bool complete = adapterInfoPatched && (patched & required) == required &&
        (patched & 0x18) != 0 && (patched & 0x60) != 0;
    if (!complete) {
        char detail[224]{};
        sprintf_s(detail, sizeof(detail),
            "TCP Protocol.dll hook patch incomplete: GetAdaptersInfo=%u Winsock bits=%04X required=%04X send=%u recv=%u",
            adapterInfoPatched ? 1u : 0u, patched, required, (patched & 0x18) != 0 ? 1u : 0u,
            (patched & 0x60) != 0 ? 1u : 0u);
        WriteNativeDiagnostic(detail);
    }
    return complete;
}
bool PatchAddressMappingImports(HMODULE module) {
    if (!module) return false;
    auto* base = reinterpret_cast<uint8_t*>(module);
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0) return false;
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS32*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR32_MAGIC) return false;
    const uint32_t imageSize = nt->OptionalHeader.SizeOfImage;
    const auto directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!directory.VirtualAddress || directory.Size < sizeof(IMAGE_IMPORT_DESCRIPTOR) ||
        !ImageRange(imageSize, directory.VirtualAddress, directory.Size)) return true;
    HMODULE iphlpapi = GetModuleHandleW(L"IPHLPAPI.dll");
    if (!iphlpapi) return false;
    auto* imports = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + directory.VirtualAddress);
    const size_t descriptorCount = directory.Size / sizeof(IMAGE_IMPORT_DESCRIPTOR);
    for (size_t descriptorIndex = 0; descriptorIndex < descriptorCount && imports[descriptorIndex].Name; ++descriptorIndex) {
        auto& descriptor = imports[descriptorIndex];
        if (!ImageRange(imageSize, descriptor.Name, 1)) return false;
        const char* dll = reinterpret_cast<const char*>(base + descriptor.Name);
        if (!memchr(dll, '\0', imageSize - descriptor.Name)) return false;
        if (_stricmp(dll, "IPHLPAPI.dll") != 0) continue;
        if (!ImageRange(imageSize, descriptor.FirstThunk, sizeof(IMAGE_THUNK_DATA32))) return false;
        const bool lookupNamesAvailable = descriptor.OriginalFirstThunk != 0;
        const uint32_t lookupRva = lookupNamesAvailable ? descriptor.OriginalFirstThunk : descriptor.FirstThunk;
        if (!ImageRange(imageSize, lookupRva, sizeof(IMAGE_THUNK_DATA32))) return false;
        auto* lookup = reinterpret_cast<IMAGE_THUNK_DATA32*>(base + lookupRva);
        auto* address = reinterpret_cast<IMAGE_THUNK_DATA32*>(base + descriptor.FirstThunk);
        const size_t lookupCount = (imageSize - lookupRva) / sizeof(IMAGE_THUNK_DATA32);
        const size_t addressCount = (imageSize - descriptor.FirstThunk) / sizeof(IMAGE_THUNK_DATA32);
        const size_t thunkCount = (std::min)(lookupCount, addressCount);
        bool terminated = false;
        for (size_t thunkIndex = 0; thunkIndex < thunkCount; ++thunkIndex) {
            const DWORD lookupValue = lookup[thunkIndex].u1.AddressOfData;
            if (lookupValue == 0) { terminated = true; break; }
            bool byOrdinal = false;
            WORD ordinal = 0;
            const char* name = nullptr;
            if (lookupNamesAvailable) {
                byOrdinal = IMAGE_SNAP_BY_ORDINAL32(lookup[thunkIndex].u1.Ordinal) != FALSE;
                ordinal = byOrdinal ? IMAGE_ORDINAL32(lookup[thunkIndex].u1.Ordinal) : 0;
                if (!byOrdinal) {
                    const uint32_t nameRva = lookup[thunkIndex].u1.AddressOfData;
                    if (!ImageRange(imageSize, nameRva, sizeof(WORD) + 1)) return false;
                    name = reinterpret_cast<const char*>(base + nameRva + sizeof(WORD));
                    if (!memchr(name, '\0', imageSize - nameRva - sizeof(WORD))) return false;
                }
            }
            const auto* imported = FindImportHook(AdapterHooks, _countof(AdapterHooks), name, ordinal,
                byOrdinal, lookupNamesAvailable, address[thunkIndex].u1.Function, iphlpapi);
            if (imported) {
                const bool wasInstalled = address[thunkIndex].u1.Function == reinterpret_cast<DWORD>(imported->hook);
                uint16_t ignored = 0;
                if (!PatchThunk(*imported, &address[thunkIndex], ignored)) return false;
                if (!wasInstalled) {
                    char detail[192]{};
                    sprintf_s(detail, sizeof(detail), "GameRanger-style %s interception installed for %s",
                        imported->name, module == GetModuleHandleW(nullptr) ? "Empires_DMW.exe" : "game module");
                    WriteNativeDiagnostic(detail);
                }
            }
        }
        if (!terminated) return false;
    }
    return true;
}
void PatchLoadedAddressMappingImports() {
    if (!realGetAdaptersInfo || !realGetIpForwardTable) return;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());
    if (snapshot == INVALID_HANDLE_VALUE) return;
    MODULEENTRY32W module{};
    module.dwSize = sizeof(module);
    if (Module32FirstW(snapshot, &module)) {
        do { PatchAddressMappingImports(module.hModule); } while (Module32NextW(snapshot, &module));
    }
    CloseHandle(snapshot);
}
class SuspendedProcessThreads {
    std::vector<HANDLE> threads;
public:
    bool SuspendOutsideEntryPoint(void* target, size_t patchLength) {
        HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snapshot == INVALID_HANDLE_VALUE) return false;
        const DWORD processId = GetCurrentProcessId();
        const DWORD currentThreadId = GetCurrentThreadId();
        const uint32_t targetStart = static_cast<uint32_t>(reinterpret_cast<uintptr_t>(target));
        THREADENTRY32 entry{};
        entry.dwSize = sizeof(entry);
        bool safe = true;
        if (Thread32First(snapshot, &entry)) {
            do {
                if (entry.th32OwnerProcessID != processId || entry.th32ThreadID == currentThreadId) continue;
                HANDLE thread = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION,
                    FALSE, entry.th32ThreadID);
                if (!thread) continue;
                if (SuspendThread(thread) == static_cast<DWORD>(-1)) {
                    CloseHandle(thread);
                    continue;
                }
                threads.push_back(thread);
                CONTEXT context{};
                context.ContextFlags = CONTEXT_CONTROL;
                if (!GetThreadContext(thread, &context)) { safe = false; break; }
                const uint32_t instructionPointer = context.Eip;
                if (instructionPointer >= targetStart && instructionPointer - targetStart < patchLength) {
                    safe = false;
                    break;
                }
            } while (Thread32Next(snapshot, &entry));
        }
        CloseHandle(snapshot);
        return safe;
    }

    ~SuspendedProcessThreads() {
        for (HANDLE thread : threads) {
            ResumeThread(thread);
            CloseHandle(thread);
        }
    }
};
bool InstallX86EntryDetour(const char* name, void* target, void* hook, void** original) {
    static_assert(sizeof(void*) == 4, "The network API detours only support the x86 game process.");
    constexpr size_t PatchLength = 5;
    constexpr uint8_t ExpectedPrologue[PatchLength]{ 0x8B, 0xFF, 0x55, 0x8B, 0xEC };
    auto* entry = static_cast<uint8_t*>(target);
    if (!entry || !hook || !original || memcmp(entry, ExpectedPrologue, PatchLength) != 0) {
        char detail[192]{};
        sprintf_s(detail, sizeof(detail),
            "Global %s detour declined: its x86 entry prologue did not match the verified hotpatch-safe pattern.", name);
        WriteNativeDiagnostic(detail);
        return false;
    }

    auto* trampoline = static_cast<uint8_t*>(VirtualAlloc(nullptr, PatchLength * 2,
        MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
    if (!trampoline) {
        char detail[160]{};
        sprintf_s(detail, sizeof(detail), "Global %s detour could not allocate its trampoline (Windows error %lu).",
            name, GetLastError());
        WriteNativeDiagnostic(detail);
        return false;
    }

    memcpy(trampoline, entry, PatchLength);
    trampoline[PatchLength] = 0xE9;
    const uint32_t entryAddress = static_cast<uint32_t>(reinterpret_cast<uintptr_t>(entry));
    const uint32_t trampolineAddress = static_cast<uint32_t>(reinterpret_cast<uintptr_t>(trampoline));
    const uint32_t returnDisplacement = (entryAddress + static_cast<uint32_t>(PatchLength)) -
        (trampolineAddress + static_cast<uint32_t>(PatchLength * 2));
    memcpy(trampoline + PatchLength + 1, &returnDisplacement, sizeof(returnDisplacement));
    FlushInstructionCache(GetCurrentProcess(), trampoline, PatchLength * 2);

    SuspendedProcessThreads suspended;
    if (!suspended.SuspendOutsideEntryPoint(entry, PatchLength)) {
        WriteNativeDiagnostic("Global IP Helper detour deferred because a game thread could not be safely suspended outside the API prologue.");
        VirtualFree(trampoline, 0, MEM_RELEASE);
        return false;
    }

    DWORD previousProtection = 0;
    if (!VirtualProtect(entry, PatchLength, PAGE_EXECUTE_READWRITE, &previousProtection)) {
        char detail[160]{};
        sprintf_s(detail, sizeof(detail), "Global %s detour could not make its entry writable (Windows error %lu).",
            name, GetLastError());
        WriteNativeDiagnostic(detail);
        VirtualFree(trampoline, 0, MEM_RELEASE);
        return false;
    }

    uint8_t entryJump[PatchLength]{};
    entryJump[0] = 0xE9;
    const uint32_t hookAddress = static_cast<uint32_t>(reinterpret_cast<uintptr_t>(hook));
    const uint32_t entryDisplacement = hookAddress - (entryAddress + static_cast<uint32_t>(PatchLength));
    memcpy(entryJump + 1, &entryDisplacement, sizeof(entryDisplacement));
    memcpy(entry, entryJump, sizeof(entryJump));
    FlushInstructionCache(GetCurrentProcess(), entry, PatchLength);
    DWORD ignoredProtection = 0;
    if (!VirtualProtect(entry, PatchLength, previousProtection, &ignoredProtection))
        WriteNativeDiagnostic("Global IP Helper API detour was installed, but its original page protection could not be restored.");
    *original = trampoline;
    char detail[144]{};
    sprintf_s(detail, sizeof(detail), "Global %s entry detour installed with an x86 trampoline.", name);
    WriteNativeDiagnostic(detail);
    return true;
}
bool InstallGlobalAddressMappingDetours() {
    void* adaptersTrampoline = nullptr;
    void* routeTableTrampoline = nullptr;
    const bool adaptersInstalled = InstallX86EntryDetour("GetAdaptersInfo",
        reinterpret_cast<void*>(realGetAdaptersInfo), reinterpret_cast<void*>(HookGetAdaptersInfo), &adaptersTrampoline);
    if (adaptersInstalled) realGetAdaptersInfo = reinterpret_cast<GetAdaptersInfoFn>(adaptersTrampoline);
    const bool routeTableInstalled = InstallX86EntryDetour("GetIpForwardTable",
        reinterpret_cast<void*>(realGetIpForwardTable), reinterpret_cast<void*>(HookGetIpForwardTable), &routeTableTrampoline);
    if (routeTableInstalled) realGetIpForwardTable = reinterpret_cast<GetIpForwardTableFn>(routeTableTrampoline);
    const bool installed = adaptersInstalled && routeTableInstalled;
    globalAddressMappingDetoursInstalled.store(installed);
    if (installed) {
        ReportMapAutomation(5, "Global GetAdaptersInfo and GetIpForwardTable entry detours are installed.");
    } else {
        ReportMapAutomation(5, "Global IP Helper entry detours are incomplete; imported-call hooks will be used where available.");
    }
    return installed;
}
DWORD WINAPI PatchWorker(void*) {
    HMODULE iphlpapi = GetModuleHandleW(L"IPHLPAPI.dll");
    if (!iphlpapi) iphlpapi = LoadLibraryW(L"IPHLPAPI.dll");
    if (iphlpapi) {
        realGetAdaptersInfo = reinterpret_cast<GetAdaptersInfoFn>(GetProcAddress(iphlpapi, "GetAdaptersInfo"));
        realGetIpForwardTable = reinterpret_cast<GetIpForwardTableFn>(GetProcAddress(iphlpapi, "GetIpForwardTable"));
    }
    if (realGetAdaptersInfo && realGetIpForwardTable) {
        InstallGlobalAddressMappingDetours();
        if (!RunNetworkMappingSelfCheck())
            WriteNativeDiagnostic("GameRanger-style address mapping self-check failed before the native game UI was shown");
        else
            WriteNativeDiagnostic("GameRanger-style address mapping self-check passed before the native game UI was shown");
    } else {
        ReportMapAutomation(5, "Network mapping could not load GetAdaptersInfo and GetIpForwardTable for its self-check.");
    }
    bool runtimeProviderLoadCheck = false;
    wchar_t runtimeProviderFlag[8]{};
    runtimeProviderLoadCheck = GetEnvironmentVariableW(L"EDMW_RELAY_TEST_LOAD_PROVIDER", runtimeProviderFlag,
        _countof(runtimeProviderFlag)) != 0 && wcscmp(runtimeProviderFlag, L"1") == 0;
    if (runtimeProviderLoadCheck && !GetModuleHandleW(L"TCP Protocol.dll")) {
        wchar_t executablePath[MAX_PATH]{};
        const DWORD pathLength = GetModuleFileNameW(nullptr, executablePath, _countof(executablePath));
        if (pathLength == 0 || pathLength >= _countof(executablePath)) {
            ReportMapAutomation(5, "Provider hook runtime check failed: could not locate the EDMW installation folder.");
            return 0;
        }
        std::wstring providerPath(executablePath, pathLength);
        const size_t separator = providerPath.find_last_of(L"\\/");
        if (separator == std::wstring::npos) {
            ReportMapAutomation(5, "Provider hook runtime check failed: the EDMW executable path has no folder.");
            return 0;
        }
        providerPath.resize(separator + 1);
        providerPath += L"TCP Protocol.dll";
        if (!LoadLibraryW(providerPath.c_str())) {
            char detail[160]{};
            sprintf_s(detail, sizeof(detail), "Provider hook runtime check could not load TCP Protocol.dll (Windows error %lu).",
                GetLastError());
            WriteNativeDiagnostic(detail);
            ReportMapAutomation(5, detail);
            return 0;
        }
        WriteNativeDiagnostic("test-only runtime check loaded TCP Protocol.dll without invoking its game API");
    }
    if (!GetModuleHandleW(L"TCP Protocol.dll")) {
        const std::vector<uint8_t> waiting{ 5, 3 };
        WriteNativeDiagnostic(WritePipeFrame(waiting)
            ? "provider-not-loaded status frame queued on game pipe"
            : "provider-not-loaded status frame could not be queued on game pipe");
    }
    bool providerStatusReported = false;
    bool routeHookStatusReported = false;
    while (true) {
        PatchLoadedAddressMappingImports();
        if (routeTableHookInstalled.load() && !routeHookStatusReported) {
            ReportMapAutomation(5, "GetIpForwardTable interception is installed for game route lookups.");
            routeHookStatusReported = true;
        }
        if (HMODULE provider = GetModuleHandleW(L"TCP Protocol.dll")) {
            const bool patched = PatchProvider(provider);
            if (!providerStatusReported) {
                WriteNativeDiagnostic(patched
                    ? "EDMW TCP Protocol.dll Winsock and virtual-address hooks installed"
                    : "EDMW TCP Protocol.dll Winsock or virtual-address hook installation failed or required imports were absent");
                std::vector<uint8_t> result{ 5, static_cast<uint8_t>(patched ? 0 : 1) };
                WriteNativeDiagnostic(WritePipeFrame(result)
                    ? "provider-hook result status frame queued on game pipe"
                    : "provider-hook result status frame could not be queued on game pipe");
                providerStatusReported = true;
                if (!patched) return 0;
            }
        }
        Sleep(500);
    }
}
DWORD WINAPI PipeWriterWorker(void*) {
    WriteNativeDiagnostic("pipe writer worker entered");
    uint32_t lastReportedStage = 0;
    uint64_t lastStageHeartbeatAt = 0;
    for (;;) {
        if (gameSetupWorkerRequested.load()) {
            const uint64_t now = GetTickCount64();
            const uint32_t stage = gameSetupWorkerStage.load();
            if (!gameSetupWorkerEntryReported.load() && !gameSetupWorkerStartFailureReported.load() &&
                now - gameSetupWorkerStartedAt.load() >= 5000) {
                gameSetupWorkerStartFailureReported.store(true);
                ReportMapAutomation(9, "The native setup thread was created but did not enter its worker; EDMW was not advanced from its initial screen.");
            } else if (stage != 0 && (stage != lastReportedStage || now - lastStageHeartbeatAt >= 5000)) {
                lastReportedStage = stage;
                lastStageHeartbeatAt = now;
                ReportMapAutomation(5, std::string("Native setup worker stage: ") + GameSetupStageName(stage) + ".");
            }
        }
        const DWORD waitResult = WaitForSingleObject(pipeWriteEvent, 1000);
        if (waitResult == WAIT_TIMEOUT) continue;
        if (waitResult != WAIT_OBJECT_0) return 1;
        if (!pipeConnected.load()) return 0;
        for (;;) {
            std::vector<uint8_t> body;
            {
                std::lock_guard<std::mutex> lock(pipeQueueLock);
                if (pipeQueue.empty()) break;
                body = std::move(pipeQueue.front());
                pipeQueueBytes -= sizeof(uint32_t) + body.size();
                pipeQueue.pop_front();
            }
            if (!pipeConnected.load()) return 2;
            const uint32_t length = static_cast<uint32_t>(body.size());
            static std::atomic<uint32_t> tracedWrites{ 0 };
            const uint32_t trace = tracedWrites.fetch_add(1);
            if (trace < 32) WriteNativeDiagnostic("queued game frame waiting for the shared pipe write lock");
            AcquireSRWLockExclusive(&pipeFrameWriteLock);
            if (trace < 32) {
                char detail[128]{};
                sprintf_s(detail, sizeof(detail), "queued game frame type=%u bytes=%zu acquired the shared pipe write lock",
                    body.empty() ? 0u : body[0], body.size());
                WriteNativeDiagnostic(detail);
            }
            const bool written = pipeConnected.load() && WriteExact(eventPipeHandle, &length, sizeof(length)) &&
                WriteExact(eventPipeHandle, body.data(), length);
            if (trace < 32) {
                char detail[128]{};
                sprintf_s(detail, sizeof(detail), "queued game frame type=%u bytes=%zu write result=%u",
                    body.empty() ? 0u : body[0], body.size(), written ? 1u : 0u);
                WriteNativeDiagnostic(detail);
            }
            if (!written) {
                pipeConnected.store(false);
                ReleaseSRWLockExclusive(&pipeFrameWriteLock);
                std::lock_guard<std::mutex> lock(pipeQueueLock);
                pipeQueue.clear();
                pipeQueueBytes = 0;
                return 3;
            }
            ReleaseSRWLockExclusive(&pipeFrameWriteLock);
        }
    }
}
DWORD WINAPI PipeWorker(void*) {
    WriteNativeDiagnostic("pipe worker entered");
    wchar_t pipeName[256]{};
    if (!GetEnvironmentVariableW(L"EDMW_RELAY_PIPE", pipeName, _countof(pipeName))) return 1;
    std::wstring path = L"\\\\.\\pipe\\" + std::wstring(pipeName);
    for (int attempt = 0; attempt < 600; ++attempt) {
        pipeHandle = CreateFileW(path.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (pipeHandle != INVALID_HANDLE_VALUE) break;
        Sleep(100);
    }
    if (pipeHandle == INVALID_HANDLE_VALUE) return 2;
    WriteNativeDiagnostic("read-only game-control pipe connected");
    wchar_t eventPipeName[256]{};
    SetLastError(ERROR_SUCCESS);
    const DWORD eventPipeNameLength = GetEnvironmentVariableW(L"EDMW_RELAY_EVENT_PIPE", eventPipeName,
        static_cast<DWORD>(_countof(eventPipeName)));
    if (!eventPipeNameLength || !eventPipeName[0]) {
        char detail[128]{};
        sprintf_s(detail, sizeof(detail), "EDMW_RELAY_EVENT_PIPE is missing or invalid (Windows error %lu)", GetLastError());
        WriteNativeDiagnostic(detail);
        CloseHandle(pipeHandle); pipeHandle = INVALID_HANDLE_VALUE;
        return 3;
    }
    const std::wstring eventPipePath = L"\\\\.\\pipe\\" + std::wstring(eventPipeName);
    for (int attempt = 0; attempt < 600; ++attempt) {
        eventPipeHandle = CreateFileW(eventPipePath.c_str(), GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (eventPipeHandle != INVALID_HANDLE_VALUE) break;
        if (attempt % 50 == 49) {
            char detail[160]{};
            sprintf_s(detail, sizeof(detail), "waiting for game-events pipe connection (attempt %d, Windows error %lu)",
                attempt + 1, GetLastError());
            WriteNativeDiagnostic(detail);
        }
        Sleep(100);
    }
    if (eventPipeHandle == INVALID_HANDLE_VALUE) {
        char detail[128]{};
        sprintf_s(detail, sizeof(detail), "game-events pipe connection failed (Windows error %lu)", GetLastError());
        WriteNativeDiagnostic(detail);
        CloseHandle(pipeHandle); pipeHandle = INVALID_HANDLE_VALUE;
        return 4;
    }
    WriteNativeDiagnostic("write-only game-events pipe connected");
    wchar_t statusPipeName[256]{};
    SetLastError(ERROR_SUCCESS);
    const DWORD statusPipeNameLength = GetEnvironmentVariableW(L"EDMW_RELAY_STATUS_PIPE", statusPipeName,
        static_cast<DWORD>(_countof(statusPipeName)));
    if (!statusPipeNameLength || !statusPipeName[0]) {
        char detail[128]{};
        sprintf_s(detail, sizeof(detail), "EDMW_RELAY_STATUS_PIPE is missing or invalid (Windows error %lu)", GetLastError());
        WriteNativeDiagnostic(detail);
        pipeConnected.store(false);
        return 5;
    }
    const std::wstring statusPath = L"\\\\.\\pipe\\" + std::wstring(statusPipeName);
    for (int attempt = 0; attempt < 600; ++attempt) {
        statusPipeHandle = CreateFileW(statusPath.c_str(), GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (statusPipeHandle != INVALID_HANDLE_VALUE) break;
        if (attempt % 50 == 49) {
            char detail[160]{};
            sprintf_s(detail, sizeof(detail), "waiting for status pipe connection (attempt %d, Windows error %lu)",
                attempt + 1, GetLastError());
            WriteNativeDiagnostic(detail);
        }
        Sleep(100);
    }
    if (statusPipeHandle == INVALID_HANDLE_VALUE) {
        char detail[128]{};
        sprintf_s(detail, sizeof(detail), "status pipe connection failed (Windows error %lu)", GetLastError());
        WriteNativeDiagnostic(detail);
        pipeConnected.store(false);
        return 6;
    }
    statusPipeConnected.store(true);
    WriteNativeDiagnostic("dedicated status pipe connected");
    pipeWriteEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!pipeWriteEvent) {
        char detail[128]{};
        sprintf_s(detail, sizeof(detail), "CreateEventW failed (Windows error %lu)", GetLastError());
        WriteNativeDiagnostic(detail);
        pipeConnected.store(false); statusPipeConnected.store(false); return 7;
    }
    WriteNativeDiagnostic("game-control pipe event created");
    pipeConnected.store(true);
    if (!CreateThread(nullptr, 0, PipeWriterWorker, nullptr, 0, nullptr)) {
        char detail[128]{};
        sprintf_s(detail, sizeof(detail), "CreateThread(PipeWriterWorker) failed (Windows error %lu)", GetLastError());
        WriteNativeDiagnostic(detail);
        pipeConnected.store(false); statusPipeConnected.store(false);
        CloseHandle(pipeWriteEvent); pipeWriteEvent = nullptr;
        return 8;
    }
    WriteNativeDiagnostic("pipe writer thread created");
    ReportMapAutomation(5, "Native bridge connected to Mod Studio's local game-control pipe.");
    bool patchWorkerStarted = false;
    while (true) {
        uint32_t length = 0;
        if (!ReadExact(pipeHandle, &length, sizeof(length))) {
            WriteNativeDiagnostic("game-control pipe closed while waiting for a request frame");
            break;
        }
        if (length == 0 || length > PipeFrameLimit) {
            WriteNativeDiagnostic("game-control pipe received an invalid request length");
            break;
        }
        std::vector<uint8_t> body(length);
        if (!ReadExact(pipeHandle, body.data(), length)) break;
        if (body[0] == 3 && length == 9) {
            const uint16_t relayPort = ReadU16(body.data() + 1);
            const uint16_t proxyPort = ReadU16(body.data() + 3);
            uint32_t physicalIpv4 = 0;
            memcpy(&physicalIpv4, body.data() + 5, sizeof(physicalIpv4));
            if (relayPort >= RelayPortBase && relayPort <= RelayPortBase + 7 && proxyPort != 0 && physicalIpv4 != 0) {
                ownRelayPort.store(relayPort);
                tcpProxyPort.store(proxyPort);
                selectedPhysicalIpv4.store(physicalIpv4);
                InitializeDirectedBroadcastAddresses();
                IN_ADDR physicalAddress{}; physicalAddress.s_addr = physicalIpv4;
                char physicalIp[INET_ADDRSTRLEN]{};
                InetNtopA(AF_INET, &physicalAddress, physicalIp, sizeof(physicalIp));
                char mappingStatus[160]{};
                sprintf_s(mappingStatus, sizeof(mappingStatus), "Room route configured relay=%u selected physical IPv4=%s",
                    relayPort, physicalIp);
                WriteNativeDiagnostic(mappingStatus);
                ReportMapAutomation(5, "Room virtual address configured. Waiting for EDMW to query its adapter list.");
                wchar_t mode[32]{};
                const bool hasMode = GetEnvironmentVariableW(L"EDMW_RELAY_MODE", mode, _countof(mode)) != 0;
                std::string roleMessage = "Native route configured; launch role is ";
                if (!hasMode) roleMessage += "missing.";
                else {
                    for (const wchar_t character : std::wstring_view(mode))
                        roleMessage.push_back(character <= 0x7F ? static_cast<char>(character) : '?');
                    roleMessage.push_back('.');
                }
                ReportMapAutomation(5, roleMessage);
                if (!hasMode || _wcsicmp(mode, L"menu") != 0) {
                    ReportMapAutomation(9, "The matchmaking bridge only supports launching EDMW's native game UI.");
                    continue;
                }
                if (!patchWorkerStarted) {
                    patchWorkerStarted = CreateThread(nullptr, 0, PatchWorker, nullptr, 0, nullptr) != nullptr;
                    if (!patchWorkerStarted) {
                        ReportMapAutomation(9, "Windows could not start the supported TCP/IP provider hook worker.");
                    }
                }
            }
        } else if (body[0] == 6 && length == 9) {
            const uint16_t localPeerPort = ReadU16(body.data() + 1);
            const uint16_t relayPeerPort = ReadU16(body.data() + 3);
            const uint16_t gamePeerPort = ReadU16(body.data() + 5);
            const uint16_t gameListenerPort = ReadU16(body.data() + 7);
            if (localPeerPort != 0 && relayPeerPort >= RelayPortBase && relayPeerPort <= RelayPortBase + 7 &&
                gamePeerPort != 0 && gameListenerPort != 0) {
                sockaddr_in peer{};
                peer.sin_family = AF_INET;
                peer.sin_addr.s_addr = htonl(VirtualNetwork | (relayPeerPort - RelayPortBase + 1));
                peer.sin_port = htons(gamePeerPort);
                {
                    std::lock_guard<std::mutex> lock(stateLock);
                    tcpPeerAddresses[TcpPeerKey(gameListenerPort, localPeerPort)] = peer;
                }
                const std::vector<uint8_t> acknowledgement{ 7, body[1], body[2], body[7], body[8] };
                WritePipeFrame(acknowledgement);
            }
        }
    }
    pipeConnected.store(false);
    statusPipeConnected.store(false);
    SetEvent(pipeWriteEvent);
    return 0;
}
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(instance);
        WriteNativeDiagnostic("relay bridge DLL_PROCESS_ATTACH");
        InstallVerificationMultiInstanceHook();
        CreateThread(nullptr, 0, PipeWorker, nullptr, 0, nullptr);
    }
    return TRUE;
}
