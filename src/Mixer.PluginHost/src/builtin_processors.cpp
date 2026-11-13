#include "builtin_processors.h"

#include <windows.h>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <limits>

#include "pluginterfaces/vst/ivsteditcontroller.h"

namespace mixer::host {

namespace {

std::string DoubleBytes(const double* v, size_t n) { return std::string(reinterpret_cast<const char*>(v), n * sizeof(double)); }

// Без плагина: выход = вход. Нужен, чтобы отделить стоимость обмена и память самого хоста от плагина.
class CopyProcessor final : public Processor {
public:
    std::string Name() const override { return "Copy"; }
    bool Prepare(double, int32_t, std::string&) override { return true; }
    int32_t Process(float* const* in, float* const* out, int32_t frames, const ParamChange*, int32_t,
                    ParamQueue&) override {
        std::memcpy(out[0], in[0], size_t(frames) * sizeof(float));
        std::memcpy(out[1], in[1], size_t(frames) * sizeof(float));
        return 0;
    }
};

// «Усилитель»: Gain (нормализованное × 2, по умолчанию 0,5 = ровно 1,0) и Bypass. Состояние компонента — два
// double, контроллера — строка, чтобы проверять обе части формата состояния.
class GainProcessor final : public Processor {
public:
    explicit GainProcessor(bool mono) : channels_(mono ? 1 : 2) {}

    std::string Name() const override { return channels_ == 1 ? "Gain (mono)" : "Gain"; }
    std::string Vendor() const override { return "Mixer"; }
    bool Prepare(double, int32_t, std::string&) override { return true; }
    int32_t InChannels() const override { return channels_; }
    int32_t OutChannels() const override { return channels_; }

    std::vector<ParamInfo> Params() override {
        ParamInfo gain;
        gain.id = 0;
        gain.title = "Gain";
        gain.units = "dB";
        gain.defaultValue = 0.5;
        gain.value = ui_[0];
        gain.text = ParamText(0, ui_[0]);
        ParamInfo bypass;
        bypass.id = 1;
        bypass.title = "Bypass";
        bypass.stepCount = 1;
        bypass.bypass = true;
        bypass.value = ui_[1];
        bypass.text = ParamText(1, ui_[1]);
        return {gain, bypass};
    }
    bool SetParam(uint32_t id, double value) override {
        if (id > 1) return false;
        ui_[id] = std::clamp(value, 0.0, 1.0);
        return true;
    }
    double ParamValue(uint32_t id) override { return id <= 1 ? ui_[id] : 0; }
    std::string ParamText(uint32_t id, double value) override {
        if (id == 1) return value >= 0.5 ? "On" : "Off";
        double g = value * 2;
        if (g <= 0) return "-inf";
        char buf[32];
        std::snprintf(buf, sizeof(buf), "%.1f", 20 * std::log10(g));
        return buf;
    }

    bool GetState(std::string& component, std::string& controller, std::string&) override {
        component = DoubleBytes(ui_, 2);
        controller = "gain-ui:" + std::to_string(++saves_);
        return true;
    }
    bool SetState(const std::string& component, const std::string& controller, std::string& error) override {
        if (component.size() != 2 * sizeof(double)) { error = "gain: bad component state"; return false; }
        std::memcpy(ui_, component.data(), component.size());
        // в процессор состояние уходит вместе с параметрами: хост перешлёт их на следующем блоке
        std::memcpy(dsp_, ui_, sizeof(dsp_));
        if (controller.rfind("gain-ui:", 0) == 0) saves_ = std::atoi(controller.c_str() + 8);
        return true;
    }

    int32_t Process(float* const* in, float* const* out, int32_t frames, const ParamChange* changes,
                    int32_t count, ParamQueue&) override {
        for (int32_t i = 0; i < count; ++i)
            if (changes[i].id <= 1) dsp_[changes[i].id] = changes[i].value;
        const float g = dsp_[1] >= 0.5 ? 1.0f : float(dsp_[0] * 2);
        for (int32_t c = 0; c < channels_; ++c)
            for (int32_t i = 0; i < frames; ++i) out[c][i] = in[c][i] * g;
        return 0;
    }

private:
    int32_t channels_;
    double ui_[2] = {0.5, 0};
    double dsp_[2] = {0.5, 0};
    int saves_ = 0;
};

// «Спит N мс» на каждом блоке: проверка дедлайна и автоотключения.
class SleepProcessor final : public Processor {
public:
    explicit SleepProcessor(int ms) : ms_(ms) {}
    std::string Name() const override { return "Sleep " + std::to_string(ms_) + " ms"; }
    bool Prepare(double, int32_t, std::string&) override { return true; }
    int32_t Process(float* const* in, float* const* out, int32_t frames, const ParamChange*, int32_t,
                    ParamQueue&) override {
        Sleep(DWORD(ms_));
        std::memcpy(out[0], in[0], size_t(frames) * sizeof(float));
        std::memcpy(out[1], in[1], size_t(frames) * sizeof(float));
        return 0;
    }

private:
    int ms_;
};

// «Падает через N блоков»: настоящее нарушение доступа в аудиопотоке.
class CrashProcessor final : public Processor {
public:
    explicit CrashProcessor(int after) : left_(after) {}
    std::string Name() const override { return "Crash"; }
    bool Prepare(double, int32_t, std::string&) override { return true; }
    int32_t Process(float* const* in, float* const* out, int32_t frames, const ParamChange*, int32_t,
                    ParamQueue&) override {
        if (left_-- <= 0) {
            volatile int* p = nullptr;
            *p = 1;
        }
        std::memcpy(out[0], in[0], size_t(frames) * sizeof(float));
        std::memcpy(out[1], in[1], size_t(frames) * sizeof(float));
        return 0;
    }

private:
    int left_;
};

// «Выдаёт NaN» после N блоков.
class NanProcessor final : public Processor {
public:
    explicit NanProcessor(int after) : left_(after) {}
    std::string Name() const override { return "NaN"; }
    bool Prepare(double, int32_t, std::string&) override { return true; }
    int32_t Process(float* const* in, float* const* out, int32_t frames, const ParamChange*, int32_t,
                    ParamQueue&) override {
        const bool nan = left_-- <= 0;
        for (int c = 0; c < 2; ++c)
            for (int32_t i = 0; i < frames; ++i)
                out[c][i] = nan ? std::numeric_limits<float>::quiet_NaN() : in[c][i];
        return 0;
    }

private:
    int left_;
};

// «Сообщает задержку N»: настоящая линия задержки на N сэмплов. Параметр Delay (нормализованное × 8192) меняет
// задержку так же, как это делают плагины: restartComponent(kLatencyChanged), новая задержка — после
// деактивации и активации.
class LatencyProcessor final : public Processor {
public:
    LatencyProcessor(int samples, bool restartOnLoad, ProcessorEvents& events)
        : events_(events), pending_(std::clamp(samples, 0, 8192)), restartOnLoad_(restartOnLoad), slowFirst_(restartOnLoad) {}

    std::string Name() const override { return "Latency"; }
    bool Prepare(double, int32_t, std::string&) override {
        latency_ = pending_;
        // как CHOWTapeModel и MAutoPitch: «задержка изменилась» сразу после активации, с той же задержкой
        if (restartOnLoad_) {
            if (prepared_++) Sleep(50);  // перенастройка не мгновенная: прогрев успевает застать хост приостановленным
            events_.OnRestart(Steinberg::Vst::kLatencyChanged);
        }
        for (auto& line : lines_) line.assign(size_t(latency_) + 1, 0.0f);
        pos_ = 0;
        return true;
    }
    int32_t LatencySamples() const override { return latency_; }

    std::vector<ParamInfo> Params() override {
        ParamInfo p;
        p.id = 0;
        p.title = "Delay";
        p.units = "smp";
        p.stepCount = 8192;
        p.value = pending_ / 8192.0;
        p.text = std::to_string(pending_);
        return {p};
    }
    bool SetParam(uint32_t id, double value) override {
        if (id != 0) return false;
        int samples = int(std::lround(std::clamp(value, 0.0, 1.0) * 8192));
        if (samples != pending_) {
            pending_ = samples;
            events_.OnRestart(Steinberg::Vst::kLatencyChanged);
        }
        return true;
    }
    double ParamValue(uint32_t) override { return pending_ / 8192.0; }
    std::string ParamText(uint32_t, double value) override { return std::to_string(std::lround(value * 8192)); }

    int32_t Process(float* const* in, float* const* out, int32_t frames, const ParamChange*, int32_t,
                    ParamQueue&) override {
        if (slowFirst_) {  // прогрев микшера должен застать хост приостановленным
            slowFirst_ = false;
            Sleep(20);
        }
        const size_t n = lines_[0].size();
        for (int32_t i = 0; i < frames; ++i) {
            for (int c = 0; c < 2; ++c) {
                lines_[c][pos_] = in[c][i];
                out[c][i] = lines_[c][(pos_ + 1) % n];
            }
            pos_ = (pos_ + 1) % n;
        }
        return 0;
    }

private:
    ProcessorEvents& events_;
    int pending_;
    bool restartOnLoad_, slowFirst_;
    int prepared_ = 0;
    int latency_ = 0;
    std::vector<float> lines_[2];
    size_t pos_ = 0;
};

}  // namespace

std::unique_ptr<Processor> MakeBuiltin(const std::string& kind, const BuiltinOptions& o, ProcessorEvents& events,
                                       std::string& error) {
    if (kind == "copy") return std::make_unique<CopyProcessor>();
    if (kind == "gain") return std::make_unique<GainProcessor>(o.mono);
    if (kind == "sleep") return std::make_unique<SleepProcessor>(o.sleepMs);
    if (kind == "crash") return std::make_unique<CrashProcessor>(o.after);
    if (kind == "nan") return std::make_unique<NanProcessor>(o.after);
    if (kind == "latency") return std::make_unique<LatencyProcessor>(o.latency, o.restartOnLoad, events);
    error = "unknown builtin: " + kind;
    return nullptr;
}

}  // namespace mixer::host
