// Mixer.PluginHost.exe — один плагин в своём процессе. Аудио идёт через общую память и пару событий,
// управление — через именованный канал (см. host.h и README.md рядом).

#include <windows.h>
#include <objbase.h>

#include <cstdio>
#include <cstdlib>

#include "args.h"
#include "host.h"
#include "scan.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"
#include "public.sdk/source/vst/utility/stringconvert.h"

using namespace mixer::host;

namespace {

constexpr UINT kExitCrashed = 5;

// Плагин упал (в любом потоке): одна строка в stderr — микшер положит её в журнал — и немедленный выход.
// Никаких окон Windows Error Reporting: процесс должен исчезнуть сразу, чтобы слот ушёл в обход.
LONG WINAPI OnCrash(EXCEPTION_POINTERS* info) {
    const auto* rec = info->ExceptionRecord;
    char module[MAX_PATH] = "?";
    HMODULE mod = nullptr;
    uintptr_t offset = 0;
    if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           static_cast<LPCSTR>(rec->ExceptionAddress), &mod)) {
        GetModuleFileNameA(mod, module, MAX_PATH);
        offset = reinterpret_cast<uintptr_t>(rec->ExceptionAddress) - reinterpret_cast<uintptr_t>(mod);
    }
    std::fprintf(stderr, "crashed: exception 0x%08lX at %s+0x%llX\n", static_cast<unsigned long>(rec->ExceptionCode),
                 module, static_cast<unsigned long long>(offset));
    std::fflush(stderr);
    TerminateProcess(GetCurrentProcess(), kExitCrashed);
    return EXCEPTION_EXECUTE_HANDLER;
}

void InstallCrashHandlers() {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    SetUnhandledExceptionFilter(OnCrash);
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
    _set_purecall_handler([] {
        std::fprintf(stderr, "crashed: pure virtual call\n");
        std::fflush(stderr);
        TerminateProcess(GetCurrentProcess(), kExitCrashed);
    });
    _set_invalid_parameter_handler([](const wchar_t*, const wchar_t*, const wchar_t*, unsigned, uintptr_t) {
        std::fprintf(stderr, "crashed: invalid CRT parameter\n");
        std::fflush(stderr);
        TerminateProcess(GetCurrentProcess(), kExitCrashed);
    });
}

// Имя хоста, которое видит плагин (IHostApplication::getName).
class MixerHostApplication final : public Steinberg::Vst::HostApplication {
public:
    Steinberg::tresult PLUGIN_API getName(Steinberg::Vst::String128 name) override {
        return Steinberg::Vst::StringConvert::convert("Mixer", name) ? Steinberg::kResultTrue : Steinberg::kInternalError;
    }
};

}  // namespace

int wmain(int argc, wchar_t** argv) {
    InstallCrashHandlers();

    std::string error;
    auto args = ParseArgs(argc, argv, error);
    if (!args) {
        std::fprintf(stderr, "%s\n%s", error.c_str(), Usage());
        return 2;
    }

    // окна редакторов: свой масштаб на каждом мониторе (до создания любых окон)
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    // окна редакторов и многие плагины ждут STA и очередь сообщений на главном потоке
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

    static MixerHostApplication hostApp;
    Steinberg::Vst::PluginContextFactory::instance().setPluginContext(&hostApp);

    int code = 0;
    if (!args->scan.empty()) {
        code = Scan(args->scan);
    } else {
        Host host(*args);
        code = host.Run();
    }

    Steinberg::Vst::PluginContextFactory::instance().setPluginContext(nullptr);
    CoUninitialize();
    return code;
}
