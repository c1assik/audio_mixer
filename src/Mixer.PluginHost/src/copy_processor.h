#pragma once

#include <cstring>

#include "processor.h"

namespace mixer::host {

// Без плагина: выход = вход. Нужен, чтобы отделить стоимость обмена и память самого хоста от плагина.
class CopyProcessor final : public Processor {
public:
    bool Prepare(double, int32_t, std::string&) override { return true; }

    int32_t Process(float* const in[2], float* const out[2], int32_t frames) override {
        std::memcpy(out[0], in[0], size_t(frames) * sizeof(float));
        std::memcpy(out[1], in[1], size_t(frames) * sizeof(float));
        return 0;
    }
};

}  // namespace mixer::host
