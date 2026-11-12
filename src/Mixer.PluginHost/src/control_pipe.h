#pragma once

#include <windows.h>

#include <functional>
#include <mutex>
#include <string>
#include <thread>

namespace mixer::host {

// Канал управления, сторона хоста. Сервер (микшер) создаёт именованный канал с доступом только для текущего
// пользователя; хост подключается клиентом. Сообщение — длина (uint32, little-endian) и тело в UTF-8 (JSON).
// Чтение идёт в своём потоке; писать можно из любого (под мьютексом).
class ControlPipe {
public:
    static constexpr uint32_t kMaxMessage = 64u << 20;  // состояние плагина бывает в мегабайтах

    ControlPipe() = default;
    ControlPipe(const ControlPipe&) = delete;
    ControlPipe& operator=(const ControlPipe&) = delete;
    ~ControlPipe();

    // name — без \\.\pipe\. expectedServerPid — если не 0, сервер канала обязан быть этим процессом.
    bool Connect(const std::wstring& name, DWORD expectedServerPid, std::string& error);

    // Поток чтения: onMessage на каждое сообщение, onClosed один раз — канал закрыт, сломан или умер parent.
    void StartReader(HANDLE parent, std::function<void(std::string&&)> onMessage, std::function<void()> onClosed);

    bool Send(const std::string& body);

private:
    void ReadLoop(HANDLE parent);
    bool ReadExact(void* buf, DWORD bytes, HANDLE parent);

    HANDLE pipe_ = INVALID_HANDLE_VALUE;
    HANDLE readEvent_ = nullptr;
    HANDLE writeEvent_ = nullptr;
    HANDLE stopEvent_ = nullptr;
    std::mutex writeLock_;
    std::thread reader_;
    std::function<void(std::string&&)> onMessage_;
    std::function<void()> onClosed_;
};

}  // namespace mixer::host
