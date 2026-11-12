#pragma once

#include <windows.h>

#include <atomic>
#include <deque>
#include <memory>
#include <optional>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include <nlohmann/json.hpp>

#include "args.h"
#include "audio_channel.h"
#include "control_pipe.h"
#include "editor_window.h"
#include "param_queue.h"
#include "processor.h"

namespace mixer::host {

// Всё, что живёт в процессе хоста: канал управления, аудиопоток, плагин и его окно.
//
// Потоки:
//  - главный (STA, очередь сообщений): все вызовы контроллера и окна плагина, все сообщения канала управления.
//    Сообщения приходят из потока чтения через PostMessage в скрытое окно, поэтому обрабатываются и тогда, когда
//    плагин крутит свой модальный цикл (диалог открытия файла, окно лицензии). ping отвечается всегда;
//    остальное, пришедшее во время вызова плагина, откладывается до его конца.
//  - поток чтения канала управления (ControlPipe).
//  - аудиопоток (MMCSS «Pro Audio»): Request → process → Done. Приостанавливается на время смены частоты,
//    задержки, шин и состояния: блоки в это время возвращаются с kDoneBypassed.
class Host final : public ProcessorEvents {
public:
    explicit Host(const Args& args);
    ~Host() override;

    int Run();

    // ProcessorEvents — из любого потока
    void OnEdit(uint32_t id, double value) override;
    void OnRestart(int32_t flags) override;
    void OnDirty() override;

private:
    enum class AudioCmd : int { Run, Suspend, Stop, Flush };

    static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp);
    LRESULT Proc(UINT msg, WPARAM wp, LPARAM lp);

    void OnControl(std::string&& text);
    void Dispatch(const nlohmann::json& msg);
    void DrainDeferred();
    void Send(const nlohmann::json& msg);
    void SendError(const nlohmann::json& req, const std::string& type, const std::string& message);

    void HandleLoad(const nlohmann::json& m);
    void HandleSetRate(const nlohmann::json& m);
    void HandleGetState(const nlohmann::json& m);
    void HandleSetState(const nlohmann::json& m);
    void HandleOpenEditor(const nlohmann::json& m);
    void HandleCloseEditor();
    void HandleSetParam(const nlohmann::json& m);
    void HandleSetEvenBlocks(const nlohmann::json& m);
    void HandleDebug(const nlohmann::json& m);
    void Quit();

    // перенастройка плагина при остановленном аудиопотоке: деактивация, setupProcessing, активация
    bool Reconfigure(double rate, bool even, std::string& error);
    bool PrepareProcessor(std::string& error);
    void PublishLayout();
    int32_t TotalLatency() const;
    nlohmann::json ParamsJson();
    nlohmann::json ParamJson(uint32_t id, double value);
    void ApplyEdit(uint32_t id, double value);
    void MarkDirty();
    void Tick();
    void HandleRestart();

    // аудиопоток
    void StartAudio();
    void Command(AudioCmd cmd);
    void AudioLoop();
    int32_t ProcessBlock(uint32_t frames);
    int32_t RunProcessor(float* const* in, float* const* out, int32_t frames);

    Args args_;
    HWND wnd_ = nullptr;
    HANDLE parent_ = nullptr;
    DWORD mainThread_ = 0;
    AudioChannel channel_;
    ControlPipe pipe_;

    std::unique_ptr<Processor> proc_;
    bool builtin_ = false;
    double rate_ = 48000;
    int32_t maxBlock_ = 0;
    bool even_ = false;
    int32_t inChannels_ = 2, outChannels_ = 2;

    // отложенные сообщения (пришли во время вызова плагина) и признак такого вызова
    int busy_ = 0;
    std::deque<std::string> deferred_;
    std::atomic<int32_t> pendingRestart_{0};

    // окно
    Steinberg::IPtr<EditorWindow> editor_;

    // параметры: главный → аудио и аудио → главный
    ParamQueue toAudio_{8192};
    ParamQueue fromAudio_{8192};
    std::unordered_set<uint32_t> readOnly_;
    std::unordered_map<uint32_t, double> outPending_;
    bool dirtyPending_ = false;
    ULONGLONG lastDirty_ = 0;

    // аудиопоток
    std::thread audio_;
    HANDLE cmdEvent_ = nullptr;
    HANDLE ackEvent_ = nullptr;
    std::atomic<int> cmd_{int(AudioCmd::Run)};
    std::atomic<bool> audioAlive_{false};  // аудиопоток ещё в цикле: иначе команды ждать некому
    std::vector<ParamChange> changes_;      // изменения к текущему блоку
    std::vector<float> mono_;               // (L+R)/2 для моно-плагина
    // «ровные блоки»: вход копится до kEvenFrames, выход отстаёт ровно на kEvenFrames
    static constexpr int32_t kEvenFrames = 256;
    std::vector<float> evenIn_[2], evenOut_[2], evenTmp_[2];
    int32_t evenInCount_ = 0, evenOutCount_ = 0;
};

}  // namespace mixer::host
