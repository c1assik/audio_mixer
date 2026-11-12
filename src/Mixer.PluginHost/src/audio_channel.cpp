#include "audio_channel.h"

#include <algorithm>
#include <atomic>
#include <cstring>

namespace mixer::host {

AudioChannel::~AudioChannel() {
    if (request_) CloseHandle(request_);
    if (done_) CloseHandle(done_);
    if (view_) UnmapViewOfFile(view_);
    if (mapping_) CloseHandle(mapping_);
}

static std::string LastError(const char* what) {
    return std::string(what) + " failed, error " + std::to_string(GetLastError());
}

bool AudioChannel::Open(const std::wstring& prefix, std::string& error) {
    mapping_ = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, (prefix + L".shm").c_str());
    if (!mapping_) { error = LastError("OpenFileMapping"); return false; }
    view_ = MapViewOfFile(mapping_, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, 0);
    if (!view_) { error = LastError("MapViewOfFile"); return false; }
    header_ = static_cast<SharedHeader*>(view_);

    if (header_->magic != kMagic || header_->version != kProtocolVersion) {
        error = "shared section: wrong magic or protocol version";
        return false;
    }
    if (header_->channels != kChannels || header_->maxFrames == 0 || header_->maxFrames > 65536) {
        error = "shared section: bad channels or maxFrames";
        return false;
    }
    maxFrames_ = header_->maxFrames;

    MEMORY_BASIC_INFORMATION mbi{};
    if (!VirtualQuery(view_, &mbi, sizeof(mbi)) || mbi.RegionSize < SectionBytes(maxFrames_)) {
        error = "shared section is smaller than its header says";
        return false;
    }

    // все страницы буферов сразу, чтобы первые блоки не платили за страничные промахи в аудиопотоке
    std::memset(Out(0), 0, size_t(kChannels) * maxFrames_ * sizeof(float));
    volatile float sink = 0;
    for (size_t i = 0; i < size_t(kChannels) * maxFrames_; i += 4096 / sizeof(float)) sink = sink + In(0)[i];

    const DWORD access = EVENT_MODIFY_STATE | SYNCHRONIZE;
    request_ = OpenEventW(access, FALSE, (prefix + L".req").c_str());
    if (!request_) { error = LastError("OpenEvent(req)"); return false; }
    done_ = OpenEventW(access, FALSE, (prefix + L".done").c_str());
    if (!done_) { error = LastError("OpenEvent(done)"); return false; }
    return true;
}

void AudioChannel::Publish(HostState state, const std::string& error) {
    if (!header_) return;
    if (!error.empty()) {
        size_t n = std::min(error.size(), size_t(kErrorBytes - 1));
        std::memcpy(header_->error, error.data(), n);
        header_->error[n] = '\0';
    }
    std::atomic_ref<int32_t>(header_->state).store(state, std::memory_order_release);
}

}  // namespace mixer::host
