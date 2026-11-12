#include "args.h"

#include <windows.h>

#include <cwchar>

namespace mixer::host {

std::string ToUtf8(const std::wstring& s) {
    if (s.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, s.data(), int(s.size()), nullptr, 0, nullptr, nullptr);
    std::string out(size_t(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, s.data(), int(s.size()), out.data(), n, nullptr, nullptr);
    return out;
}

const char* Usage() {
    return "Mixer.PluginHost " MIXER_PLUGINHOST_VERSION "\n"
           "usage: Mixer.PluginHost --channel <name> --parent <pid> (--plugin <path.vst3> [--class <guid>] | --builtin copy)\n"
           "                        [--stall-every <blocks> --stall-ms <ms>]\n";
}

static bool ParseUInt(const wchar_t* s, uint32_t& out) {
    wchar_t* end = nullptr;
    unsigned long v = std::wcstoul(s, &end, 10);
    if (end == s || *end != L'\0') return false;
    out = uint32_t(v);
    return true;
}

std::optional<Args> ParseArgs(int argc, wchar_t** argv, std::string& error) {
    Args a;
    for (int i = 1; i < argc; ++i) {
        std::wstring key = argv[i];
        auto value = [&]() -> const wchar_t* {
            if (i + 1 >= argc) return nullptr;
            return argv[++i];
        };
        const wchar_t* v = nullptr;
        if (key == L"--channel" && (v = value())) {
            a.channel = v;
        } else if (key == L"--parent" && (v = value())) {
            if (!ParseUInt(v, a.parentPid)) { error = "bad --parent"; return std::nullopt; }
        } else if (key == L"--plugin" && (v = value())) {
            a.pluginPath = ToUtf8(v);
        } else if (key == L"--class" && (v = value())) {
            a.classId = ToUtf8(v);
        } else if (key == L"--builtin" && (v = value())) {
            if (std::wstring(v) != L"copy") { error = "unknown --builtin"; return std::nullopt; }
            a.builtinCopy = true;
        } else if (key == L"--stall-every" && (v = value())) {
            if (!ParseUInt(v, a.stallEvery)) { error = "bad --stall-every"; return std::nullopt; }
        } else if (key == L"--stall-ms" && (v = value())) {
            if (!ParseUInt(v, a.stallMs)) { error = "bad --stall-ms"; return std::nullopt; }
        } else {
            error = "unknown or incomplete argument: " + ToUtf8(key);
            return std::nullopt;
        }
    }
    if (a.channel.empty()) { error = "--channel is required"; return std::nullopt; }
    if (a.pluginPath.empty() == !a.builtinCopy) { error = "exactly one of --plugin or --builtin is required"; return std::nullopt; }
    return a;
}

}  // namespace mixer::host
