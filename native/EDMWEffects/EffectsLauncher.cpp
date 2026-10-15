#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <bcrypt.h>
#include <cstdio>
#include <string>
#include <vector>
#pragma comment(lib, "bcrypt.lib")

namespace {
bool SupportedExecutable(const wchar_t* path) {
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    bool ok = BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0 &&
        BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0;
    uint8_t buffer[65536];
    DWORD read = 0;
    while (ok) {
        if (!ReadFile(file, buffer, sizeof(buffer), &read, nullptr)) { ok = false; break; }
        if (!read) break;
        ok = BCryptHashData(hash, buffer, read, 0) >= 0;
    }
    uint8_t digest[32]{};
    if (ok) ok = BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0;
    if (hash) BCryptDestroyHash(hash);
    if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    CloseHandle(file);
    constexpr uint8_t expected[] = { 0x9A,0x71,0x3D,0x50,0x34,0xD0,0xCF,0xFE,0xDA,0xDD,0x2D,0x50,0x26,0x6F,0x15,0x76,
        0x9F,0x90,0x5E,0x29,0x8B,0xDC,0x4F,0x87,0xCB,0x90,0xDD,0xF6,0xCA,0xAF,0x4A,0xE3 };
    return ok && !memcmp(digest, expected, sizeof(expected));
}

bool RemoteCall(HANDLE process, LPTHREAD_START_ROUTINE routine, const std::wstring& argument, DWORD& result) {
    const size_t length = (argument.size() + 1) * sizeof(wchar_t);
    void* remote = VirtualAllocEx(process, nullptr, length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) return false;
    SIZE_T written = 0;
    HANDLE thread = nullptr;
    if (WriteProcessMemory(process, remote, argument.c_str(), length, &written) && written == length)
        thread = CreateRemoteThread(process, nullptr, 0, routine, remote, 0, nullptr);
    const bool finished = thread && WaitForSingleObject(thread, 15000) == WAIT_OBJECT_0;
    const bool success = finished && GetExitCodeThread(thread, &result) && result;
    if (thread) CloseHandle(thread);
    // A timed-out thread might still read its argument. The caller terminates
    // this newly created, still suspended game on any failure.
    if (finished || !thread) VirtualFreeEx(process, remote, 0, MEM_RELEASE);
    return success;
}
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 3 || !SupportedExecutable(argv[1])) {
        fwprintf(stderr, L"Runtime effects require the supported EDMW executable.\n"); return 2;
    }
    wchar_t ownPath[32768];
    if (!GetModuleFileNameW(nullptr, ownPath, _countof(ownPath))) return 3;
    const std::wstring own(ownPath);
    const auto dll = own.substr(0, own.find_last_of(L"\\/") + 1) + L"EDMWEffects.dll";
    HMODULE local = LoadLibraryExW(dll.c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);
    const auto initialize = local ? GetProcAddress(local, "InitializeEffects") : nullptr;
    if (!initialize) { if (local) FreeLibrary(local); fwprintf(stderr, L"The bundled runtime DLL is missing or invalid.\n"); return 3; }
    const auto initializeOffset = reinterpret_cast<uintptr_t>(initialize) - reinterpret_cast<uintptr_t>(local);
    FreeLibrary(local);
    const std::wstring executable(argv[1]);
    const auto workingDirectory = executable.substr(0, executable.find_last_of(L"\\/"));
    std::wstring command = L"\"" + executable + L"\"";
    STARTUPINFOW startup{}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED,
        nullptr, workingDirectory.c_str(), &startup, &process)) {
        fwprintf(stderr, L"Windows could not start EDMW (%lu).\n", GetLastError()); return 4;
    }
    DWORD remoteModule = 0, result = 0;
    const auto load = reinterpret_cast<LPTHREAD_START_ROUTINE>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW"));
    const bool ready = load && RemoteCall(process.hProcess, load, dll, remoteModule) &&
        RemoteCall(process.hProcess, reinterpret_cast<LPTHREAD_START_ROUTINE>(remoteModule + initializeOffset), argv[2], result) && result == 1;
    const bool started = ready && ResumeThread(process.hThread) != static_cast<DWORD>(-1);
    if (!started) {
        TerminateProcess(process.hProcess, 5);
        WaitForSingleObject(process.hProcess, 5000);
        fwprintf(stderr, L"Runtime initialization failed; the new game process was stopped before play.\n");
    }
    // Successful injection alone does not mean the game finished starting. In
    // particular, an invalid installed command can fail as the menu's native
    // technology data is loaded, after InitializeEffects has already returned.
    bool exitedDuringStartup = false;
    if (started && WaitForSingleObject(process.hProcess, 5000) == WAIT_OBJECT_0) {
        DWORD exitCode = 0;
        GetExitCodeProcess(process.hProcess, &exitCode);
        fwprintf(stderr, L"EDMW exited during startup (code 0x%08lX). Runtime configuration: %s\n", exitCode, argv[2]);
        exitedDuringStartup = true;
    }
    CloseHandle(process.hThread); CloseHandle(process.hProcess);
    return !started ? 5 : exitedDuringStartup ? 6 : 0;
}
