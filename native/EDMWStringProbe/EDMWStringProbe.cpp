#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <wchar.h>

static bool ContainsInsensitive(const wchar_t* text, const wchar_t* needle) {
    if (!text || !needle || !*needle) return false;
    const size_t textLength = wcslen(text);
    const size_t needleLength = wcslen(needle);
    for (size_t index = 0; index + needleLength <= textLength; ++index)
        if (_wcsnicmp(text + index, needle, needleLength) == 0) return true;
    return false;
}

int wmain(int argc, wchar_t** argv) {
    if (argc < 3) {
        fwprintf(stderr, L"Usage: EDMWStringProbe.exe <resource DLL> <search text> [search text...]\n");
        return 2;
    }
    HMODULE module = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_AS_DATAFILE);
    if (!module) return 3;
    wchar_t value[2048] = {};
    unsigned matches = 0;
    bool hasExactIds = false;
    for (int term = 2; term < argc; ++term) {
        if (argv[term][0] != L'#') continue;
        hasExactIds = true;
        wchar_t* end = nullptr;
        const unsigned long id = wcstoul(argv[term] + 1, &end, 10);
        if (!end || *end || id == 0 || id > 65535) continue;
        const int length = LoadStringW(module, static_cast<unsigned>(id), value,
            static_cast<int>(sizeof(value) / sizeof(value[0])));
        if (length > 0) { wprintf(L"%lu\t%ls\n", id, value); ++matches; }
        else wprintf(L"%lu\t<MISSING>\n", id);
    }
    if (hasExactIds) {
        FreeLibrary(module);
        wprintf(L"MATCHES\t%u\n", matches);
        return 0;
    }
    for (unsigned id = 1; id <= 65535; ++id) {
        const int length = LoadStringW(module, id, value, static_cast<int>(sizeof(value) / sizeof(value[0])));
        if (length <= 0) continue;
        bool matched = false;
        for (int term = 2; term < argc; ++term) matched = matched || ContainsInsensitive(value, argv[term]);
        if (matched) { wprintf(L"%u\t%ls\n", id, value); ++matches; }
    }
    FreeLibrary(module);
    wprintf(L"MATCHES\t%u\n", matches);
    return 0;
}
