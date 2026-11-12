// Режим --scan: загрузить модуль VST3, опросить фабрику и напечатать её классы одной строкой JSON в stdout.
// Микшер запускает его в отдельном короткоживущем процессе с таймаутом, так что зависший или падающий
// при загрузке плагин ломает только этот процесс.

#include "scan.h"

#include <windows.h>

#include <cstdio>

#include "args.h"
#include "vst3_processor.h"
#include "nlohmann/json.hpp"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"

namespace mixer::host {

using json = nlohmann::json;

int Scan(const std::wstring& path) {
    json out;
    std::string error;
    auto module = VST3::Hosting::Module::create(ResolveModulePath(ToUtf8(path)), error);
    if (!module) {
        out["error"] = error;
    } else {
        auto factory = module->getFactory();
        factory.setHostContext(Steinberg::Vst::PluginContextFactory::instance().getPluginContext());
        const std::string factoryVendor = factory.info().vendor();
        json classes = json::array();
        for (auto& c : factory.classInfos()) {
            classes.push_back({
                {"cid", c.ID().toString()},
                {"category", c.category()},
                {"name", c.name()},
                {"vendor", c.vendor().empty() ? factoryVendor : c.vendor()},
                {"version", c.version()},
                {"sdkVersion", c.sdkVersion()},
                {"subCategories", c.subCategories()},
            });
        }
        out["vendor"] = factoryVendor;
        out["classes"] = std::move(classes);
    }
    // старые плагины отдают имена не в UTF-8: заменяем такие байты, а не падаем
    const std::string text = out.dump(-1, ' ', false, json::error_handler_t::replace) + "\n";
    std::fwrite(text.data(), 1, text.size(), stdout);
    std::fflush(stdout);
    // без выгрузки модуля: ExitDll и деструкторы некоторых плагинов падают, а ответ уже напечатан
    TerminateProcess(GetCurrentProcess(), module ? 0 : 1);
    return 0;
}

}  // namespace mixer::host
