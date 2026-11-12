#pragma once

#include <memory>
#include <string>
#include <vector>

#include "processor.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivstcomponent.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "public.sdk/source/vst/hosting/eventlist.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"

namespace mixer::host {

class ComponentHandler;

// Один аудиоэффект VST3: модуль → фабрика → компонент и контроллер (через PlugProvider из SDK).
class Vst3Processor final : public Processor {
public:
    explicit Vst3Processor(ProcessorEvents& events);
    ~Vst3Processor() override;

    // Загрузить модуль и создать класс. classId пустой — первый класс категории «Audio Module Class».
    bool Load(const std::string& path, const std::string& classId, std::string& error);

    std::string Name() const override { return name_; }
    std::string Vendor() const override { return vendor_; }
    std::string Version() const override { return version_; }
    const std::string& ClassId() const { return classId_; }

    bool Prepare(double sampleRate, int32_t maxFrames, std::string& error) override;
    void Unprepare() override;
    int32_t LatencySamples() const override { return latency_; }
    int32_t InChannels() const override { return inChannels_; }
    int32_t OutChannels() const override { return outChannels_; }

    std::vector<ParamInfo> Params() override;
    bool SetParam(uint32_t id, double value) override;
    double ParamValue(uint32_t id) override;
    std::string ParamText(uint32_t id, double value) override;
    void OnOutputParam(uint32_t id, double value) override;

    bool GetState(std::string& component, std::string& controller, std::string& error) override;
    bool SetState(const std::string& component, const std::string& controller, std::string& error) override;

    bool HasEditor() override;
    Steinberg::IPlugView* CreateEditor() override;

    void BeginProcessing() override;
    void EndProcessing() override;
    int32_t Process(float* const* in, float* const* out, int32_t frames, const ParamChange* changes,
                    int32_t changeCount, ParamQueue& outChanges) override;

private:
    bool NegotiateBuses(std::string& error);
    void BuildBusBuffers(int32_t maxFrames);

    ProcessorEvents& events_;
    VST3::Hosting::Module::Ptr module_;
    Steinberg::IPtr<Steinberg::Vst::PlugProvider> provider_;
    Steinberg::IPtr<Steinberg::Vst::IComponent> component_;
    Steinberg::IPtr<Steinberg::Vst::IAudioProcessor> processor_;
    Steinberg::IPtr<Steinberg::Vst::IEditController> controller_;
    Steinberg::IPtr<ComponentHandler> handler_;
    std::string name_, vendor_, version_, classId_;

    // всё, что нужно process, выделяется в Prepare
    Steinberg::Vst::ProcessData data_{};
    Steinberg::Vst::ProcessContext context_{};
    Steinberg::Vst::EventList events_list_;
    Steinberg::Vst::ParameterChanges inParams_;
    Steinberg::Vst::ParameterChanges outParams_;
    std::vector<Steinberg::Vst::AudioBusBuffers> inBuses_, outBuses_;
    std::vector<std::vector<Steinberg::Vst::Sample32*>> inPtrs_, outPtrs_;
    std::vector<std::vector<float>> scratch_;  // каналы побочных шин (sidechain и прочие)

    int32_t inChannels_ = 2;
    int32_t outChannels_ = 2;
    int32_t latency_ = 0;
    int32_t hasEditor_ = -1;  // -1 — ещё не проверяли
    bool active_ = false;
    bool processing_ = false;
};

}  // namespace mixer::host
