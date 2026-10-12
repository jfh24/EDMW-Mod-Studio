#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <vector>

int wmain(int argc, wchar_t** argv) {
    if (argc != 4) {
        fwprintf(stderr, L"usage: EDMWResourceSnapshot <pid> <hex-address> <byte-count>\n");
        return 2;
    }
    const DWORD pid = wcstoul(argv[1], nullptr, 10);
    const uintptr_t address = static_cast<uintptr_t>(_wcstoui64(argv[2], nullptr, 0));
    const size_t length = static_cast<size_t>(_wcstoui64(argv[3], nullptr, 0));
    if (!pid || !address || length < 4 || length > 0x1000 || (length & 3)) return 2;
    HANDLE process = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) {
        fwprintf(stderr, L"open-error=%lu\n", GetLastError());
        return 3;
    }
    std::vector<unsigned char> bytes(length);
    SIZE_T read = 0;
    const BOOL ok = ReadProcessMemory(process, reinterpret_cast<const void*>(address),
                                      bytes.data(), bytes.size(), &read);
    const DWORD error = ok ? ERROR_SUCCESS : GetLastError();
    CloseHandle(process);
    if (!ok || read != bytes.size()) {
        fwprintf(stderr, L"read-error=%lu bytes=%zu\n", error, static_cast<size_t>(read));
        return 4;
    }
    for (size_t offset = 0; offset < bytes.size(); offset += 4) {
        uint32_t value = 0;
        memcpy(&value, bytes.data() + offset, sizeof(value));
        wprintf(L"%03zX=%08X\n", offset, value);
    }
    return 0;
}
