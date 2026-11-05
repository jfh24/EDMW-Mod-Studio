#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <iphlpapi.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <deque>
#include <mutex>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

namespace {
constexpr uint16_t RelayPortBase = 40000;
constexpr uint32_t VirtualNetwork = 0x0AFEFF00; // 10.254.255.0/24
constexpr size_t PipeFrameLimit = 2048;
constexpr size_t MaximumQueuedPipeBytes = 512 * 1024;
constexpr size_t TunnelHeaderSize = 14;
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
HANDLE pipeHandle = INVALID_HANDLE_VALUE;
HANDLE pipeWriteEvent = nullptr;
std::mutex stateLock, pipeQueueLock;
std::deque<std::vector<uint8_t>> pipeQueue;
size_t pipeQueueBytes = 0;
std::atomic<bool> pipeConnected{ false };
std::unordered_map<SOCKET, sockaddr_in> virtualPeers;
std::unordered_map<uint32_t, sockaddr_in> tcpPeerAddresses;
std::atomic<uint16_t> ownRelayPort{ 0 };
std::atomic<uint16_t> tcpProxyPort{ 0 };
std::once_flag broadcastAddressInitialization;
std::vector<uint32_t> directedBroadcastAddresses;

uint16_t ReadU16(const uint8_t* p) { return static_cast<uint16_t>((p[0] << 8) | p[1]); }
void WriteU16(uint8_t* p, uint16_t value) { p[0] = static_cast<uint8_t>(value >> 8); p[1] = static_cast<uint8_t>(value); }
uint32_t TcpPeerKey(uint16_t listenerPort, uint16_t localProxyPort) {
    return (static_cast<uint32_t>(listenerPort) << 16) | localProxyPort;
}
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
bool IsBroadcastOrMulticast(const sockaddr_in& address) {
    const uint32_t ip = ntohl(address.sin_addr.s_addr);
    return ip == INADDR_BROADCAST || (ip >> 28) == 0xE || IsDirectedBroadcast(ip);
}
uint16_t RouteFor(const sockaddr_in& address) {
    const uint32_t ip = ntohl(address.sin_addr.s_addr);
    const uint8_t last = static_cast<uint8_t>(ip);
    if (ip == INADDR_BROADCAST || (ip >> 28) == 0xE || IsDirectedBroadcast(ip)) return 0;
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
    return WritePipeFrame(body);
}
int RelaySend(SOCKET socket, const char* bytes, int length, const sockaddr_in& target) {
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
    if (address && address->sa_family == AF_INET && length >= static_cast<int>(sizeof(sockaddr_in)) && IsDatagram(socket)) {
        sockaddr_in peer{}; memcpy(&peer, address, sizeof(peer));
        if (RouteFromVirtualAddress(peer) == 0 && !IsBroadcastOrMulticast(peer) && ownRelayPort.load() != RelayPortBase)
            peer.sin_addr.s_addr = htonl(VirtualNetwork | 1);
        std::lock_guard<std::mutex> lock(stateLock); virtualPeers[socket] = peer;
        return 0;
    }
    if (address && address->sa_family == AF_INET && length >= static_cast<int>(sizeof(sockaddr_in)) &&
        IsSocketType(socket, SOCK_STREAM) && tcpProxyPort.load() != 0) {
        sockaddr_in target{}; memcpy(&target, address, sizeof(target));
        const uint16_t relayPort = RouteFor(target);
        if (relayPort == 0) { WSASetLastError(WSAEHOSTUNREACH); return SOCKET_ERROR; }
        sockaddr_in proxy{};
        proxy.sin_family = AF_INET;
        proxy.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        proxy.sin_port = htons(tcpProxyPort.load());
        const int result = realConnect(socket, reinterpret_cast<const sockaddr*>(&proxy), sizeof(proxy));
        if (result == SOCKET_ERROR) return result;
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
            if (count == SOCKET_ERROR || count == 0) return SOCKET_ERROR;
            sent += count;
        }
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
    for (;;) {
        sockaddr_in source{}; int sourceLength = sizeof(source);
        const int received = realRecvFrom(socket, frame.data(), static_cast<int>(frame.size()), flags,
            reinterpret_cast<sockaddr*>(&source), &sourceLength);
        if (received == SOCKET_ERROR) return received;
        const bool tunnelFrame = received >= static_cast<int>(TunnelHeaderSize) && memcmp(frame.data(), "EDMWRLY1", 8) == 0;
        const int unwrapped = Unwrap(frame.data(), received, bytes, length, from, fromLength, 0, false);
        if (unwrapped == -2) continue;
        if (!tunnelFrame && from && fromLength && *fromLength >= sourceLength) {
            memcpy(from, &source, sourceLength);
            *fromLength = sourceLength;
        }
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
bool ImageRange(uint32_t imageSize, uint32_t rva, size_t length) {
    return rva <= imageSize && length <= static_cast<size_t>(imageSize - rva);
}
const ImportHook* FindProviderHook(const char* name, WORD ordinal, bool byOrdinal,
    bool lookupNamesAvailable, DWORD resolvedAddress, HMODULE winsock) {
    for (const auto& candidate : ProviderHooks) {
        if (lookupNamesAvailable) {
            if ((byOrdinal && candidate.ordinal == ordinal) ||
                (!byOrdinal && name && _stricmp(candidate.name, name) == 0)) return &candidate;
            continue;
        }
        // OriginalFirstThunk can be absent in a valid PE image. In that case
        // FirstThunk contains resolved function pointers, not import-name RVAs.
        // Match those pointers against the loaded Winsock exports instead of
        // interpreting them as RVAs and crashing the patch worker.
        FARPROC original = GetProcAddress(winsock, candidate.name);
        if (original && reinterpret_cast<DWORD>(original) == resolvedAddress) return &candidate;
        original = GetProcAddress(winsock, MAKEINTRESOURCEA(candidate.ordinal));
        if (original && reinterpret_cast<DWORD>(original) == resolvedAddress) return &candidate;
    }
    return nullptr;
}
bool PatchThunk(const ImportHook& imported, IMAGE_THUNK_DATA32* address, uint16_t& patched) {
    const DWORD replacement = reinterpret_cast<DWORD>(imported.hook);
    if (address->u1.Function != replacement) {
        DWORD prior = 0;
        if (!VirtualProtect(&address->u1.Function, sizeof(address->u1.Function), PAGE_READWRITE, &prior)) return false;
        address->u1.Function = replacement;
        DWORD ignored = 0;
        const bool restored = VirtualProtect(&address->u1.Function, sizeof(address->u1.Function), prior, &ignored) != FALSE;
        if (!restored) return false;
    }
    patched |= imported.bit;
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
    if (!winsock) return false;
    auto* imports = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + directory.VirtualAddress);
    uint16_t patched = 0;
    const size_t descriptorCount = directory.Size / sizeof(IMAGE_IMPORT_DESCRIPTOR);
    for (size_t descriptorIndex = 0; descriptorIndex < descriptorCount && imports[descriptorIndex].Name; ++descriptorIndex) {
        auto& descriptor = imports[descriptorIndex];
        if (!ImageRange(imageSize, descriptor.Name, 1)) return false;
        const char* dll = reinterpret_cast<const char*>(base + descriptor.Name);
        if (!memchr(dll, '\0', imageSize - descriptor.Name)) return false;
        if (_stricmp(dll, "WS2_32.dll") != 0) continue;
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
            const auto* imported = FindProviderHook(name, ordinal, byOrdinal, lookupNamesAvailable,
                address[thunkIndex].u1.Function, winsock);
            if (imported && !PatchThunk(*imported, &address[thunkIndex], patched)) return false;
        }
        if (!terminated) return false;
    }
    constexpr uint16_t required = 0x187;
    return (patched & required) == required && (patched & 0x18) != 0 && (patched & 0x60) != 0;
}
DWORD WINAPI PatchWorker(void*) {
    const std::vector<uint8_t> waiting{ 5, 3 };
    WritePipeFrame(waiting);
    while (true) {
        if (HMODULE provider = GetModuleHandleW(L"TCP Protocol.dll")) {
            std::vector<uint8_t> result{ 5, static_cast<uint8_t>(PatchProvider(provider) ? 0 : 1) };
            WritePipeFrame(result);
            return 0;
        }
        Sleep(100);
    }
}
DWORD WINAPI PipeWriterWorker(void*) {
    for (;;) {
        if (WaitForSingleObject(pipeWriteEvent, INFINITE) != WAIT_OBJECT_0) return 1;
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
            if (!WriteExact(pipeHandle, &length, sizeof(length)) ||
                !WriteExact(pipeHandle, body.data(), length)) {
                pipeConnected.store(false);
                std::lock_guard<std::mutex> lock(pipeQueueLock);
                pipeQueue.clear();
                pipeQueueBytes = 0;
                return 3;
            }
        }
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
    pipeWriteEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!pipeWriteEvent) { pipeConnected.store(false); return 3; }
    pipeConnected.store(true);
    if (!CreateThread(nullptr, 0, PipeWriterWorker, nullptr, 0, nullptr)) {
        pipeConnected.store(false);
        CloseHandle(pipeWriteEvent); pipeWriteEvent = nullptr;
        return 4;
    }
    bool patchWorkerStarted = false;
    while (true) {
        uint32_t length = 0;
        if (!ReadExact(pipeHandle, &length, sizeof(length)) || length == 0 || length > PipeFrameLimit) break;
        std::vector<uint8_t> body(length);
        if (!ReadExact(pipeHandle, body.data(), length)) break;
        if (body[0] == 3 && length == 5) {
            const uint16_t relayPort = ReadU16(body.data() + 1);
            const uint16_t proxyPort = ReadU16(body.data() + 3);
            if (relayPort >= RelayPortBase && relayPort <= RelayPortBase + 7 && proxyPort != 0) {
                ownRelayPort.store(relayPort);
                tcpProxyPort.store(proxyPort);
                InitializeDirectedBroadcastAddresses();
                if (!patchWorkerStarted) {
                    patchWorkerStarted = CreateThread(nullptr, 0, PatchWorker, nullptr, 0, nullptr) != nullptr;
                    if (!patchWorkerStarted) {
                        const std::vector<uint8_t> result{ 5, 2 };
                        WritePipeFrame(result);
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
    SetEvent(pipeWriteEvent);
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
