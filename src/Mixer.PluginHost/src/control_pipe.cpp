#include "control_pipe.h"

namespace mixer::host {

ControlPipe::~ControlPipe() {
    if (stopEvent_) SetEvent(stopEvent_);
    if (reader_.joinable()) reader_.join();
    if (pipe_ != INVALID_HANDLE_VALUE) CloseHandle(pipe_);
    if (readEvent_) CloseHandle(readEvent_);
    if (writeEvent_) CloseHandle(writeEvent_);
    if (stopEvent_) CloseHandle(stopEvent_);
}

bool ControlPipe::Connect(const std::wstring& name, DWORD expectedServerPid, std::string& error) {
    const std::wstring path = L"\\\\.\\pipe\\" + name;
    for (int attempt = 0;; ++attempt) {
        pipe_ = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                            FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
        if (pipe_ != INVALID_HANDLE_VALUE) break;
        const DWORD e = GetLastError();
        // сервер занят другим подключением или ещё не начал ждать — недолго подождать
        if ((e == ERROR_PIPE_BUSY || e == ERROR_FILE_NOT_FOUND) && attempt < 50) {
            if (e == ERROR_PIPE_BUSY) WaitNamedPipeW(path.c_str(), 100);
            else Sleep(20);
            continue;
        }
        error = "cannot open control pipe, error " + std::to_string(e);
        return false;
    }
    if (expectedServerPid) {
        ULONG serverPid = 0;
        if (!GetNamedPipeServerProcessId(pipe_, &serverPid) || serverPid != expectedServerPid) {
            error = "control pipe belongs to another process";
            return false;
        }
    }
    DWORD mode = PIPE_READMODE_BYTE;
    SetNamedPipeHandleState(pipe_, &mode, nullptr, nullptr);
    readEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    writeEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    stopEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!readEvent_ || !writeEvent_ || !stopEvent_) {
        error = "CreateEvent failed";
        return false;
    }
    return true;
}

void ControlPipe::StartReader(HANDLE parent, std::function<void(std::string&&)> onMessage,
                              std::function<void()> onClosed) {
    onMessage_ = std::move(onMessage);
    onClosed_ = std::move(onClosed);
    reader_ = std::thread([this, parent] { ReadLoop(parent); });
}

bool ControlPipe::ReadExact(void* buf, DWORD bytes, HANDLE parent) {
    auto* p = static_cast<char*>(buf);
    while (bytes > 0) {
        OVERLAPPED ov{};
        ov.hEvent = readEvent_;
        ResetEvent(readEvent_);
        DWORD got = 0;
        if (!ReadFile(pipe_, p, bytes, &got, &ov)) {
            if (GetLastError() != ERROR_IO_PENDING) return false;
            HANDLE waits[3] = {readEvent_, stopEvent_, parent};
            const DWORD n = parent ? 3 : 2;
            const DWORD w = WaitForMultipleObjects(n, waits, FALSE, INFINITE);
            if (w != WAIT_OBJECT_0) {
                CancelIoEx(pipe_, &ov);
                GetOverlappedResult(pipe_, &ov, &got, TRUE);
                return false;
            }
            if (!GetOverlappedResult(pipe_, &ov, &got, FALSE)) return false;
        }
        if (got == 0) return false;
        p += got;
        bytes -= got;
    }
    return true;
}

void ControlPipe::ReadLoop(HANDLE parent) {
    for (;;) {
        uint32_t len = 0;
        if (!ReadExact(&len, sizeof(len), parent)) break;
        if (len > kMaxMessage) break;
        std::string body(len, '\0');
        if (len && !ReadExact(body.data(), len, parent)) break;
        onMessage_(std::move(body));
    }
    if (onClosed_) onClosed_();
}

bool ControlPipe::Send(const std::string& body) {
    std::lock_guard lock(writeLock_);
    if (pipe_ == INVALID_HANDLE_VALUE || body.size() > kMaxMessage) return false;
    std::string frame(sizeof(uint32_t) + body.size(), '\0');
    const uint32_t len = uint32_t(body.size());
    memcpy(frame.data(), &len, sizeof(len));
    memcpy(frame.data() + sizeof(len), body.data(), body.size());
    OVERLAPPED ov{};
    ov.hEvent = writeEvent_;
    ResetEvent(writeEvent_);
    DWORD written = 0;
    if (!WriteFile(pipe_, frame.data(), DWORD(frame.size()), &written, &ov)) {
        if (GetLastError() != ERROR_IO_PENDING) return false;
        if (!GetOverlappedResult(pipe_, &ov, &written, TRUE)) return false;
    }
    return written == frame.size();
}

}  // namespace mixer::host
