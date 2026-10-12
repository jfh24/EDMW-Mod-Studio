#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

namespace {

struct ModuleRange {
    uintptr_t begin = 0;
    uintptr_t end = 0;
    std::string name;
};

struct ThreadTime {
    DWORD id = 0;
    uint64_t ticks = 0;
    uint64_t delta = 0;
};

uint64_t FileTimeTicks(const FILETIME& value) {
    ULARGE_INTEGER joined = {};
    joined.LowPart = value.dwLowDateTime;
    joined.HighPart = value.dwHighDateTime;
    return joined.QuadPart;
}

std::vector<DWORD> ProcessThreads(DWORD processId) {
    std::vector<DWORD> result;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return result;
    THREADENTRY32 entry = {};
    entry.dwSize = sizeof(entry);
    if (Thread32First(snapshot, &entry)) {
        do {
            if (entry.th32OwnerProcessID == processId) result.push_back(entry.th32ThreadID);
        } while (Thread32Next(snapshot, &entry));
    }
    CloseHandle(snapshot);
    return result;
}

uint64_t ReadThreadTicks(DWORD threadId) {
    HANDLE thread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, FALSE, threadId);
    if (!thread) return 0;
    FILETIME created = {}, exited = {}, kernel = {}, user = {};
    const bool read = GetThreadTimes(thread, &created, &exited, &kernel, &user) != FALSE;
    CloseHandle(thread);
    return read ? FileTimeTicks(kernel) + FileTimeTicks(user) : 0;
}

std::vector<ModuleRange> ProcessModules(DWORD processId) {
    std::vector<ModuleRange> result;
    HANDLE snapshot = CreateToolhelp32Snapshot(
        TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, processId);
    if (snapshot == INVALID_HANDLE_VALUE) return result;
    MODULEENTRY32 entry = {};
    entry.dwSize = sizeof(entry);
    if (Module32First(snapshot, &entry)) {
        do {
            ModuleRange module;
            module.begin = reinterpret_cast<uintptr_t>(entry.modBaseAddr);
            module.end = module.begin + entry.modBaseSize;
            module.name = entry.szModule;
            result.push_back(module);
        } while (Module32Next(snapshot, &entry));
    }
    CloseHandle(snapshot);
    return result;
}

const ModuleRange* FindModule(const std::vector<ModuleRange>& modules, uintptr_t address) {
    const auto found = std::find_if(modules.begin(), modules.end(),
        [address](const ModuleRange& module) {
            return address >= module.begin && address < module.end;
        });
    return found == modules.end() ? nullptr : &*found;
}

void PrintAddress(const std::vector<ModuleRange>& modules, uintptr_t address) {
    const ModuleRange* module = FindModule(modules, address);
    if (module)
        std::printf("%s+0x%08llX", module->name.c_str(),
                    static_cast<unsigned long long>(address - module->begin));
    else
        std::printf("0x%08llX", static_cast<unsigned long long>(address));
}

bool SampleThread(HANDLE process, const std::vector<ModuleRange>& modules,
                  DWORD threadId, uint64_t delta) {
    HANDLE thread = OpenThread(
        THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION,
        FALSE, threadId);
    if (!thread) return false;
    if (SuspendThread(thread) == static_cast<DWORD>(-1)) {
        CloseHandle(thread);
        return false;
    }

    CONTEXT context = {};
    context.ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER;
    const bool gotContext = GetThreadContext(thread, &context) != FALSE;
    if (gotContext) {
        std::printf("THREAD %lu DELTA %llu EIP ", threadId,
                    static_cast<unsigned long long>(delta));
        PrintAddress(modules, context.Eip);
        std::printf(" ESP 0x%08lX EBP 0x%08lX EAX 0x%08lX EBX 0x%08lX "
                    "ECX 0x%08lX EDX 0x%08lX ESI 0x%08lX EDI 0x%08lX\n",
                    context.Esp, context.Ebp, context.Eax, context.Ebx,
                    context.Ecx, context.Edx, context.Esi, context.Edi);

        DWORD locals[16] = {};
        SIZE_T localBytes = 0;
        const uintptr_t localBase = static_cast<uintptr_t>(context.Ebp) - 0x20;
        if (ReadProcessMemory(process, reinterpret_cast<const void*>(localBase),
                              locals, sizeof(locals), &localBytes)) {
            std::printf("  EBP_LOCALS");
            for (size_t index = 0; index < localBytes / sizeof(locals[0]); ++index)
                std::printf(" [%+lld]=%08lX", static_cast<long long>(index * 4) - 0x20,
                            locals[index]);
            std::printf("\n");
        }

        float position[2] = {};
        SIZE_T positionBytes = 0;
        if (ReadProcessMemory(process, reinterpret_cast<const void*>(context.Edi),
                              position, sizeof(position), &positionBytes) &&
            positionBytes == sizeof(position))
            std::printf("  POSITION x=%.9g y=%.9g\n",
                        static_cast<double>(position[0]),
                        static_cast<double>(position[1]));

        float bounds[4] = {};
        SIZE_T boundsBytes = 0;
        if (ReadProcessMemory(process,
                              reinterpret_cast<const void*>(context.Esi + 0x450),
                              bounds, sizeof(bounds), &boundsBytes) &&
            boundsBytes == sizeof(bounds))
            std::printf("  BOUNDS %.9g %.9g %.9g %.9g\n",
                        static_cast<double>(bounds[0]), static_cast<double>(bounds[1]),
                        static_cast<double>(bounds[2]), static_cast<double>(bounds[3]));

        DWORD stack[96] = {};
        SIZE_T bytesRead = 0;
        if (ReadProcessMemory(process, reinterpret_cast<const void*>(context.Esp),
                              stack, sizeof(stack), &bytesRead)) {
            std::printf("  MODULE_STACK");
            const size_t count = bytesRead / sizeof(stack[0]);
            size_t emitted = 0;
            for (size_t index = 0; index < count && emitted < 24; ++index) {
                if (!FindModule(modules, stack[index])) continue;
                std::printf(" [%zu]=", index);
                PrintAddress(modules, stack[index]);
                ++emitted;
            }
            std::printf("\n");
        }
    }

    ResumeThread(thread);
    CloseHandle(thread);
    return gotContext;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc != 2) {
        std::fprintf(stderr, "usage: ThreadSampler.exe <pid>\n");
        return 2;
    }
    const DWORD processId = std::strtoul(argv[1], nullptr, 10);
    HANDLE process = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
                                 FALSE, processId);
    if (!process) {
        std::fprintf(stderr, "OpenProcess failed: %lu\n", GetLastError());
        return 3;
    }
    const auto modules = ProcessModules(processId);
    if (modules.empty()) {
        std::fprintf(stderr, "module enumeration failed: %lu\n", GetLastError());
        CloseHandle(process);
        return 4;
    }
    for (const auto& module : modules)
        std::printf("MODULE %s 0x%08llX-0x%08llX\n", module.name.c_str(),
                    static_cast<unsigned long long>(module.begin),
                    static_cast<unsigned long long>(module.end));

    for (int sample = 0; sample < 8; ++sample) {
        std::vector<ThreadTime> times;
        for (DWORD id : ProcessThreads(processId))
            times.push_back({id, ReadThreadTicks(id), 0});
        Sleep(250);
        for (auto& time : times) {
            const uint64_t now = ReadThreadTicks(time.id);
            time.delta = now >= time.ticks ? now - time.ticks : 0;
        }
        std::sort(times.begin(), times.end(),
            [](const ThreadTime& left, const ThreadTime& right) {
                return left.delta > right.delta;
            });
        std::printf("SAMPLE %d\n", sample);
        for (size_t index = 0; index < (std::min)(times.size(), size_t{3}); ++index)
            SampleThread(process, modules, times[index].id, times[index].delta);
    }
    CloseHandle(process);
    return 0;
}
