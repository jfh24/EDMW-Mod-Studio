#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <array>
#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

namespace {
constexpr uint16_t RelayPortBase = 40000;
constexpr uint32_t VirtualNetwork = 0x0AFEFF00; // 10.254.255.0/24
constexpr size_t PipeFrameLimit = 2048;
constexpr size_t TunnelHeaderSize = 14;
using SocketFn = SOCKET(WSAAPI*)(int, int, int);
using BindFn = int(WSAAPI*)(SOCKET, const sockaddr*, int);
using ConnectFn = int(WSAAPI*)(SOCKET, const sockaddr*, int);
using CloseFn = int(WSAAPI*)(SOCKET);
using SendFn = int(WSAAPI*)(SOCKET, const char*, int, int);
using SendToFn = int(WSAAPI*)(SOCKET, const char*, int, int, const sockaddr*, int);
using RecvFn = int(WSAAPI*)(SOCKET, char*, int, int);
using RecvFromFn = int(WSAAPI*)(SOCKET, char*, int, int, sockaddr*, int*);

SocketFn realSocket = ::socket;
BindFn realBind = ::bind;
ConnectFn realConnect = ::connect;
CloseFn realClose = ::closesocket;
SendFn realSend = ::send;
SendToFn realSendTo = ::sendto;
RecvFn realRecv = ::recv;
RecvFromFn realRecvFrom = ::recvfrom;
HANDLE pipeHandle = INVALID_HANDLE_VALUE;
std::mutex stateLock, pipeWriteLock;
std::unordered_map<SOCKET, sockaddr_in> virtualPeers;
std::atomic<uint16_t> ownRelayPort{ 0 };

uint16_t ReadU16(const uint8_t* p) { return static_cast<uint16_t>((p[0] << 8) | p[1]); }
void WriteU16(uint8_t* p, uint16_t value) { p[0] = static_cast<uint8_t>(value >> 8); p[1] = static_cast<uint8_t>(value); }
bool ReadExact(HANDLE pipe, void* buffer, DWORD bytes) {
    auto* next = static_cast<uint8_t*>(buffer);
    while (bytes) {
        DWORD count = 0;
        if (!ReadFile(pipe, next, bytes, &count, nullptr) || count == 0) return false;
        next += count; bytes -= count;
    }
    return true;
}
bool WriteExact(HANDLE pipe, const void* buffer, DWORD bytes) {
    auto* next = static_cast<const uint8_t*>(buffer);
    while (bytes) {
        DWORD count = 0;
        if (!WriteFile(pipe, next, bytes, &count, nullptr) || count == 0) return false;
        next += count; bytes -= count;
    }
    return true;
}
bool WritePipeFrame(const std::vector<uint8_t>& body) {
    if (pipeHandle == INVALID_HANDLE_VALUE || body.empty() || body.size() > PipeFrameLimit) return false;
    const uint32_t length = static_cast<uint32_t>(body.size());
    std::lock_guard<std::mutex> lock(pipeWriteLock);
    return WriteExact(pipeHandle, &length, sizeof(length)) && WriteExact(pipeHandle, body.data(), length);
}
bool IsDatagram(SOCKET socket) {
    int type = 0, size = sizeof(type);
    return ::getsockopt(socket, SOL_SOCKET, SO_TYPE, reinterpret_cast<char*>(&type), &size) == 0 && type == SOCK_DGRAM;
}
uint16_t RouteFor(const sockaddr_in& address) {
    const uint32_t ip = ntohl(address.sin_addr.s_addr);
    const uint8_t last = static_cast<uint8_t>(ip);
    if (ip == INADDR_BROADCAST || (ip >> 28) == 0xE) return 0;
    if ((ip & 0xFFFFFF00u) == VirtualNetwork && last >= 1 && last <= 8)
        return static_cast<uint16_t>(RelayPortBase + last - 1);
    // LAN discovery is broadcast from the host. A guest's direct host address
    // also maps to the room host when a game's own UI supplies a non-virtual IP.
    return ownRelayPort.load() == RelayPortBase ? 0 : RelayPortBase;
}
bool LocalEndpoint(SOCKET socket, sockaddr_in& local) {
    int size = sizeof(local);
    ZeroMemory(&local, sizeof(local));
    local.sin_family = AF_INET;
    return ::getsockname(socket, reinterpret_cast<sockaddr*>(&local), &size) == 0 && local.sin_port != 0;
}
int RelaySend(SOCKET socket, const char* bytes, int length, const sockaddr_in& target) {
    if (length <= 0 || length > 1200 || target.sin_port == 0) { WSASetLastError(WSAEMSGSIZE); return SOCKET_ERROR; }
    sockaddr_in local{};
    if (!LocalEndpoint(socket, local)) return SOCKET_ERROR;
    std::vector<uint8_t> body(9 + static_cast<size_t>(length));
    body[0] = 1;
    WriteU16(body.data() + 1, RouteFor(target));
    WriteU16(body.data() + 3, ntohs(local.sin_port));
    WriteU16(body.data() + 5, ntohs(target.sin_port));
    WriteU16(body.data() + 7, static_cast<uint16_t>(length));
    memcpy(body.data() + 9, bytes, static_cast<size_t>(length));
    if (!WritePipeFrame(body)) { WSASetLastError(WSAENETUNREACH); return SOCKET_ERROR; }
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

SOCKET WSAAPI HookSocket(int af, int type, int protocol) {
    const SOCKET socket = realSocket(af, type, protocol);
    return socket;
}
int WSAAPI HookBind(SOCKET socket, const sockaddr* address, int length) {
    const int result = realBind(socket, address, length);
    if (result == 0 && IsDatagram(socket)) {
        sockaddr_in local{};
        if (LocalEndpoint(socket, local)) {
            std::vector<uint8_t> body(7);
            body[0] = 4; WriteU16(body.data() + 1, ntohs(local.sin_port));
            memcpy(body.data() + 3, &local.sin_addr.s_addr, sizeof(local.sin_addr.s_addr));
            if (!WritePipeFrame(body)) WSASetLastError(WSAENETDOWN);
        }
    }
    return result;
}
int WSAAPI HookConnect(SOCKET socket, const sockaddr* address, int length) {
    if (address && address->sa_family == AF_INET && length >= static_cast<int>(sizeof(sockaddr_in)) && IsDatagram(socket)) {
        sockaddr_in peer{}; memcpy(&peer, address, sizeof(peer));
        if (RouteFromVirtualAddress(peer) == 0 && ownRelayPort.load() != RelayPortBase)
            peer.sin_addr.s_addr = htonl(VirtualNetwork | 1);
        std::lock_guard<std::mutex> lock(stateLock); virtualPeers[socket] = peer;
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
        return RelaySend(socket, bytes, length, target);
    }
    return realSendTo(socket, bytes, length, flags, address, addressLength);
}
int WSAAPI HookSend(SOCKET socket, const char* bytes, int length, int flags) {
    if (IsDatagram(socket)) {
        sockaddr_in target{};
        { std::lock_guard<std::mutex> lock(stateLock); const auto found = virtualPeers.find(socket); if (found != virtualPeers.end()) target = found->second; }
        if (target.sin_family == AF_INET) return RelaySend(socket, bytes, length, target);
    }
    return realSend(socket, bytes, length, flags);
}
int WSAAPI HookRecvFrom(SOCKET socket, char* bytes, int length, int flags, sockaddr* from, int* fromLength) {
    if (!IsDatagram(socket) || length < 0) return realRecvFrom(socket, bytes, length, flags, from, fromLength);
    std::vector<char> frame(static_cast<size_t>(length) + 2048);
    sockaddr_in source{}; int sourceLength = sizeof(source);
    const int received = realRecvFrom(socket, frame.data(), static_cast<int>(frame.size()), flags,
        reinterpret_cast<sockaddr*>(&source), &sourceLength);
    if (received == SOCKET_ERROR) return received;
    const bool tunnelFrame = received >= static_cast<int>(TunnelHeaderSize) && memcmp(frame.data(), "EDMWRLY1", 8) == 0;
    const int unwrapped = Unwrap(frame.data(), received, bytes, length, from, fromLength, 0, false);
    if (unwrapped == -2) { WSASetLastError(WSAEWOULDBLOCK); return SOCKET_ERROR; }
    if (!tunnelFrame && from && fromLength && *fromLength >= sourceLength) {
        memcpy(from, &source, sourceLength);
        *fromLength = sourceLength;
    }
    return unwrapped;
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

void PatchThunk(uint8_t* base, IMAGE_THUNK_DATA32* lookup, IMAGE_THUNK_DATA32* address) {
    FARPROC hook = nullptr;
    const bool byOrdinal = IMAGE_SNAP_BY_ORDINAL32(lookup->u1.Ordinal) != FALSE;
    const WORD ordinal = byOrdinal ? IMAGE_ORDINAL32(lookup->u1.Ordinal) : 0;
    const char* name = byOrdinal ? "" : reinterpret_cast<const char*>(base + lookup->u1.AddressOfData + 2);
    if ((byOrdinal && ordinal == 23) || (!byOrdinal && strcmp(name, "socket") == 0)) hook = reinterpret_cast<FARPROC>(HookSocket);
    else if ((byOrdinal && ordinal == 2) || (!byOrdinal && strcmp(name, "bind") == 0)) hook = reinterpret_cast<FARPROC>(HookBind);
    else if ((byOrdinal && ordinal == 4) || (!byOrdinal && strcmp(name, "connect") == 0)) hook = reinterpret_cast<FARPROC>(HookConnect);
    else if ((byOrdinal && ordinal == 3) || (!byOrdinal && strcmp(name, "closesocket") == 0)) hook = reinterpret_cast<FARPROC>(HookClose);
    else if ((byOrdinal && ordinal == 19) || (!byOrdinal && strcmp(name, "send") == 0)) hook = reinterpret_cast<FARPROC>(HookSend);
    else if ((byOrdinal && ordinal == 20) || (!byOrdinal && strcmp(name, "sendto") == 0)) hook = reinterpret_cast<FARPROC>(HookSendTo);
    else if ((byOrdinal && ordinal == 16) || (!byOrdinal && strcmp(name, "recv") == 0)) hook = reinterpret_cast<FARPROC>(HookRecv);
    else if ((byOrdinal && ordinal == 17) || (!byOrdinal && strcmp(name, "recvfrom") == 0)) hook = reinterpret_cast<FARPROC>(HookRecvFrom);
    if (!hook) return;
    DWORD prior = 0;
    if (VirtualProtect(&address->u1.Function, sizeof(address->u1.Function), PAGE_READWRITE, &prior)) {
        address->u1.Function = reinterpret_cast<DWORD>(hook);
        DWORD ignored = 0; VirtualProtect(&address->u1.Function, sizeof(address->u1.Function), prior, &ignored);
    }
}
bool PatchProvider(HMODULE provider) {
    auto* base = reinterpret_cast<uint8_t*>(provider);
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS32*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR32_MAGIC) return false;
    const auto directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!directory.VirtualAddress) return false;
    auto* imports = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + directory.VirtualAddress);
    uint8_t patched = 0;
    for (; imports->Name; ++imports) {
        const char* dll = reinterpret_cast<const char*>(base + imports->Name);
        if (_stricmp(dll, "WS2_32.dll") != 0) continue;
        auto* lookup = reinterpret_cast<IMAGE_THUNK_DATA32*>(base + (imports->OriginalFirstThunk ? imports->OriginalFirstThunk : imports->FirstThunk));
        auto* address = reinterpret_cast<IMAGE_THUNK_DATA32*>(base + imports->FirstThunk);
        for (; lookup->u1.AddressOfData; ++lookup, ++address) {
            const DWORD before = address->u1.Function;
            PatchThunk(base, lookup, address);
            if (before == address->u1.Function) continue;
            const bool byOrdinal = IMAGE_SNAP_BY_ORDINAL32(lookup->u1.Ordinal) != FALSE;
            const WORD ordinal = byOrdinal ? IMAGE_ORDINAL32(lookup->u1.Ordinal) : 0;
            const char* name = byOrdinal ? "" : reinterpret_cast<const char*>(base + lookup->u1.AddressOfData + 2);
            if ((byOrdinal && ordinal == 2) || (!byOrdinal && strcmp(name, "bind") == 0)) patched |= 0x01;
            else if ((byOrdinal && ordinal == 4) || (!byOrdinal && strcmp(name, "connect") == 0)) patched |= 0x02;
            else if ((byOrdinal && ordinal == 3) || (!byOrdinal && strcmp(name, "closesocket") == 0)) patched |= 0x04;
            else if ((byOrdinal && ordinal == 19) || (!byOrdinal && strcmp(name, "send") == 0)) patched |= 0x08;
            else if ((byOrdinal && ordinal == 20) || (!byOrdinal && strcmp(name, "sendto") == 0)) patched |= 0x10;
            else if ((byOrdinal && ordinal == 16) || (!byOrdinal && strcmp(name, "recv") == 0)) patched |= 0x20;
            else if ((byOrdinal && ordinal == 17) || (!byOrdinal && strcmp(name, "recvfrom") == 0)) patched |= 0x40;
        }
    }
    constexpr uint8_t required = 0x07;
    return (patched & required) == required && (patched & 0x18) != 0 && (patched & 0x60) != 0;
}
DWORD WINAPI PatchWorker(void*) {
    while (true) {
        if (HMODULE provider = GetModuleHandleW(L"TCP Protocol.dll")) {
            std::vector<uint8_t> result{ 5, static_cast<uint8_t>(PatchProvider(provider) ? 0 : 1) };
            WritePipeFrame(result);
            return 0;
        }
        Sleep(100);
    }
}
DWORD WINAPI PipeWorker(void*) {
    wchar_t pipeName[256]{};
    if (!GetEnvironmentVariableW(L"EDMW_RELAY_PIPE", pipeName, _countof(pipeName))) return 1;
    std::wstring path = L"\\\\.\\pipe\\" + std::wstring(pipeName);
    for (int attempt = 0; attempt < 600; ++attempt) {
        pipeHandle = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (pipeHandle != INVALID_HANDLE_VALUE) break;
        Sleep(100);
    }
    if (pipeHandle == INVALID_HANDLE_VALUE) return 2;
    bool patchWorkerStarted = false;
    while (true) {
        uint32_t length = 0;
        if (!ReadExact(pipeHandle, &length, sizeof(length)) || length == 0 || length > PipeFrameLimit) break;
        std::vector<uint8_t> body(length);
        if (!ReadExact(pipeHandle, body.data(), length)) break;
        if (body[0] == 3 && length == 3) {
            const uint16_t relayPort = ReadU16(body.data() + 1);
            if (relayPort >= RelayPortBase && relayPort <= RelayPortBase + 7) {
                ownRelayPort.store(relayPort);
                if (!patchWorkerStarted) {
                    patchWorkerStarted = CreateThread(nullptr, 0, PatchWorker, nullptr, 0, nullptr) != nullptr;
                    if (!patchWorkerStarted) {
                        const std::vector<uint8_t> result{ 5, 2 };
                        WritePipeFrame(result);
                    }
                }
            }
        }
    }
    return 0;
}
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(instance);
        CreateThread(nullptr, 0, PipeWorker, nullptr, 0, nullptr);
    }
    return TRUE;
}
