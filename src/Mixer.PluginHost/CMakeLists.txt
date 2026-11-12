cmake_minimum_required(VERSION 3.28)

# Mixer.PluginHost.exe — процесс, в котором живёт один плагин VST3 (см. «Микшер для Windows: поддержка VST»).
# Аудиоканал (общая память + пара событий), канал управления (именованный канал, JSON), окно редактора.
# Пример AGain из SDK собирается для замеров и интеграционных тестов.

project(MixerPluginHost VERSION 1.0.0 LANGUAGES CXX)

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "Mixer.PluginHost собирается только под x64")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# статический CRT: хост не должен требовать VC++ Redistributable на машине пользователя
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

option(MIXER_BUILD_AGAIN "Собрать пример AGain из SDK (замеры и тесты с настоящим плагином; нужен VSTGUI)" ON)

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

# --- nlohmann/json (MIT): тела сообщений канала управления --------------------------------------
FetchContent_Declare(nlohmann_json
    URL      https://github.com/nlohmann/json/releases/download/v3.11.3/json.tar.xz
    URL_HASH SHA256=d6c65aca6b1ed68e7a182f4757257b107ae403032760ed6ef121c9d55e81757d
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE
    EXCLUDE_FROM_ALL
)
FetchContent_MakeAvailable(nlohmann_json)

# --- части SDK для хоста, которых нет в sdk_hosting --------------------------------------------
# module_win32.cpp написан под C++17 (u8string() там — std::string), поэтому отдельной библиотекой.
add_library(mixer_sdk_hosting_win STATIC
    ${vst3sdk_SOURCE_DIR}/public.sdk/source/vst/hosting/plugprovider.cpp
    ${vst3sdk_SOURCE_DIR}/public.sdk/source/vst/hosting/module_win32.cpp
    ${vst3sdk_SOURCE_DIR}/public.sdk/source/common/memorystream.cpp
)
set_target_properties(mixer_sdk_hosting_win PROPERTIES CXX_STANDARD 17)
target_link_libraries(mixer_sdk_hosting_win PUBLIC sdk_hosting)
target_compile_definitions(mixer_sdk_hosting_win PRIVATE _SILENCE_CXX17_CODECVT_HEADER_DEPRECATION_WARNING)

# --- хост ---------------------------------------------------------------------------------------
add_executable(Mixer.PluginHost
    src/main.cpp
    src/args.cpp
    src/args.h
    src/base64.h
    src/shared_audio.h
    src/audio_channel.cpp
    src/audio_channel.h
    src/control_pipe.cpp
    src/control_pipe.h
    src/host.cpp
    src/host.h
    src/editor_window.cpp
    src/editor_window.h
    src/param_queue.h
    src/processor.h
    src/scan.cpp
    src/scan.h
    src/builtin_processors.cpp
    src/builtin_processors.h
    src/vst3_processor.cpp
    src/vst3_processor.h
)
target_link_libraries(Mixer.PluginHost PRIVATE mixer_sdk_hosting_win nlohmann_json::nlohmann_json avrt user32 ole32)
target_compile_definitions(Mixer.PluginHost PRIVATE
    UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN _CRT_SECURE_NO_WARNINGS
    MIXER_PLUGINHOST_VERSION="${PROJECT_VERSION}")
if(MSVC)
    target_compile_options(Mixer.PluginHost PRIVATE /W4 /permissive- /utf-8 /EHsc)
    # консольное: микшер запускает его без окна (CREATE_NO_WINDOW) и читает stderr в свой журнал
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

# --- тестовые модули для сканера: зависает, падает, «VST2» (build/test-modules) ------------------
foreach(_kind hang crash vst2)
    add_library(mixer_test_${_kind} SHARED test/test_module.cpp)
    string(TOUPPER ${_kind} _define)
    target_compile_definitions(mixer_test_${_kind} PRIVATE MIXER_TEST_${_define})
    if(_kind STREQUAL "vst2")
        set_target_properties(mixer_test_${_kind} PROPERTIES OUTPUT_NAME vst2 SUFFIX ".dll")
    else()
        set_target_properties(mixer_test_${_kind} PROPERTIES OUTPUT_NAME ${_kind} SUFFIX ".vst3")
    endif()
    set_target_properties(mixer_test_${_kind} PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/test-modules"
        RUNTIME_OUTPUT_DIRECTORY_DEBUG "${CMAKE_BINARY_DIR}/test-modules"
        RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/test-modules")
    add_dependencies(Mixer.PluginHost mixer_test_${_kind})
endforeach()
