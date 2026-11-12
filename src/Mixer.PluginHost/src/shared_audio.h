// Разметка общей памяти аудиоканала «микшер ↔ хост». Зеркало на C#: tools/Mixer.PluginBench/SharedLayout.cs.
// Любое изменение — повысить kProtocolVersion и поправить обе стороны.
//
// Секция: заголовок 4096 байт, затем вход и выход float32, планарно: in[ch][maxFrames], out[ch][maxFrames].
// Поля, которые пишет одна сторона на каждом блоке, лежат в разных строках кэша, чтобы не мешать друг другу.
//
// Шаг блока:
//   микшер: пишет вход, frames, requestQpc, затем requestSeq (release) → SetEvent(Request)
//   хост:   просыпается, читает requestSeq (acquire), считает, пишет выход и время, затем doneSeq (release) → SetEvent(Done)
//   микшер: ждёт doneSeq == requestSeq до дедлайна; иначе блок идёт в обход, поздний ответ узнаётся по номеру.
#pragma once

#include <cstddef>
#include <cstdint>

namespace mixer::host {

constexpr uint32_t kMagic = 0x4850584D;  // 'MXPH'
constexpr uint32_t kProtocolVersion = 1;
constexpr uint32_t kChannels = 2;
constexpr uint32_t kHeaderBytes = 4096;
constexpr uint32_t kErrorBytes = 1024;

enum HostState : int32_t {
    kStateStarting = 0,  // хост запущен, плагин грузится
    kStateReady = 1,     // плагин активирован, можно слать блоки
    kStateFailed = 2,    // не загрузился; причина в error
    kStateStopped = 3,   // хост вышел из аудиоцикла
};

enum RequestFlags : uint32_t {
    kFlagStop = 1u << 0,  // выйти из аудиоцикла (вместе с SetEvent(Request))
};

struct SharedHeader {
    // --- строка 0: пишет микшер один раз, до запуска хоста ---
    uint32_t magic;
    uint32_t version;
    uint32_t sampleRate;
    uint32_t maxFrames;
    uint32_t channels;
    uint32_t reserved0[11];

    // --- строка 1: пишет хост один раз, при загрузке ---
    alignas(64) int32_t state;  // HostState
    int32_t latencySamples;
    int32_t pluginInChannels;   // 1 или 2: моно-плагин получает (L+R)/2, выход дублируется
    int32_t pluginOutChannels;
    uint32_t hostPid;
    uint32_t reserved1[11];

    // --- строка 2: пишет микшер на каждом блоке ---
    alignas(64) uint64_t requestSeq;
    int64_t requestQpc;  // QueryPerformanceCounter перед SetEvent(Request)
    uint32_t frames;
    uint32_t flags;      // RequestFlags
    uint32_t reserved2[10];

    // --- строка 3: пишет хост на каждом блоке ---
    alignas(64) uint64_t doneSeq;
    int64_t wakeQpc;       // хост проснулся по Request
    int64_t doneQpc;       // перед SetEvent(Done)
    int64_t processTicks;  // длительность IAudioProcessor::process, тики QPC
    int32_t processResult; // tresult последнего process
    uint32_t reserved3[7];

    // --- текст ошибки загрузки, UTF-8 ---
    alignas(64) char error[kErrorBytes];
};

static_assert(offsetof(SharedHeader, state) == 64);
static_assert(offsetof(SharedHeader, requestSeq) == 128);
static_assert(offsetof(SharedHeader, frames) == 144);
static_assert(offsetof(SharedHeader, doneSeq) == 192);
static_assert(offsetof(SharedHeader, processResult) == 224);
static_assert(offsetof(SharedHeader, error) == 256);
static_assert(sizeof(SharedHeader) <= kHeaderBytes);

constexpr size_t SectionBytes(uint32_t maxFrames) {
    return kHeaderBytes + size_t(2) * kChannels * maxFrames * sizeof(float);
}

inline float* InputChannel(void* section, uint32_t maxFrames, uint32_t ch) {
    return reinterpret_cast<float*>(static_cast<char*>(section) + kHeaderBytes) + size_t(ch) * maxFrames;
}

inline float* OutputChannel(void* section, uint32_t maxFrames, uint32_t ch) {
    return InputChannel(section, maxFrames, 0) + size_t(kChannels + ch) * maxFrames;
}

}  // namespace mixer::host
