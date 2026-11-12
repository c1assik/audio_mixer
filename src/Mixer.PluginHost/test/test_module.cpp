// Тестовые модули для сканера (C#-тесты, «Сканирование и каталог»): не настоящие плагины, только точки входа.
//   MIXER_TEST_HANG  — hang.vst3: GetPluginFactory не возвращается
//   MIXER_TEST_CRASH — crash.vst3: GetPluginFactory падает (access violation)
//   MIXER_TEST_VST2  — vst2.dll: только точка входа VST2

#include <windows.h>

#if defined(MIXER_TEST_VST2)
extern "C" __declspec(dllexport) void* VSTPluginMain(void*) { return nullptr; }
#else
extern "C" __declspec(dllexport) void* GetPluginFactory() {
#if defined(MIXER_TEST_HANG)
    for (;;) Sleep(1000);
#elif defined(MIXER_TEST_CRASH)
    *static_cast<volatile int*>(nullptr) = 1;
#endif
    return nullptr;
}
#endif
