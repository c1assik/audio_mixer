# Mixer.PluginHost

The process that hosts one VST3 plug-in for the mixer (see «Микшер для Windows: поддержка VST», sections «Архитектура» and «Протокол обмена с хостом»). C++20, MSVC, CMake, VST3 SDK 3.8 (MIT), JSON for Modern C++ 3.11 (MIT).

**State: VST stage 1.** The audio channel, the control pipe with all messages of the specification, the editor window, even blocks, built-in test plug-ins. The mixer side is [src/Mixer.Plugins](../Mixer.Plugins) (`PluginHost`, `AudioLink`). The exe is embedded in `Mixer.exe` and extracted on first use to `%LocalAppData%\Mixer\bin\<version>-<sha8>\` together with the license texts.

## Build

Requires Visual Studio 2022 (or Build Tools) with the C++ x64 tools, a Windows SDK and CMake:

```powershell
winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 --add Microsoft.VisualStudio.Component.Windows11SDK.22621 --add Microsoft.VisualStudio.Component.VC.CMake.Project"
./tools/build-pluginhost.ps1            # Release; -Config Debug; -NoAgain to skip the SDK example
```

Output: `build/bin/Mixer.PluginHost.exe` (about 0.8 MB) and the SDK example `build/VST3/Release/again.vst3`. CMake downloads the SDK from tag `v3.8.1_build_84` (only `base`, `cmake`, `pluginterfaces`, `public.sdk`, plus `vstgui4` for AGain) and `json.tar.xz` 3.11.3 (checked by SHA-256). To use a local SDK, pass `-DMIXER_VST3_SDK_DIR=<path>`. The CRT is static, so the exe needs no VC++ Redistributable.

Build the host before `dotnet build`: `Mixer.Plugins` embeds `build/bin/Mixer.PluginHost.exe` if it exists, and builds without it otherwise (with a message). `tools/publish.ps1` builds the host itself and refuses to publish without it.

## Command line

```
Mixer.PluginHost --channel <name> --pipe <name> --parent <pid> [--stall-every <blocks> --stall-ms <ms>]
```

| Argument | Meaning |
| --- | --- |
| `--channel` | name prefix of the audio objects the mixer created: `<name>.shm` (section), `<name>.req`, `<name>.done` (auto-reset events) |
| `--pipe` | control pipe name (`\\.\pipe\<name>`) |
| `--parent` | the mixer's PID. The host checks that the pipe server is this process and exits when it ends |
| `--stall-every`, `--stall-ms` | fault injection: every N-th block the host sleeps M ms before `process` |

The plug-in is loaded by the `load` message, not on the command line. Exit codes: 0 normal exit (quit, pipe closed, parent gone), 2 bad arguments, 3 parent, channel or pipe not found, 5 the plug-in crashed (a line `crashed: exception 0x… at <module>+0x…` goes to stderr first), 6 the pipe closed and the main thread did not finish within 5 s.

## Threads

- **Main thread** (STA, message loop): every call into the plug-in's controller and editor, every control message. The pipe reader posts each message to a hidden message-only window, so messages are handled even while the plug-in runs its own modal loop (a file dialog, a license window). `ping` is answered at once; other messages that arrive during a call into the plug-in are deferred until it returns.
- **Pipe reader**: overlapped reads; also wakes on the parent's death.
- **Audio thread** (MMCSS "Pro Audio", FTZ/DAZ): waits for `Request`, runs the block, answers `Done`. A command event suspends it for reconfiguration (`setRate`, latency or bus change, `setState`, even blocks) and flushes parameters before `getState`. While suspended it answers every block with `kDoneBypassed`, and the mixer passes the block dry.

## Audio channel

The section is a 4096-byte header followed by float32 buffers, planar: `in[2][maxFrames]`, `out[2][maxFrames]`. The layout is `src/shared_audio.h`; its C# mirror is `src/Mixer.Plugins/Host/SharedLayout.cs`. Protocol version 2.

| Offset | Written by | Fields |
| --- | --- | --- |
| 0 | mixer, once | magic `MXPH`, protocol version, initial sample rate, `maxFrames`, channels |
| 64 | host, on load and changes | state (starting / ready / failed / stopped), latency (with even blocks), plug-in in/out channels, host PID |
| 128 | mixer, per block | `requestSeq`, `requestQpc`, `frames`, flags (stop) |
| 192 | host, per block | `doneSeq`, `wakeQpc`, `doneQpc`, `processTicks`, `processResult`, `doneFlags` (bypassed) |
| 256 | host | load error text, UTF-8, 1024 bytes |

One block:

1. The mixer checks `doneSeq == lastSent`. If not, the host is still working on an overdue block and is reading the input: this block goes dry ("busy") and nothing is sent.
2. It copies the input, writes `frames`, then `requestSeq` (release) and sets `Request`.
3. The host wakes up, reads `requestSeq` (acquire), takes the queued parameter changes, calls `process` on the shared buffers, writes the output, timings and `doneFlags`, then `doneSeq` (release) and sets `Done`.
4. The mixer spins on `doneSeq` for up to 50 µs, then waits on `Done` together with a high-resolution timer set to the deadline (block start + 40% of the period). Every wake-up compares `doneSeq` with the block number, so a late `Done` from an earlier block is ignored. `doneFlags & bypassed` means the host did not run the plug-in.

A mono plug-in gets (L+R)/2 and its output is copied to both channels; the host does this, so the section is always stereo. Bus arrangements are tried in the order stereo→stereo, mono→mono, mono→stereo. Auxiliary (sidechain) buses are switched off and get their own zeroed buffers.

**Even blocks.** With `evenBlocks`, the host feeds the plug-in exactly 256 frames at a time (`setupProcessing` gets `maxSamplesPerBlock` 256). Input collects in a 256-frame buffer; output comes from a queue that starts with 256 frames of silence. The output is therefore always exactly 256 frames behind, whatever sizes the mixer sends, and the reported latency grows by 256.

## Control pipe

The mixer creates the pipe with access for the current user only (`PipeOptions.CurrentUserOnly`); the host connects as the client. A message is a 32-bit little-endian length followed by a UTF-8 JSON object with a `type`. Requests carry an integer `req`, which the answer repeats. State travels as base64 in the format below.

| Message | Direction | Answer / meaning |
| --- | --- | --- |
| `load { req, path, classId?, rate, maxBlock, evenBlocks, state? }` or `load { req, builtin, options, … }` | mixer → host | `ready` or `failed { message }` |
| `ready { req, name, vendor, version, classId, latency, pluginLatency, inChannels, outChannels, evenBlocks, hasEditor, params }` | host → mixer | plug-in loaded and active. `latency` includes even blocks |
| `setRate { req, rate }` | mixer → host | suspend, `setActive(false)`, `setupProcessing`, `setActive(true)`, resume; state is kept. Answer `rateSet { rate, latency }` |
| `getState { req }` / `state { req, blob }` | both | parameters are flushed to the processor first (`process` with 0 samples) |
| `setState { req, blob }` / `stateSet { req, params }` | both | a slot preset; blocks go dry meanwhile |
| `openEditor { ownerHwnd, title, x?, y?, w?, h? }` / `closeEditor` | mixer → host | answer `editorOpened { x, y, w, h }` or `editorFailed { message }`; closing (by the user or by `closeEditor`) sends `editorClosed { x, y, w, h }` |
| `setParam { id, value }` | mixer → host | to the controller and, on the next block, to the processor. Echo: `param` with the value's text |
| `param { id, value, text }` | host → mixer | an edit in the plug-in window (`performEdit`), an echo of `setParam`, or a value the processor changed itself (not for read-only parameters, at most every 33 ms) |
| `params { params }` / `getParams { req }` | both | the whole list again (`restartComponent` with `kParamValuesChanged` or `kParamTitlesChanged`) |
| `setEvenBlocks { req, on }` | mixer → host | answer `latencyChanged` |
| `latencyChanged { samples, inChannels, outChannels }` | host → mixer | `restartComponent` with `kLatencyChanged`, `kIoChanged` or `kReloadComponent`; the host reactivates the plug-in first |
| `dirty` | host → mixer | something changed in the editor (`endEdit`, `setDirty`, `performEdit`); at most every 250 ms |
| `ping { t }` / `pong { t }` | mixer → host → mixer | once a second; the main thread answers |
| `quit` | mixer → host | close the editor, stop the audio thread, unload, exit 0 |
| `error { req?, message }` | host → mixer | a request failed, or an unsolicited problem |
| `debug { op, … }` | mixer → host | built-in plug-ins only: `edit` (as if in the plug-in window), `freeze` (block the main thread), `dirty`, `restart` |

Parameter entries in `ready`, `params` and `stateSet`: `{ id, title, units, steps, default, value, text, readOnly, hidden, bypass, list }`, values normalized to 0…1.

**State format** (also the `.state` file): a 32-bit little-endian length and the bytes of `IComponent::getState`, then a length and the bytes of `IEditController::getState` (empty when the controller has no state of its own). On load and `setState` the host calls `IComponent::setState`, `IEditController::setComponentState` with the same bytes, and `IEditController::setState`.

## Editor window

A top-level window of the host process (class `MixerPluginEditor`) holding the plug-in's `IPlugView` (`kPlatformTypeHWND`). The mixer's main window becomes its owner through `GWLP_HWNDPARENT`, so the editor stays above it and hides when it is minimized. The process is Per-Monitor V2 DPI aware: the view gets `IPlugViewContentScaleSupport::setContentScaleFactor(dpi / 96)` before `attached` and on `WM_DPICHANGED`. `IPlugFrame::resizeView` resizes the window; user resizing goes through `checkSizeConstraint` and `onSize`; the window has a sizing border only if `canResize` says so. A remembered position is used if it is on a monitor, otherwise the window is centred on the owner. A remembered size is applied only to resizable views. `editorOpened` and `editorClosed` report the window's top-left corner and the view size, for the slot to remember.

## Built-in plug-ins

`load { builtin: "<kind>", options: { mono, sleepMs, after, latency } }`, for the C# tests and benchmarks without real plug-ins:

| Kind | Does |
| --- | --- |
| `copy` | output = input |
| `gain` | Gain (normalized × 2, default 0.5 = exactly 1.0) and Bypass (`kIsBypass`). State: two doubles plus a controller string that counts saves. `mono: true` makes it a 1-in, 1-out plug-in |
| `sleep` | sleeps `sleepMs` (50) in every block |
| `crash` | access violation in the audio thread after `after` blocks |
| `nan` | outputs NaN after `after` blocks |
| `latency` | a real delay of `latency` (512) samples. Its Delay parameter changes the latency the way plug-ins do: `restartComponent(kLatencyChanged)` |
