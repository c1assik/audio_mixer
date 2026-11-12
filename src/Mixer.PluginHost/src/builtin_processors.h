// Встроенные «плагины» хоста: без DLL, для тестов C#-части и замеров (см. «План работ»: «хост со встроенными
// тестовыми плагинами»). Загружаются сообщением load { builtin: "<вид>", options: {…} }.
#pragma once

#include <memory>
#include <string>

#include "processor.h"

namespace mixer::host {

struct BuiltinOptions {
    bool mono = false;       // gain: моно-плагин (1 вход, 1 выход)
    int sleepMs = 50;        // sleep: столько спит каждый блок
    int after = 0;           // crash, nan: после стольких блоков
    int latency = 512;       // latency: задержка, сэмплов
};

// Виды: copy, gain, sleep, crash, nan, latency. nullptr — неизвестный вид, причина в error.
std::unique_ptr<Processor> MakeBuiltin(const std::string& kind, const BuiltinOptions& options,
                                       ProcessorEvents& events, std::string& error);

}  // namespace mixer::host
