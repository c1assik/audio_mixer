#pragma once

#include <atomic>
#include <cstdint>
#include <vector>

namespace mixer::host {

struct ParamChange {
    uint32_t id;
    double value;  // нормализованное, 0…1
};

#pragma warning(push)
#pragma warning(disable : 4324)  // выравнивание индексов по строкам кэша — нарочно

// Кольцо «один пишет, один читает» без блокировок. Ёмкость задаётся до начала работы.
// Переполнение — новое изменение теряется (Push возвращает false): для параметров это лучше, чем ждать.
class ParamQueue {
public:
    explicit ParamQueue(size_t capacity = 4096) : items_(capacity + 1) {}

    bool Push(uint32_t id, double value) {
        const size_t w = write_.load(std::memory_order_relaxed);
        const size_t next = (w + 1) % items_.size();
        if (next == read_.load(std::memory_order_acquire)) return false;
        items_[w] = {id, value};
        write_.store(next, std::memory_order_release);
        return true;
    }

    bool Pop(ParamChange& out) {
        const size_t r = read_.load(std::memory_order_relaxed);
        if (r == write_.load(std::memory_order_acquire)) return false;
        out = items_[r];
        read_.store((r + 1) % items_.size(), std::memory_order_release);
        return true;
    }

private:
    std::vector<ParamChange> items_;
    alignas(64) std::atomic<size_t> write_{0};
    alignas(64) std::atomic<size_t> read_{0};
};

#pragma warning(pop)

}  // namespace mixer::host
