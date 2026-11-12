#pragma once

#include <cstdint>
#include <string>

namespace mixer::host {

// То, что хост вызывает на каждом блоке. Порядок: Prepare (главный поток) → BeginProcessing,
// Process…, EndProcessing (аудиопоток) → деструктор (главный поток).
class Processor {
public:
    virtual ~Processor() = default;

    // Согласовать шины и частоту, активировать. false — причина в error.
    virtual bool Prepare(double sampleRate, int32_t maxFrames, std::string& error) = 0;

    virtual void BeginProcessing() {}
    virtual void EndProcessing() {}

    // in/out — по два канала; для моно-плагина хост сам сводит вход и дублирует выход.
    // Возвращает tresult (0 — успех).
    virtual int32_t Process(float* const in[2], float* const out[2], int32_t frames) = 0;

    virtual int32_t LatencySamples() const { return 0; }
    virtual int32_t InChannels() const { return 2; }
    virtual int32_t OutChannels() const { return 2; }
};

}  // namespace mixer::host
