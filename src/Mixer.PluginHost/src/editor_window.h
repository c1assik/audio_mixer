#pragma once

#include <windows.h>

#include <functional>
#include <optional>
#include <string>

#include "pluginterfaces/base/funknownimpl.h"
#include "pluginterfaces/gui/iplugview.h"
#include "pluginterfaces/gui/iplugviewcontentscalesupport.h"

namespace mixer::host {

struct EditorRect {
    int x = 0, y = 0;  // левый верхний угол окна, экранные пиксели
    int w = 0, h = 0;  // размер содержимого (окна плагина), пиксели
};

// Окно редактора: верхнее окно хоста, в котором живёт IPlugView плагина. Владелец — главное окно микшера
// (другой процесс): сворачивается вместе с ним и всегда над ним. Процесс хоста — Per-Monitor V2.
class EditorWindow final : public Steinberg::U::Implements<Steinberg::U::Directly<Steinberg::IPlugFrame>> {
public:
    using ClosedFn = std::function<void(const EditorRect&)>;

    // view — новая ссылка (окно её забирает). rect — запомненные положение и размер (размер — только если
    // плагин умеет менять размер). nullptr — причина в error.
    static Steinberg::IPtr<EditorWindow> Open(Steinberg::IPlugView* view, HWND owner, const std::wstring& title,
                                              const std::optional<EditorRect>& rect, ClosedFn onClosed,
                                              std::string& error);

    // Закрыть программно (closeEditor, выход). onClosed вызывается и здесь.
    void Close();
    void BringToFront();
    bool IsOpen() const { return hwnd_ != nullptr; }
    EditorRect Rect() const;

    // IPlugFrame
    Steinberg::tresult PLUGIN_API resizeView(Steinberg::IPlugView* view, Steinberg::ViewRect* newSize) override;

    ~EditorWindow() override;

private:
    EditorWindow() = default;
    static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp);
    LRESULT Proc(UINT msg, WPARAM wp, LPARAM lp);
    void SetClientSize(int w, int h);
    void ApplyScale();
    SIZE ViewSize() const;

    HWND hwnd_ = nullptr;
    Steinberg::IPlugView* view_ = nullptr;
    ClosedFn onClosed_;
    bool resizable_ = false;
    bool inResize_ = false;
    bool closing_ = false;
};

}  // namespace mixer::host
