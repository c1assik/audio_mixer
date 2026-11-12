#pragma once

#include <windows.h>

#include <string>

#include "shared_audio.h"

namespace mixer::host {

// Сторона хоста в аудиоканале: секцию и события создаёт микшер, хост их открывает по имени.
class AudioChannel {
public:
    AudioChannel() = default;
    AudioChannel(const AudioChannel&) = delete;
    AudioChannel& operator=(const AudioChannel&) = delete;
    ~AudioChannel();

    bool Open(const std::wstring& prefix, std::string& error);

    SharedHeader* Header() const { return header_; }
    uint32_t MaxFrames() const { return maxFrames_; }
    float* In(uint32_t ch) const { return InputChannel(view_, maxFrames_, ch); }
    float* Out(uint32_t ch) const { return OutputChannel(view_, maxFrames_, ch); }
    HANDLE RequestEvent() const { return request_; }
    HANDLE DoneEvent() const { return done_; }

    // Записать состояние хоста (и причину ошибки) в заголовок.
    void Publish(HostState state, const std::string& error = {});

private:
    HANDLE mapping_ = nullptr;
    void* view_ = nullptr;
    SharedHeader* header_ = nullptr;
    HANDLE request_ = nullptr;
    HANDLE done_ = nullptr;
    uint32_t maxFrames_ = 0;
};

}  // namespace mixer::host
