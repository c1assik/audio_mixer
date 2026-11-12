#pragma once

#include <memory>
#include <string>
#include <vector>

#include "processor.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivstcomponent.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "public.sdk/source/vst/hosting/eventlist.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"

namespace mixer::host {

// Один аудиоэффект VST3: модуль → фабрика → компонент и контроллер (через PlugProvider из SDK).
class Vst3Processor final : public Processor {
public:
    ~Vst3Processor() override;

    // Загрузить модуль и создать класс. classId пустой — первый класс категории «Audio Module Class».
    bool Load(const std::string& path, const std::string& classId, std::string& error);

    bool Prepare(double sampleRate, int32_t maxFrames, std::string& error) override;
    void BeginProcessing() override;
    void EndProcessing() override;
    int32_t Process(float* const in[2], float* const out[2], int32_t frames) override;

    int32_t LatencySamples() const override { return latency_; }
    int32_t InChannels() const override { return inChannels_; }
    int32_t OutChannels() const override { return outChannels_; }

    const std::string& Name() const { return name_; }

private:
    bool NegotiateBuses(std::string& error);
    void BuildBusBuffers(int32_t maxFrames);

    VST3::Hosting::Module::Ptr module_;
    Steinberg::IPtr<Steinberg::Vst::PlugProvider> provider_;
    Steinberg::IPtr<Steinberg::Vst::IComponent> component_;
    Steinberg::IPtr<Steinberg::Vst::IAudioProcessor> processor_;
    std::string name_;

    // всё, что нужно process, выделяется в Prepare
    Steinberg::Vst::ProcessData data_{};
    Steinberg::Vst::ProcessContext context_{};
    Steinberg::Vst::EventList events_;
    Steinberg::Vst::ParameterChanges inParams_;
    Steinberg::Vst::ParameterChanges outParams_;
    std::vector<Steinberg::Vst::AudioBusBuffers> inBuses_, outBuses_;
    std::vector<std::vector<Steinberg::Vst::Sample32*>> inPtrs_, outPtrs_;
    std::vector<std::vector<float>> scratch_;  // каналы побочных шин (sidechain и прочие)
    std::vector<float> mono_;                  // (L+R)/2 для моно-плагина

    int32_t inChannels_ = 2;
    int32_t outChannels_ = 2;
    int32_t latency_ = 0;
    bool active_ = false;
    bool processing_ = false;
};

}  // namespace mixer::host
