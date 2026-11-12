#pragma once

#include <string>

namespace mixer::host {

// --scan: напечатать описание модуля (классы фабрики) одной строкой JSON и выйти. Код 0 — модуль загрузился,
// 1 — нет (причина в поле error); падение плагина — код 5 из обработчика сбоев.
int Scan(const std::wstring& path);

}  // namespace mixer::host
