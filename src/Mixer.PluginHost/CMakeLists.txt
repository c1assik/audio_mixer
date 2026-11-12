cmake_minimum_required(VERSION 3.28)

# Mixer.PluginHost.exe — процесс, в котором живёт один плагин VST3 (см. «Микшер для Windows: поддержка VST»).
# Этап 0: прототип аудиоканала (общая память + пара событий) и пример AGain из SDK для замеров.

project(MixerPluginHost VERSION 0.1.0 LANGUAGES CXX)

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "Mixer.PluginHost собирается только под x64")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# статический CRT: хост не должен требовать VC++ Redistributable на машине пользователя
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

option(MIXER_BUILD_AGAIN "Собрать пример AGain из SDK (замеры этапа 0; нужен VSTGUI)" ON)

# --- VST3 SDK 3.8 (MIT) -------------------------------------------------------------------------
# Свой SDK можно подложить через -DMIXER_VST3_SDK_DIR=..., иначе он скачивается с тега.
set(MIXER_VST3_SDK_TAG "v3.8.1_build_84" CACHE STRING "Тег vst3sdk")
set(MIXER_VST3_SDK_DIR "" CACHE PATH "Готовый каталог vst3sdk (вместо скачивания)")

# только то, что нужно: без примеров хостов, валидатора, ссылок в Common Files и moduleinfo
set(SMTG_ENABLE_VST3_HOSTING_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VST3_PLUGIN_EXAMPLES ${MIXER_BUILD_AGAIN} CACHE BOOL "" FORCE)
set(SMTG_RUN_VST_VALIDATOR OFF CACHE BOOL "" FORCE)
set(SMTG_CREATE_PLUGIN_LINK OFF CACHE BOOL "" FORCE)
set(SMTG_CREATE_MODULE_INFO OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VSTGUI_SUPPORT ${MIXER_BUILD_AGAIN} CACHE BOOL "" FORCE)
set(SMTG_USE_STATIC_CRT ON CACHE BOOL "" FORCE)

include(FetchContent)
if(MIXER_VST3_SDK_DIR)
    FetchContent_Declare(vst3sdk SOURCE_DIR "${MIXER_VST3_SDK_DIR}" EXCLUDE_FROM_ALL)
else()
    set(_mixer_sdk_modules base cmake pluginterfaces public.sdk)
    if(MIXER_BUILD_AGAIN)
        list(APPEND _mixer_sdk_modules vstgui4)
    endif()
    FetchContent_Declare(vst3sdk
        GIT_REPOSITORY https://github.com/steinbergmedia/vst3sdk.git
        GIT_TAG        ${MIXER_VST3_SDK_TAG}
        GIT_SHALLOW    TRUE
        GIT_SUBMODULES ${_mixer_sdk_modules}
        GIT_PROGRESS   TRUE
        EXCLUDE_FROM_ALL
    )
endif()
FetchContent_MakeAvailable(vst3sdk)

# --- части SDK для хоста, которых нет в sdk_hosting --------------------------------------------
# module_win32.cpp написан под C++17 (u8string() там — std::string), поэтому отдельной библиотекой.
add_library(mixer_sdk_hosting_win STATIC
    ${vst3sdk_SOURCE_DIR}/public.sdk/source/vst/hosting/plugprovider.cpp
    ${vst3sdk_SOURCE_DIR}/public.sdk/source/vst/hosting/module_win32.cpp
)
set_target_properties(mixer_sdk_hosting_win PROPERTIES CXX_STANDARD 17)
target_link_libraries(mixer_sdk_hosting_win PUBLIC sdk_hosting)
target_compile_definitions(mixer_sdk_hosting_win PRIVATE _SILENCE_CXX17_CODECVT_HEADER_DEPRECATION_WARNING)

# --- хост ---------------------------------------------------------------------------------------
add_executable(Mixer.PluginHost
    src/main.cpp
    src/args.cpp
    src/args.h
    src/shared_audio.h
    src/audio_channel.cpp
    src/audio_channel.h
    src/processor.h
    src/copy_processor.h
    src/vst3_processor.cpp
    src/vst3_processor.h
)
target_link_libraries(Mixer.PluginHost PRIVATE mixer_sdk_hosting_win avrt)
target_compile_definitions(Mixer.PluginHost PRIVATE
    UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN
    MIXER_PLUGINHOST_VERSION="${PROJECT_VERSION}")
if(MSVC)
    target_compile_options(Mixer.PluginHost PRIVATE /W4 /permissive- /utf-8 /EHsc)
    # консольное приложение: окна редактора появятся на этапе 1, сейчас нужен вывод в консоль
    target_link_options(Mixer.PluginHost PRIVATE /SUBSYSTEM:CONSOLE)
endif()
if(MIXER_BUILD_AGAIN)
    # пример из SDK собирается вместе с хостом; бандл: ${CMAKE_BINARY_DIR}/VST3/<Config>/again.vst3
    add_dependencies(Mixer.PluginHost again)
endif()
set_target_properties(Mixer.PluginHost PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    RUNTIME_OUTPUT_DIRECTORY_DEBUG "${CMAKE_BINARY_DIR}/bin"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/bin")
