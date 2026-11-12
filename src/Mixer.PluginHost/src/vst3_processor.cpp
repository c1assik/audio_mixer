#include "vst3_processor.h"

#include <algorithm>
#include <cstring>

#include "pluginterfaces/base/funknownimpl.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/vstspeaker.h"

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace mixer::host {

Vst3Processor::~Vst3Processor() {
    if (processor_ && processing_) processor_->setProcessing(false);
    if (component_ && active_) component_->setActive(false);
    processor_ = nullptr;
    component_ = nullptr;
    provider_ = nullptr;  // terminate компонента и контроллера
    module_ = nullptr;    // выгрузка DLL
}

bool Vst3Processor::Load(const std::string& path, const std::string& classId, std::string& error) {
    std::string moduleError;
    module_ = VST3::Hosting::Module::create(path, moduleError);
    if (!module_) {
        error = "cannot load module: " + moduleError;
        return false;
    }

    VST3::Optional<VST3::UID> wanted;
    if (!classId.empty()) {
        wanted = VST3::UID::fromString(classId);
        if (!wanted) {
            error = "bad class id: " + classId;
            return false;
        }
    }

    auto factory = module_->getFactory();
    for (auto& info : factory.classInfos()) {
        if (info.category() != kVstAudioEffectClass) continue;
        if (wanted && *wanted != info.ID()) continue;
        provider_ = owned(new PlugProvider(factory, info, true));
        name_ = info.name();
        break;
    }
    if (!provider_) {
        error = wanted ? "no audio effect class " + classId + " in module" : "no audio effect class in module";
        return false;
    }
    if (!provider_->initialize()) {
        error = "plug-in failed to initialize";
        return false;
    }
    component_ = provider_->getComponentPtr();
    if (!component_) {
        error = "plug-in has no component";
        return false;
    }
    processor_ = U::cast<IAudioProcessor>(component_);
    if (!processor_) {
        error = "component is not an IAudioProcessor";
        return false;
    }
    return true;
}

static int32_t ChannelsOf(IAudioProcessor& p, BusDirection dir, int32 bus) {
    SpeakerArrangement arr = 0;
    if (p.getBusArrangement(dir, bus, arr) != kResultTrue) return -1;
    return SpeakerArr::getChannelCount(arr);
}

bool Vst3Processor::NegotiateBuses(std::string& error) {
    int32 numIn = component_->getBusCount(kAudio, kInput);
    int32 numOut = component_->getBusCount(kAudio, kOutput);
    if (numIn < 1 || numOut < 1) {
        error = "plug-in needs an audio input and an audio output";
        return false;
    }

    std::vector<SpeakerArrangement> ins(static_cast<size_t>(numIn));
    std::vector<SpeakerArrangement> outs(static_cast<size_t>(numOut));
    for (int32 i = 0; i < numIn; ++i) processor_->getBusArrangement(kInput, i, ins[size_t(i)]);
    for (int32 i = 0; i < numOut; ++i) processor_->getBusArrangement(kOutput, i, outs[size_t(i)]);

    // стерео, затем моно; что плагин примет первым, то и берём
    const SpeakerArrangement candidates[][2] = {
        {SpeakerArr::kStereo, SpeakerArr::kStereo},
        {SpeakerArr::kMono, SpeakerArr::kMono},
        {SpeakerArr::kMono, SpeakerArr::kStereo},
    };
    for (auto& c : candidates) {
        ins[0] = c[0];
        outs[0] = c[1];
        if (processor_->setBusArrangements(ins.data(), numIn, outs.data(), numOut) == kResultTrue) break;
    }

    // итог читаем у плагина: он мог отказать во всём и оставить своё
    inChannels_ = ChannelsOf(*processor_, kInput, 0);
    outChannels_ = ChannelsOf(*processor_, kOutput, 0);
    if (inChannels_ < 1 || inChannels_ > 2 || outChannels_ < 1 || outChannels_ > 2) {
        error = "unsupported bus layout: " + std::to_string(inChannels_) + " in, " +
                std::to_string(outChannels_) + " out";
        return false;
    }

    // главные шины включены, побочные (sidechain) выключены — их не поддерживаем
    for (int32 i = 0; i < numIn; ++i) component_->activateBus(kAudio, kInput, i, i == 0);
    for (int32 i = 0; i < numOut; ++i) component_->activateBus(kAudio, kOutput, i, i == 0);
    return true;
}

void Vst3Processor::BuildBusBuffers(int32_t maxFrames) {
    auto build = [&](BusDirection dir, std::vector<AudioBusBuffers>& buses,
                     std::vector<std::vector<Sample32*>>& ptrs) {
        int32 n = component_->getBusCount(kAudio, dir);
        buses.assign(size_t(n), AudioBusBuffers{});
        ptrs.assign(size_t(n), {});
        for (int32 b = 0; b < n; ++b) {
            int32 channels = std::max(0, ChannelsOf(*processor_, dir, b));
            ptrs[size_t(b)].assign(size_t(channels), nullptr);
            if (b > 0) {
                // побочные шины: свои нулевые буферы, чтобы плагин не читал и не писал мимо
                for (int32 c = 0; c < channels; ++c) {
                    scratch_.emplace_back(size_t(maxFrames), 0.0f);
                    ptrs[size_t(b)][size_t(c)] = scratch_.back().data();
                }
            }
            buses[size_t(b)].numChannels = channels;
            buses[size_t(b)].channelBuffers32 = ptrs[size_t(b)].data();
        }
    };
    scratch_.clear();
    scratch_.reserve(64);  // указатели на данные векторов не должны переезжать
    build(kInput, inBuses_, inPtrs_);
    build(kOutput, outBuses_, outPtrs_);
    mono_.assign(size_t(maxFrames), 0.0f);

    data_ = ProcessData{};
    data_.processMode = kRealtime;
    data_.symbolicSampleSize = kSample32;
    data_.numInputs = int32(inBuses_.size());
    data_.numOutputs = int32(outBuses_.size());
    data_.inputs = inBuses_.data();
    data_.outputs = outBuses_.data();
    data_.inputParameterChanges = &inParams_;
    data_.outputParameterChanges = &outParams_;
    data_.inputEvents = &events_;
    data_.processContext = &context_;
}

bool Vst3Processor::Prepare(double sampleRate, int32_t maxFrames, std::string& error) {
    if (processor_->canProcessSampleSize(kSample32) != kResultTrue) {
        error = "plug-in cannot process 32-bit float";
        return false;
    }
    if (!NegotiateBuses(error)) return false;

    ProcessSetup setup{kRealtime, kSample32, maxFrames, sampleRate};
    if (processor_->setupProcessing(setup) != kResultOk) {
        error = "setupProcessing failed";
        return false;
    }

    // очереди параметров — заранее, чтобы плагин не выделял память в аудиопотоке
    int32 paramCount = 0;
    if (auto controller = provider_->getControllerPtr()) paramCount = controller->getParameterCount();
    inParams_.setMaxParameters(std::max<int32>(paramCount, 1));
    outParams_.setMaxParameters(std::max<int32>(paramCount, 1));

    BuildBusBuffers(maxFrames);

    if (component_->setActive(true) != kResultOk) {
        error = "setActive(true) failed";
        return false;
    }
    active_ = true;
    latency_ = int32_t(processor_->getLatencySamples());

    context_ = ProcessContext{};
    context_.sampleRate = sampleRate;
    context_.state = ProcessContext::kContTimeValid;
    return true;
}

void Vst3Processor::BeginProcessing() {
    // setProcessing вызываем из аудиопотока: так делают большие хосты, и плагины на это рассчитывают.
    // Результат не проверяем: многие плагины отвечают kNotImplemented и при этом нормально считают.
    processor_->setProcessing(true);
    processing_ = true;
}

void Vst3Processor::EndProcessing() {
    if (processing_) processor_->setProcessing(false);
    processing_ = false;
}

int32_t Vst3Processor::Process(float* const in[2], float* const out[2], int32_t frames) {
    auto& inMain = inPtrs_[0];
    auto& outMain = outPtrs_[0];
    if (inChannels_ == 1) {
        float* m = mono_.data();
        for (int32_t i = 0; i < frames; ++i) m[i] = 0.5f * (in[0][i] + in[1][i]);
        inMain[0] = m;
    } else {
        inMain[0] = in[0];
        inMain[1] = in[1];
    }
    outMain[0] = out[0];
    if (outChannels_ == 2) outMain[1] = out[1];

    for (auto& b : inBuses_) b.silenceFlags = 0;
    for (auto& b : outBuses_) b.silenceFlags = 0;
    inParams_.clearQueue();
    outParams_.clearQueue();
    events_.clear();

    data_.numSamples = frames;
    tresult r = processor_->process(data_);

    if (outChannels_ == 1) std::memcpy(out[1], out[0], size_t(frames) * sizeof(float));
    context_.continousTimeSamples += frames;
    return int32_t(r);
}

}  // namespace mixer::host
