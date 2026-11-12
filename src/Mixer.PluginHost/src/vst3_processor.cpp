#include "vst3_processor.h"

#include <algorithm>
#include <cstring>
#include <filesystem>

#include "args.h"
#include "pluginterfaces/base/funknownimpl.h"
#include "pluginterfaces/gui/iplugview.h"
#include "pluginterfaces/vst/vstspeaker.h"
#include "public.sdk/source/common/memorystream.h"
#include "public.sdk/source/vst/utility/stringconvert.h"

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace mixer::host {

// IComponentHandler: так контроллер плагина сообщает хосту о правках в своём окне и о перезапуске.
class ComponentHandler final : public U::Implements<U::Directly<IComponentHandler, IComponentHandler2>> {
public:
    explicit ComponentHandler(ProcessorEvents& events) : events_(events) {}

    tresult PLUGIN_API beginEdit(ParamID) override { return kResultOk; }
    tresult PLUGIN_API performEdit(ParamID id, ParamValue value) override {
        events_.OnEdit(id, value);
        return kResultOk;
    }
    tresult PLUGIN_API endEdit(ParamID) override {
        events_.OnDirty();
        return kResultOk;
    }
    tresult PLUGIN_API restartComponent(int32 flags) override {
        events_.OnRestart(flags);
        return kResultOk;
    }

    tresult PLUGIN_API setDirty(TBool state) override {
        if (state) events_.OnDirty();
        return kResultOk;
    }
    tresult PLUGIN_API requestOpenEditor(FIDString) override { return kNotImplemented; }
    tresult PLUGIN_API startGroupEdit() override { return kResultOk; }
    tresult PLUGIN_API finishGroupEdit() override { return kResultOk; }

private:
    ProcessorEvents& events_;
};

std::string ResolveModulePath(const std::string& path) {
    namespace fs = std::filesystem;
    std::error_code ec;
    const fs::path bundle(FromUtf8(path));
    if (!fs::is_directory(bundle, ec)) return path;
    const fs::path arch = bundle / "Contents" / "x86_64-win";
    if (fs::exists(arch / bundle.filename(), ec)) return path;
    fs::path only;
    for (const auto& e : fs::directory_iterator(arch, ec)) {
        if (!e.is_regular_file(ec) || e.path().extension() != ".vst3") continue;
        if (!only.empty()) return path;  // несколько — не угадываем, пусть SDK скажет, чего не нашёл
        only = e.path();
    }
    return only.empty() ? path : ToUtf8(only.wstring());
}

Vst3Processor::Vst3Processor(ProcessorEvents& events) : events_(events) {}

Vst3Processor::~Vst3Processor() {
    if (processor_ && processing_) processor_->setProcessing(false);
    if (component_ && active_) component_->setActive(false);
    if (controller_) controller_->setComponentHandler(nullptr);
    controller_ = nullptr;
    processor_ = nullptr;
    component_ = nullptr;
    provider_ = nullptr;  // terminate компонента и контроллера
    module_ = nullptr;    // выгрузка DLL
}

bool Vst3Processor::Load(const std::string& path, const std::string& classId, std::string& error) {
    std::string moduleError;
    module_ = VST3::Hosting::Module::create(ResolveModulePath(path), moduleError);
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
        vendor_ = info.vendor().empty() ? factory.info().vendor() : info.vendor();
        version_ = info.version();
        classId_ = info.ID().toString();
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
    controller_ = provider_->getControllerPtr();
    if (controller_) {
        handler_ = owned(new ComponentHandler(events_));
        controller_->setComponentHandler(handler_);
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

    data_ = ProcessData{};
    data_.processMode = kRealtime;
    data_.symbolicSampleSize = kSample32;
    data_.numInputs = int32(inBuses_.size());
    data_.numOutputs = int32(outBuses_.size());
    data_.inputs = inBuses_.data();
    data_.outputs = outBuses_.data();
    data_.inputParameterChanges = &inParams_;
    data_.outputParameterChanges = &outParams_;
    data_.inputEvents = &events_list_;
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
    int32 paramCount = controller_ ? controller_->getParameterCount() : 0;
    inParams_.setMaxParameters(std::max<int32>(paramCount, 1));
    outParams_.setMaxParameters(std::max<int32>(paramCount, 1));

    BuildBusBuffers(maxFrames);

    if (component_->setActive(true) != kResultOk) {
        error = "setActive(true) failed";
        return false;
    }
    active_ = true;
    latency_ = int32_t(processor_->getLatencySamples());

    const auto keptTime = context_.continousTimeSamples;
    context_ = ProcessContext{};
    context_.sampleRate = sampleRate;
    context_.continousTimeSamples = keptTime;
    context_.state = ProcessContext::kContTimeValid;
    return true;
}

void Vst3Processor::Unprepare() {
    if (component_ && active_) component_->setActive(false);
    active_ = false;
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

int32_t Vst3Processor::Process(float* const* in, float* const* out, int32_t frames, const ParamChange* changes,
                               int32_t changeCount, ParamQueue& outChanges) {
    auto& inMain = inPtrs_[0];
    auto& outMain = outPtrs_[0];
    for (int32_t c = 0; c < inChannels_; ++c) inMain[size_t(c)] = in[c];
    for (int32_t c = 0; c < outChannels_; ++c) outMain[size_t(c)] = out[c];

    for (auto& b : inBuses_) b.silenceFlags = 0;
    for (auto& b : outBuses_) b.silenceFlags = 0;
    inParams_.clearQueue();
    outParams_.clearQueue();
    events_list_.clear();
    for (int32_t i = 0; i < changeCount; ++i) {
        int32 queueIndex = 0, pointIndex = 0;
        if (auto* q = inParams_.addParameterData(changes[i].id, queueIndex)) q->addPoint(0, changes[i].value, pointIndex);
    }

    data_.numSamples = frames;
    tresult r = processor_->process(data_);

    // что плагин выдал сам (индикаторы, зависимые параметры) — в контроллер, последнее значение блока
    for (int32 i = 0, n = outParams_.getParameterCount(); i < n; ++i) {
        auto* q = outParams_.getParameterData(i);
        if (!q || q->getPointCount() <= 0) continue;
        int32 offset = 0;
        double value = 0;
        if (q->getPoint(q->getPointCount() - 1, offset, value) == kResultTrue) outChanges.Push(q->getParameterId(), value);
    }
    context_.continousTimeSamples += frames;
    return int32_t(r);
}

// --- параметры ----------------------------------------------------------------------------------

std::vector<ParamInfo> Vst3Processor::Params() {
    std::vector<ParamInfo> list;
    if (!controller_) return list;
    const int32 n = controller_->getParameterCount();
    list.reserve(size_t(std::max<int32>(n, 0)));
    for (int32 i = 0; i < n; ++i) {
        ParameterInfo pi{};
        if (controller_->getParameterInfo(i, pi) != kResultOk) continue;
        ParamInfo p;
        p.id = pi.id;
        p.title = Steinberg::Vst::StringConvert::convert(pi.title);
        p.units = Steinberg::Vst::StringConvert::convert(pi.units);
        p.stepCount = pi.stepCount;
        p.defaultValue = pi.defaultNormalizedValue;
        p.readOnly = (pi.flags & ParameterInfo::kIsReadOnly) != 0;
        p.hidden = (pi.flags & ParameterInfo::kIsHidden) != 0;
        p.bypass = (pi.flags & ParameterInfo::kIsBypass) != 0;
        p.list = (pi.flags & ParameterInfo::kIsList) != 0;
        p.value = controller_->getParamNormalized(pi.id);
        p.text = ParamText(pi.id, p.value);
        list.push_back(std::move(p));
    }
    return list;
}

bool Vst3Processor::SetParam(uint32_t id, double value) {
    if (!controller_) return false;
    return controller_->setParamNormalized(id, std::clamp(value, 0.0, 1.0)) == kResultOk;
}

double Vst3Processor::ParamValue(uint32_t id) { return controller_ ? controller_->getParamNormalized(id) : 0; }

std::string Vst3Processor::ParamText(uint32_t id, double value) {
    if (!controller_) return {};
    String128 text{};
    if (controller_->getParamStringByValue(id, value, text) != kResultOk) return {};
    return Steinberg::Vst::StringConvert::convert(text);
}

void Vst3Processor::OnOutputParam(uint32_t id, double value) {
    if (controller_) controller_->setParamNormalized(id, value);
}

// --- состояние ----------------------------------------------------------------------------------

bool Vst3Processor::GetState(std::string& component, std::string& controller, std::string& error) {
    MemoryStream cs;
    if (component_->getState(&cs) != kResultOk) {
        error = "IComponent::getState failed";
        return false;
    }
    component.assign(cs.getData() ? cs.getData() : "", size_t(cs.getSize()));
    controller.clear();
    if (controller_) {
        MemoryStream es;
        // kNotImplemented — у контроллера нет своего состояния, это не ошибка
        if (controller_->getState(&es) == kResultOk && es.getData())
            controller.assign(es.getData(), size_t(es.getSize()));
    }
    return true;
}

bool Vst3Processor::SetState(const std::string& component, const std::string& controller, std::string& error) {
    {
        MemoryStream cs(const_cast<char*>(component.data()), TSize(component.size()));
        if (component_->setState(&cs) != kResultOk) {
            error = "IComponent::setState failed";
            return false;
        }
    }
    if (controller_) {
        MemoryStream cs(const_cast<char*>(component.data()), TSize(component.size()));
        controller_->setComponentState(&cs);
        if (!controller.empty()) {
            MemoryStream es(const_cast<char*>(controller.data()), TSize(controller.size()));
            controller_->setState(&es);
        }
    }
    return true;
}

// --- окно ---------------------------------------------------------------------------------------

bool Vst3Processor::HasEditor() {
    if (hasEditor_ < 0) {
        hasEditor_ = 0;
        if (IPlugView* view = CreateEditor()) {
            hasEditor_ = 1;
            view->release();
        }
    }
    return hasEditor_ == 1;
}

IPlugView* Vst3Processor::CreateEditor() {
    if (!controller_) return nullptr;
    IPlugView* view = controller_->createView(ViewType::kEditor);
    if (!view) return nullptr;
    if (view->isPlatformTypeSupported(kPlatformTypeHWND) != kResultTrue) {
        view->release();
        return nullptr;
    }
    return view;
}

}  // namespace mixer::host
