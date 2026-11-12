#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "param_queue.h"

namespace Steinberg { class IPlugView; }

namespace mixer::host {

// Описание параметра для списка в панели микшера и для зеркала изменений.
struct ParamInfo {
    uint32_t id = 0;
    std::string title;
    std::string units;
    int32_t stepCount = 0;  // 0 — непрерывный, 1 — переключатель, N — N+1 значений
    double defaultValue = 0;
    double value = 0;       // текущее, нормализованное
    std::string text;       // текущее значение строкой (getParamStringByValue)
    bool readOnly = false;
    bool hidden = false;
    bool bypass = false;    // kIsBypass: обход силами самого плагина
    bool list = false;      // kIsList: значения — пункты списка
};

// Обратные вызовы плагина (IComponentHandler и т. п.). Могут прийти из любого потока: хост сам переносит их
// в главный поток.
class ProcessorEvents {
public:
    virtual ~ProcessorEvents() = default;
    virtual void OnEdit(uint32_t id, double value) = 0;  // performEdit: правка в окне плагина
    virtual void OnRestart(int32_t flags) = 0;           // restartComponent (флаги RestartFlags из VST3)
    virtual void OnDirty() = 0;                          // setDirty, beginEdit…endEdit
};

// Один плагин. Главный поток: всё, кроме BeginProcessing / Process / EndProcessing (аудиопоток).
// Порядок: Prepare → (аудиопоток: BeginProcessing, Process…, EndProcessing) → Unprepare → Prepare … → деструктор.
class Processor {
public:
    virtual ~Processor() = default;

    virtual std::string Name() const = 0;
    virtual std::string Vendor() const { return {}; }
    virtual std::string Version() const { return {}; }

    // Согласовать шины и частоту, активировать. false — причина в error.
    virtual bool Prepare(double sampleRate, int32_t maxFrames, std::string& error) = 0;
    // Деактивировать (перед новой частотой, сменой шин или задержки).
    virtual void Unprepare() {}

    virtual int32_t LatencySamples() const { return 0; }
    virtual int32_t InChannels() const { return 2; }
    virtual int32_t OutChannels() const { return 2; }

    // --- параметры (сторона контроллера) ---
    virtual std::vector<ParamInfo> Params() { return {}; }
    // Значение от микшера: в контроллер. В процессор его доставит хост через очередь. false — нет такого.
    virtual bool SetParam(uint32_t id, double value) { (void)id; (void)value; return false; }
    virtual double ParamValue(uint32_t id) { (void)id; return 0; }
    virtual std::string ParamText(uint32_t id, double value) { (void)id; (void)value; return {}; }
    // Изменение, которое процессор выдал сам (outputParameterChanges): в контроллер.
    virtual void OnOutputParam(uint32_t id, double value) { (void)id; (void)value; }

    // --- состояние: IComponent::getState и IEditController::getState ---
    virtual bool GetState(std::string& component, std::string& controller, std::string& error) {
        component.clear(); controller.clear(); (void)error; return true;
    }
    virtual bool SetState(const std::string& component, const std::string& controller, std::string& error) {
        (void)component; (void)controller; (void)error; return true;
    }

    // --- окно редактора ---
    virtual bool HasEditor() { return false; }
    // Новый IPlugView с одной ссылкой (вызывающий освобождает), или nullptr.
    virtual Steinberg::IPlugView* CreateEditor() { return nullptr; }

    // --- аудиопоток ---
    virtual void BeginProcessing() {}
    virtual void EndProcessing() {}
    // in — InChannels() каналов, out — OutChannels(). changes — изменения параметров к этому блоку.
    // Изменения, которые выдал сам плагин, — в outChanges. Возвращает tresult (0 — успех).
    virtual int32_t Process(float* const* in, float* const* out, int32_t frames,
                            const ParamChange* changes, int32_t changeCount, ParamQueue& outChanges) = 0;
};

}  // namespace mixer::host
