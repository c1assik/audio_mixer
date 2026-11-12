#pragma once

#include <cstdint>
#include <optional>
#include <string>

namespace mixer::host {

struct Args {
    std::wstring channel;    // префикс имён аудиоканала: <channel>.shm, <channel>.req, <channel>.done
    std::wstring pipe;       // имя канала управления (без \\.\pipe\)
    uint32_t parentPid = 0;  // хост выходит, когда этот процесс завершился; он же — сервер канала управления

    // внедрение сбоев для проверки дедлайна: каждый N-й блок хост спит M мс
    uint32_t stallEvery = 0;
    uint32_t stallMs = 0;
};

// Разбирает командную строку. При ошибке возвращает nullopt и пишет причину в error.
std::optional<Args> ParseArgs(int argc, wchar_t** argv, std::string& error);

std::string ToUtf8(const std::wstring& s);
std::wstring FromUtf8(const std::string& s);

const char* Usage();

}  // namespace mixer::host
