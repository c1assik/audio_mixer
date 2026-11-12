#include "host.h"

#include <avrt.h>
#include <objbase.h>
#include <xmmintrin.h>

#include <algorithm>
#include <cstdio>
#include <cstring>

#include "base64.h"
#include "builtin_processors.h"
#include "vst3_processor.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"

using nlohmann::json;
using namespace Steinberg;

namespace mixer::host {

namespace {

constexpr UINT kMsgControl = WM_APP + 1;       // lp: std::string* — сообщение из канала управления
constexpr UINT kMsgPipeClosed = WM_APP + 2;
constexpr UINT kMsgEdit = WM_APP + 3;          // wp: id, lp: биты double — performEdit не из главного потока
constexpr UINT kMsgRestart = WM_APP + 4;
constexpr UINT kMsgDirty = WM_APP + 5;
constexpr UINT kMsgEditorClosed = WM_APP + 6;  // окно уже закрыто: отпустить объект
constexpr UINT_PTR kTickTimer = 1;
constexpr UINT kTickMs = 33;
constexpr ULONGLONG kDirtyThrottleMs = 250;

int64_t Qpc() {
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return t.QuadPart;
}

void Log(const std::string& text) {
    std::fprintf(stderr, "%s\n", text.c_str());
    std::fflush(stderr);
}

// Формат состояния: длина (uint32) и данные IComponent::getState, затем длина и данные IEditController::getState.
std::string PackState(const std::string& component, const std::string& controller) {
    std::string out;
    auto put = [&](const std::string& part) {
        const uint32_t n = uint32_t(part.size());
        out.append(reinterpret_cast<const char*>(&n), sizeof(n));
        out.append(part);
    };
    put(component);
    put(controller);
    return out;
}

bool UnpackState(const std::string& blob, std::string& component, std::string& controller) {
    size_t pos = 0;
    auto take = [&](std::string& part) {
        uint32_t n = 0;
        if (blob.size() - pos < sizeof(n)) return false;
        std::memcpy(&n, blob.data() + pos, sizeof(n));
        pos += sizeof(n);
        if (blob.size() - pos < n) return false;
        part.assign(blob, pos, n);
        pos += n;
        return true;
    };
    return take(component) && take(controller) && pos == blob.size();
}

void CopyReq(const json& from, json& to) {
    if (auto it = from.find("req"); it != from.end()) to["req"] = *it;
}

}  // namespace

Host::Host(const Args& args) : args_(args) {
    changes_.resize(8192);
}

Host::~Host() {
    editor_ = nullptr;
    if (audio_.joinable()) {
        Command(AudioCmd::Stop);
        audio_.join();
    }
    proc_.reset();
    if (cmdEvent_) CloseHandle(cmdEvent_);
    if (ackEvent_) CloseHandle(ackEvent_);
    if (parent_) CloseHandle(parent_);
    if (wnd_) DestroyWindow(wnd_);
}

int Host::Run() {
    mainThread_ = GetCurrentThreadId();

    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = &Host::WndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"MixerPluginHostControl";
    RegisterClassExW(&wc);
    wnd_ = CreateWindowExW(0, wc.lpszClassName, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, wc.hInstance, nullptr);
    if (!wnd_) {
        Log("cannot create the control window");
        return 3;
    }
    SetWindowLongPtrW(wnd_, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(this));

    if (args_.parentPid) {
        parent_ = OpenProcess(SYNCHRONIZE, FALSE, args_.parentPid);
        if (!parent_) {
            Log("parent process " + std::to_string(args_.parentPid) + " not found");
            return 3;
        }
    }

    std::string error;
    if (!channel_.Open(args_.channel, error)) {
        Log("audio channel: " + error);
        return 3;
    }
    channel_.Header()->hostPid = GetCurrentProcessId();
    rate_ = double(channel_.Header()->sampleRate);
    maxBlock_ = int32_t(channel_.MaxFrames());

    cmdEvent_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    ackEvent_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);

    if (!pipe_.Connect(args_.pipe, args_.parentPid, error)) {
        Log("control pipe: " + error);
        return 3;
    }
    HWND wnd = wnd_;
    pipe_.StartReader(
        parent_,
        [wnd](std::string&& text) {
            auto* p = new std::string(std::move(text));
            if (!PostMessageW(wnd, kMsgControl, 0, reinterpret_cast<LPARAM>(p))) delete p;
        },
        [wnd] {
            PostMessageW(wnd, kMsgPipeClosed, 0, 0);
            // микшер ушёл: если главный поток завис в плагине и не выйдет сам — через 5 с выходим силой
            std::thread([] {
                Sleep(5000);
                TerminateProcess(GetCurrentProcess(), 6);
            }).detach();
        });

    SetTimer(wnd_, kTickTimer, kTickMs, nullptr);

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    KillTimer(wnd_, kTickTimer);
    if (editor_) editor_->Close();
    editor_ = nullptr;
    if (audio_.joinable()) {
        Command(AudioCmd::Stop);
        audio_.join();
    }
    proc_.reset();
    return 0;
}

LRESULT CALLBACK Host::WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    auto* self = reinterpret_cast<Host*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (self) return self->Proc(msg, wp, lp);
    return DefWindowProcW(hwnd, msg, wp, lp);
}

LRESULT Host::Proc(UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
        case kMsgControl: {
            std::unique_ptr<std::string> text(reinterpret_cast<std::string*>(lp));
            OnControl(std::move(*text));
            return 0;
        }
        case kMsgPipeClosed:
            Log("control pipe closed");
            Quit();
            return 0;
        case kMsgEdit: {
            double v;
            std::memcpy(&v, &lp, sizeof(v));
            ApplyEdit(uint32_t(wp), v);
            return 0;
        }
        case kMsgRestart:
            if (busy_ == 0) HandleRestart();
            return 0;
        case kMsgDirty:
            MarkDirty();
            return 0;
        case kMsgEditorClosed:
            if (editor_ && !editor_->IsOpen()) editor_ = nullptr;
            return 0;
        case WM_TIMER:
            if (wp == kTickTimer && busy_ == 0) Tick();
            return 0;
        default:
            return DefWindowProcW(wnd_, msg, wp, lp);
    }
}

// --- канал управления ---------------------------------------------------------------------------

void Host::Send(const json& msg) {
    pipe_.Send(msg.dump(-1, ' ', false, json::error_handler_t::replace));
}

void Host::SendError(const json& req, const std::string& type, const std::string& message) {
    json out{{"type", type}, {"message", message}};
    CopyReq(req, out);
    Send(out);
}

void Host::OnControl(std::string&& text) {
    json msg = json::parse(text, nullptr, false);
    if (msg.is_discarded() || !msg.is_object() || !msg.contains("type") || !msg["type"].is_string()) {
        SendError(json::object(), "error", "bad message");
        return;
    }
    const std::string type = msg["type"].get<std::string>();
    if (type == "ping") {
        // отвечает главный поток: pong значит, что он крутит очередь сообщений (и модальные циклы плагина тоже)
        json pong{{"type", "pong"}};
        if (msg.contains("t")) pong["t"] = msg["t"];
        Send(pong);
        return;
    }
    if (busy_ > 0) {
        deferred_.push_back(std::move(text));
        return;
    }
    ++busy_;
    try {
        Dispatch(msg);
    } catch (const std::exception& e) {
        SendError(msg, "error", std::string("bad message: ") + e.what());
    }
    --busy_;
    DrainDeferred();
}

void Host::DrainDeferred() {
    if (busy_ > 0) return;
    while (!deferred_.empty()) {
        std::string next = std::move(deferred_.front());
        deferred_.pop_front();
        OnControl(std::move(next));
    }
    if (pendingRestart_.load() != 0) HandleRestart();
}

void Host::Dispatch(const json& m) {
    const std::string type = m["type"].get<std::string>();
    if (type == "load") HandleLoad(m);
    else if (type == "quit") Quit();
    else if (!proc_) SendError(m, "error", "no plug-in loaded");
    else if (type == "setRate") HandleSetRate(m);
    else if (type == "getState") HandleGetState(m);
    else if (type == "setState") HandleSetState(m);
    else if (type == "openEditor") HandleOpenEditor(m);
    else if (type == "closeEditor") HandleCloseEditor();
    else if (type == "setParam") HandleSetParam(m);
    else if (type == "setEvenBlocks") HandleSetEvenBlocks(m);
    else if (type == "getParams") {
        json out{{"type", "params"}, {"params", ParamsJson()}};
        CopyReq(m, out);
        Send(out);
    } else if (type == "debug") HandleDebug(m);
    else SendError(m, "error", "unknown message: " + type);
}

// --- загрузка -----------------------------------------------------------------------------------

void Host::HandleLoad(const json& m) {
    if (proc_) {
        SendError(m, "failed", "a plug-in is already loaded");
        return;
    }
    std::string error;
    rate_ = m.value("rate", rate_);
    maxBlock_ = std::clamp(m.value("maxBlock", maxBlock_), 1, int32_t(channel_.MaxFrames()));
    even_ = m.value("evenBlocks", false);

    std::unique_ptr<Processor> proc;
    if (m.contains("builtin")) {
        BuiltinOptions o;
        if (auto it = m.find("options"); it != m.end() && it->is_object()) {
            o.mono = it->value("mono", o.mono);
            o.sleepMs = it->value("sleepMs", o.sleepMs);
            o.after = it->value("after", o.after);
            o.latency = it->value("latency", o.latency);
        }
        proc = MakeBuiltin(m["builtin"].get<std::string>(), o, *this, error);
        builtin_ = true;
    } else {
        auto vst = std::make_unique<Vst3Processor>(*this);
        if (vst->Load(m.value("path", std::string()), m.value("classId", std::string()), error)) proc = std::move(vst);
    }
    if (proc && m.contains("state") && m["state"].is_string()) {
        std::string component, controller;
        auto blob = Base64Decode(m["state"].get<std::string>());
        if (!blob || !UnpackState(*blob, component, controller)) {
            error = "bad state";
            proc.reset();
        } else if (!proc->SetState(component, controller, error)) {
            error = "state not accepted: " + error;
            proc.reset();
        }
    }
    if (proc) {
        proc_ = std::move(proc);
        if (!PrepareProcessor(error)) proc_.reset();
    }
    if (!proc_) {
        channel_.Publish(kStateFailed, error);
        Log("load: " + error);
        SendError(m, "failed", error);
        return;
    }

    StartAudio();
    json ready{{"type", "ready"},
               {"name", proc_->Name()},
               {"vendor", proc_->Vendor()},
               {"version", proc_->Version()},
               {"latency", TotalLatency()},
               {"pluginLatency", proc_->LatencySamples()},
               {"inChannels", inChannels_},
               {"outChannels", outChannels_},
               {"evenBlocks", even_},
               {"hasEditor", proc_->HasEditor()},
               {"params", ParamsJson()}};
    if (auto* vst = dynamic_cast<Vst3Processor*>(proc_.get())) ready["classId"] = vst->ClassId();
    CopyReq(m, ready);
    Send(ready);
}

bool Host::PrepareProcessor(std::string& error) {
    const int32_t block = even_ ? kEvenFrames : maxBlock_;
    if (!proc_->Prepare(rate_, block, error)) return false;
    inChannels_ = proc_->InChannels();
    outChannels_ = proc_->OutChannels();
    mono_.assign(size_t(maxBlock_), 0.0f);
    for (int c = 0; c < 2; ++c) {
        evenIn_[c].assign(kEvenFrames, 0.0f);
        evenTmp_[c].assign(kEvenFrames, 0.0f);
        evenOut_[c].assign(size_t(2 * kEvenFrames + maxBlock_), 0.0f);
    }
    evenInCount_ = 0;
    evenOutCount_ = kEvenFrames;  // выход отстаёт на один ровный блок: столько тишины в начале
    PublishLayout();
    return true;
}

int32_t Host::TotalLatency() const {
    return (proc_ ? proc_->LatencySamples() : 0) + (even_ ? kEvenFrames : 0);
}

void Host::PublishLayout() {
    SharedHeader* h = channel_.Header();
    std::atomic_ref<int32_t>(h->latencySamples).store(TotalLatency(), std::memory_order_relaxed);
    h->pluginInChannels = inChannels_;
    h->pluginOutChannels = outChannels_;
}

bool Host::Reconfigure(double rate, bool even, std::string& error) {
    const bool running = audio_.joinable();
    if (running) Command(AudioCmd::Suspend);
    proc_->Unprepare();
    rate_ = rate;
    even_ = even;
    const bool ok = PrepareProcessor(error);
    if (ok && running) Command(AudioCmd::Run);
    if (!ok) Log("reconfigure: " + error);
    return ok;
}

// --- сообщения после загрузки -------------------------------------------------------------------

void Host::HandleSetRate(const json& m) {
    const double rate = m.at("rate").get<double>();
    std::string error;
    if (rate != rate_) {
        const int32_t before = TotalLatency();
        if (!Reconfigure(rate, even_, error)) {
            SendError(m, "error", "setRate: " + error);
            return;
        }
        if (TotalLatency() != before) Send({{"type", "latencyChanged"}, {"samples", TotalLatency()},
                                            {"inChannels", inChannels_}, {"outChannels", outChannels_}});
    }
    json out{{"type", "rateSet"}, {"rate", rate_}, {"latency", TotalLatency()}};
    CopyReq(m, out);
    Send(out);
}

void Host::HandleSetEvenBlocks(const json& m) {
    const bool on = m.at("on").get<bool>();
    if (on != even_) {
        std::string error;
        if (!Reconfigure(rate_, on, error)) {
            SendError(m, "error", "setEvenBlocks: " + error);
            return;
        }
    }
    json out{{"type", "latencyChanged"}, {"samples", TotalLatency()}, {"evenBlocks", even_},
             {"inChannels", inChannels_}, {"outChannels", outChannels_}};
    CopyReq(m, out);
    Send(out);
}

void Host::HandleGetState(const json& m) {
    // правки, которые ещё не дошли до процессора (узел молчит, блоков нет), должны попасть в состояние
    if (audio_.joinable()) Command(AudioCmd::Flush);
    std::string component, controller, error;
    if (!proc_->GetState(component, controller, error)) {
        SendError(m, "error", "getState: " + error);
        return;
    }
    json out{{"type", "state"}, {"blob", Base64Encode(PackState(component, controller))}};
    CopyReq(m, out);
    Send(out);
}

void Host::HandleSetState(const json& m) {
    std::string component, controller, error;
    auto blob = Base64Decode(m.at("blob").get<std::string>());
    if (!blob || !UnpackState(*blob, component, controller)) {
        SendError(m, "error", "setState: bad state");
        return;
    }
    // подмена состояния на ходу у многих плагинов щёлкает: блоки на это время идут сухими
    const bool running = audio_.joinable();
    if (running) Command(AudioCmd::Suspend);
    const bool ok = proc_->SetState(component, controller, error);
    if (running) Command(AudioCmd::Run);
    if (!ok) {
        SendError(m, "error", "setState: " + error);
        return;
    }
    json out{{"type", "stateSet"}, {"params", ParamsJson()}};
    CopyReq(m, out);
    Send(out);
}

void Host::HandleSetParam(const json& m) {
    const uint32_t id = m.at("id").get<uint32_t>();
    const double value = std::clamp(m.at("value").get<double>(), 0.0, 1.0);
    if (!proc_->SetParam(id, value)) {
        SendError(m, "error", "setParam: unknown parameter " + std::to_string(id));
        return;
    }
    toAudio_.Push(id, value);
    // эхо с текстом значения: строка параметра в панели микшера показывает его в единицах плагина
    Send(ParamJson(id, proc_->ParamValue(id)));
}

void Host::HandleOpenEditor(const json& m) {
    if (editor_ && editor_->IsOpen()) {
        editor_->BringToFront();
        const EditorRect r = editor_->Rect();
        Send({{"type", "editorOpened"}, {"x", r.x}, {"y", r.y}, {"w", r.w}, {"h", r.h}});
        return;
    }
    editor_ = nullptr;
    IPlugView* view = proc_->CreateEditor();
    if (!view) {
        Send({{"type", "editorFailed"}, {"message", "plug-in has no editor"}});
        return;
    }
    const HWND owner = reinterpret_cast<HWND>(static_cast<uintptr_t>(m.value("ownerHwnd", uint64_t(0))));
    const std::wstring title = FromUtf8(m.value("title", proc_->Name()));
    std::optional<EditorRect> rect;
    if (m.contains("x") && m.contains("y")) {
        EditorRect r;
        r.x = m["x"].get<int>();
        r.y = m["y"].get<int>();
        r.w = m.value("w", 0);
        r.h = m.value("h", 0);
        rect = r;
    }
    std::string error;
    HWND wnd = wnd_;
    editor_ = EditorWindow::Open(
        view, owner, title, rect,
        [this, wnd](const EditorRect& r) {
            Send({{"type", "editorClosed"}, {"x", r.x}, {"y", r.y}, {"w", r.w}, {"h", r.h}});
            // объект окна ещё на стеке своей оконной процедуры: отпустить позже
            PostMessageW(wnd, kMsgEditorClosed, 0, 0);
        },
        error);
    if (!editor_) {
        view->release();
        Send({{"type", "editorFailed"}, {"message", error}});
        return;
    }
    const EditorRect r = editor_->Rect();
    Send({{"type", "editorOpened"}, {"x", r.x}, {"y", r.y}, {"w", r.w}, {"h", r.h}});
}

void Host::HandleCloseEditor() {
    if (editor_) editor_->Close();
}

void Host::HandleDebug(const json& m) {
    // только для встроенных: имитация того, что делают настоящие плагины и что ломается у плохих
    if (!builtin_) {
        SendError(m, "error", "debug is only for builtin plug-ins");
        return;
    }
    const std::string op = m.value("op", std::string());
    if (op == "edit") {
        // правка «в окне плагина»: контроллер уже знает значение, хост получает performEdit
        const uint32_t id = m.at("id").get<uint32_t>();
        const double value = m.at("value").get<double>();
        proc_->SetParam(id, value);
        OnEdit(id, value);
    } else if (op == "freeze") {
        Sleep(DWORD(m.value("ms", 10000)));  // главный поток не крутит очередь: ping останется без ответа
    } else if (op == "dirty") {
        OnDirty();
    } else if (op == "restart") {
        OnRestart(m.value("flags", 0));
    } else {
        SendError(m, "error", "unknown debug op: " + op);
    }
}

void Host::Quit() {
    if (editor_) editor_->Close();
    editor_ = nullptr;
    if (audio_.joinable()) {
        Command(AudioCmd::Stop);
        audio_.join();
    }
    PostQuitMessage(0);
}

// --- параметры ----------------------------------------------------------------------------------

json Host::ParamJson(uint32_t id, double value) {
    return {{"type", "param"}, {"id", id}, {"value", value}, {"text", proc_->ParamText(id, value)}};
}

json Host::ParamsJson() {
    json list = json::array();
    readOnly_.clear();
    for (const ParamInfo& p : proc_->Params()) {
        if (p.readOnly) readOnly_.insert(p.id);
        list.push_back({{"id", p.id},
                        {"title", p.title},
                        {"units", p.units},
                        {"steps", p.stepCount},
                        {"default", p.defaultValue},
                        {"value", p.value},
                        {"text", p.text},
                        {"readOnly", p.readOnly},
                        {"hidden", p.hidden},
                        {"bypass", p.bypass},
                        {"list", p.list}});
    }
    return list;
}

void Host::OnEdit(uint32_t id, double value) {
    if (GetCurrentThreadId() != mainThread_) {
        LPARAM bits = 0;
        static_assert(sizeof(bits) == sizeof(value));
        std::memcpy(&bits, &value, sizeof(value));
        PostMessageW(wnd_, kMsgEdit, WPARAM(id), bits);
        return;
    }
    ApplyEdit(id, value);
}

void Host::ApplyEdit(uint32_t id, double value) {
    if (!proc_) return;
    toAudio_.Push(id, value);  // процессор узнаёт о правке в окне только от хоста
    Send(ParamJson(id, value));
    MarkDirty();
}

void Host::OnDirty() {
    if (GetCurrentThreadId() != mainThread_) {
        PostMessageW(wnd_, kMsgDirty, 0, 0);
        return;
    }
    MarkDirty();
}

void Host::MarkDirty() {
    const ULONGLONG now = GetTickCount64();
    if (now - lastDirty_ >= kDirtyThrottleMs) {
        lastDirty_ = now;
        dirtyPending_ = false;
        Send({{"type", "dirty"}});
    } else {
        dirtyPending_ = true;
    }
}

void Host::OnRestart(int32_t flags) {
    pendingRestart_.fetch_or(flags);
    PostMessageW(wnd_, kMsgRestart, 0, 0);
}

void Host::HandleRestart() {
    const int32_t flags = pendingRestart_.exchange(0);
    if (!flags || !proc_) return;
    using namespace Steinberg::Vst;
    if (flags & (kReloadComponent | kIoChanged | kLatencyChanged)) {
        ++busy_;
        std::string error;
        const bool ok = Reconfigure(rate_, even_, error);
        --busy_;
        // плагин, который зовёт restartComponent прямо из setActive, не должен зациклить перенастройку
        pendingRestart_.fetch_and(~int32_t(kReloadComponent | kIoChanged | kLatencyChanged));
        if (!ok) Send({{"type", "error"}, {"message", "restart: " + error}});
        Send({{"type", "latencyChanged"}, {"samples", TotalLatency()}, {"inChannels", inChannels_},
              {"outChannels", outChannels_}});
    }
    if (flags & (kParamValuesChanged | kParamTitlesChanged)) Send({{"type", "params"}, {"params", ParamsJson()}});
}

void Host::Tick() {
    if (!proc_) return;
    ParamChange c;
    while (fromAudio_.Pop(c)) {
        proc_->OnOutputParam(c.id, c.value);
        if (!readOnly_.count(c.id)) outPending_[c.id] = c.value;
    }
    for (const auto& [id, value] : outPending_) Send(ParamJson(id, value));
    outPending_.clear();
    if (dirtyPending_ && GetTickCount64() - lastDirty_ >= kDirtyThrottleMs) MarkDirty();
}

// --- аудиопоток ---------------------------------------------------------------------------------

void Host::StartAudio() {
    cmd_.store(int(AudioCmd::Run));
    audioAlive_.store(true);
    audio_ = std::thread([this] { AudioLoop(); });
    WaitForSingleObject(ackEvent_, INFINITE);  // BeginProcessing сделан, состояние Ready опубликовано
}

void Host::Command(AudioCmd cmd) {
    if (!audioAlive_.load()) return;
    cmd_.store(int(cmd));
    SetEvent(cmdEvent_);
    // если плагин завис в process, ждать бесполезно — но и продолжать нельзя: микшер заметит по ping
    WaitForSingleObject(ackEvent_, INFINITE);
}

void Host::AudioLoop() {
    DWORD taskIndex = 0;
    HANDLE mmcss = AvSetMmThreadCharacteristicsW(L"Pro Audio", &taskIndex);
    // денормализованные числа в хвостах фильтров стоят в десятки раз дороже: FTZ + DAZ, как в DAW
    _mm_setcsr(_mm_getcsr() | 0x8040);

    proc_->BeginProcessing();
    bool running = true;
    channel_.Publish(kStateReady);
    SetEvent(ackEvent_);

    SharedHeader* h = channel_.Header();
    std::atomic_ref<uint64_t> requestSeq(h->requestSeq);
    std::atomic_ref<uint64_t> doneSeq(h->doneSeq);

    // команда первой: при непрерывных Request она не должна ждать
    HANDLE waits[3] = {cmdEvent_, channel_.RequestEvent(), parent_};
    const DWORD waitCount = parent_ ? 3 : 2;
    uint64_t lastSeq = doneSeq.load(std::memory_order_relaxed);
    bool parentDied = false;

    for (;;) {
        const DWORD w = WaitForMultipleObjects(waitCount, waits, FALSE, INFINITE);
        if (w == WAIT_OBJECT_0) {
            const auto cmd = AudioCmd(cmd_.load());
            if (cmd == AudioCmd::Stop) break;
            if (cmd == AudioCmd::Suspend && running) {
                proc_->EndProcessing();
                running = false;
            } else if (cmd == AudioCmd::Run && !running) {
                proc_->BeginProcessing();
                running = true;
            } else if (cmd == AudioCmd::Flush && running) {
                // process с 0 сэмплами: плагин забирает накопленные изменения параметров (так в VST3 положено)
                float* const in[2] = {channel_.In(0), channel_.In(1)};
                float* const out[2] = {channel_.Out(0), channel_.Out(1)};
                RunProcessor(in, out, 0);
            }
            SetEvent(ackEvent_);
            continue;
        }
        if (w != WAIT_OBJECT_0 + 1) {  // родитель умер или ожидание сломалось
            parentDied = true;
            break;
        }
        const int64_t wake = Qpc();

        const uint64_t seq = requestSeq.load(std::memory_order_acquire);
        if (h->flags & kFlagStop) break;
        if (seq == lastSeq) continue;  // лишний сигнал

        uint32_t frames = h->frames;
        if (frames > channel_.MaxFrames()) frames = channel_.MaxFrames();

        int32_t result = 0;
        uint32_t doneFlags = 0;
        const int64_t t0 = Qpc();
        if (running && int32_t(frames) <= maxBlock_) {
            if (args_.stallEvery && seq % args_.stallEvery == 0) Sleep(args_.stallMs);
            result = ProcessBlock(frames);
        } else {
            doneFlags = kDoneBypassed;
        }
        const int64_t t1 = Qpc();

        h->wakeQpc = wake;
        h->processTicks = t1 - t0;
        h->processResult = result;
        h->doneFlags = doneFlags;
        h->doneQpc = Qpc();
        doneSeq.store(seq, std::memory_order_release);
        SetEvent(channel_.DoneEvent());
        lastSeq = seq;
    }

    if (running) proc_->EndProcessing();
    channel_.Publish(kStateStopped);
    if (mmcss) AvRevertMmThreadCharacteristics(mmcss);
    audioAlive_.store(false);
    SetEvent(ackEvent_);
    // родитель умер, а главный поток этого ещё не знает: разбудить его
    if (parentDied) PostMessageW(wnd_, kMsgPipeClosed, 0, 0);
}

int32_t Host::RunProcessor(float* const* in, float* const* out, int32_t frames) {
    // изменения параметров к этому блоку: всё, что накопилось в очереди
    int32_t count = 0;
    ParamChange c;
    while (count < int32_t(changes_.size()) && toAudio_.Pop(c)) changes_[size_t(count++)] = c;

    float* pin[2] = {in[0], in[1]};
    float* pout[2] = {out[0], out[1]};
    if (inChannels_ == 1) {
        float* m = mono_.data();
        for (int32_t i = 0; i < frames; ++i) m[i] = 0.5f * (in[0][i] + in[1][i]);
        pin[0] = m;
    }
    const int32_t r = proc_->Process(pin, pout, frames, changes_.data(), count, fromAudio_);
    if (outChannels_ == 1) std::memcpy(out[1], out[0], size_t(frames) * sizeof(float));
    return r;
}

int32_t Host::ProcessBlock(uint32_t frames) {
    float* const in[2] = {channel_.In(0), channel_.In(1)};
    float* const out[2] = {channel_.Out(0), channel_.Out(1)};
    if (!even_) return RunProcessor(in, out, int32_t(frames));
    int32_t result = 0;
    // ровные блоки: копим вход по kEvenFrames, выход отдаём из очереди, которая на kEvenFrames позади
    float* const evIn[2] = {evenIn_[0].data(), evenIn_[1].data()};
    float* const evTmp[2] = {evenTmp_[0].data(), evenTmp_[1].data()};
    int32_t pos = 0;
    while (pos < int32_t(frames)) {
        const int32_t take = std::min(kEvenFrames - evenInCount_, int32_t(frames) - pos);
        for (int c = 0; c < 2; ++c) std::memcpy(evIn[c] + evenInCount_, in[c] + pos, size_t(take) * sizeof(float));
        evenInCount_ += take;
        pos += take;
        if (evenInCount_ == kEvenFrames) {
            result = RunProcessor(evIn, evTmp, kEvenFrames);
            for (int c = 0; c < 2; ++c)
                std::memcpy(evenOut_[c].data() + evenOutCount_, evTmp[c], size_t(kEvenFrames) * sizeof(float));
            evenOutCount_ += kEvenFrames;
            evenInCount_ = 0;
        }
    }
    // evenInCount_ + evenOutCount_ == kEvenFrames + frames, evenInCount_ < kEvenFrames ⇒ хватает
    for (int c = 0; c < 2; ++c) {
        std::memcpy(out[c], evenOut_[c].data(), size_t(frames) * sizeof(float));
        std::memmove(evenOut_[c].data(), evenOut_[c].data() + frames,
                     size_t(evenOutCount_ - int32_t(frames)) * sizeof(float));
    }
    evenOutCount_ -= int32_t(frames);
    return result;
}

}  // namespace mixer::host
