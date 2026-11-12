#include "editor_window.h"

#include <algorithm>

using namespace Steinberg;

namespace mixer::host {

namespace {

constexpr wchar_t kClassName[] = L"MixerPluginEditor";

void RegisterClassOnce() {
    static bool done = false;
    if (done) return;
    done = true;
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.style = CS_DBLCLKS;
    wc.lpfnWndProc = DefWindowProcW;  // настоящая процедура ставится после создания
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
    wc.hbrBackground = nullptr;
    wc.lpszClassName = kClassName;
    RegisterClassExW(&wc);
}

bool Same(const ViewRect& a, const ViewRect& b) {
    return a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom;
}

DWORD StyleFor(bool resizable) {
    DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;
    if (resizable) style |= WS_THICKFRAME | WS_MAXIMIZEBOX;
    return style;
}

}  // namespace

IPtr<EditorWindow> EditorWindow::Open(IPlugView* view, HWND owner, const std::wstring& title,
                                      const std::optional<EditorRect>& rect, ClosedFn onClosed, std::string& error) {
    RegisterClassOnce();
    IPtr<EditorWindow> w = owned(new EditorWindow());
    w->view_ = view;
    w->onClosed_ = std::move(onClosed);
    w->resizable_ = view->canResize() == kResultTrue;

    const DWORD style = StyleFor(w->resizable_);
    SIZE size = w->ViewSize();
    w->hwnd_ = CreateWindowExW(0, kClassName, title.c_str(), style, CW_USEDEFAULT, CW_USEDEFAULT,
                               std::max<LONG>(size.cx, 100), std::max<LONG>(size.cy, 50), nullptr, nullptr,
                               GetModuleHandleW(nullptr), nullptr);
    if (!w->hwnd_) {
        error = "CreateWindowEx failed, error " + std::to_string(GetLastError());
        w->view_ = nullptr;  // вызывающий освободит view сам
        return nullptr;
    }
    SetWindowLongPtrW(w->hwnd_, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(w.get()));
    SetWindowLongPtrW(w->hwnd_, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&EditorWindow::WndProc));
    // владелец из другого процесса: окно над ним и сворачивается вместе с ним
    if (owner && IsWindow(owner)) SetWindowLongPtrW(w->hwnd_, GWLP_HWNDPARENT, reinterpret_cast<LONG_PTR>(owner));

    view->setFrame(w.get());
    w->ApplyScale();  // плагин может ответить resizeView: размер окна поправится там
    if (view->attached(w->hwnd_, kPlatformTypeHWND) != kResultOk) {
        view->setFrame(nullptr);
        DestroyWindow(w->hwnd_);
        w->hwnd_ = nullptr;
        w->view_ = nullptr;
        error = "IPlugView::attached failed";
        return nullptr;
    }

    // запомненный размер — если плагин умеет его менять и согласен с ним
    size = w->ViewSize();
    if (rect && w->resizable_ && rect->w > 0 && rect->h > 0) {
        ViewRect r(0, 0, rect->w, rect->h);
        if (view->checkSizeConstraint(&r) == kResultTrue || r.getWidth() > 0) {
            if (view->onSize(&r) == kResultOk) size = {r.getWidth(), r.getHeight()};
        }
    }
    w->SetClientSize(size.cx, size.cy);

    // положение: запомненное, если оно на каком-нибудь мониторе, иначе по центру окна микшера
    RECT wr{};
    GetWindowRect(w->hwnd_, &wr);
    const int ww = wr.right - wr.left, wh = wr.bottom - wr.top;
    int x = 0, y = 0;
    bool placed = false;
    if (rect) {
        RECT probe{rect->x, rect->y, rect->x + std::min(ww, 200), rect->y + 40};
        if (MonitorFromRect(&probe, MONITOR_DEFAULTTONULL)) {
            x = rect->x;
            y = rect->y;
            placed = true;
        }
    }
    if (!placed) {
        RECT anchor{};
        if (!(owner && GetWindowRect(owner, &anchor) && !IsIconic(owner))) {
            MONITORINFO mi{sizeof(mi)};
            GetMonitorInfoW(MonitorFromWindow(w->hwnd_, MONITOR_DEFAULTTOPRIMARY), &mi);
            anchor = mi.rcWork;
        }
        x = (anchor.left + anchor.right - ww) / 2;
        y = (anchor.top + anchor.bottom - wh) / 2;
        MONITORINFO mi{sizeof(mi)};
        GetMonitorInfoW(MonitorFromRect(&anchor, MONITOR_DEFAULTTONEAREST), &mi);
        x = std::clamp(x, int(mi.rcWork.left), std::max(int(mi.rcWork.left), int(mi.rcWork.right) - ww));
        y = std::clamp(y, int(mi.rcWork.top), std::max(int(mi.rcWork.top), int(mi.rcWork.bottom) - wh));
    }
    SetWindowPos(w->hwnd_, nullptr, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    // монитор мог смениться вместе с положением: масштаб — ещё раз
    w->ApplyScale();
    ShowWindow(w->hwnd_, SW_SHOWNORMAL);
    w->BringToFront();
    return w;
}

EditorWindow::~EditorWindow() {
    if (hwnd_) {
        // окно ещё открыто (хост выходит): без onClosed, вызывающий уже не слушает
        onClosed_ = nullptr;
        Close();
    }
}

SIZE EditorWindow::ViewSize() const {
    ViewRect r{};
    if (view_ && view_->getSize(&r) == kResultOk && r.getWidth() > 0 && r.getHeight() > 0)
        return {r.getWidth(), r.getHeight()};
    return {400, 300};
}

void EditorWindow::SetClientSize(int w, int h) {
    if (!hwnd_) return;
    RECT r{0, 0, w, h};
    const UINT dpi = GetDpiForWindow(hwnd_);
    AdjustWindowRectExForDpi(&r, DWORD(GetWindowLongPtrW(hwnd_, GWL_STYLE)), FALSE,
                             DWORD(GetWindowLongPtrW(hwnd_, GWL_EXSTYLE)), dpi);
    inResize_ = true;
    SetWindowPos(hwnd_, nullptr, 0, 0, r.right - r.left, r.bottom - r.top,
                 SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOCOPYBITS);
    inResize_ = false;
}

void EditorWindow::ApplyScale() {
    if (!view_ || !hwnd_) return;
    if (auto css = U::cast<IPlugViewContentScaleSupport>(view_))
        css->setContentScaleFactor(float(GetDpiForWindow(hwnd_)) / float(USER_DEFAULT_SCREEN_DPI));
}

void EditorWindow::BringToFront() {
    if (!hwnd_) return;
    if (IsIconic(hwnd_)) ShowWindow(hwnd_, SW_RESTORE);
    SetWindowPos(hwnd_, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
    // сработает, если микшер разрешил (AllowSetForegroundWindow) перед openEditor
    SetForegroundWindow(hwnd_);
}

EditorRect EditorWindow::Rect() const {
    EditorRect out;
    if (!hwnd_) return out;
    WINDOWPLACEMENT wp{sizeof(wp)};
    RECT wr{};
    if (GetWindowPlacement(hwnd_, &wp) && wp.showCmd != SW_SHOWNORMAL)
        wr = wp.rcNormalPosition;  // свёрнутое или развёрнутое — запоминаем обычное
    else
        GetWindowRect(hwnd_, &wr);
    out.x = wr.left;
    out.y = wr.top;
    SIZE s = ViewSize();
    out.w = s.cx;
    out.h = s.cy;
    return out;
}

void EditorWindow::Close() {
    if (!hwnd_ || closing_) return;
    closing_ = true;
    const EditorRect rect = Rect();
    if (view_) {
        view_->removed();
        view_->setFrame(nullptr);
        view_->release();
        view_ = nullptr;
    }
    HWND h = hwnd_;
    hwnd_ = nullptr;
    SetWindowLongPtrW(h, GWLP_USERDATA, 0);
    DestroyWindow(h);
    closing_ = false;
    if (auto fn = std::move(onClosed_)) fn(rect);
}

tresult PLUGIN_API EditorWindow::resizeView(IPlugView* view, ViewRect* newSize) {
    if (!newSize || view != view_) return kInvalidArgument;
    if (!hwnd_) return kInternalError;
    if (inResize_) return kResultFalse;
    ViewRect cur{};
    view_->getSize(&cur);
    if (Same(cur, *newSize)) return kResultTrue;
    SetClientSize(newSize->getWidth(), newSize->getHeight());
    view_->getSize(&cur);
    if (!Same(cur, *newSize)) view_->onSize(newSize);
    return kResultTrue;
}

LRESULT CALLBACK EditorWindow::WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    auto* self = reinterpret_cast<EditorWindow*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (self) return self->Proc(msg, wp, lp);
    return DefWindowProcW(hwnd, msg, wp, lp);
}

LRESULT EditorWindow::Proc(UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
        case WM_ERASEBKGND:
            return TRUE;  // рисует плагин
        case WM_SIZE:
            // размер, который задали мы сами (resizeView, смена DPI), плагину уже известен
            if (view_ && !inResize_ && wp != SIZE_MINIMIZED) {
                RECT cr{};
                GetClientRect(hwnd_, &cr);
                ViewRect r(0, 0, cr.right - cr.left, cr.bottom - cr.top);
                ViewRect cur{};
                if (view_->getSize(&cur) == kResultOk && !Same(cur, r)) view_->onSize(&r);
            }
            return 0;
        case WM_SIZING:
            if (view_ && resizable_) {
                auto* edge = reinterpret_cast<RECT*>(lp);
                RECT wr{}, cr{};
                GetWindowRect(hwnd_, &wr);
                GetClientRect(hwnd_, &cr);
                const int frameW = (wr.right - wr.left) - (cr.right - cr.left);
                const int frameH = (wr.bottom - wr.top) - (cr.bottom - cr.top);
                ViewRect want(0, 0, (edge->right - edge->left) - frameW, (edge->bottom - edge->top) - frameH);
                if (view_->checkSizeConstraint(&want) == kResultTrue) {
                    const int w = want.getWidth() + frameW, h = want.getHeight() + frameH;
                    if (wp == WMSZ_LEFT || wp == WMSZ_TOPLEFT || wp == WMSZ_BOTTOMLEFT) edge->left = edge->right - w;
                    else edge->right = edge->left + w;
                    if (wp == WMSZ_TOP || wp == WMSZ_TOPLEFT || wp == WMSZ_TOPRIGHT) edge->top = edge->bottom - h;
                    else edge->bottom = edge->top + h;
                }
                return TRUE;
            }
            break;
        case WM_DPICHANGED: {
            // новый масштаб плагину; размер — какой он выберет сам, положение — что предложила система
            const auto* suggested = reinterpret_cast<const RECT*>(lp);
            inResize_ = true;
            SetWindowPos(hwnd_, nullptr, suggested->left, suggested->top, 0, 0,
                         SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            inResize_ = false;
            ApplyScale();
            SIZE s = ViewSize();
            SetClientSize(s.cx, s.cy);
            return 0;
        }
        case WM_GETDPISCALEDSIZE:
            return FALSE;  // размер после смены DPI задаёт плагин (см. WM_DPICHANGED)
        case WM_CLOSE:
            Close();
            return 0;
        default:
            break;
    }
    return DefWindowProcW(hwnd_, msg, wp, lp);
}

}  // namespace mixer::host
