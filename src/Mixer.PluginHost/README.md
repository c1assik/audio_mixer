# Mixer.PluginHost

The process that hosts one VST3 plug-in for the mixer (see «Микшер для Windows: поддержка VST», section «Архитектура»). C++20, MSVC, CMake, VST3 SDK 3.8 (MIT).

**State: VST stage 0.** Only the audio channel exists. There is no control pipe yet: the host gets everything it needs on the command line and reports its state in the shared header. Measurements are in [tools/Mixer.PluginBench](../../tools/Mixer.PluginBench/README.md).

## Build

Requires Visual Studio 2022 (or Build Tools) with the C++ x64 tools, a Windows SDK and CMake:

```powershell
winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 --add Microsoft.VisualStudio.Component.Windows11SDK.22621 --add Microsoft.VisualStudio.Component.VC.CMake.Project"
./tools/build-pluginhost.ps1            # Release; -Config Debug; -NoAgain to skip the SDK example
```

Output: `build/bin/Mixer.PluginHost.exe` and the SDK example `build/VST3/Release/again.vst3`. CMake downloads the SDK from tag `v3.8.1_build_84` (only `base`, `cmake`, `pluginterfaces`, `public.sdk`, plus `vstgui4` for AGain). To use a local copy, pass `-DMIXER_VST3_SDK_DIR=<path>`. The CRT is static, so the exe needs no VC++ Redistributable.

## Command line

```
Mixer.PluginHost --channel <name> --parent <pid> (--plugin <path.vst3> [--class <guid>] | --builtin copy)
                 [--stall-every <blocks> --stall-ms <ms>]
```

| Argument | Meaning |
| --- | --- |
| `--channel` | name prefix of the objects the mixer created: `<name>.shm` (section), `<name>.req`, `<name>.done` (auto-reset events) |
| `--parent` | the host leaves its audio loop and exits when this process ends |
| `--plugin`, `--class` | module path (bundle or single file); class GUID, otherwise the first `Audio Module Class` |
| `--builtin copy` | no plug-in: output = input. A baseline for exchange cost and memory |
| `--stall-every`, `--stall-ms` | fault injection: every N-th block the host sleeps M ms before `process` |

Exit codes: 0 normal exit, 2 bad arguments, 3 parent or channel not found, 4 plug-in did not load (reason in the header and on stderr).

## Audio channel

The section is a 4096-byte header followed by float32 buffers, planar: `in[2][maxFrames]`, `out[2][maxFrames]`. The layout is `src/shared_audio.h`; its C# mirror is `tools/Mixer.PluginBench/SharedLayout.cs`. Fields written on every block by different sides are in different cache lines.

| Offset | Written by | Fields |
| --- | --- | --- |
| 0 | mixer, once | magic `MXPH`, protocol version, sample rate, `maxFrames`, channels |
| 64 | host, once | state (starting / ready / failed / stopped), latency, plug-in in/out channels, host PID |
| 128 | mixer, per block | `requestSeq`, `requestQpc`, `frames`, flags (stop) |
| 192 | host, per block | `doneSeq`, `wakeQpc`, `doneQpc`, `processTicks`, `processResult` |
| 256 | host | load error text, UTF-8, 1024 bytes |

One block:

1. The mixer checks `doneSeq == lastSent`. If not, the host is still working on an overdue block and is reading the input: this block goes dry ("busy") and nothing is sent.
2. It copies the input, writes `frames`, then `requestSeq` (release) and sets `Request`.
3. The host wakes up, reads `requestSeq` (acquire), calls `process`, writes the output and timings, then `doneSeq` (release) and sets `Done`.
4. The mixer spins on `doneSeq` for up to 50 µs, then waits on `Done` together with a high-resolution timer set to the deadline (block start + 40% of the period). Every wake-up compares `doneSeq` with the block number, so a late `Done` from an earlier block is ignored.

The host thread is registered with MMCSS as "Pro Audio" and runs with FTZ/DAZ. Both sides touch every page of the section before the first block. The mixer then sends 8 blocks of silence with a 1 s deadline before real audio.

A mono plug-in gets (L+R)/2 and its output is copied to both channels; the host does this, so the section is always stereo. Bus arrangements are tried in the order stereo→stereo, mono→mono, mono→stereo. Auxiliary (sidechain) buses are switched off and get their own zeroed buffers.
