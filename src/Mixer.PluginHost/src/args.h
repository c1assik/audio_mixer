#pragma once

#include <cstdint>
#include <optional>
#include <string>

namespace mixer::host {

struct Args {
    std::wstring channel;    // префикс имён: <channel>.shm, <channel>.req, <channel>.done
    uint32_t parentPid = 0;  // хост выходит, когда этот процесс завершился
    std::string pluginPath;  // UTF-8, путь к .vst3 (бандл или файл)
    std::string classId;     // GUID класса; пусто — первый аудиоэффект модуля
    bool builtinCopy = false;  // без плагина: копирует вход в выход (база для замеров)

    // внедрение сбоев для проверки дедлайна: каждый N-й блок хост спит M мс
    uint32_t stallEvery = 0;
    uint32_t stallMs = 0;
};

// Разбирает командную строку. При ошибке возвращает nullopt и пишет причину в error.
std::optional<Args> ParseArgs(int argc, wchar_t** argv, std::string& error);

std::string ToUtf8(const std::wstring& s);

const char* Usage();

}  // namespace mixer::host
