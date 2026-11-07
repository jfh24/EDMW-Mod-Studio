#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <bcrypt.h>
#include <cstdint>
#include <cstdio>
#include <string>
#pragma comment(lib, "bcrypt.lib")

namespace {
bool SupportedExecutable(const wchar_t* path) {
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    BCRYPT_ALG_HANDLE algorithm = nullptr; BCRYPT_HASH_HANDLE hash = nullptr;
    bool ok = BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0 &&
        BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0;
    uint8_t buffer[65536]; DWORD read = 0;
    while (ok) { if (!ReadFile(file, buffer, sizeof(buffer), &read, nullptr)) { ok = false; break; }
        if (!read) break; ok = BCryptHashData(hash, buffer, read, 0) >= 0; }
    uint8_t digest[32]{}; if (ok) ok = BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0;
    if (hash) BCryptDestroyHash(hash); if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0); CloseHandle(file);
    constexpr uint8_t expected[] = { 0x9A,0x71,0x3D,0x50,0x34,0xD0,0xCF,0xFE,0xDA,0xDD,0x2D,0x50,0x26,0x6F,0x15,0x76,
        0x9F,0x90,0x5E,0x29,0x8B,0xDC,0x4F,0x87,0xCB,0x90,0xDD,0xF6,0xCA,0xAF,0x4A,0xE3 };
    return ok && memcmp(digest, expected, sizeof(expected)) == 0;
}
bool SupportedTcpProvider(const wchar_t* path) {
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    BCRYPT_ALG_HANDLE algorithm = nullptr; BCRYPT_HASH_HANDLE hash = nullptr;
    bool ok = BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0 &&
        BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0;
    uint8_t buffer[65536]; DWORD read = 0;
    while (ok) { if (!ReadFile(file, buffer, sizeof(buffer), &read, nullptr)) { ok = false; break; }
        if (!read) break; ok = BCryptHashData(hash, buffer, read, 0) >= 0; }
    uint8_t digest[32]{}; if (ok) ok = BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0;
    if (hash) BCryptDestroyHash(hash); if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0); CloseHandle(file);
    constexpr uint8_t expected[] = { 0xDC,0x8C,0xBD,0x06,0xA6,0x50,0xC8,0x8E,0xC6,0x4A,0xEA,0x47,0x62,0x28,0xC5,0x69,
        0xBB,0x1D,0xDE,0xBB,0xF6,0x00,0x02,0x6E,0xE6,0xDB,0xDF,0xB4,0xAA,0xDD,0xC2,0x15 };
    return ok && memcmp(digest, expected, sizeof(expected)) == 0;
}
bool Inject(HANDLE process, LPTHREAD_START_ROUTINE loadLibrary, const std::wstring& path) {
    const size_t bytes = (path.size() + 1) * sizeof(wchar_t);
    void* remote = VirtualAllocEx(process, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) return false;
    SIZE_T written = 0;
    HANDLE thread = nullptr;
    if (WriteProcessMemory(process, remote, path.c_str(), bytes, &written) && written == bytes)
        thread = CreateRemoteThread(process, nullptr, 0, loadLibrary, remote, 0, nullptr);
    const bool finished = thread && WaitForSingleObject(thread, 15000) == WAIT_OBJECT_0;
    DWORD module = 0; const bool loaded = finished && GetExitCodeThread(thread, &module) && module != 0;
    if (thread) CloseHandle(thread);
    if (finished || !thread) VirtualFreeEx(process, remote, 0, MEM_RELEASE);
    return loaded;
}
}

int wmain(int argc, wchar_t** argv) {
    const std::wstring executable(argc > 1 ? argv[1] : L"");
    const auto workingDirectory = executable.substr(0, executable.find_last_of(L"\\/"));
    const std::wstring provider = workingDirectory + L"\\TCP Protocol.dll";
    if (argc != 4 ||
        !SupportedExecutable(argv[1]) || !SupportedTcpProvider(provider.c_str())) {
        fwprintf(stderr, L"Internet play requires the supported EDMW executable and TCP/IP provider.\n"); return 2;
    }
    if (!SetEnvironmentVariableW(L"EDMW_RELAY_PIPE", argv[3]) ||
        !SetEnvironmentVariableW(L"EDMW_RELAY_MODE", L"menu")) {
        fwprintf(stderr, L"Could not configure the relay bridge's native game process.\n"); return 3;
    }
    std::wstring command = L"\"" + executable + L"\"";
    STARTUPINFOW startup{}; startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESHOWWINDOW;
    startup.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION game{};
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED,
        nullptr, workingDirectory.c_str(), &startup, &game)) {
        fwprintf(stderr, L"Windows could not start EDMW (%lu).\n", GetLastError()); return 4;
    }
    const auto loadLibrary = reinterpret_cast<LPTHREAD_START_ROUTINE>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW"));
    const bool injected = loadLibrary && Inject(game.hProcess, loadLibrary, argv[2]);
    const bool started = injected && ResumeThread(game.hThread) != static_cast<DWORD>(-1);
    if (!started) {
        TerminateProcess(game.hProcess, 5); WaitForSingleObject(game.hProcess, 5000);
        fwprintf(stderr, L"The relay bridge could not be installed; the new game process was stopped.\n");
    } else {
        fwprintf(stdout, L"%lu\n", game.dwProcessId); fflush(stdout);
        WaitForSingleObject(game.hProcess, INFINITE);
    }
    CloseHandle(game.hThread); CloseHandle(game.hProcess);
    return started ? 0 : 5;
}
