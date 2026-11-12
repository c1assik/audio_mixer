// Mixer.PluginHost.exe — один плагин в своём процессе. Этап 0: аудиоканал без канала управления;
// всё, что нужно для загрузки, приходит в командной строке, состояние — в заголовке общей памяти.

#include <windows.h>
#include <avrt.h>
#include <objbase.h>
#include <xmmintrin.h>

#include <atomic>
#include <cstdio>
#include <memory>
#include <thread>

#include "args.h"
#include "audio_channel.h"
#include "copy_processor.h"
#include "vst3_processor.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"

using namespace mixer::host;

namespace {

int64_t Qpc() {
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return t.QuadPart;
}

// Аудиоцикл: ждать Request, посчитать блок, ответить Done. Никаких аллокаций и блокировок,
// кроме ожидания события. Выход — флаг Stop от микшера или смерть родительского процесса.
void AudioLoop(AudioChannel& ch, Processor& proc, HANDLE parent, const Args& args, DWORD mainThreadId) {
    DWORD taskIndex = 0;
    HANDLE mmcss = AvSetMmThreadCharacteristicsW(L"Pro Audio", &taskIndex);
    // денормализованные числа в хвостах фильтров стоят в десятки раз дороже: FTZ + DAZ, как в DAW
    _mm_setcsr(_mm_getcsr() | 0x8040);

    proc.BeginProcessing();
    ch.Publish(kStateReady);

    SharedHeader* h = ch.Header();
    float* const in[2] = {ch.In(0), ch.In(1)};
    float* const out[2] = {ch.Out(0), ch.Out(1)};
    const uint32_t maxFrames = ch.MaxFrames();
    std::atomic_ref<uint64_t> requestSeq(h->requestSeq);
    std::atomic_ref<uint64_t> doneSeq(h->doneSeq);

    HANDLE waits[2] = {ch.RequestEvent(), parent};
    const DWORD waitCount = parent ? 2 : 1;
    uint64_t lastSeq = doneSeq.load(std::memory_order_relaxed);

    for (;;) {
        DWORD w = WaitForMultipleObjects(waitCount, waits, FALSE, INFINITE);
        if (w != WAIT_OBJECT_0) break;  // родитель умер или ожидание сломалось
        const int64_t wake = Qpc();

        const uint64_t seq = requestSeq.load(std::memory_order_acquire);
        if (h->flags & kFlagStop) break;
        if (seq == lastSeq) continue;  // лишний сигнал

        uint32_t frames = h->frames;
        if (frames > maxFrames) frames = maxFrames;

        if (args.stallEvery && seq % args.stallEvery == 0) Sleep(args.stallMs);

        const int64_t t0 = Qpc();
        const int32_t r = proc.Process(in, out, int32_t(frames));
        const int64_t t1 = Qpc();

        h->wakeQpc = wake;
        h->processTicks = t1 - t0;
        h->processResult = r;
        h->doneQpc = Qpc();
        doneSeq.store(seq, std::memory_order_release);
        SetEvent(ch.DoneEvent());
        lastSeq = seq;
    }

    proc.EndProcessing();
    ch.Publish(kStateStopped);
    if (mmcss) AvRevertMmThreadCharacteristics(mmcss);
    PostThreadMessageW(mainThreadId, WM_QUIT, 0, 0);
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    std::string error;
    auto args = ParseArgs(argc, argv, error);
    if (!args) {
        std::fprintf(stderr, "%s\n%s", error.c_str(), Usage());
        return 2;
    }

    // окна редакторов и многие плагины ждут STA и очередь сообщений на главном потоке
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    MSG msg;
    PeekMessageW(&msg, nullptr, WM_USER, WM_USER, PM_NOREMOVE);  // создать очередь до старта аудиопотока

    HANDLE parent = nullptr;
    if (args->parentPid) {
        parent = OpenProcess(SYNCHRONIZE, FALSE, args->parentPid);
        if (!parent) {
            std::fprintf(stderr, "parent process %lu not found\n", static_cast<unsigned long>(args->parentPid));
            return 3;
        }
    }

    AudioChannel channel;
    if (!channel.Open(args->channel, error)) {
        std::fprintf(stderr, "audio channel: %s\n", error.c_str());
        return 3;
    }
    SharedHeader* h = channel.Header();
    h->hostPid = GetCurrentProcessId();

    static Steinberg::Vst::HostApplication hostApp;
    Steinberg::Vst::PluginContextFactory::instance().setPluginContext(&hostApp);

    std::unique_ptr<Processor> proc;
    if (args->builtinCopy) {
        proc = std::make_unique<CopyProcessor>();
    } else {
        auto vst = std::make_unique<Vst3Processor>();
        if (!vst->Load(args->pluginPath, args->classId, error)) {
            channel.Publish(kStateFailed, error);
            std::fprintf(stderr, "load: %s\n", error.c_str());
            return 4;
        }
        proc = std::move(vst);
    }
    if (!proc->Prepare(double(h->sampleRate), int32_t(channel.MaxFrames()), error)) {
        channel.Publish(kStateFailed, error);
        std::fprintf(stderr, "prepare: %s\n", error.c_str());
        return 4;
    }
    h->latencySamples = proc->LatencySamples();
    h->pluginInChannels = proc->InChannels();
    h->pluginOutChannels = proc->OutChannels();

    std::thread audio(AudioLoop, std::ref(channel), std::ref(*proc), parent, std::cref(*args), GetCurrentThreadId());

    while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    audio.join();
    proc.reset();
    Steinberg::Vst::PluginContextFactory::instance().setPluginContext(nullptr);
    if (parent) CloseHandle(parent);
    CoUninitialize();
    return 0;
}
