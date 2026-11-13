# Mixer for Windows: developer notes

The user guide is in the [README](../README.md). The specification and plan (in Russian) is `Микшер для Windows спецификация и план.md` in the repository root; section names from it are quoted below in Russian. Stage 0 (risk check): [tools/Mixer.Probe](../tools/Mixer.Probe/README.md). The v2 specification is `Микшер для Windows v2 спецификация и план.md`; its stage 0 results (captures, new applications, direct route, `IAudioClient3`) are in the same Mixer.Probe README; stages 1 (buses), 2 (processing), 3 (applications), 4 (low latency), 5 (migration and release 2.0), 6 (virtual cables and the setup assistant), 7 (scenes, hotkeys, tray, autostart, themes) and 8 (tests and release 2.1) are described below in «v2 stage 1» … «v2 stage 8». The VST support specification is `Микшер для Windows поддержка VST.md`; its stages 0 (exchange prototype), 1 (the host), 2 (scanner and catalog), 3 (the engine), 4 (the UI) and 5 (compatibility and tests) are in «VST stage 0» … «VST stage 5» below.

The UI is in English, or in Russian when Windows is in Russian (see "Localization"). Log messages are in English; code comments are in Russian. Russian strings quoted below are shown as they appear in the Russian UI.

## Layout

| Project | Contents |
| --- | --- |
| `src/Mixer.Audio` | Audio engine and WASAPI interop, no WPF: source and output nodes, wires, buses (per-output instances and mix plans, pinned buses with taps), resamplers, PI controller, ramps, limiter, processing chain (plug-in slots, gate, EQ, compressor), peak meters; low-latency streams (`IAudioClient3`) with automatic fallback; process loopback, `IAudioPolicyConfig`, audio sessions with their volume, meter and new-session notifications, probe stream, device change notifications (`IMMNotificationClient`) |
| `src/Mixer.Core` | Graph model and `graph.json` (scenes, hotkeys, cable links), the cable pairing table and cable checks, the setup assistant's layout builder, the Modular rack layout, device lookup by ID, name and default role, the feedback rule, paths through buses, building the engine snapshot; process snapshot, Take over with the `boundApps` journal, sink validation, watchdog, the `MixerHost` control layer with hot-plug and sleep handling; a WPF-free editor: history, edits (including grouping into a bus), wire validation, layout, store with backups, chain presets, application catalog; file log |
| `src/Mixer.App` | UI (WPF, CommunityToolkit.Mvvm): graph canvas, cards, wires, palette, settings, processing side panel with the EQ curve, setup assistant, scenes, global hotkeys and push-to-talk, tray icon, autostart, themes (Graphite, Daylight, Modular with its rack view). Builds `Mixer.exe` |
| `src/Mixer.Plugins` | VST3 plug-ins, the mixer side: starting and supervising `Mixer.PluginHost.exe` (`PluginHost`: Job Object, control pipe, ping), the audio channel (`AudioLink`), the state format, the embedded host and its extraction (`HostBinary`), third-party licenses; the scanner and the catalog with its cache (`PluginScanner`, `PluginCatalog`). No WPF, no audio engine |
| `src/Mixer.PluginHost` | C++ process hosting one VST3 plug-in: audio channel, control pipe, editor window, even blocks, built-in test plug-ins. Built by `tools/build-pluginhost.ps1`, not by `dotnet build`; embedded into `Mixer.Plugins` when built. See its [README](../src/Mixer.PluginHost/README.md) |
| `licenses` | Texts of third-party licenses shown in Settings → «О программе» |
| `tools/Mixer.PluginBench` | VST stages 0–2 and 5: a benchmark of the exchange with the host, a manual check of the editor window, a scan of the installed plug-ins, the compatibility check |
| `tools/Mixer.Run` | Console host for stages 1–2: runs a JSON graph through `MixerHost` with statistics, lists applications, checks a sink, measures loop latency (also with low latency) |
| `tests/Mixer.Tests` | xUnit tests without a sound card, including UI view models (on their own STA thread, without a window) |
| `tools/publish.ps1`, `tools/make-icon.ps1` | Release build of a single `Mixer.exe` and the program icon (stage 5) |

Build: `powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-pluginhost.ps1` (C++, once and after host changes; without it the solution still builds, but without the plug-in host), then `dotnet build Mixer.sln -c Release`; tests: `dotnet test tests/Mixer.Tests -c Release`; release: `powershell -NoProfile -ExecutionPolicy Bypass -File tools\publish.ps1` (see "Stage 5").

## Stage 1: audio engine

### How it works

- **Source** (`SourceNode`) is a WASAPI capture thread (shared, event-driven, MMCSS "Pro Audio"). Each packet is converted to float32 stereo (mono is duplicated, multichannel is downmixed per ITU, LFE dropped), goes through the source's gain, mono and balance, and is written into every wire. A loopback source also plays a silent stream into the same device: without it, loopback delivers no packets while nobody plays to the device.
- **Wire** (`Wire`) is an SPSC ring of 200 ms with its own resampler. The output reads it on its own period. The ratio is r = f_src / f_out × (1 + ε). The correction ε comes from a controller driven by ring fill (target: two output periods, 20 ms), in two modes; see deviation 7. If the ring runs dry, the output plays out the remainder with a 2 ms fade and refills to the target. If it overflows, or the surplus arrives as a jump of more than 10 ms, the excess is dropped down to the target, also with a fade.
- **Output** (`OutputNode`) has its own render loop clocked by the device. It sums the wires, applies the output gain, a limiter (−1 dBFS, 1 ms attack, 100 ms release) and a hard ceiling of ±1.0. L/R go to FL/FR, other channels are zero.
- **Engine** (`AudioEngine.Apply`) receives an immutable routing snapshot and swaps the nodes' wire arrays atomically. Audio threads have no locks and no allocations; GC runs in `SustainedLowLatency` mode. A wire being removed is faded out first.
- A node reopens its device 2 s after an error. If opening hangs for more than 8 s (the audio service is unhealthy; a USB microphone here takes 4–16 s to open), the node shows an error naming the hung call.

### Deviations from the specification

1. **No NAudio.** The WASAPI interop is our own, carried over from `Mixer.Probe`, where it was verified in stage 0. Every call returns an HRESULT without exceptions, so the audio loop has neither exceptions nor allocations. Process loopback needs a custom wrapper in stage 2 anyway, so all interop lives in one place.
2. **The default resampler is a polyphase windowed sinc** (Kaiser, 32 zero crossings, 512 phases, kernel stretched when downsampling). Catmull-Rom remains an option (`--resampler cubic`). Measurements:

   | THD+N | 1 kHz, 44.1→48 | 1 kHz, 48→44.1 | 10 kHz, 44.1→48 | 10 kHz, 48→44.1 |
   | --- | --- | --- | --- | --- |
   | sinc | −100.9 dB | −101.0 dB | −100.4 dB | −101.7 dB |
   | Catmull-Rom | −89.5 dB | −91.7 dB | −26.4 dB | −29.0 dB |

   At 1 kHz Catmull-Rom formally passes the −80 dB threshold, but it is dirty at high frequencies. At 48→44.1 sinc suppresses 23 kHz by 90 dB.
3. **Ring fill is not estimated from the raw `Available`.** Blocks are written and read in 10 ms chunks, so the raw fill is a sawtooth with a slowly drifting phase, and the PI controller would swing ε by ±1000 ppm. Instead a continuous estimate is used: fill + (read time − last write time) × f_src. Both times are cleaned of wake-up jitter by a 2nd-order DLL with a 0.1 Hz bandwidth.
4. **The output buffer queue is capped at 15 ms** (`RenderQueueMs`). For a 10 ms request Windows gave CABLE Input 1056 frames (22 ms). Keeping such a buffer full would add 12 ms to the latency budget beyond the estimate.
5. **2 ms headroom before an underrun.** An underrun is declared when the ring holds less than a block plus a fade. Otherwise, if the ring ran out exactly on a block boundary, there would be nothing left to fade, and it would click.
6. The limiter has no lookahead, so as not to add latency. The first ~1 ms of a sharp attack may pass above −1 dBFS, but not above 0 dBFS.
7. **Two-mode drift controller and direct rate estimation.** The specification assumed audio arrives evenly. A real USB microphone (fifine) delivers exactly 441 frames every 10.00 ms, and its +566 ppm surplus as one extra back-to-back packet every 17.7 s, flagged DATA_DISCONTINUITY. The ring then lives on a +10 ms sawtooth. A fast PI chased it by ±1000 ppm and never learned the average drift. Now:
   - **acquisition** (first ≥ 15 s): the PI from the specification, τ 1 s, Kp 0.8, Ki 0.08; converges in < 10 s;
   - **tracking**: ε = direct drift estimate + a slow PI (τ 5 s, Kp 0.05, Ki 0.001). The estimate is a weighted regression of "frames vs. QPC" on each side of the wire (`RateEstimator`, 10 min forgetting). Extra and missing packets count as part of the rate;
   - if the source drops packets (negative drift), the ring target rises by one of its packets; otherwise every drop would be an underrun;
   - the writer's DLL is reset on a discontinuity and on a persistent step (3 events in a row > 3 ms);
   - integrator anti-windup.
8. **The direct drift estimate may go up to ±2%** (`MaxDriftPpm`; the PI term on top of it is still ±1000 ppm). With two outputs, VB-Cable consumed audio 1% slower than real time (47,510 fps instead of 48,000), and with a ±1000 ppm limit the wire lost audio to drops every 2 s. The strict specification mode is `--max-drift-ppm 1000`.

### Tests (70, `dotnet test`)

- Resampler: THD+N < −80 dB for 44.1→48 and 48→44.1 at 1 kHz and across the band, rejection above the new Nyquist frequency, ratios down to 192→44.1.
- PI controller on a wire model with synthetic clocks: +300 ppm converges in 8.6 s (−300 ppm, 44.1→48, 48→44.1: under 10 s). With 0…1 ms wake-up jitter ε swings by ±29 ppm, with 0…3 ms by ±114 ppm. No underruns. The tone at the wire output after convergence is no worse than −75 dB THD+N, no clicks. Gain tuning: `TuningSweep` (run manually).
- Wire: a stalled source gives exactly one underrun without a click; a stalled output gives one overrun and a return to the target; a source burst gives one excess drop; a 10 ms gap gives no underrun; after start the fill equals the target.
- Bursty source like the fifine: at +566 ppm the true latency (ADC to read) stays within 23.7…25.8 ms with no glitches. At −566 ppm: one underrun on the first drop, then 34.7…36.8 ms. At +150 ppm: 23.3…28.0 ms once the drift shows up in the data (an extra packet every 67 s). An output with a ±1% clock: after acquisition it tracks without drops, ε within ±300 ppm of the drift.
- Downmix of 5.1/7.1/mono, writing to 7.1/mono/pcm16, `WAVEFORMATEXTENSIBLE` parsing.
- Ramps (10 ms gain, 5 ms mute), balance, limiter, peak meter.
- Feedback rule (loopback → the same output; CABLE Output → CABLE Input and CABLE In 16ch), device lookup by ID and name, config serialization round trip.

### Running

```
tools\Mixer.Run\bin\Release\net8.0-windows\Mixer.Run.exe devices --formats
Mixer.Run.exe example graph.json --in "fifine" --out "Динамики" --out2 "CABLE Input"
Mixer.Run.exe run graph.json --seconds 3600 --report 10 --csv run.csv
Mixer.Run.exe latency --in "<line input>" --out "<line output>"
```

Every few seconds `run` prints node states, ring fill, ε, latency estimate, underruns/overruns, CPU and allocations. Exit code 0 means there were no glitches. `latency` injects an impulse into the input and measures how many samples it takes to come back through the "output → cable → input" loop. That is the end-to-end "input → output" latency including driver buffers.

### Status as of 9 November 2026

- Done: engine, config, host, tests.
- **One-hour run** (`run --seconds 3600`): fifine microphone (44.1 kHz, mono) → fifine speakers (48 kHz) and CABLE Input (48 kHz), before change 8.
  - **Microphone → fifine speakers: 0 underruns, 0 overruns, 0 drops, 0 losses in 3600 s.** Drift estimated at +522…+590 ppm, ε 342…684 ppm. Ring fill 16.1…22.4 ms, engine latency estimate 39.8…50.9 ms. The ring sawtooth mirrors the sawtooth in the capture buffer, so the true latency is steadier than this estimate. CPU: 0.07% on average, 0.11% peak (12 logical cores). No steady-state allocations apart from printing the status itself (~8 KB per minute); GC 0/0/0.
  - Microphone → CABLE Input: 1734 excess drops. CABLE Input ran at −10,197 ppm (47,510.6 fps); the buffer was empty at thread wake-up 7344 times. On its own, 3 minutes passed without glitches. A repeat run is needed after change 8.
  - Average rates by QPC over the hour: microphone +552 ppm, fifine speakers +9 ppm.
- **Not done:** end-to-end latency on real devices; there is no "line out → line in" cable. There is only the engine estimate (40–50 ms without driver buffers). The 48→48 kHz path was not tested on this machine: the microphone runs at 44.1 kHz, and the 44.1→48 path from the criterion was tested.
- **Incident.** The first run on devices was a CABLE Input → CABLE Output loop in one process. `IAudioClient.Initialize` for the CABLE Output capture hung, and at 13:54 the Windows Audio service hung (`AppHangXProcB1`, audiodg). Windows restarted it. The cause was not established: it may have been the loop through VB-Cable itself, or lingering service ill health after stage 0. Loops through both ends of the cable have not been run since.

## Stage 2: applications

### How it works

- **Application node** (`kind: app`, `exe`) is a process loopback (`ActivateAudioInterfaceAsync`, `INCLUDE_TARGET_PROCESS_TREE`) on the root of the exe's process tree. With several roots (two instances), each gets its own capture: in the engine these are sources `id@pid` with their own wires, summed at the outputs. If the process isn't running, the node waits and the wires stay in the config.
- **All system audio node** (`kind: system`) is a process loopback `EXCLUDE_TARGET_PROCESS_TREE` on the mixer's PID, one per layout.
- **Capture format** is float32 7.1 (`WAVEFORMATEXTENSIBLE`, mask `0x63F`), flags `LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM`; the engine downmixes to stereo per ITU. The rate is that of the node's outputs if they all share one, otherwise 48 kHz (`GraphBuilder.CaptureRate`).
- **Process watcher** (`MixerHost`, 1 s tick) snapshots processes (Toolhelp32) and audio sessions (`IAudioSessionManager2`). When the roots of the graph's applications change, the graph is rebuilt and the engine restarts only the sources that changed.
- **Take over** (`TakeOverManager`, `takeOver` on by default): binding to the sink via `IAudioPolicyConfig` for the Console, Multimedia and Communications roles on the PIDs with audio sessions and on the roots, verified with `Get`. The exe is written to `boundApps` **before** the first `Set` and removed after a successful reset. `E_INVALIDARG` from a process without an audio context is not an error: retry on the next tick. Node removed or mode turned off: the binding is removed. Normal exit: audio stops first, then bindings are removed from live processes. The rest stay in the journal and are reset on the next start as soon as the process appears and touches audio. If a binding is set but the application's active session is still on the old device, the node asks to restart playback.
- **Sink validation** (`SinkValidator`, `StreamProbe`): playback devices only. A Voicemeeter VAIO input without Voicemeeter running is rejected **without** a probe stream. Others get a probe stream on a separate thread with a 2 s timeout: if the stream hangs, it is abandoned, and a repeat check of the same device doesn't touch it. The probe stream must also consume audio (padding decreases). Warnings: the sink is the default device; the sink is VAIO with Voicemeeter running; the sink has fewer channels than the default device (see "Device check", item 3).
- **Watchdog** (`CaptureWatchdog`): the target has had an active session for 3 s and there have been no packets (including `SILENT`) for 3 s, so the capture is recreated. Recreation means a new node with new wires. The old thread gets 500 ms, then is abandoned, and its client gets `IAudioClient.Stop` from the thread pool (in stage 0 only that released a hung `GetNextPacketSize`).
- **Graph warnings**: All system audio and an application in Take over mode go to the same output (`DoubleSound`, with a list of applications). An output, loopback or CABLE Output is the sink or hears it (`SinkConflict`).
- All COM calls except audio threads go through one MTA thread in `MixerHost`, not the UI thread. If process loopback is unavailable (Windows 10 before 20348), application nodes aren't created and show a clear reason. If `IAudioPolicyConfig` is unavailable, Take over is off with a hint about the Windows volume mixer.

### Deviations from the specification

9. **Application pauses are not underruns.** When an application is silent, process loopback delivers no packets and the wire's ring drains. For application sources (`SourceClock.Intermittent`) the wire treats silence longer than 100 ms as a pause (`Wire.Gaps`), not a glitch. The audio behaves as in an underrun: fade, then refill to target. Pauses are subtracted from the rate estimation time; otherwise a minute of silence would look like a source running 50% slow. A packet late by less than 100 ms is still an underrun. Device behaviour is unchanged from stage 1.
10. **Watcher tick of 1 s, not 2 s.** The acceptance criterion requires resetting a binding within 3 s of the application starting, and the process still has to touch audio first. A 2 s tick leaves no margin.
11. **The watchdog waits until the session has been active for 3 s in a row.** Otherwise freshly started playback would be taken for a failure.
12. **Capture rate is computed over all of the node's wires, including disabled ones.** Otherwise enabling a wire would restart the capture. Unknown rates (VAIO endpoints aren't queried) are ignored.
13. **The `boundApps` journal belongs to `MixerHost`.** A graph from the UI is accepted without the journal and saved together with it. `Mixer.Run run` writes the journal back to `graph.json` and reformats the file in doing so.

### Tests (117 total: 115 pass, 2 manual tuning sweeps skipped)

- Process snapshot: roots and tree of a multi-process application, the mixer is never a target, binding targets (sessions + roots), the mixer's own sessions don't count.
- Graph: two roots give two captures with wires, All system audio excludes the mixer and exists once, capture rate 48/44.1 by outputs, process loopback unavailable, applications have no loops through a device, double-sound and sink warnings in the graph.
- Take over on an `IAudioPolicyConfig` model with stage 0 data (binding per exe, `E_INVALIDARG` without an audio context). The journal is written before `Set`, and a journal error blocks the binding. No audio context: retry. Turning the mode off removes the binding. Reset after a crash waits for the application to start. On exit live applications are reset, the rest stay in the journal. A restarted application is bound by its new PIDs. Sink change, sink failure, `E_ACCESSDENIED`, `Get` mismatch, the "restart playback" hint.
- Sink: VAIO without Voicemeeter is rejected without a probe stream; with Voicemeeter it is checked and marked unreliable. Probe stream timeout, capture device, default device, stereo sink with a 7.1 default device.
- Watchdog: 3 s without packets during an active session; `SILENT` and no session are normal; the session must be active continuously.
- Wire: a 2 s application pause gives 0 underruns, 1 pause, no click. A 50 ms late packet is an underrun. A one-minute pause with +300 ppm drift: ε afterwards 300…302 ppm, fill 20.0 ms. A stalled device is still an underrun.
- 7.1 float format to `WAVEFORMATEXTENSIBLE` and back, endpoint interface path.

### Running

```
Mixer.Run.exe apps
Mixer.Run.exe sink-check --sink "CABLE Input"
Mixer.Run.exe example app.json --no-mic --app firefox --out "Динамики" --sink "CABLE Input"
Mixer.Run.exe run app.json --report 5
```

While `run` is going, an application in Take over mode plays only through the mixer. Ctrl+C removes the binding. If the mixer process is killed, the next `run` with the same `graph.json` removes it.

### Device check, 9 November 2026

The source was the `Mixer.Probe tone` generator (−70 dBFS, FL/FR only, automatic stream routing, playing to Sonar Gaming: 96 kHz, 7.1). Output: fifine speakers (48 kHz); sink: CABLE Input. CABLE Output was not opened.

1. **`sink-check` CABLE Input**: suitable, with the warning from item 3.
2. **Application capture and Take over, 40 s**: the binding was set 0.2 s after start, removed on exit, journal empty. 0 underruns, overruns, drops and pauses. CPU: 0.19% on average, 0.22% peak (12 logical cores). Engine latency estimate 45.6 ms. Allocations ~64 KB/s come from the control thread (process and session snapshot once a second), not audio threads. GC over 40 s: 0/0/0. Without applications in the graph no snapshot is taken.
3. **Found: a 13.5 dB loss when a 7.1 stream moves to a stereo sink.** Without Take over the node shows exactly −70.0 dBFS. With Take over it is −83.5. A tone opened directly on CABLE Input is −70.0 again. So the 7.1 capture and the engine downmix are correct. A stream opened in the format of a 7.1 device and moved to a stereo device without reopening is downmixed by Windows itself, with normalization. Applications that reopen the stream on device change don't lose level. This can't be fixed from the mixer: only another undocumented API changes another device's format. Sink validation now warns when the sink has fewer channels than the default device and suggests a fix: set the sink to 7.1 or restart playback. All VB-Cable inputs on this machine are stereo.
4. **Killing the mixer (`taskkill /F`)**: the binding stayed in Windows (all three roles on CABLE Input), `mixer.probe.exe` was in `boundApps`. The next start with Take over off removed the binding on the first tick; the tone's session went back to Sonar.
5. The node waits while the application isn't running: «приложение mixer.probe.exe не запущено, узел ждёт» (application not running, node waiting).

**Not tested:** Discord and browsers under the mixer, All system audio on devices (it would send all system audio to the headphones), two roots of one exe, the watchdog on a real hang (it can't be triggered safely), builds 23H2 and 24H2, Windows 10 without process loopback.

## Stage 3: graph UI

### How it works

- **The source of truth is an immutable `GraphConfig` in `GraphHistory`** (`Mixer.Core/Editing`). Every UI action builds a new graph with a pure function (`GraphEdits`). The history pushes it to the cards (`MainViewModel.Sync`), the store and the host. Undo/redo: 50 steps. A continuous edit (fader, wheel and drag over a pill, moving a node, balance) is merged into one step by key while edits come more often than every 1.5 s and the mouse isn't released.
- **The host takes edits coalesced** (`MixerHost.Update`): while the control thread is busy, only the latest graph is applied. `GraphChange` decides how much work to do. Positions and colours don't touch the engine. Gains and mute rebuild the snapshot without querying devices and processes. New nodes and wires re-read devices and immediately reconcile Take over bindings.
- **The UI reads the engine without locks**: `AudioEngine.Snapshot` is published at the end of `Apply`. The UI thread doesn't wait for `Apply`, which can take up to 2 s stopping a hung node. Node state and tooltips are polled 4 times a second, peaks 30 times (`MeterClock` on `CompositionTarget.Rendering`; the clock stops while the window is minimized). Meters and wires are drawn in `OnRender`, without per-frame bindings. A wire is redrawn only when its level moves to a different one of 20 steps.
- **Custom canvas**: `WireLayer` draws all Bézier curves in one element and acts as the background (catches clicks on wires and empty space). Cards and pills are an `ItemsControl` on a `Canvas`. Gestures live in `GraphView`, logic in the view models.
- **One wire check for all paths** (`ConnectionRules`): the same `FeedbackRules` as in graph building, plus duplicates and direction. You can drag from a source to an output and back. While a wire is being dragged:
  - valid ports are outlined;
  - occupied ones (a wire already exists) are grey dashed;
  - forbidden ones are red, with the reason next to the cursor;
  - the own column dims.

  A wire that became a loop after a device change is drawn red dashed with the label «петля» (loop).
- **Saving** (`GraphStore`). The graph is written atomically 1 s after the last edit. The host writes the `boundApps` journal immediately, before `Set`. The file is always assembled from the latest UI graph and the latest host journal under one lock, so writes don't overwrite each other. A broken `graph.json` is moved aside to `graph.json.broken-<time>`; otherwise autosave would overwrite it with an empty layout. Path: `%AppData%\Mixer\graph.json`.
- **A sink is assigned only after validation.** In Settings, choosing a device starts a probe stream on the control thread (`MixerHost.ProbeSinkAsync`). If it isn't suitable, it isn't assigned, and the reason shows right away. The host remembers a good result for 30 s and doesn't open a second probe stream on assignment.
- **Palette** (`MixerHost.QueryCatalogAsync`, `AppCatalog`). Section order:
  1. applications with an audio session (playing ones have a meter);
  2. other applications with a window;
  3. inputs;
  4. output loopbacks;
  5. All system audio.

  The mixer and its child processes are not listed. Icons come from the exe (`SHGetFileInfo`), names from `FileDescription`.
- Only one instance runs at a time (named mutex): a second start brings the first window to the front. The title bar is dark, the accent colour comes from Windows (too dark a colour is lightened).

### Controls

| Action | How |
| --- | --- |
| Add a node | «+ Источник» / «+ Выход»; right-click an empty spot in a column: the node lands where you clicked |
| Connect | drag from a port dot to a node in the other column (also from an output to a source); node menu → «Вывести в…» / «Принимать из…» |
| Move or delete a wire | drag the wire by its end (the third nearest the node) to another node or into empty space; click the wire and press Delete; right-click → «Удалить» |
| Wire volume | wheel over the pill (Shift: 0.1 dB step), drag the pill up and down; click to turn off/on, double-click for 0 dB |
| Node volume | fader (a click sets the value and starts dragging; with Shift it doesn't snap to 0 dB), wheel over it, double-click for 0 dB; M button |
| Rename | double-click the name or F2 |
| Highlight routes | hover over a node |
| Undo, redo | Ctrl+Z, Ctrl+Y (or Ctrl+Shift+Z) |
| Zoom and scroll | Ctrl + wheel (towards the cursor), Ctrl +/−/0; middle button or Space + drag |
| Mono, balance, Take over, device, colour | node menu ("⋯" or right-click) |
| Take over sink | ⚙ in the header |

### Deviations from the specification

14. **Custom canvas, not Nodify.** The graph is bipartite, columns are fixed, and a node moves only within its own column. The pill sits in the middle of the wire, and wire thickness and brightness follow the level 30 times a second. Nodify would have needed reworking for all of this. The custom canvas is three classes (`WireLayer`, `GraphView`, `NodeCard`) with no dependencies.
15. **Cards are 248 px wide, as in the mockup**, not 220 px: 220 px doesn't fit the name, subtitle, limiter and warning badges.
16. **The wheel over a node's fader also changes volume**, as over a pill. Dragging a pill up and down changes the wire volume (the mockup gives the pill an `ns-resize` cursor). A click on a pill and a following double-click are merged into one history step.
17. **A wire is re-hooked by its end**: you have to grab the third of the curve nearest the node; a click in the middle only selects. Otherwise any imprecise press would tear the wire.
18. **Autosave (after 1 s) is done now**, not in stage 4: the UI is unusable without it. Five backups were left for stage 4 (done, see "Stage 4").
19. **Nodes on Voicemeeter devices wait for Voicemeeter to start** (`RuntimeContext.VoicemeeterRunning`). A VAIO output, loopback or input without Voicemeeter isn't opened at all. The node shows «ждёт Voicemeeter» (waiting for Voicemeeter) and connects by itself when Voicemeeter starts. In the palette such devices are unavailable with the same reason. In stage 0 a stream on VAIO without Voicemeeter hung the Windows Audio service, and a saved layout would open such a stream on start.
20. The Default device node (and the «Микрофон по умолчанию» row from the palette mockup) doesn't exist yet. It arrives in stage 4 together with `IMMNotificationClient` (done, see "Stage 4").
21. **All system audio doesn't repeat applications that have their own node**; this was pulled in from v2 (specification, «Почему в v1 нет узла „Всё, кроме приложения“»). Exclude in process loopback takes one PID, and that is taken by the mixer. So if the layout has at least one Application node, All system audio is assembled from include captures of all processes with an audio session (`SystemCapture`). The mixer and the trees of applications with their own node aren't included. A capture climbs to the root of the same exe (Chrome is one capture), nested trees aren't duplicated, and there are at most 24 captures (active ones first). The process watcher picks up new sessions within ≤ 1 s; the first fraction of a second of a new application's audio doesn't reach the node. A process whose tree contains the mixer or an application with its own node (usually `explorer.exe`) isn't captured: a subtree can't be cut out of an include, and capturing it would cause a loop or double sound. Such processes are listed in the node's tooltip. Without Application nodes the previous single exclude capture remains. The `DoubleSound` warning is now raised only if All system audio runs as a single capture and an application with its own node goes to the same output, regardless of Take over.

### Tests (152 total: 150 pass, 2 manual tuning sweeps skipped)

- History: undo and redo, a drag merges into one step, a new step starts after a pause and after releasing the mouse, a 50-step limit.
- Edits: removing a node with its wires, re-hooking that keeps gain, rejecting duplicates, node ID allocation.
- Wire check: direction normalization; duplicate; two sources and two outputs; loopback and cable loops (CABLE Input, CABLE In 16ch) with a reason; loopback into its own disconnected output.
- Layout: a node doesn't leave its column; a new node lands below the lowest one or in a free spot by click.
- `GraphChange`: positions, parameters, structure (wires, Take over, sink, re-hook, duplicates).
- Voicemeeter: a VAIO output waits while Voicemeeter isn't running; if it is running or unknown, it works.
- All system audio without applications that have their own node: one capture per root of a process with audio, without the mixer and excluded trees; the parent of the mixer or of an excluded application is skipped; climbing to the exe root stops before a tree with an excluded application; one capture per tree; expired and dead sessions don't count; capture limit with priority for active ones; without Application nodes, the previous exclude capture.
- Store: the UI graph and the host journal don't overwrite each other in either direction; write after 1 s; a broken file is moved aside; positions, names, gains, mute, mono, balance and colour survive writing and reading.
- Application catalog: applications with audio first; the mixer, its child processes and services hidden; processes counted; the exe path is found.
- View models (STA thread, no window):
  - cards follow the graph;
  - a fader drag is one history step, volume rounds to 0.1 dB and snaps to 0;
  - a forbidden wire isn't created, the reason is shown;
  - ports light up while dragging, routes on hover;
  - re-hooking, deleting and undo;
  - click and double-click on a pill;
  - a new node lands in its column and gets a free colour;
  - renaming and changing device;
  - palette: sections, search by full device name, skipping headers, adding;
  - numbers in Russian format, with a real minus sign;
  - meter: 20 dB/s decay, 1.5 s hold, limiter.

### Running

```
dotnet build Mixer.sln -c Release
src\Mixer.App\bin\Release\net8.0-windows\Mixer.exe
```

Development switches:

- `--graph <file>`: a custom `graph.json`;
- `--no-audio`: UI without the engine: devices aren't opened, bindings aren't touched, the mutex isn't taken;
- `--open palette-sources|palette-outputs|settings`: open the palette or settings on start;
- `--snapshot <png>`: after 2.5 s, save the window to a PNG and exit;
- `--lang en|ru`: the UI language for this run, instead of the one chosen in Settings or the Windows one (see "Localization").

### Status as of 9 November 2026

- Done:
  - canvas and cards with all states from the mockup;
  - wires with pills and tooltips;
  - palette, route highlighting, feedback prevention;
  - undo/redo, zoom and scroll;
  - node and wire menus, sink settings, autosave.

  The solution builds without warnings.
- Checked: the look of the main window, palette and settings, from snapshots in `--no-audio` mode with the streamer layout from the mockup. The palette, against the live list of applications and devices on this machine.
- **Not checked:**
  - the UI with audio: never run with the engine on devices;
  - mouse gestures: checked only by reading the code (the view models beneath them are covered by tests);
  - CPU with the window open in the acceptance scenario (4 sources, 3 outputs);
  - the "streamer layout in 5 minutes" criterion with three people.

## Stage 4: resilience

### How it works

- **Hot-plug** (`DeviceWatcher`, `MixerHost.RefreshDevices`). The `IMMNotificationClient` subscription is created on the control thread. The handler, on an audio service thread, only queues a mark. After 0.5 s of quiet in the events the host re-reads devices, but no later than 2 s after the first event: a USB headset sends a burst of events. If the list changed, the graph is rebuilt:
  - a device disappeared: the node leaves the engine, the card shows «устройство отключено» (device disconnected), wires and volume stay;
  - a device came back (with the same ID or under the same name): the node starts again;
  - a device's format changed (`PKEY_AudioEngine_DeviceFormat`): its nodes are recreated. Wire resamplers are computed for the new rate, and application capture rates are recalculated;
  - the event concerns the Take over sink: it is re-validated with a probe stream.

  The log records what changed, e.g. «подключено «Гарнитура»; устройство связи для воспроизведения по умолчанию: «Гарнитура»» (headset connected; default communications playback device: headset). If Windows rejects the subscription, the device list is polled every 3 s.
- **Default device node** (`defaultRole: console | communications` on an input, loopback or output; `endpointId` empty). The device is taken from Windows by role: Console is the ordinary default device, Communications is the communications device that Discord and calls use. When the device changes in Windows, the node switches to the new one at the next rebuild. In the palette such rows come first in the «Входы», «Звук выходов» and «Выходы» sections. The «для связи» (communications) row is shown only if it is a different device. Any node can be switched to "by default" and back via «Выбрать другое устройство» (choose another device) in the node menu. The card subtitle is «по умолчанию · Наушники» (default · headphones); the tooltip shows the full name of the current device.
- **Loops with default nodes.** Loopback of the default device into the default output of the same role is a loop for any device; such a wire isn't created. A wire that became a loop after the default device changed doesn't go to the engine and is drawn red dashed «петля», as in stage 3.
- **Sleep and wake** (`PowerEvents`, `SleepDetector`). Notifications come through `PowerRegisterSuspendResumeNotification`: the same `PBT_APMSUSPEND` / `PBT_APMRESUME*` a window gets in `WM_POWERBROADCAST`, but without a window. 1 s after wake the host re-reads devices and recreates all engine nodes: WASAPI clients may silently stop working after sleep, and the wire clocks are off by the sleep time. There is a fallback in case the notification doesn't arrive. `GetTickCount64` keeps running during sleep, while `QueryUnbiasedInterruptTime` doesn't. If the difference between them grew by more than 1 s within a tick, the host assumes the system slept.
- **Device in use** (another application in exclusive mode, `AUDCLNT_E_DEVICE_IN_USE`) is a wait, not an error. The card shows «занято другим приложением» (in use by another application), and the node retries every 2 s.
- **Backups** (`GraphStore`). When writing `graph.json`, the previous file is moved by the same `File.Replace` to `graph.json.bak-YYYYMMDD-hhmmss`. A backup is made on the first write of a session, then no more often than every 10 minutes. The last 5 are kept. If `graph.json` can't be read, it is moved aside to `graph.json.broken-…` and the graph is taken from the most recent readable backup. A file access error (e.g. an antivirus is holding it) isn't treated as corruption: otherwise a backup would overwrite a fresh graph.
- **Log** (`FileLog`): `%LocalAppData%\Mixer\logs\mixer-YYYYMMDD.log`, in 2 MB parts (`mixer-YYYYMMDD-2.log`), the last 10 files kept. A background thread writes lines, the caller doesn't wait for the disk; if the queue overflows, the log notes how many lines were lost. What goes into the log:
  - start and exit, layout loading and its errors, save errors;
  - all host messages: devices, sleep, Take over, watchdog;
  - engine node transitions: «работает (48000 Гц, 2 кан.)» (running, 48000 Hz, 2 ch), «ждёт: устройство отключено» (waiting: device disconnected), «ошибка: …» (error: …);
  - unhandled exceptions in the UI, tasks and the process.

  Audio threads don't write to the log: the control thread collects their state once a second.

### Deviations from the specification

22. **A disappeared device removes the node from the engine**, rather than just stopping its thread. For the user there is no difference: the card is grey, the wires are in place. A returning device starts as a new node with new wires. Debounce: 0.5 s of quiet, but no longer than 2 s from the first event.
23. **Sleep via `PowerRegisterSuspendResumeNotification`, not `WM_POWERBROADCAST`.** The events are the same, but no window is needed, so sleep handling also works in `Mixer.Run`. Nothing stops on suspend; threads are recreated 1 s after wake. The clock-based fallback sleep detector goes beyond the specification.
24. **Five backups are not the last five writes.** The graph is written 1 s after every edit, and five copies would cover a few seconds of fader work. A backup is made on the first write of a session and then no more often than every 10 minutes. Restoring a broken graph from a backup goes beyond the specification.
25. **The «для связи» (communications) row in the palette appears only if it is a different device**; otherwise there would be two identical rows. A communications node can also be made from any node's menu.
26. **A busy device is retried every 2 s**, not 3 s: that is the node's general retry interval.

### Tests (164 total: 162 pass, 2 manual tuning sweeps skipped)

- Default device: lookup by role and direction; the old `endpointId`/`endpointName` of such a node aren't used. An output follows a change of the default device, waits with a clear reason when there is no device, and the wire remains. Changing the role is a structural edit. `defaultRole` survives JSON.
- Loops: default loopback → default output of the same role is forbidden even without a device list. Into an explicit output that is currently the default: forbidden; into communications (a different device): allowed. A wire that became a loop after the default device changed doesn't go to the engine.
- Debounce: fires after 0.5 s of quiet; a stream of events fires no later than 2 s.
- Device change descriptions: connected, disconnected, renamed, default and communications device changed; which IDs are affected.
- Sleep detector: work isn't sleep, an hour of sleep is detected, clock jitter under 1 s isn't sleep.
- Backups: first write of a session, no more often than 10 minutes, no more than 5, the backup holds the state before the write. A broken graph is restored from the most recent readable backup (a broken backup is skipped); without backups, an empty layout with an explanation.
- Log: line format, parts by size and by day, 10 files kept.
- UI: «по умолчанию» rows come first in palette sections; the «для связи» row only for a different device. Switching a node to "by default" and back changes the name only if the mixer gave it; undo restores the device.

**Not covered by tests:** recreating engine nodes (`AudioEngine.Restart`). Engine nodes aren't created in tests because they open devices immediately.

### Running

A new `Mixer.exe` switch: `--log-dir <folder>`, a custom log folder. `Mixer.Run run` prints the same host messages (devices, sleep, node states) to the console.

### Status as of 10 November 2026

- Done: hot-plug, default nodes, sleep and wake, "in use", backups, log. The solution builds without warnings.
- Checked: the look of "by default" cards and of a loop wire, from a snapshot in `--no-audio` mode; the startup log is written.
- **Not checked on devices:**
  - plugging and unplugging a USB headset during playback (criterion: audio returns by itself within 3 s);
  - changing the default device and format in the Sound control panel;
  - sleep and wake (criterion: 5 s);
  - exclusive mode in another application;
  - the `IMMNotificationClient` and `PowerRegisterSuspendResumeNotification` subscriptions on this machine.

## Stage 5: release

### How it works

- **A single `Mixer.exe`, no installation** (`src/Mixer.App/Properties/PublishProfiles/win-x64.pubxml`): self-contained win-x64, single-file, ReadyToRun, compressed, WPF native libraries inside the exe (`IncludeNativeLibrariesForSelfExtract`; on first start they are extracted to `%TEMP%\.net\Mixer`). Debug symbols are embedded so crash logs have line numbers. No trimming: WPF doesn't support it. Satellite resource languages: `ru;en`.
- **Compression is kept**: 67.9 MB instead of 157 MB, at a cost of ~0.35 s on start (3 runs with `--snapshot`: 0.9–1.0 s versus 0.5–1.2 s uncompressed, excluding the 2.5 s snapshot delay).
- **Version**: `Directory.Build.props` (`Version` 1.0.0, `Product`). Shown in the exe properties and in the first log line.
- **Icon**: `src/Mixer.App/Mixer.ico`, drawn by `tools/make-icon.ps1` from the header logo (`IconLogo`): 16–256 px, PNG inside ICO. WPF uses the exe icon for both the window and the taskbar.
- **`tools/publish.ps1`**:
  1. tests (`-SkipTests` to skip);
  2. `dotnet publish` with the profile into `dist\`; everything except `Mixer.exe` is deleted;
  3. signing, if a certificate is given: `-CertThumbprint <thumbprint>` (current user store) or `-Pfx <file> -PfxPassword <password>`; `signtool` is looked up in the Windows SDK, the timestamp server is `-TimestampUrl` (DigiCert by default), followed by `signtool verify /pa`;
  4. launch check: `Mixer.exe --no-audio --snapshot` in a temporary folder with its own graph and log; devices aren't opened, bindings aren't touched; the window must open and close within 60 s;
  5. `dist\Mixer.exe.sha256`.
- **README screenshots** (`docs/images`) are cut from the design mockup (`Windows Mixer — mockup.html`), one board per page, rendered by headless Edge at 2× scale.

### Deviations from the specification

27. **No installer** (decided 10 November 2026): the program is a single exe, and its data lives in `%AppData%\Mixer` and `%LocalAppData%\Mixer`. Uninstalling means deleting the exe and these folders.
28. **No code signing yet**: there is no certificate. The script signs if one is provided. Without a signature, SmartScreen shows "Windows protected your PC" on first start; the README explains this.
29. **The 8-hour stress test** was run by the user on 10 November 2026, without issues according to the user; it was not repeated here.

### Status as of 10 November 2026

- Done: publish profile, release script, icon, version 1.0.0, user README in English with screenshots. `tools/publish.ps1` ran end to end: 162 tests, build, launch check of the published exe. The solution builds without warnings.
- Acceptance criteria are in the plan, section «Критерии приёмки», marked with what has been verified.

## v2 stage 1: buses

### How it works

- **Bus node** (`kind: bus`) sits in the middle column of the canvas. It has an input port on the left, an output port on the right, a fader, mute, a meter and a processing chain. Wires go source → bus, bus → bus and bus → output; a source still can't receive a wire and an output still can't send one.
- **No clock of its own.** In v1 each output summed its own wires. In v2 each output, in its own period, computes the buses it is reached from (`MixPlanner`): all buses with a path to the output, in topological order (Kahn). A bus that leads to two outputs exists twice in the engine (`BusInstance`, one per output) with its own processing state, as the specification asks. A wire from a source into such a bus becomes two rings with two resamplers, one per output clock (`Wire.OutputId`); the v1 adaptive resampler stays on the wire. A wire out of a bus is a `BusLink`: same clock, no ring, only a gain ramp and a meter.
- **One immutable plan per output** (`MixPlan`): steps (a bus with the wires and links that feed it), then direct wires, then links from buses. `AudioEngine.Apply` builds it and swaps it with one reference, as it did with the wire arrays. Bus instances, rings and links are reused across `Apply` while their output node is the same, so a fader move doesn't reset processing state. Removed wires and links are faded out first (5 ms mute ramp), as before.
- **Checks** (`ConnectionRules`, `GraphPaths`):
  - a wire that closes a cycle through buses is forbidden; the reason shows the cycle: «Цикл через шины: «A» → «B» → «A»»;
  - no more than 2 buses on any source → output path;
  - the v1 endpoint rule now looks at every source above the wire and every output below it through buses. Loopback of the headphones into a bus that leads to the headphones is a loop, and the reason shows the path: «… Путь петли: «Звук наушников» → «Игра + чат» → «Наушники»».
- **Graph building** (`GraphBuilder`): buses go to the engine as `BusSpec`. Cycles from a hand-edited file are dropped with an `Invalid` issue (the first wire in the file wins). A loop that appears later (the default device changed) drops only the source's wire on the loop path, and only that wire is drawn red: the bus's own wires carry other sources too. An application's capture rate is picked from every output it reaches through buses. The double-sound warning also looks through buses.
- **Group into bus** (`GraphEdits.GroupIntoBus`): select two or more sources or buses (Ctrl + click) and press «Сгруппировать в шину» in the toolbar or in the node menu. The new bus gets a wire from every selected node and takes over their common outputs (targets every selected node feeds with an enabled wire); the direct wires to those outputs are removed. Levels don't change: every old gain g(source, output) is split as a(source) + b(output), with a on the source → bus wire and b on the bus → output wire. Wires out of the bus are at most 0 dB. «Разгруппировать» (bus menu) does the reverse: every input goes straight to every output at input + bus fader + output gain.
- **Canvas**: three columns; a bus is added with «+ Шина», or by right-clicking empty space in the middle column. A bus without inputs or without outputs is drawn dimmed with the tooltip «ничего не слышно». A wire leaving a bus is grey. The footer counts buses. While a wire is dragged from a bus, the side being dragged decides the targets: from the output port — buses and outputs; from the input port — sources and buses.
- **`graph.json` version 2.** A v1 file is read as is; before the first save it is copied to `graph.v1.json` next to it (`GraphStore.Upgrade`).

### Deviations from the specification

30. **The `graph.json` version and the `graph.v1.json` copy are done now, not in stage 5.** v1 can't read `kind: bus` or `chain`, so a v2 graph saved with version 1 would make v1 move it aside as broken. The rest of the migration (cable pairing, the «Сгруппировать в шину?» offer) is still stage 5.
31. **«Not more than 2 levels» is read as «at most 2 buses on any path».** Grouping that would make a third level is refused with the same reason.
32. **Grouping keeps levels exactly or not at all.** If the selected sources reach a common output at levels that can't be split as a(source) + b(output) (for example, the microphone is 6 dB louder than the game in the headphones but 6 dB quieter in the stream), that output stays on direct wires, and the message says so. Grouping without any common output still creates the bus with inputs only.
33. **Ungrouping isn't specified in detail.** A disabled wire or a muted bus gives a disabled direct wire; a direct wire that already exists is kept as is; the bus's processing is lost (the message says so).
34. **A loop through a bus after a device change drops only the source wire**, not the bus wires (see «How it works»).

### Tests

`BusTests`, `UiBusTests`:

- planner: per-output instances in topological order, a bus that leads nowhere is not computed, a cycle is skipped;
- mixing in the output period with synthetic sources: bus fader, wire and link gains, bus → bus → output plus a direct wire, muted bus and disabled link;
- checks: both drag directions, a cycle with its path, three buses in a row, loopback through a bus with its path (both ways), CABLE Output through a bus;
- builder: buses and their wires, capture rate through buses, a loop after a device change drops only the source wire, a cycle from a file;
- grouping keeps every level (a non-decomposable output stays direct), no common outputs, ungrouping restores levels, a muted bus, three levels refused;
- layout, `graph.json` round trip with a bus and a chain, clamping of hand-edited values, the `graph.v1.json` copy, chain edits are parameter changes;
- view models: a new bus in the middle column, Ctrl + click and grouping as one undo step, ungrouping from the menu, port highlighting from both bus ports, a silent bus.

## v2 stage 2: processing

### How it works

- **Chain** (`ProcessingChain`, one per engine node: source capture, bus instance, output): gate → EQ → compressor, then the node's fader and mute; on outputs the v1 limiter stays last. Our own C#, `Span<float>`, no allocations (a test checks it, including parameter changes). No lookahead, no added latency.
  - **Gate**: shared stereo peak detector (instant attack, 30 ms release, so it doesn't fall between the periods of a low note), threshold, hold, then the gain moves to 1 with the attack time and to the depth with the release time.
  - **EQ**: high-pass 40–300 Hz (RBJ, Q 1/√2, 12 dB/oct) and four bands (peak, low shelf, high shelf, off), RBJ Audio EQ Cookbook in double, direct form I.
  - **Compressor**: soft knee (Giannoulis–Massberg–Reiss gain computer), shared stereo detector, decoupled peak detector on the gain reduction (release holds the peak, attack smooths it), makeup.
- **Parameters arrive in the routing snapshot** (`ChainSettings`, immutable) and the audio thread moves every parameter to the new value over 10 ms: frequencies on a log scale, gains in dB, compressor thresholds sample by sample. Biquad coefficients are recomputed every 16 frames while a transition is running. A band changes type (or turns off) through 0 dB, where peak and shelves are exactly unity, and waits there for two transition times before switching. Turning a block on or off is a 10 ms S-curve crossfade with the dry signal; a block that is fully off isn't computed at all.
- **Applications with processing**: an App or All system audio node can be several captures. Processing per capture would compress each application on its own, so once such a node has a chain, its captures (at 0 dB) are summed into an implicit bus `id#fx` that carries the node's chain, fader, balance and mute. The implicit bus stays when blocks are switched off, so toggling a block doesn't rebuild wires.
- **On the card**: chips of enabled blocks (Gate · EQ · Comp) and the current compressor reduction (`−4 dB`, held 0.5 s); «+ FX» when nothing is on. A chip, «+ FX» or «Обработка…» in the node menu opens the side panel.
- **Side panel** (`ProcessingPanel`): preset (built-in and own), «Сохранить как…», gate with its level bar and threshold mark and «открыт/закрыт», EQ curve with draggable points (drag: frequency and gain; wheel: Q; right click: band type; double click: 0 dB; Shift: frequency only) and a table of bands (wheel over a value changes it), compressor with a reduction bar, «До» and «После» meters at the bottom. Every knob move is a graph edit through the history, merged into one undo step per drag, so Ctrl+Z works in the panel too.
- **Presets** (`ChainPresets`): «Голос — чистый», «Голос — эфирный», «Музыка потише и ровнее». A preset is copied into the node, not linked. Own presets are stored in `settings.json` next to `graph.json`.

### Deviations from the specification

35. **Biquad coefficients are computed by the audio thread**, not the UI thread. Only the audio thread knows the device rate (it is known after the device opens and can change on hot-plug); the computation is a handful of `Math` calls per band every 16 frames during a 10 ms transition and allocates nothing. The UI computes the same response for the curve.
36. **Direct form I.** The transposed form II gave a −37 dB transient while a shelf's gain was being turned (its state depends on the coefficients); direct form I keeps past inputs and outputs and stays below −70 dB.
37. **Processing on application nodes goes through an implicit bus** (see «How it works»). The specification doesn't say where a multi-capture node is processed.
38. **Preset values are a starting point, not tuned by ear.** The specification leaves tuning by ear to stage 2; that needs a listening session with a real microphone.
39. **The gate panel shows all five parameters** (the mockup shows threshold and release only).

### Tests

`ProcessingTests`, `ProcessingBudgetTests`, `UiBusTests`:

- **EQ response matches the design within ±0.1 dB** (criterion): a high-pass and four bands of every type, measured with a sine fit at 11 frequencies from 30 Hz to 19 kHz against |H| from the RBJ formulas. Measured error: under 0.001 dB. RBJ design points: peak gain at its centre, −3.01 dB at the high-pass cutoff, 12 dB/oct, shelves.
- **No clicks while turning knobs on a 1 kHz sine** (criterion): every parameter is swept over its range and back with a new value every 10 ms. The detector is the residual above 6 kHz (four Butterworth high-passes); the limit is −60 dB below the signal. A test checks the detector itself: a 1 dB gain step without a ramp is caught, a clean sine is not. Results: EQ frequency −78, gain −87, Q −127, shelf −72, high-pass −124; compressor −83…−124; gate −97…−128; turning blocks, the high-pass and bands on and off and switching band types −70…−120.
- compressor static curve on a sine (−13.50 dBFS against −13.51 expected) and the reduction meter; the shared detector keeps the stereo image; gate: open, hold, closed at −60 dB;
- a disabled chain and a block that has faded out pass audio bit-exact;
- no allocations while processing and changing parameters;
- **CPU: the full chain on a stereo node takes 0.16% of one core** (budget 0.3%, Release, 10 s of noise, best of 5, not in parallel with other tests);
- presets are within the specification ranges; values from a hand-edited file are clamped;
- view models: chips follow the chain; a knob drag is one undo step and Ctrl+Z brings the panel back; presets are applied and recognised; band type and curve drag; deleting the node closes its panel; log slider mapping.

### Status as of 10 November 2026

- Done: buses, checks, grouping, processing, side panel, presets. 231 tests pass (2 manual tuning sweeps skipped). The solution builds without warnings.
- Checked: the look of the canvas with buses and of the side panel, from snapshots in `--no-audio` mode with the streamer layout from the v2 mockup (`--open fx:<node>[:gate|eq|comp]` opens the panel on start). The user's own v1 graph was upgraded by the new build (`graph.v1.json` written) and played through it without errors in the log.
- Checked by ear by the user on 10 November 2026 (buses and processing on real devices): no issues, according to the user; not repeated here.
- **Not checked**: CPU of the 8-node layout with full chains (criterion: 5% on a 4-core CPU).

## v2 stage 3: applications

### How it works

- **Exclusions in All system audio** (`exclude: ["telegram.exe", …]` on the System node). The node menu has «Исключить приложения»: excluded exes (checked; a click brings one back), the applications playing right now (a click excludes one) and a box for any exe name, so an application can be excluded before it makes a sound. Excluded exes join the same banned set as applications with their own node (`GraphBuilder.SystemExclusions`), so the node is assembled from per-process include captures (`SystemCapture`) as soon as the set is not empty.
- **Keep applications that have their own node** (`includeOwnNodes`, «Не исключать приложения со своим узлом», off by default). With it on, applications with a node are not banned. With an empty exclusion list the node goes back to the single exclude capture. The double-sound warning now fires whenever All system audio contains an application with its own node and both reach the same output, in either mode.
- **What was skipped, and why.** `SystemPlan.Skipped` is a list of `SkippedProcess(exe, reason, because)`: the mixer is in its process tree (`ContainsMixer`, checked first: it would be a loop), an excluded application is in its tree (`ContainsExcluded`, naming that application), or the 24-capture ceiling was reached (`CaptureLimit`). The card shows a «пропущено: N» badge whose tooltip lists every process with its reason. The subtitle reads «всё, кроме Discord, telegram.exe».
- **Fast pickup of new applications** (`SessionNotifier`). The host subscribes to `IAudioSessionNotification::OnSessionCreated` on every playback device while the graph has App or System nodes, from its MTA control thread and after `GetSessionEnumerator`. The callback, on an audio service thread, only posts one coalesced job (the mixer's own sessions are ignored). The job takes a process and session snapshot (8–11 ms in stage 0) and rebuilds the graph if the capture set changed. The log notes how long that took from the notification («new audio session handled in N ms»). The subscription is recreated when the device list changes and after sleep. The 1 s tick stays as a fallback.
- **Direct routes** (`GraphBuilder.DirectRouteOf`, `DirectRoute`). An App node goes direct when all of the following hold:
  - Take over is on and `directRoute` is not off;
  - exactly one enabled wire, into an output rather than a bus;
  - no active processing on the node or on that output;
  - no mono or balance;
  - node + wire + output gain is at most 0 dB;
  - the output is present and is not the sink.

  A direct node has no captures and no engine wires. Instead `TakeOverManager` binds the application to the output itself (each exe now has its own target: the sink or the output of its direct route, `AppBinding.Target`). The `boundApps` journal covers direct bindings too, so they are reset on exit and after a crash.
- **Switching** (`MixerHost.Rebuild`). Capture → direct: the application is bound to the output first (a partial `Reconcile`), then `Engine.Apply` fades out its capture. Direct → capture: `Engine.Apply` opens the capture with its wires switched off (the output still reads them, so the rings fill, but nothing is heard while the application still plays directly). `WaitWarm` waits until every capture has a running wire, at most 300 ms (51–60 ms on the device). Then the application moves to the sink and the wires switch on in the same instant. This is the stage 0 measurement setup, where the capture was already running at `Set`. Neither path waits for a session on the new device.
- **A sink and an output on the same cable** (`FeedbackRules.SameCable`). An output on «CABLE In 16ch» while the sink is «CABLE Input» (or the other way round) gets a `SinkConflict` warning. Only one input of a VB-Cable can be open at a time, so applications moved to the sink fall silent or stop.
- **Session volume and meter** (`DirectVolumes`, `AppSession`). The node level and mute (node + wire + output, at most 0 dB, mute of the node or the output) become `ISimpleAudioVolume` on the application's sessions on that output, including inactive ones, so the level is right before the first sound. A new session gets it on `OnSessionCreated`. The value is set only when it differs, so other volume controls aren't woken for nothing. The meter is the sessions' `IAudioMeterInformation`, polled 30 times a second by the control thread, multiplied by the session volume (`MeterIsPreVolume`, see deviation 43) and shown on the card and the wire.
- **Cut-off labels** (`TrimTip`): every label that ends with an ellipsis (card name and subtitle, the toolbar hint, palette and settings rows, the processing panel title and preset) shows its full text on hover, above the label's own tooltip if it has one. The toolbar is now a three-column grid, so the hint is cut with an ellipsis instead of sliding under the zoom buttons.
- **The `volumeApps` journal.** Windows keeps a session volume per application and device. So an exe is written to `volumeApps` in `graph.json` before its volume is first lowered. When the node leaves the direct route, its sessions go back to 100% without mute after the binding has moved. On exit this happens after the bindings are reset. After a crash it happens on the next start, as soon as the application is running (all its sessions on all playback devices). Only then is the exe struck from the journal.
- **On the canvas**: the direct wire's pill shows a lightning bolt and «напрямую». Its tooltip reads «Прямой маршрут · 0 мс», «Firefox играет прямо в «Наушники», мимо микшера; микшер задаёт только устройство и громкость». The card subtitle is «приложение · напрямую в Наушники». The footer counts direct wires («3 провода (1 напрямую)»). The node menu has «Прямой маршрут» (available while Take over is on). `Mixer.Run run` prints direct routes with their session peaks, the volume journal and skipped processes.

### Deviations from the specification

40. **Two more direct-route conditions: no mono or balance on the node, no processing on the output.** Session volume can't reproduce mono or balance, and an application playing straight into the device would bypass the output's EQ and compressor. The output limiter is bypassed too, which is harmless at ≤ 0 dB.
41. **A direct route needs Take over.** It is a binding made by the mixer, like Take over; with Take over off the user has asked the mixer not to move the application. On the other hand it needs neither a sink nor process loopback: on Windows 10 before 20348 an App node on a direct route works, and only nodes that need a capture show «недоступно».
42. **The `volumeApps` journal goes beyond the specification.** Without it a crash would leave the application quieter on that device until someone moved its slider in the Windows volume mixer.
43. **The session meter reads before the session volume** and is multiplied by it. The documentation says so for the endpoint meter and says nothing about sessions. Checked on 11 November 2026: a −70 dBFS tone on a direct route gave a raw session peak of −70.0 dBFS at both 0 dB and −6 dB, and the card value −76.0 at −6 dB.
44. **Keeping applications with their own node and an empty exclusion list gives the single exclude capture**, the cheapest form; with exclusions it gives per-process captures in which those applications are not banned.
45. **The mixer takes precedence among skip reasons**: explorer.exe, the parent of both the mixer and Discord, is reported as «в его дереве процессов работает микшер».
46. The skipped-process list used to be in the node tooltip; it is now the «пропущено: N» badge, as the specification asks. The tooltip keeps what is captured and what is excluded.
47. **Direct → capture warms the capture up first** (see «How it works»). Opening it at the moment of `Set` gave a 95 ms gap: the first packet, filling the ring and the output queue all came after the application had left the output.
48. **The direct → capture gap is 55–70 ms, not ≤ 50 ms.** The application's stream has a hole while Windows moves it, and process loopback sees the same hole (stage 0). On top of that, the wire refills its ring to the 20 ms target after an underrun before it plays again, the protection against crackle from stage 1. The stage 0 tool has no such refill, and on the same day it showed 50 ms. Making the switch shorter would mean relaxing that protection at the switch, so it is left as is. Capture → direct is 0 ms.
49. The same-cable sink warning goes beyond the specification. It was found during the device check, where a test output on CABLE In 16ch made every application moved to CABLE Input stop.

### Tests

`AppsV2Tests`, plus updates in `AppTests` and `EditorTests`:

- exclusion of `telegram.exe` without its own node: Telegram is not captured, the others are (criterion); exclusion edits normalize the name, don't duplicate it and are structural changes; `includeOwnNodes` falls back to one capture and warns about double sound only on a shared output; with an exclusion list it keeps the app in per-process captures and warns;
- skip reasons (mixer, excluded application with its name, capture limit) and the tooltip text;
- direct route: one wire to an output goes direct with the summed gain and mute, no sources and no engine wires; 13 cases that keep the node on capture (second wire, bus, node or output processing, above 0 dB, mono, balance, switched off, no Take over, bindings unavailable, output disconnected, output is the sink, wire off); a disabled second wire doesn't matter; works without the app running, without a sink and without process loopback;
- bindings per exe: direct and sink targets side by side, a target change rebinds, a partial reconcile neither releases nor clears status, «restart playback» is measured against the target;
- session volume on fake sessions: the journal is written before the first change, only the app's sessions on that output change, leaving the direct route restores 100% and clears the journal, a crash journal waits until the app runs, a journal failure leaves the volume alone, the meter follows the level and mute, exit restores everything;
- an output on the other input of the sink cable (CABLE In 16ch with the CABLE Input sink) is a sink conflict, another cable is not;
- `graph.json` round trip of `exclude`, `includeOwnNodes`, `directRoute`, `volumeApps`; the store doesn't let the UI graph and the host's volume journal overwrite each other.

### Running

```
Mixer.Run.exe run graph.json --watch --envelope lb --envelope-csv env.csv
```

`--watch` applies edits of `graph.json` while running, as edits from the UI would be. `--envelope <node>` records a 5 ms RMS envelope of that source (put a loopback of the output on the graph without wires). At the end it reports, for every edit, the longest gap and the amount of double sound against the level before the edit. `run` also prints direct routes with their session peaks, the volume journal and skipped processes.

### Device check, 11 November 2026

The application was a `Mixer.Probe tone` (−70 dBFS, automatic stream routing). The direct-route output was the fifine speakers, the sink CABLE Input, and a loopback of the fifine speakers measured the envelope (`--envelope`). Mixer.exe was closed. The runs were made by the user with a script, after the auto-mode classifier stopped further device probes from the session.

1. **Direct route**: the binding moved the tone from Realtek Digital Output to the fifine speakers within 1 s of start, without restarting playback. Mixer CPU was 0.16–0.37% of the machine with no captures.
2. **Session volume**: −6 dB on the node lowered the tone in the fifine loopback from −73.0 to −79.0 dB RMS. Back at 0 dB, the volume returned to 100% and the exe was struck from the journal. On the meter, see deviation 43.
3. **`OnSessionCreated`**: a tone started while the mixer was running got its direct binding «new audio session handled in 35 ms from the notification».
4. **Switching**: 4 cycles of a second wire to Realtek Digital Output on and off.

   | Direction | Longest gap | Double sound |
   | --- | --- | --- |
   | direct → capture, capture opened at `Set` | 95 ms (all 4 cycles) | 0 ms |
   | direct → capture, capture warmed up first (final) | 55–70 ms | 0 ms |
   | capture → direct | 0 ms | 0–10 ms |
   | stage 0 tool on the same devices, same day (control) | 50 ms / 15 ms | 0 ms |

5. **Found on the way: two inputs of one cable.** In the first runs the second output was CABLE In 16ch. While the mixer held it open, every move of the tone into CABLE Input ended with `AUDCLNT_E_NOT_INITIALIZED`, and the tone stopped. Variants with a stereo capture format, a capture 3 s old, no session handles and a fresh exe all failed the same way. With CABLE Input kept open by the mixer, the mixer reported CABLE In 16ch as «занято другим приложением», and the tone survived. Hence the new warning (deviation 49).
6. **The fifine speakers were unreliable at times.** In the first two runs `IAudioClient.Initialize` hung for 22 s and more than 60 s right at start, before any binding touched the device; later runs opened them in 1–6 s.

### Status as of 11 November 2026

- Done: exclusions and skipped processes, `OnSessionCreated`, direct routes with session volume and meter, the same-cable sink warning. 264 tests pass (2 manual tuning sweeps skipped). The solution builds without warnings.
- Checked on devices: the direct route, session volume and meter, pickup of a new application (35 ms), switching gaps (see the table).
- **Not checked on devices**: Discord and a game (not used on this machine), exclusions and the skipped badge with real processes, the volume journal after killing the mixer, the UI with the engine (the direct pill, the skipped badge).

## v2 stage 4: low latency

### How it works

- **`IAudioClient3` per node** (`lowLatency` in `graph.json`; «Низкая задержка» in the node menu). The node thread asks the driver for its engine periods (`GetSharedModeEnginePeriod`) and opens the stream with `InitializeSharedAudioStream` at the minimum period (`LowLatencyStream`, `AudioNode.OpenShared`). The period actually in force is read back with `GetCurrentSharedModeEnginePeriod`: if another program already holds the device engine at some other small period, that one stays. Defaults: on for outputs, off for device inputs (experimental there, as the specification asks), not available on loopback and application captures (their period is set by someone else's stream).
- **Where it changes nothing.** A driver whose minimum is its default period (every physical card on the developer machine: 480 frames) is opened with the plain `Initialize`, exactly as in v1. If `IAudioClient3` is missing or `InitializeSharedAudioStream` fails (for example `AUDCLNT_E_ENGINE_PERIODICITY_LOCKED`), the client is released, a new one is activated, and the node opens with the plain `Initialize`. The status is kept per node (`LowLatencyInfo`: Active, Unsupported, Unavailable, FellBack) and shown in the card tooltip and the log.
- **Output queue.** With a small period the engine keeps the device queue at period + max(period, 2 ms) (`OutputNode.QueueFramesFor`): 256 frames (5.3 ms) at 128 frames. The usual period keeps the v1 15 ms.
- **Adaptive ring target** (`Wire.TargetSeconds`): two periods of the slower end of the wire, but at least 4 ms. The source publishes its period in `SourceClock.Started`, and the output passes its own through `MixPlan.Render` to every wire, including wires into buses. At 10 ms on both ends this is 20 ms, as in v1. A wire whose period changes (an output fell back) re-primes with the new target.
- **Auto fallback** (`GlitchWindow`): more than 3 glitches within 60 s while the small period is active, and the node reopens at once with the usual period. Glitches are an empty device buffer at wake-up (output), `DATA_DISCONTINUITY` after the first packet (input), and an underrun on a wire whose target was lowered by the small period (deviation 51). The engine remembers the fallback per node and device (`AudioEngine.Fallbacks`), so a node recreated after sleep, hot-plug or a rebuild starts at the usual period right away. The memory is cleared when the user turns the mode off; turning it off and on again is the way to retry.
- **On the card**: the tooltip says «Низкая задержка: период 2,7 мс вместо 10 мс», «Низкая задержка недоступна: драйвер не даёт период меньше 10 мс» or why `IAudioClient3` didn't start. A fallback is a warning: «Низкая задержка выключена: больше 3 сбоев за минуту. Узел вернулся на обычный период 10 мс. Чтобы попробовать снова, выключите и включите «Низкая задержка» в меню узла». The log records every change of the state («low latency on, period 128 frames (2.67 ms, default 480)»).
- `Mixer.Run run` prints the period and glitch count of every node with the mode on. `Mixer.Run latency --low-latency` (both ends) or `--low-latency-out` (output only) measures the end-to-end loop latency with the mode on, for the 25 ms criterion on a card with small periods.

### Deviations from the specification

50. **Off by default on virtual cable outputs.** VB-Cable reports 128 frames, but in stage 1 CABLE Input consumed audio 1% slower than real time and left its buffer empty thousands of times an hour already at 10 ms. With a 5 ms queue such an output would glitch and fall back on every start. The specification also says the mixer can't shorten the VB-Cable path. It can still be turned on by hand.
51. **Wire underruns count towards the fallback** when the wire's target was lowered by the small period. Otherwise a machine whose threads wake up too late for a 5.3 ms target would crackle without ever falling back: the device buffer itself doesn't run dry, the ring does.
52. **The underrun fade is a quarter of the ring target, 1–2 ms** (v1: 2 ms). The ring keeps a fade's worth of audio in reserve; at a 5.3 ms target a 2 ms reserve left 0.7 ms for late wake-ups. Wire model with 128-frame periods on both ends, 60 s: with 0…1 ms wake-up jitter, ~100 underruns a minute before, 0 after; with 0…1.5 ms, 8 a minute (the fallback's job); with 0…0.5 ms, 0 either way. At the usual 20 ms target the fade is 2 ms as before.
53. **A driver without small periods is opened with the plain `Initialize`**, not with `InitializeSharedAudioStream` at the default period: that is the v1 path, checked on devices in stages 1–3.
54. **The fallback is remembered while the program runs**, not across restarts: a new start tries again.

### Tests

`LowLatencyTests`:

- ring target: 20 ms at the usual period on both ends, 5.33 ms at 128 frames, the slower end decides, 4 ms minimum;
- wire model at 128 frames on both ends, 0…1 ms wake-up jitter, +100 ppm, 60 s: no glitches, fill 5.3 ms, wire latency 6 ms; 10 ms ↔ 2.67 ms ends keep 20 ms without glitches; the target follows an output that falls back; the plan passes the output period to wires into buses and direct wires;
- an underrun on a lowered target is reported to the output, on the usual target it isn't;
- output queue: 15 ms as in v1, two periods, the 2 ms margin, capped by the device buffer;
- glitch window: the 4th glitch within a minute trips it, one every 25 s doesn't, the window slides;
- a node created with a remembered fallback starts at the usual period, a node with the mode off forgets it; loopback and application captures have no mode;
- defaults (outputs on, inputs off, loopback never, VB-Cable output off), the engine snapshot, `graph.json` round trip (defaults aren't written), a change is a structural edit; the menu toggle writes only non-default values and undoes; card tooltip texts; log lines.

**Not covered by tests**: the `IAudioClient3` calls themselves and the fallback memory in `AudioEngine` (engine nodes open devices).

### Status as of 11 November 2026

- Done: `IAudioClient3` per node with fallback to the v1 path, auto fallback, adaptive ring target, menu, tooltip, warning, log, `Mixer.Run` switches.
- **Not checked on devices.** No physical card on the developer machine has periods below 480 frames (stage 0), so the mode changes nothing on them: their nodes take the v1 path. VB-Cable (128 frames) is off by default. The 25 ms criterion needs another PC: `Mixer.Probe lowlat --list` must show a minimum below 480, then `Mixer.Run latency --in <line in> --out <line out> --low-latency` with a cable from the output to the input.

## v2 stage 5: migration and release 2.0

### How it works

- **Migration of `graph.json`.** The file version and the `graph.v1.json` copy were done in stage 1 (deviation 30). `GraphStore.LoadOrEmpty` now also reports that the file was upgraded, and the log says so («graph upgraded to version 2, original kept next to it»). The graph itself moves over as is: v2 fields are empty, processing is off, there are no buses.
- **«Сгруппировать в шину?» after migration** (`GraphEdits.GroupSuggestions`, `MainViewModel.OfferGrouping`). Sources whose enabled wires go to the same set of at least two outputs (directly, not through a bus) are offered as a group, one offer at a time, in a panel at the bottom of the canvas: «Схема перенесена с версии 1. «Игра» и «Discord» идут в одни и те же выходы («Наушники» и «Стрим»). Сгруппировать в шину? …» with «Сгруппировать в шину» and «Не нужно». Accepting is the stage 1 grouping (levels kept, one undo step). An offer whose nodes were deleted meanwhile is skipped. Nothing changes until the user chooses.
- **Version 2.0.0** (`Directory.Build.props`); the user guide (README) describes buses, processing, exclusions, direct routes, low latency and upgrading from 1.0.

### Deviations from the specification

55. **Cable pairing is not part of the 2.0 migration.** Cables as entities (pairing table, roles) are stage 6, release 2.1; in 2.0 there is nothing to pair them into. CABLE Input and CABLE Output nodes move over as ordinary device nodes.
56. **A group is offered only for two or more common outputs.** With one output a bus saves no wires. The specification only says «одинаковый набор выходов».
57. **The offer appears once per upgrade**: on the first start after the file is upgraded. If the user changes nothing, the file stays version 1 on disk and the offer appears again on the next start.

### Acceptance criteria 2.0

| Criterion | Result |
| --- | --- |
| Streamer layout (4 sources, 2 buses, 2 outputs) works 8 hours; CPU ≤ 5% on a 4-core CPU with processing on every node | **Computation measured, the 8-hour run not done.** `StreamerBudgetTests`: sources with full chains, rings with resamplers (44.1 → 48 kHz for the microphone), the «game + chat» bus computed twice (two outputs), outputs with chains and the limiter: 2.3% of one core, 0.6% of a 4-core CPU. Thread wake-ups, WASAPI and the UI come on top (0.07–0.4% of the machine in the stage 1–3 runs). The 8-hour run on devices is for the user |
| A wire closing a cycle through buses or loopback can't be created; the hint shows the loop path | met: `BusTests` (stage 1) |
| Turning any EQ, compressor or gate knob on a 1 kHz sine doesn't click; EQ response within ±0.1 dB | met: `ProcessingTests` (stage 2): residual −70 dB or lower, response error under 0.001 dB |
| All system audio with telegram.exe excluded (no node of its own): Telegram isn't in it, the others are; skipped processes are listed with their reason | met by tests (`AppsV2Tests`, stage 3); not checked with real processes |
| A direct route adds no latency or mixer CPU; switching gaps ≤ 50 ms | **partly**: capture → direct 0 ms, direct → capture 55–70 ms on the device (deviation 48) |
| A new application gets into All system audio within 100 ms of its first sound | met: 35 ms from `OnSessionCreated`, which arrives at the application's `Initialize`, before its first sound (stage 3, on the device) |
| Microphone → headphones ≤ 25 ms on a card with small buffers | **not checked**: no such card on the developer machine (stage 4). In the model the wire takes 6 ms at 128 frames instead of 20 ms |
| A v1 layout sounds the same after migration; `graph.v1.json` is kept | met: `MigrationV2Tests` (a v1 file gives the same engine snapshot: sources, outputs, wires, levels, no processing or buses; the copy is byte-identical and isn't overwritten). The user's own v1 graph was upgraded and played through on 10 November |

## Localization

All strings added in v2 are in `Strings.resx` / `Strings.ru.resx` (and `CoreStrings`, `AudioStrings`); `LocalizationTests` check that both languages have the same keys and placeholders. A scan for Cyrillic literals in UI code found only the quotes of the grouping offer, which now come from the resources too («…» in Russian, “…” in English). Snapshots of the offer in both languages were checked with `--no-audio --snapshot`.

### Tests

`MigrationV2Tests`, `StreamerBudgetTests`: v1 file → the same engine snapshot, the `graph.v1.json` copy, upgraded once; group suggestions (same outputs, one output, already in a bus); the offer: text, grouping as one undo step with every level kept, dismissing, an offer whose node was deleted; the streamer CPU budget. 293 tests pass (2 manual tuning sweeps skipped). The solution builds without warnings.

### Status as of 11 November 2026

- Done: migration with the grouping offer, acceptance criteria run (table above), localization check, version 2.0.0, README. `tools/publish.ps1` ran end to end: 293 tests, `dist\Mixer.exe` 68.3 MB, launch check of the published exe.
- **Not done**: the 8-hour run of the streamer layout on devices, the 25 ms criterion (another PC), Discord and a game on a direct route. The GitHub release is made by pushing the `v2.0.0` tag (see README, «For developers»).

## v2 stage 6: virtual cables and the setup assistant

### How it works

- **Pairing table** (`Mixer.Core/Cables/CableTable.cs`, `cables.json`). A device's Windows name is «Description (Driver)». A table entry names the driver and the descriptions of the cable's playback ends (inputs, main one first) and recording end (output); `*` is a wildcard. Built in: VB-Cable (CABLE Input and CABLE In 16ch → CABLE Output), Cable A–D, Hi-Fi Cable, Virtual Audio Cable («Line N» on both ends, `pairByName`). The table is an embedded resource of the exe; a `cables.json` next to `Mixer.exe` adds entries checked before the built-in ones. `Pairs(devices)` lists cables found among the devices, even with one end missing (`Complete` says whether both are there).
- **The loop rules use the table.** `FeedbackRules.PairedCableInputs` and `SameCable` take the pair from the table, so «Line 1» of Virtual Audio Cable no longer counts as a loop with «Line 2»; a driver that isn't in the table keeps the v1 heuristic (VB-Audio + «Cable»). A cable linked by hand adds its other end to the source's taps.
- **A cable is a node with a role** (`cable: { pairId, role, partnerId?, partnerName? }`). The role follows how the mixer uses the cable: an output on the cable's input is a *virtual mic* (the mixer plays, another app records), an input on the cable's output is a *virtual input*. Nodes on table cables get the link by themselves (`CableTable.LinkOf`), so it is written to the file only by the palette, the assistant, the v1 migration and manual linking. In the palette the cable is one row under «Виртуальные кабели» (after the outputs, before the inputs), with its main end hidden from the plain device list. The card reads «кабель-микрофон · CABLE-A» / «кабель-вход · CABLE»; the tooltip names both ends; the footer lists the cables and who uses them («Кабели: CABLE-A (Микрофон стрима) · CABLE-B свободен»).
- **Manual linking**: node menu → «Связать как виртуальный кабель» on an input or output that isn't a table cable, pick the device at the other end (`GraphEdits.LinkCable`); «Отвязать кабель» undoes it.
- **«Использовать как микрофон в…»** (`micFor` on a virtual mic). The host reuses `TakeOverManager` with the capture flow of `IAudioPolicyConfig` (`WindowsEndpointPolicy.ForCapture`) and its own journal `micApps`: the exe goes to the journal before the first `Set`, the cable's output is set for the Console, Multimedia and Communications roles on the app's PIDs, checked with `Get`, and the binding is removed when the exe leaves every `micFor` (unticked or node deleted). Unlike Take over it is **not** released on exit: the app keeps recording from the cable, as after Earmark. The menu offers OBS Studio, the call apps, the apps playing now and a box for any exe, and reminds that the app must have «По умолчанию» selected. The card shows «Микрофон для: OBS Studio» and, in its tooltip, the state per app.
- **Checks** (`CableChecks`, every host tick while the graph has cables): another process plays into a virtual mic (active render sessions on the cable's inputs, the mixer's tree excluded); the two ends run at different rates (mix formats, cached like output rates); an app with the cable assigned records from another device (capture sessions of all recording devices, read only while some node has `micFor`). Each is a card warning with what to do.
- **Migration**: a v1 `graph.json` gets cable links on upgrade (`GraphStore.LinkCables`, role from how the mixer used the cable), the item left over from stage 5.
- **Setup assistant** (`SetupAssistant.Build`, `SetupViewModel`, `SetupView`; mockup «Setup assistant, step 2»). Step 1: headphones and microphone (the Windows defaults, cables and Voicemeeter hidden). Step 2: «Только наушники», «Стрим (OBS)», «Звонок (Discord, Zoom)», with the cable check: cables needed, found (names), «Проверить снова», a link to vb-audio.com; the step re-reads devices every 3 s, so a cable installed meanwhile shows up by itself; «Далее» waits for enough cables. Step 3: apps to keep separate (the catalog without OBS and the call apps) and «Запускать вместе с Windows». «Собрать схему» replaces the layout in one undo step (the sink and the journals stay) and says what to do in the apps. It opens by itself on the first start with an empty layout (`settings.json`: `setupShown`), from the empty canvas and from Settings.
- **The layout it builds**: the microphone with «Голос — чистый»; for a stream, «Весь звук системы» and the microphone → «Микрофон стрима» on the first cable, `micFor: [obs64.exe]`; for a call, the microphone → «Микрофон звонка» on the next cable, `micFor`: Discord and the call apps that are running. «Весь звук системы» is not wired to the headphones: the apps already play there. A separate app becomes an App node wired to the headphones only: that is a direct route (one volume, no capture, no sink), and «Весь звук системы» leaves it out, so it stays off the stream. The main microphone is set to the new microphone node.

### Deviations from the specification

58. **The table lives inside the exe.** The release is a single `Mixer.exe`, so «JSON next to the program» is the embedded `cables.json`, updated with the program. A `cables.json` next to the exe extends it.
59. **A manual link is stored in the node** (`partnerId`, `partnerName`), not in a shared table: the node already holds its own device, and the link goes away with the node.
60. **The role is fixed by the node's kind** rather than chosen separately: an output can only write into the cable, an input can only read from it. To use the cable the other way, add its other end.
61. **The `micApps` journal and assignments that outlive the mixer** go beyond the specification, which only asks to remove an assignment when the node is deleted. Without a journal, deleting the node (or a crash) would leave the assignment in Windows with nobody to undo it.
62. **The assistant asks for the devices first** (step 1) and folds «which cables» into step 2, as the mockup does («Step 2 of 3» with the cable check inline).
63. **What «держать отдельно» means.** A separate app gets its own node into the headphones and is left out of «Весь звук системы», so it is off the stream; the specification doesn't say. The stream gets no bus: two wires into the cable keep the layout minimal.

### Tests

`CableTests`, `UiCableTests`: pairing (VB-Cable with two inputs, Cable A, two Virtual Audio Cable lines, an unknown cable, one end only), an own `cables.json` first and a broken one ignored, roles by node kind, manual linking and unlinking, loops through Virtual Audio Cable lines and through a hand-linked cable, the v1 migration, `graph.json` round trip of `cable`, `micFor`, `micApps`, `micFor` edits (normalized, no duplicates) as structural changes, the store keeping the host's `micApps`; the microphone assignment on the `IAudioPolicyConfig` model (journal before `Set`, cable output for all roles, «unavailable» releases it, removal releases it, an app that isn't running waits); the three checks; the assistant's layouts (stream with one cable, stream and call with one and two cables, headphones only), which build without issues and with a direct route for the separate app; the palette row, the card, the footer, the assistant's three steps with the cable shortage blocking «Далее», one undo step, the first-start offer; the autostart `Run` value.

### Status as of 11 November 2026

- Done: the table, roles, palette, card, footer, manual linking, «Использовать как микрофон в…» with its journal, the three checks, the migration, the assistant.
- **Not checked on devices**: assigning a microphone to a real OBS and Discord, the checks with real sessions, the assistant on a clean machine with one VB-Cable (criterion: under 2 minutes).

## v2 stage 7: scenes, hotkeys, tray, autostart, themes

### How it works

- **Scenes** (`Scenes`, `MainViewModel.Scenes.cs`): `scenes: [{ id, name, hotkey, nodes, wires }]` stores node faders and mutes, which processing blocks are on, wire levels and on/off; `activeScene` is the one shown in the header. Applying a scene changes only nodes and wires it knows. The 150 ms fade is driven by the UI: every 15 ms a new graph from `Scenes.Interpolate` goes through the history under one merge key (one undo step) and to the host, and the engine ramps each step over its usual 10 ms. Levels move linearly in dB; a node being muted ramps to −60 dB and gets its mute at the end, a node being unmuted gets sound at once from −60 dB; processing blocks switch at the start (the engine's 10 ms crossfade). The header shows «Сцена ▾», a dot when the levels no longer match, «Сохранить сцену» and «Сбросить»; the scene menu applies, renames, deletes, sets the hotkey and creates a scene from the current levels.
- **Hotkeys** (`KeyChord`, `GlobalHotkeys`, `HotkeyService`, `HotkeyBox`). `hotkeys: [{ action, target, keys }]` for node actions (mute, +1 dB, −1 dB, push-to-talk, push-to-mute), `scenes[].hotkey` for scenes; one combination is one action across both lists. `RegisterHotKey` runs on a message-only window in the UI thread (`MOD_NOREPEAT` except for the volume steps). A combination is checked when it is assigned: a plain key needs Ctrl, Alt, Shift or Win (F1–F24, the numeric keypad and media keys may go alone), and a probe `RegisterHotKey` tells whether another program holds it. While an assignment box has focus the mixer's own hotkeys are released. Each action pops up a short confirmation by the tray (`TrayToast`: topmost, `WS_EX_NOACTIVATE`, so a game keeps the keyboard), which can be turned off.
- **Push-to-talk and push-to-mute**: a `WH_KEYBOARD_LL` hook, installed only while such a key is assigned, on its own thread with a message loop (a hook on the UI thread is silently removed when the UI is busy); it only watches keys, never swallows them. The state is an override outside the history and `graph.json`: the host gets `Effective(graph)` with the node muted, and the card says «push-to-talk · молчит» / «· в эфире».
- **Tray** (`TrayIcon`, without WinForms): `Shell_NotifyIcon` on a hidden window, re-added after Explorer restarts (`TaskbarCreated`). The icon is drawn from the header logo: a green dot when the main microphone is on, red with a slash when it is off, a yellow sign when a node is in error; the tooltip says the same. The menu: scenes, «Выключить микрофон», «Открыть», «Выход». The main microphone (`mainMic`) is set in Settings or in a microphone's menu. Close hides the window to the tray (Settings can turn that off) with a one-time hint; a second start shows the hidden window through a named event. When the window is hidden or minimized, a device that disconnects or returns raises a Windows notification (can be turned off).
- **Autostart** (`Autostart`): the `Mixer` value under `HKCU\…\Run`, `"<exe>" --minimized` (plus `--graph` for a non-default layout). Off by default; turned on in the assistant or in Settings. `--minimized` starts with the tray icon and no window; the meters' clock stays stopped.
- **Themes** (`ThemeManager`, `Themes/Graphite.xaml`, `Daylight.xaml`, `Modular.xaml`, `Styles.xaml`). Each theme dictionary has the same keys: colours, brushes, the source palette `Source0…7`, wire, meter, EQ and Modular panel colours, `UiFont`, `PanelFont`, `IsDarkTheme`. `App.OnStartup` merges the theme and then `Styles.xaml` (the former `Theme.xaml` without colours) before the window exists; the theme applies after a restart, like the language. Code that draws by itself takes colours from the theme (`ThemeColors`); the source palette stays in `graph.json` as Graphite colours, and a theme shows its own shade of the same slot. The Windows accent is used in Graphite only; Daylight has a light title bar. Settings has a picker with previews (mockup «Theme · settings picker») and «Перезапустить».
- **Modular** (`RackView`, `ModuleView`, `Knob`, `LedMeter`, `RackLayout`; mockup «Theme · Modular»). The rack is computed: modules left to right in column order (sources, buses, outputs), by Y within a column, wrapping to the window width. A module is a 3U panel with screws and caps lettering in Oswald: sources aluminium, buses black, outputs cream; a LEVEL knob (drag up and down, wheel, arrows, double-click for 0 dB; a `RangeBase` with a slider automation peer), a column of 10 LEDs per channel, a MUTE toggle with an LED, GATE · EQ · COMP labels with LEDs opening the same side panel. Jacks: OUT on a source; IN 1…N on a bus or output, one per incoming wire plus a free one (a module widens past three jacks), and OUT on a bus. Patch cables are quadratic curves sagging by 50 + 0.22 × length, drawn over the panels with a shadow and plugs; several cables from one OUT are a stack of plugs. Hit testing and dragging use the same `WireGeometry.At`. A small knob above each IN is that cable's level. Gestures: OUT → IN connects; a plug pulled from an IN re-hooks the cable or, dropped in empty space, removes it; a free IN connects towards a source; dragging a module swaps its Y with the neighbour it is dropped on, so Graphite keeps a valid layout. Hovering a module highlights its routes (others dim) and makes the other cables translucent. Right-click: module and cable menus, the source palette on empty space. Oswald (SIL OFL, `src/Mixer.App/Fonts/Oswald-OFL.txt`) is embedded in the exe.

### Deviations from the specification

64. **`activeScene` is stored** in `graph.json` (not in the specification's table), so the header shows the scene after a restart.
65. **The scene fade is stepped by the UI** every 15 ms rather than one 150 ms ramp in the engine; each step is ramped over 10 ms by the engine, and the residual above 6 kHz on a 1 kHz sine stays at −75 dB or lower (see stage 8).
66. **Scene hotkeys live in `scenes[].hotkey` only**; `hotkeys` holds node actions. Both lists exist in the specification; storing a scene key twice would let them disagree.
67. **A plain key needs a modifier** (except F1–F24, the numeric keypad and media keys), so a hotkey doesn't fire while typing. Push-to-talk may use any key: the hook doesn't swallow it.
68. **Push-to-talk is not a graph edit**: no history step and nothing saved on every press.
69. **Tray without WinForms** (`Shell_NotifyIcon` directly), so WinForms isn't pulled into the single exe. Device notifications are tray balloons, which Windows shows as notifications.
70. **Modular is its own view** (`RackView`) rather than a `DataTemplateSelector` inside the canvas: the rack has its own layout, jacks, cable knobs and gestures. The view models and commands are shared, so nothing about the graph is duplicated.
71. **Moving a module swaps it with a neighbour** (their Y values) instead of computing a new Y: the canvas in Graphite stays free of overlaps.
72. **Oswald is the variable-weight file**; WPF uses its default instance and synthesizes bold. The `--theme`, `--minimized` and `--tray` (tray and hotkeys with `--no-audio`) switches, and `--open setup[:step]`, go beyond the specification.

### Tests

`SceneHotkeyTests`, `ThemeTests`: a scene captures levels and applies them without touching structure or unknown nodes; the fade ramps up and down monotonically, mutes only at the end, unmutes from −60 dB and keeps positions; one combination per action across scenes and nodes; `graph.json` round trip of scenes, hotkeys, `mainMic`, `activeScene`; a deleted node takes its hotkeys and the main-mic role; the header's name, dot, «Сбросить» (one undo step), «Сохранить», naming; the scene switch in time; chord parsing and printing, native modifiers, the modifier rule; assignment refused when Windows holds the key, reassignment with a message, mute and ±1 dB actions (±1 dB steps merge into one undo), clearing; push-to-talk and push-to-mute on the host graph without touching history, the main microphone state and the tray toggle; the tray icon's badges; startup switches and restart arguments; preferences (theme, flags, an unknown theme falls back); the theme picker and its restart note; every theme has the same keys; `Styles.xaml` and the settings view build with each theme; Daylight contrast (text 4.5:1, wires and meters 3:1 on every background); the source palette slot; the rack order, jacks, widths, wrapping, cables and plug stacking, module swaps; the cable sag and hit testing; the knob scale and the LEDs.

### Status as of 11 November 2026

- Done: scenes, hotkeys, push-to-talk, tray, autostart, notifications, the three themes and the settings picker. Checked from snapshots in `--no-audio` mode: Graphite, Daylight, Modular with the streamer layout, Settings, the assistant. With `--no-audio --tray`, the tray icon appeared and both scene hotkeys registered.
- **Not checked on devices**: hotkeys and push-to-talk in a full-screen game (and with anti-cheat), autostart after a reboot, device notifications, the tray with the engine running.

## v2 stage 8: tests and release 2.1

### Acceptance criteria 2.1

| Criterion | Result |
| --- | --- |
| On a clean system with one VB-Cable the assistant builds a working «stream to OBS» layout in under 2 minutes and assigns OBS its microphone | **met by tests, not on a clean system.** `AcceptanceV21Tests`: five clicks from the first-start offer; the layout builds without issues, microphone and system audio go into CABLE Input, OBS gets CABLE Output for all three roles on the `IAudioPolicyConfig` model (journal first). The 2 minutes with a person and a real OBS are for the user |
| A scene switch takes at most 150 ms and doesn't click | met: `AcceptanceV21Tests` puts a 1 kHz sine through the engine's gain stage with the scene fade's steps every 15 ms: the last step lands at 150 ms; residual above 6 kHz −95 dB (0 → −20 dB), −87 dB (−20 → +6), −81 dB (mute), −75 dB (unmute), limit −60 dB; the same change made instantly is caught. `SceneHotkeyTests` runs the fade in the UI to the end |
| Hotkeys and push-to-talk work in a full-screen game; no blocks in 2–3 games with anti-cheat | **not checked**: no games with anti-cheat on the developer machine. The hook is installed only while a push-to-talk key is assigned and never swallows keys |
| After a reboot with autostart on, the mixer starts minimized to the tray and the layout plays without opening the window | **not checked after a reboot.** Tests: the `Run` value is `"<exe>" --minimized`, `--minimized` is parsed and dropped on restart; `--minimized` shows the tray and no window |

### Localization

All new strings are in `Strings.resx` / `Strings.ru.resx` and `CoreStrings`; `LocalizationTests` check both languages. A scan for Cyrillic literals in `src` (outside comments and resources) found none. Snapshots were checked in English and Russian.

### Tests

`AcceptanceV21Tests` (above). 334 tests pass (2 manual tuning sweeps skipped). The solution builds without warnings.

### Status as of 11 November 2026

- Version 2.1.0 (`Directory.Build.props`); README describes the assistant, cables, scenes, hotkeys, tray, autostart, themes and upgrading from 2.0.
- **Not done**: the device checks listed under stages 6 and 7 and the two acceptance criteria above. The GitHub release is made by pushing the `v2.1.0` tag.

## VST stage 0: host exchange prototype

The specification is `Микшер для Windows поддержка VST.md`. Stage 0 checks the riskiest assumption of the design: a synchronous call into a separate plug-in process per block (shared memory and a pair of events) is cheap enough for the audio thread. The gate is an exchange cost of at most 100 µs at p99.

### How it works

- **`src/Mixer.PluginHost`** (C++20, CMake, VST3 SDK 3.8.1 fetched at build time) loads one VST3 effect through the SDK's `PlugProvider`, negotiates stereo (or mono) buses and activates the plug-in. Its audio thread (MMCSS "Pro Audio", FTZ/DAZ) waits for `Request`, calls `process` on the shared buffers, and answers with `Done`. The host exits when the parent process dies. `--builtin copy` runs it without a plug-in. Layout and protocol are in its [README](../src/Mixer.PluginHost/README.md).
- **`tools/Mixer.PluginBench`** is the mixer side (`HostChannel`) and a benchmark. Per block it spins on the answer for up to 50 µs, then waits on `Done` together with a high-resolution timer at the deadline (40% of the period). A late answer is recognised by its block number. Hosts run in a Job Object with kill-on-close. See its [README](../tools/Mixer.PluginBench/README.md).
- CI builds the host and AGain in a separate `pluginhost` job. `Mixer.exe` does not include the host yet.

### Deviations from the specification

1. **No control pipe yet.** Load parameters come on the command line, and the host reports its state (starting / ready / failed), latency and channel counts in the shared header. The pipe and its messages are stage 1.
2. **"Busy" blocks.** With one pair of buffers, the mixer may not write the next input while the host is still processing an overdue block. Such blocks go dry without being sent. The specification only says that a late answer is dropped by number.
3. **The deadline wait uses a high-resolution waitable timer** together with `Done` (`WaitForMultipleObjects`). The millisecond timeout of `WaitForSingleObject` is too coarse for the 1.07 ms deadline at the 2.67 ms period. A millisecond timeout stays as a backstop in case the timer cannot be set.
4. **Warm-up and prefaulting.** Both sides touch every page of the section before the first block, and the mixer sends 8 silent blocks with a 1 s deadline before audio (the slot is still "loading"). Without the warm-up, the first `process` missed the deadline once at 128 frames.
5. **The header** carries `requestQpc`, `wakeQpc`, `doneQpc` and `processResult` for diagnostics, and a stop flag instead of a bypass flag: bypass is decided in the mixer.
6. **The mono downmix is done in the host**, so the section is always stereo.
7. **AGain needs VSTGUI**, so the stage 0 build also fetches `vstgui4`. `-NoAgain` (`MIXER_BUILD_AGAIN=OFF`) builds the host alone.

### Results

The gate is passed: over one hour with AGain, exchange p99 was 28.9 µs at 480 frames and 31.0 µs at 128 frames. There were 0 deadline misses in 1.71 million blocks, and every output matched its input bit for bit. Memory per host is 1.2 MB private without a plug-in and 25–27 MB private (36–38 MB working set) with AGain, with no growth. The CPU cost of the exchange is ≤ 0.05% of all cores per plug-in. Details, tails and the deadline check are in the [Mixer.PluginBench README](../tools/Mixer.PluginBench/README.md#results-windows-11-25h2-26200-ryzen-5-5600g-12-logical-cores-12-november-2026).

### Status as of 12 November 2026

- Done: the host with AGain, the benchmark, a one-hour run at 480 and 128 frames, a 5-minute run without a plug-in, the stall check, and the CI build.
- Next (stage 1): the control pipe, the editor window, even blocks, embedding the host into `Mixer.exe`, and the MIT license text for the SDK in the licenses folder and the About window.

## VST stage 1: the host

Stage 1 of `Микшер для Windows поддержка VST.md` is the plug-in host itself: loading modules and bundles, bus negotiation, the audio channel, every message of the control pipe, the editor window with DPI and an owner, even blocks, the Job Object, the CI build. Slots in the engine (stage 3), the scanner (stage 2) and the UI (stage 4) come later. Nothing in the mixer starts a host yet, except the `--check-plugin-host` switch.

### How it works

- **`Mixer.PluginHost.exe`** (C++) loads the plug-in only when the `load` message arrives. Its main thread owns the controller and the editor and handles control messages. Messages are posted to it through a hidden window, so `ping` is answered even inside a plug-in's modal loop; other messages that arrive during a call into the plug-in wait until it returns. The audio thread can be suspended for reconfiguration; while suspended it answers each block with a "bypassed" flag. Parameter changes go to the processor through a lock-free queue and reach it with the next block (or with a 0-sample `process` before `getState`). Changes the processor makes itself go back to the controller. Protocol, threads, the window and the built-in plug-ins are described in the host's [README](../src/Mixer.PluginHost/README.md).
- **`Mixer.Plugins`** (C#): `PluginHost.Start` creates the section, events and pipe (current user only), starts the host with `CREATE_NO_WINDOW` in a kill-on-close Job Object, reads its stderr into the log, and pings it every second. `LoadAsync`, `GetStateAsync`, `SetStateAsync`, `SetRateAsync`, `SetEvenBlocksAsync` and `GetParamsAsync` are requests matched by `req`. `SetParam`, `OpenEditor` (with `AllowSetForegroundWindow` for the host) and `CloseEditor` are one-way. Events: `ParamChanged`, `ParamsChanged`, `LayoutChanged`, `Dirty`, `EditorOpened`, `EditorClosed`, `EditorFailed`, `Terminated`. `HostExit` says why the host ended: `Quit`, `Crashed` (with the host's `crashed: exception … at module+offset` line), `Hung` (no answer for 3 s with no request pending; the process is killed) or `Killed`. After `ready`, 8 silent blocks with a 1 s deadline warm the plug-in up. `AudioLink.Process` is the stage 0 transport plus the `Bypassed` outcome.
- **Embedding.** `Mixer.Plugins.csproj` embeds `src/Mixer.PluginHost/build/bin/Mixer.PluginHost.exe` if it exists. `HostBinary.Locate` takes `MIXER_PLUGINHOST` if it is set. Otherwise it extracts the exe (through a temporary file) and the licenses to `%LocalAppData%\Mixer\bin\<version>-<sha8>\` and deletes the other version folders it can delete. `Mixer.exe --check-plugin-host` extracts the host, starts it with the built-in gain, runs one block and exits with 0 or 1.
- **Build and release.** CI builds the host (with AGain) before the solution, in the same job, and runs the tests with `MIXER_REQUIRE_PLUGINHOST=1`. `tools/publish.ps1` builds the host (unless `-SkipHostBuild`) and signs it with the same certificate before it is embedded. It then publishes, and after the window check runs `--check-plugin-host` on the published exe.
- **Licenses.** `licenses/` holds the MIT texts of the VST 3 SDK and JSON for Modern C++. Settings ends with «О программе»: the version, a line saying that VST3 plug-ins run through the VST 3 SDK and that VST is Steinberg's trademark (no logo), and the three licenses (with Oswald's OFL), which expand on click. `--open settings:about` opens Settings scrolled down to it.

### Deviations from the specification

1. **More messages than in the table.** Requests carry `req`, and the answers have their own types: `failed`, `error`, `rateSet`, `stateSet`. Added: `setState` (slot presets need it at runtime), `getParams` / `params` (`restartComponent` with new values or titles), `setEvenBlocks`, `editorOpened` / `editorClosed` / `editorFailed` (the window's position and size for the slot), `quit`, and `debug` for built-in plug-ins only.
2. **`ping` goes one way.** The mixer pings and the host's main thread answers. The host learns that the mixer is gone from the closed pipe and the parent's handle; if its main thread is stuck, it terminates itself 5 s later. While a request is pending, the 3-second hang rule waits, because the request has its own timeout (load 30 s, others 10 s): a plug-in may load for a long time without pumping messages.
3. **The built-in test plug-ins live in the shipped host**, behind `load { builtin }`, not in a separate test build. That leaves one exe to build, embed and test. Only the mixer can reach them, through its own pipe.
4. **Parameters are flushed before `getState`** with a 0-sample `process`. Without it, an edit made while no audio flows never reaches the processor's state; AGain showed this.
5. **`setState` at runtime suspends the audio thread**, so the plug-in never sees a state swap in the middle of `process`. Those blocks go dry.
6. **`kReloadComponent` is handled like `kIoChanged`** (deactivate, renegotiate buses, activate) rather than by recreating the component with its state.
7. **The version folder is `<version>-<sha8>`**, not `<version>`: a host rebuilt under the same version must not reuse a stale copy.
8. **«О программе» is a section of Settings.** The mixer has no separate About window.
9. **`dirty` is throttled** to one message per 250 ms. Values the processor changes by itself reach the mixer at most every 33 ms, and never for read-only parameters (meters).
10. **The host ends a crash itself.** An unhandled exception filter writes one line to stderr and exits with code 5. Windows Error Reporting dialogs are switched off, so a crashed host disappears at once instead of waiting on a dialog.
11. **Protocol version 2** adds `doneFlags` to the header.
12. **Restart after a crash and the "three crashes in 5 minutes" rule are not here.** `PluginHost` reports what happened; the slot (stage 3) decides.
13. **A third component in the host**: JSON for Modern C++ (MIT), for the message bodies.

### Tests

`PluginHostTests`, `PluginHostTimingTests` and `AboutTests` (20 tests). They start real host processes and need the C++ build; without it they pass empty, unless `MIXER_REQUIRE_PLUGINHOST=1`.

- The built-in gain loads with its parameters, processes bit-exact, takes `setParam` with an echo and its text, and bypasses through its `kIsBypass` parameter. A mono plug-in gets (L+R)/2 in both channels.
- State round-trips byte for byte through a new host, and `setState` at runtime changes the output. Garbage is refused and the host stays up.
- The latency plug-in reports 512, delays an impulse by exactly 512, and changes to 256 through `restartComponent`. Even blocks with block sizes 100, 333, 7, 480, 1000, 1, 256, 255, 257 delay the signal by exactly 256 frames, bit for bit, and can be switched off at runtime. `setRate` keeps parameters.
- An edit "in the plug-in window" reaches the processor and raises `dirty`. NaN passes through unchanged (catching it is the slot's job). Load failures are reported and the host stays up.
- A crash in the audio thread ends the host with `Crashed`, exit code 5 and `0xC0000005`. Quit exits with 0, and a closed pipe makes the host exit on its own. A 20 ms plug-in with a 4 ms deadline is missed, then busy, and the late answer is dropped. A frozen main thread is `Hung` within 1–4.5 s (with a 2 s threshold; stage 4 raised it from 1 s, see «VST stage 4»), while an idle host is not.
- The state format, the embedded host's extraction (version folder, licenses, old folders removed, no rewrite on the second call), the license texts and «О программе».
- With AGain from the SDK: loading by path and by class GUID, parameters (Gain, VuPPM read-only, Bypass), unity gain bit-exact, state round trip through a new host.

Manual: the editor window and the exchange benchmark (`Mixer.PluginBench`, see its [README](../tools/Mixer.PluginBench/README.md#stage-1-checks-12-november-2026)), and `Mixer.exe --check-plugin-host` on a published single-file exe.

### Status as of 12 November 2026

- Done: the host with every message, the window, even blocks and test plug-ins; `Mixer.Plugins`; embedding and extraction; CI and `publish.ps1`; licenses and «О программе». 354 tests pass (2 manual sweeps skipped), and the solution builds without warnings. The published single-file `Mixer.exe` (72.6 MB) extracts the host and runs a block.
- **Not checked**: the editor at more than 100% scaling and when moved between monitors (this machine has one monitor at 100%), a resizable plug-in, plug-ins with license windows, real third-party plug-ins (stage 5), antivirus reactions to the extracted host, a signed release, and the CI run itself.

## VST stage 2: scanner and catalog

Stage 2 of `Микшер для Windows поддержка VST.md`: the `--scan` mode, `moduleinfo.json`, timeouts, the cache, the «не загрузился» and «не поддерживается» lists, the signature check. Nothing in the UI uses the catalog yet (stage 4); `Mixer.PluginBench --scan` prints it.

### How it works

- **Files** (`PluginFiles`): the standard folders are `Common Files\VST3` and `%LOCALAPPDATA%\Programs\Common\VST3`; the caller adds the user's folders. The walk returns `*.vst3` bundles (without entering them), single-file `*.vst3` and `*.dll`, skips folder links and stops 8 levels deep. The module file of a bundle is `Contents\x86_64-win\<bundle name>`, or the only `.vst3` there: Elgato names its DLL `ElgatoEq_v1.3.0.110.vst3` inside `ElgatoEq.vst3`. The host's loader does the same (`ResolveModulePath`), so such plug-ins now load at all; the SDK's loader looks only for the bundle's name.
- **Without loading** (`PluginFiles.Precheck`, `PEReader`): a bundle with only `x86-win` is 32-bit, one with neither x64 nor x86 (ARM only) has no x64 version; a PE with machine I386 is 32-bit. A `.dll` that exports `VSTPluginMain` or `main` and not `GetPluginFactory` is VST2. Other `.dll` files are not plug-ins and appear nowhere.
- **Fast path**: a bundle's `Contents\Resources\moduleinfo.json` is read by the mixer itself (comments and trailing commas allowed), without a process. If it can't be parsed, the module is loaded instead.
- **Slow path**: `Mixer.PluginHost.exe --scan <path>` in its own process in the Job Object: loads the module, prints the factory's classes as one JSON line and terminates without unloading (some plug-ins crash in `ExitDll`). Timeout 10 s, then the process tree is killed: «не ответил за 10 с». A crash ends the process with code 5 and the `crashed: …` line. `ScanAsync(path, withWindow: true)` (`RescanAsync` in the catalog) is «Просканировать с окном»: no timeout, and the scan process may take the foreground.
- **Catalog** (`PluginCatalog`): effects are classes of category `Audio Module Class` with the `Fx` subcategory; the groups for the picker are the other subcategories (Dynamics, EQ…). «Не загрузились»: crashed, timed out, failed to load. «Не поддерживается»: VST2, 32-bit, no x64 version, a module without audio modules, and instruments (also from modules that have effects). `Find(classId)` finds an effect by its class GUID. `Progress` reports «проверено N из M» after each module.
- **Cache** `%LocalAppData%\Mixer\plugins.json`: every module with its result, keyed by path, size and modification time of the module file. `RefreshAsync(folders)` drops vanished modules and scans only new and changed ones, one at a time, in the background; `full: true` rescans everything. A broken cache is ignored and rewritten. When to refresh (only if the graph has plug-ins or the picker is open) is the caller's decision, in stages 3–4.
- **Signature** (`Authenticode`): `WinVerifyTrust` on the module file, without network access or revocation checks; the signer is the certificate's CN. Valid, invalid, or not signed.

### Deviations from the specification

1. **`moduleinfo.json` is read in the mixer**, not in a `--scan` process: no process at all for such bundles.
2. **Timed-out and crashed modules are cached** like the others, so they are not retried on every start: the user retries with «Просканировать с окном» or a full rescan.
3. **The host finds a bundle's DLL by its only `.vst3`** when it is not named after the bundle (Elgato).

### Tests

`PluginCatalogTests` (8): `moduleinfo.json` without a process (JSON5, effect, instrument, controller class skipped, vendor from the factory, lookup by GUID); 32-bit, ARM-only and non-plug-in files; the signature of `coreclr.dll` valid (".NET"), invalid after a patch, missing on an empty file; AGain through the factory, and a copy of AGain whose DLL is not named after the bundle scans and loads; the crashing module (`0xC0000005`), the hanging one (timeout) and VST2 without a process; the acceptance scenario: 98 bundles plus a hanging and a crashing module, scanned in the background, then from the cache in under 1 s without a process, then only the changed module is checked and the deleted one disappears; a broken cache. The host build adds three test modules for them in `build/test-modules`: `hang.vst3`, `crash.vst3`, `vst2.dll`.

### Status as of 12 November 2026

- Done: the scan mode, the fast path, the catalog with the cache, the lists, the signature check. 362 tests pass (2 manual sweeps skipped), and the solution builds without warnings.
- On this machine (`Mixer.PluginBench --scan --full`): 6 effects in 0.31 s — five Elgato bundles through `moduleinfo.json` (signed by Corsair Memory, Inc.) and Basic Noise Gate (single file, unsigned) through the factory; from the cache 0.02 s. Elgato EQ loads and processes in the host.
- **Not checked**: a plug-in with a license window («Просканировать с окном»), real 32-bit and VST2 plug-ins, catalog-signed files (the signer is then not shown).

## VST stage 3: the engine

Stage 3 of `Микшер для Windows поддержка VST.md`: `PluginSlot` in `ProcessingChain`, pinned buses, crossfades that respect the plug-in's latency, NaN and overload protection, latency accounting, restart and automatic switch-off. Slots come from `graph.json`; there is no UI to add them yet (stage 4).

### How it works

- **`graph.json` version 3.** A node has `plugins`: `{ id, group: pre|post, enabled, format, classId, name, vendor, path, state, evenBlocks }`, and a bus may have `clockOutput`. An older file is read as is and kept next to it as `graph.v2.json` (as before for v1); 2.1 refuses version 3.
- **The chain** (`ProcessingChain`): input → «до» slots → gate → EQ → compressor → «после» slots → fader. A chain with slots is never idle; `ChainMeters` stay at its edges. `LatencySamples` is the sum of its slots.
- **The slot in the audio thread** (`PluginSlot`, `Mixer.Audio/Dsp`): copies the block to the host's section, waits up to the deadline (block start + 40% of the node's period; nodes pass their period), and takes the output. The dry signal always goes through a delay line of the plug-in's latency, so switching between dry and wet never shifts the sound: on/off is a 10 ms crossfade, coming back after a missed block 2 ms. A block with no wet output (late, host busy or suspended, host gone) goes dry; if wet was playing, the step is smoothed by an offset that decays over 2 ms. NaN or infinity: the block goes dry and the slot stops sending blocks. Above +12 dBFS: hard clip at +12 dBFS, counted. Two threads never run one slot: the second gets the block unchanged. No allocations, locks or logging; counters (misses, NaN, loud blocks, process time) are taken by the control thread.
- **Pinned buses** (`MixPlanner`, `AudioEngine.Apply`, `BusInstance`): a bus with slots exists once, in the thread of its main output: `clockOutput` if the bus reaches it, otherwise an output with «Низкая задержка», otherwise the first one. Other outputs get it through a `Wire` with its own ring and resampler (a tap), written by the bus after its fader with the main output's clock (`BusInstance.Clock`). Buses above a pinned bus are computed by another output only if they reach it some other way. The planner is a fixed point over "which buses does this output need"; without slots it gives the same plans as before. An App or All system audio node with slots gets its implicit `#fx` bus (like a chain), and never a direct route; an output with slots is never the target of a direct route.
- **The control layer** (`PluginRack`, `Mixer.Core/Plugins`, owned by `MixerHost`): one slot and one host per `PluginConfig`. `Sync` runs before each graph build; `GraphBuilder` takes the slots through `RuntimeContext.Plugins`. On ready the slot is attached at the rate the node last used (48 kHz before that); when the node runs at another rate, `setRate`. Switching off uses the plug-in's `kIsBypass` parameter when it has one, otherwise a crossfade to dry. A crash or a hang: dry at once, restart after 2 s with the last state; three crashes in 5 minutes — «Сбой» until `Restart`. More than 10 missed blocks in 10 s — switched off as too slow. NaN — switched off. The state is taken from the plug-in 2 s after its last `dirty` and kept for restarts (`StateSaved` for stage 4, which writes the files; a `state` file is read on load). A plug-in not found by path is looked up by its class GUID in the catalog's cache; if it isn't there, the standard folders are scanned once per run and missing slots retry. Limits: two slots per group per node, 16 per graph; extra slots stay in «Сбой» without a host.
- **Latency** (`PluginLatency`): the sum of slot latencies on every source → output path. The header's total latency and a wire's tooltip add the largest one for that source and output. Paths from one source into one output that differ by more than 2 ms are listed in `HostStatus.LatencyWarnings` (stage 4 shows them on the output). `HostStatus.Plugins` has every slot's state, fault, latency, rate, load (the share of the period spent in `process`, smoothed over 2 s) and the +12 dBFS flag.

### Deviations from the specification

1. **Slots live in `node.plugins`, not `chain.plugins`.** Chain presets and normalization rebuild the chain from its three blocks and would drop the slots.
2. **A switched-off slot still feeds the plug-in.** Otherwise, when switched back on, its delay line and tails would play what was stuck in them when it was switched off (a test caught a 0.95 step with a 512-sample delay). This is what the `kIsBypass` path does anyway.
3. **The main output defaults to an output with «Низкая задержка»** (then graph order), not the measured period: the period is known only after the device opens, and the plan is built before that.
4. **A step to dry without wet output is smoothed by a decaying offset**, not a crossfade: there is nothing to crossfade with when the block is late. Coming back from dry is a real 2 ms crossfade.
5. **NaN and "too slow" stop the host**; only `Restart` brings it back. A crash restart runs on its own 2 s timer, not on the 1 s tick.
6. **State files are not written yet** (stage 4); restarts use the state kept in memory. Done in stage 4.
7. **`Mixer.Audio` references `Mixer.Plugins`**: the slot calls `AudioLink` directly.

### Tests

`PluginEngineTests` (18): the pinned bus is computed once by its main output (low latency, or the chosen clock), another output gets a remote wire and doesn't compute the buses above it; a bus without slots is not pinned; a tap carries the bus's sum into the other output's ring. The slot fades in over exactly 10 ms; with a 512-sample plug-in, switching off and on leaves the output equal to the input delayed by 512, bit for bit; NaN is replaced by dry and reported; a late host goes dry and counts misses; a chain with only slots runs and sums latency. `PluginRack` with built-in plug-ins: loading, off through `kIsBypass` (gain) and through a crossfade (copy) without restarting the host; killing the host process — dry, back with the same state in 1.9–3 s (acceptance criterion); three crashes wait for `Restart`; "sleeps 50 ms" switched off (criterion: within 10 s); NaN switches off; following the node's rate; missing plug-ins and the per-group limit start no host. The builder passes slots and gives an App node an `#fx` bus without a direct route; `graph.json` keeps the slots at version 3; parallel paths with 10.7 ms of difference warn, a disabled wire removes the warning.

### Status as of 12 November 2026

- Done: everything above. 380 tests pass (2 manual sweeps skipped); the plug-in tests passed three runs in a row; the solution builds without warnings.
- **Device check, 12 November 2026** (`Mixer.Run run`, which now prints a line per slot and the latency warnings). Elgato Noise Removal on the source and Elgato EQ on a bus going to the fifine speakers and CABLE Input. Both plug-ins loaded in under 1 s (Elgato's DLLs are not named after their bundles, see stage 2). The bus was pinned to the speakers, and CABLE Input read it through its tap: ring at its 20 ms target, 0 underruns. Load was 1.2% (Noise Removal) and 0.3% (EQ) of the period; the mixer's CPU averaged 0.37%. When both hosts stopped answering, each slot went dry and was back 2 s later.
- **Windows Audio hung during that check** (svchost AppHang at 17:08:41 and 17:11:31, then a restart by the service manager). `IAudioClient.Initialize` on the fifine microphone hung from the start. The cause was the antivirus: it holds microphone access for a new program until the user allows it. It was neither the plug-ins nor the engine. No hosts were left over after the runs.
- **Manual check by the user, 12 November 2026** (`probe-out/vst3-mic.json` and `vst3-mic-bus.json` through `Mixer.Run run`, microphone access allowed): the microphone through Elgato Noise Removal into the speakers, the bus with Elgato EQ into the speakers and CABLE Input, and a host killed in Task Manager. Everything worked.
- **Not checked on devices**: the 8-hour run with 6 plug-ins, CPU per plug-in over a long run. Nothing in the UI shows the slots yet.

## VST stage 4: the UI

Stage 4 of `Микшер для Windows поддержка VST.md`: slots in the processing panel, the plug-in picker, the parameter list, the FX chip, the 4HP module in Modular, EN/RU strings, state files and slot presets.

### How it works

- **Processing panel.** «Плагины до» above the gate and «Плагины после» below the compressor, each with up to two slot rows and «+ Плагин». A row: drag handle, name and vendor, switch, «Открыть», «⋯»; under it the slot's output meter, latency («+10,7 мс») and load («4%»). A failed slot has a red frame, says what happened and offers «Перезапустить» (`PluginRack.Restart`). Dragging a row by its handle reorders it or moves it to the other group (two per group). «⋯»: replace, move to the other group, save preset (the name is typed in the menu), load preset, even blocks, remove. Every change is a graph edit through the history (`PluginEdits`), so Ctrl+Z works.
- **«Открыть»** opens the plug-in's own window in its host (`OpenEditor` with the main window as owner, title «Плагин — Узел», last position and size). When it closes, the bounds go into the slot (`editor` in `graph.json`) and the state is taken. A plug-in without a window gets its parameter list in the panel instead: title, slider, value text from the plug-in; hidden, read-only and bypass parameters are not shown. One `PluginSlotViewModel` per slot (`MainViewModel.SlotVm`) holds the host subscriptions.
- **The picker** is the palette in a third mode: search by name and vendor, «Недавние» (last 8, `settings.json`), the plug-ins' categories, «Не загрузились» (Enter scans the module again with its window, no timeout) and «Не поддерживается» with reasons; the subtitle shows the signer. The cache is shown at once; opening the picker refreshes the catalog (standard folders plus the user's) in the background, with «проверено N из M», and new plug-ins appear as they are found. A new slot gets a fresh ID (one whose state file does not exist yet) and the state file `plugins/<node>-<slot>.state`.
- **Node card**: chip «FX n» (slots switched on) next to Gate · EQ · Comp; a yellow dot when a slot has failed. Outputs show the latency warning for parallel paths (both paths and the difference). **Modular**: each slot is a 4HP module (29 px) docked to the right of its node module, with the name vertical, an LED (on) and EDIT; the rack is wider by that much per slot. A bus with plug-ins has «Считать по часам…» in its menu (automatic or one of the outputs it reaches).
- **State files** (`PluginRack`): written atomically to `%AppData%\Mixer\plugins\` (next to `graph.json`; `StateFolder`) 2 s after the last `dirty`, when the editor closes, when a preset is saved or loaded, and for every running plug-in on exit (up to 3 s). A slot reloaded for the same plug-in (even blocks, path) keeps its unsaved state. **Slot presets**: `presets\<classId>\<name>.state`; loading one is `setState` on the running plug-in and is written to the slot's file.
- **Scenes** keep only which slots are on (`scene.nodes.<id>.plugins`), not plug-in state.
- **Settings → «Плагины VST3»**: the user's folders (added to the standard ones for the picker and for finding missing plug-ins) and «Пересканировать всё» (full rescan, then missing slots retry).

### Deviations from the specification

1. **The picker is the «Добавить источник» palette in a plug-in mode**, not a separate window. «Не поддерживается» is listed there too.
2. **State files are never deleted**, so undoing a removed slot and restoring a graph backup still find their state; a new slot skips IDs whose file is left over. Backups therefore don't copy the `plugins` folder.
3. **Moving the editor window is a history step** (merged per window), so Ctrl+Z after closing an editor moves its saved position back.
4. **A slot preset can be saved only while the plug-in runs** (the state comes from the plug-in); without audio (`--no-audio`) the menu says that the plug-in is not running.
5. **The wire tooltip to a non-main output of a pinned bus does not show the extra ring latency yet.**
6. **Fix in `PluginHost`**: disposing a host while it was warming up after `ready` unmapped the shared section under the warm-up thread (`AccessViolationException`, the whole process). `Dispose` now waits for the warm-up, and a warm-up never starts after `Dispose`. Removing a slot while its plug-in loads could have crashed the mixer.
7. **Fix in `ControlPipe`** (found on CI: `Store_saves_after_delay` failed because the store's timer never ran, then `control pipe is closed`). `Send` wrote synchronously to a pipe opened as asynchronous; such a write waits for its completion, which runs on a thread-pool thread. The 1-second pings come from the pool and queued behind the write lock, so under load they took every pool thread while the lock holder waited for a pool thread to complete its write — a dump showed 599 blocked pool threads, and the hang check (after the ping) never ran. `Send` now only queues the frame; one asynchronous writer per pipe sends frames in order, so no thread ever waits on the pipe. A write starts at once in `Send` when the pipe is idle (no hop to the pool); messages sent meanwhile are written after it, in order. Closing the pipe ends a stuck write. `PluginRack` also stops hosts with `await StopAsync()` instead of `Dispose` on a pool thread, which waited up to 5 s per host synchronously. Reproduced with `DOTNET_PROCESSOR_COUNT=2` (a run hung for 10 minutes).

### Tests

`PluginUiTests` (8, serial with the timing tests): slot edits (two per group, move, replace in place, remove, IDs that skip leftover state files); scenes switch slots and keep their state; the rack is 4HP wider per slot with the OUT jack in place; the picker from a catalog of two fake bundles (groups, search, signer), adding a slot (ID, state path, class), the panel rows and the chip, the switch through the history with undo, moving to «после» with the same row model, removing. With the host: the acceptance criterion — the state of an edited plug-in survives a restart (component bit for bit, the controller gets its own); the state is written on exit before `dirty` fired; a preset loads into the running slot and its file; a host disposed during warm-up does not crash (it did before the fix, 3 of 3); sending to a pipe nobody reads never waits, closing it ends the stuck write, and frames arrive in order.

### Status as of 12 November 2026

- Done: everything above. 387 tests pass (2 manual sweeps skipped), three full runs in a row; the solution builds without warnings. Checked by snapshots (`--no-audio`): the panel with slots in Graphite and the 4HP modules in Modular.
- **Not checked**: plug-in windows from the mixer with real plug-ins (owner, DPI, position memory), dragging slots and the menus by hand, the picker on a real plug-in folder, and the whole UI with audio running. That is the user's check and stage 5.

## VST stage 5: compatibility and tests

Stage 5 of `Микшер для Windows поддержка VST.md`: a list of 20 popular VST3 effects, at least half of them for voice, run through the host, with the results in a compatibility table, plus stress tests and crash tests.

### How it works

- **`Mixer.PluginBench --compat`** runs the check on every effect in the catalog. It goes through the same `PluginHost` and `AudioLink` code as the mixer. Steps: load; 29 s of SAPI speech with noise in 480-frame blocks; the editor window over an owner window while audio runs (owner check, screenshot); one parameter changed, then a new host loaded with the saved state; a rate change 48 → 44.1 → 48 kHz; ragged blocks of 1–1024 frames in a fresh host, compared with the 480-frame output. The tool writes a Markdown table and JSON to `probe-out/`. Details are in its [README](../tools/Mixer.PluginBench/README.md#compatibility-check-stage-5). A plug-in **works** when it meets the specification's criterion: it loads, sounds right in 480-frame blocks, its window works, and the edited parameter survives a restart. Ragged blocks and the rate change are reported but do not decide the result.
- **Stress and crash tests** (`PluginStressTests`) use the built-in plug-ins of the host, so they need no third-party plug-ins (see "Tests").

### Compatibility, 13 November 2026

Windows 11 25H2, Ryzen 5 5600G. **20 of the 20 plug-ins on the list work** (the criterion is 18). 11 of them are for voice (rows 1–11). The two extra Elgato plug-ins at the end work too. Every plug-in loaded, played, and opened its own window over the owner window: the owner was correct, the window was not blank, and it closed. Every plug-in kept the edited parameter through a host restart. Paid plug-ins ran as trials. "Load" is the time to `ready` and the private memory of the host. "p99" is the 99th percentile of `process` time as a share of the 10 ms period.

| # | Plug-in | Kind | Licence | Load | Latency | p99 | Ragged blocks | 48 → 44.1 → 48 kHz | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | Elgato Noise Removal | voice: denoiser | free | 74 ms, 5 MB | 0 | 2.9% | same level, **different waveform** | ok | output depends on the block size; with even blocks it differs from 480 as well |
| 2 | Elgato De-Esser | voice: de-esser | free | 75 ms, 7 MB | 0 | 0.0% | bit-exact | ok | |
| 3 | Basic Noise Gate (Videophile) | voice: gate | free | 36 ms, 4 MB | 0 | 0.3% | bit-exact | ok | single-file `.vst3` |
| 4 | RNNoise (werman) | voice: denoiser | free, open source | 34 ms, 4 MB | 1440 | 2.0% | bit-exact | ok | ships VST2 `.dll` files next to it, which the catalog lists as «VST2 не поддерживается» |
| 5 | Bertom Denoiser Classic | voice: denoiser | free | 43 ms, 4 MB | 0 | 0.3% | bit-exact | ok | JUCE: a changed parameter reaches `getState` only after its message-thread timer runs, about 300 ms later |
| 6 | Graillon 3 (Auburn Sounds) | voice: changer, pitch | free edition | 62 ms, 4 MB | 1074 | 2.2% | same level, different waveform (−4 dB), repeatable | ok | Dplug |
| 7 | MAutoPitch (MeldaProduction) | voice: autotune | free | 50 ms, 11 MB | 1024 | 1.1% | ok | ok | about 0.1 s after load it reports a latency change with the same latency: one block is dry while the host reconfigures |
| 8 | TDR Nova | voice: dynamic EQ | free | 109 ms, 18 MB | 184 | 1.2% | bit-exact | ok | |
| 9 | TDR Kotelnikov | voice/bus: compressor | free | 102 ms, 10 MB | 184 | 4.4% | bit-exact | ok | |
| 10 | FabFilter Pro-Q 4 | voice: EQ | paid | 75 ms, 11 MB | 0 | 0.0% | bit-exact | ok | |
| 11 | FabFilter Pro-DS | voice: de-esser | paid | 58 ms, 6 MB | 720 | 0.6% | bit-exact | ok | |
| 12 | Elgato Compressor | compressor | free | 47 ms, 3 MB | 0 | 0.2% | **−13 dB**; with «ровные блоки» bit-exact | ok | needs «Ровные блоки» on application nodes |
| 13 | Valhalla Supermassive | reverb | free | 57 ms, 13 MB | 0 | 0.8% | bit-exact | ok | |
| 14 | LoudMax (Thomas Mundt) | limiter | free | 28 ms, 3 MB | 60 | 0.1% | bit-exact | ok | |
| 15 | Youlean Loudness Meter 2 | LUFS meter | free | 76 ms, **289 MB** | 0 | 1.1% | bit-exact | ok | its only parameter is the preset selector, and the state bytes differ after a restart: settings made in its window should be checked by hand |
| 16 | Xfer OTT | multiband compressor | free | 62 ms, 8 MB | 2 | 1.0% | bit-exact | ok | |
| 17 | Voxengo SPAN | analyzer | free | 38 ms, 20 MB | 0 | 0.0% | bit-exact | ok | rounds a parameter to steps it does not declare (0.25 → 0.2308) |
| 18 | Surge XT Effects | effects | free, open source | 115 ms, 28 MB | 0 | 0.2% | ok, shifted by up to 31 frames | ok | works in 32-frame blocks inside and reports latency 0. The Surge XT synthesizer is listed as «инструмент» |
| 19 | CHOWTapeModel (chowdsp) | tape | free, open source | 589 ms, **217 MB** | 39 | 4.0% | bit-exact | ok, ≈ 90 ms suspended | **did not load before the fix** (see below) |
| 20 | TAL-Chorus-LX | chorus | free | 34 ms, 3 MB | 0 | 0.2% | bit-exact | ok | |
| — | Elgato EQ | EQ | free | 623 ms, 7 MB | 0 | 0.3% | ≈ (−70 dB) | **crashes** | the slot comes back at the new rate 2 s later |
| — | Elgato Pip-Boy Voice FX | voice effect | free | 126 ms, 14 MB | 0 | 6.8% | ok | ok | not deterministic: two runs of the same input differ |

Not supported, as expected: `DragonflyEarlyReflections.vst3` is a 32-bit build («32-bit»), the Surge XT synthesizer is an instrument, and RNNoise's `.dll` files are VST2.

What the check found:

1. **Fix: plug-ins that restart right after activation could not load.** CHOWTapeModel calls `restartComponent` from `setActive`. The host reconfigures it and answers the warm-up blocks after `ready` as "suspended". `AudioLink.WarmUp` treated that answer as a failure ("host did not answer a warm-up block"), so the slot showed «не загрузился». The warm-up now waits until the reconfiguration ends, up to 10 s; missed and busy blocks still fail it. The test is `Load_WaitsForAPluginThatRestartsRightAfterActivation`: the built-in `latency` plug-in got a `restartOnLoad` option, and the test fails without the fix.
2. **Fix (a stage 4 bug, found by the user's manual check): slots read their state from the current folder.** In `App.xaml.cs` the comment and the `Plugins = new PluginRackOptions { StateFolder = … }` assignment ended up on one line, so `StateFolder` was never set. The rack then resolved `plugins/<node>-<slot>.state` against the process's current folder (`bin\Debug\…\plugins`), while the picker looks for a free slot ID next to `graph.json`. It found no file there and gave a new slot `p1` again, and that slot read the state another plug-in had left under `p1`: Graillon 3 was loaded with Supermassive's state and failed with «state not accepted: IComponent::setState failed». The options are now built by `AppOptions.HostOptions`, and `Rack_AndPicker_UseTheSameStateFile_NextToTheGraph` checks that the picker and the rack use the same file next to `graph.json`. State files written before the fix stay in the folder the mixer was started from; the mixer does not move them.
3. **Elgato EQ crashes on any real rate change** (`0xC0000005` or `0xC0000374` inside its module). Changing 48 → 48 kHz and loading directly at 44.1 or 96 kHz both work. A host that skips renegotiating the buses crashes the same way, so the cause is in the plug-in. In the mixer the slot goes dry and comes back at the node's rate after 2 s; `CrashOnARateChange_ComesBackAtTheNodeRate` covers this. Each rate change counts as a crash, so three changes within 5 minutes put the slot in «Сбой».
4. **Elgato Compressor is 13 dB quieter on ragged blocks**, which application captures produce. With «Ровные блоки» its output matches the 480-frame output bit for bit. The mixer does not switch that flag on by itself.
5. **Skipping a reconfiguration when the reported latency is unchanged would be wrong.** MAutoPitch reports a latency change with the same value. Many plug-ins, like the built-in `latency`, return the new latency only after reactivation, so the host has to reconfigure every time. The cost is one dry block, which the slot smooths.
6. **Four plug-ins give different output with other block sizes**: Noise Removal, Graillon, Surge XT Effects and Pip-Boy. The level stays the same, but whether it sounds right can only be heard. The check writes both outputs to `probe-out/compat/<name>-480.wav` and `-ragged.wav`; nobody has listened to them yet.
7. **Memory**: most hosts take 3–28 MB private. CHOWTapeModel (217 MB) and Youlean (289 MB) take far more, so 16 heavy plug-ins would need several GB. The ceiling of 16 stays.

The gain at 44.1 kHz is not listed: the rate change comes after the parameter edit, so it compares a different setting.

### Deviations from the specification

1. **The table is here, not in the README**, as the user asked.
2. **Two plug-ins of the list were replaced.** TAL-Chorus-LX took the place of Waves (Waves needs an account). Elgato Compressor took the place of Dragonfly Room Reverb: only Dragonfly's 32-bit build was installed, and it stays as the 32-bit example.
3. **The compatibility check is a console tool**, not part of `dotnet test`, because it depends on the plug-ins installed on the machine. The tests use the built-in plug-ins instead.
4. **Audio runs without pauses**, with a 1 s deadline. Load is taken from the `process` time; the deadline is not checked. Blocks the host answers as suspended are not counted as failures, and after a rate change the check waits until the host is no longer suspended.
5. **All 16 slots come back 2.2–5 s after a mass kill**, not within 3 s. The 3 s criterion is for one host; 16 hosts starting at once take longer.

### Tests (397 total: 395 pass, 2 manual tuning sweeps skipped)

`PluginStressTests` (5, serial with the timing tests):

- **16 slots** run on 4 nodes under audio, and the 17th slot gets «лимит». In 5 s there were 0–3 misses in about 8,000 blocks (the test allows up to 1%). No host restarted on its own. All 16 hosts were then killed at once: every slot went dry and came back, and no host processes were left after `Dispose`.
- **Slot churn**: slots are added, removed, moved between groups, switched on and off, and their even blocks toggled every 20–150 ms for 8 s under audio. That is 43–51 edits and 119–142 hosts. Afterwards the last layout loads in full, the audio stays finite, and no host processes are left.
- **A crashing slot among working ones**: the slot crashes again and again until «Сбой». Its neighbours on the same node and on another node stay running. The slot right after it in the same chain loses one block per crash, because the block's deadline is shared.
- **A crash during a rate change** (the Elgato EQ case): the slot comes back at the node's rate, whether the host died before, during or after `setRate`.
- **A module that crashes on load**: three attempts, then «Сбой», and no new hosts.

`PluginHostTests` adds `Load_WaitsForAPluginThatRestartsRightAfterActivation` (fix 1), and `PluginUiTests` adds `Rack_AndPicker_UseTheSameStateFile_NextToTheGraph` (fix 2).

Manual check of the criterion «после аварийного завершения микшера ни одного `Mixer.PluginHost.exe` не остаётся»: `Mixer.PluginBench --builtin --frames 480,128` was force-killed while two hosts ran in its Job Object (the same `PluginHost` code as the mixer). Both hosts were gone within 0.5 s.

### Acceptance criteria

| Criterion | Status |
| --- | --- |
| 6 plug-ins on 4 nodes for 8 hours, a pinned bus into two outputs, faults no worse than without plug-ins, ≤ 0.5% CPU per plug-in for the exchange | **not checked**: needs the user's devices. Exchange CPU ≤ 0.05% per plug-in (stage 0) |
| A host killed in Task Manager: no gap, dry, back in 3 s with its state | met: `Rack_RestartsACrashedHostWithItsLastState` (1.9–3 s, stage 3) and the user's check on 12 November |
| "Sleeps 50 ms" is switched off within 10 s, the rest plays | met: `Rack_SlowPluginIsSwitchedOff` (stage 3) |
| Settings made in the plug-in window survive a restart, bit for bit for the test plug-in | met: `State_SurvivesARestart_BitForBit` (stage 4); 20 of 20 real plug-ins keep an edited parameter. In the mixer itself only after fix 2 |
| A scan of 100 plug-ins with a hanging and a crashing one finishes, the UI responds, the cache run takes < 1 s | met: `PluginCatalogTests` (stage 2) |
| A pinned bus into two outputs with different clocks: one plug-in process, sound in both | met: the device check on 12 November (stage 3) |
| 512 samples show «+10,7 мс» at 48 kHz and add to the total; the parallel-path warning appears | met: stage 3 and 4 tests |
| No `Mixer.PluginHost.exe` left after the mixer crashes | met: the manual check above |
| At least 18 of the 20 listed plug-ins work | met: 20 of 20 |

### Status as of 13 November 2026

- Done: the compatibility check, the table, the warm-up fix, the state folder fix, the stress and crash tests. 395 tests pass (2 manual sweeps skipped).
- **Not checked**: the 8-hour run on devices; listening to the `-ragged.wav` files of Noise Removal, Graillon, Surge XT Effects and Pip-Boy; Youlean's settings through a restart by hand; plug-in windows on a monitor above 100% scaling; the plug-ins in the running mixer UI.
- The first of five runs of the 16-slot test failed: one host ended by itself during the 5 s of audio, with 17 misses, and its log was not kept. It has not happened since. The test now prints the slot's state and fault if it happens again.

## Localization

The UI is in English by default and in Russian when the Windows display language is Russian. The user can pick the language in Settings (⚙) → "Language": "Same as Windows", "English" or "Русский" (language names are shown in their own language). The choice is saved at once to `settings.json` next to `graph.json` (`%AppData%\Mixer`; with `--graph`, next to that file) and applies after a restart: the panel then shows "Restart now", which exits normally (graph saved, Take over redirects released) and starts a new instance with the same switches, minus `--lang`, `--open` and `--snapshot`. Strings are not swapped live: XAML takes them once, when elements are created.

Order at startup: `--lang en|ru`, then `settings.json`, then the Windows language. A missing or broken `settings.json` means "Same as Windows"; a broken one is noted in the log.

- **Strings** live in resources, one pair per project: `src/Mixer.App/Resources/Strings.resx` (UI), `src/Mixer.Core/Resources/CoreStrings.resx` (messages the core builds for node cards, wires and settings: waiting and error reasons, loops, sink checks, Take over states), `src/Mixer.Audio/Resources/AudioStrings.resx` (device and stream errors). The neutral `.resx` is English, `.ru.resx` is Russian with the same keys. Keys are `Area_What`; strings with placeholders end in `Format`.
- **Accessor classes** (`Strings`, `CoreStrings`, `AudioStrings`, the `*.Designer.cs` next to each `.resx`) are generated by `tools/gen-strings.ps1` and checked in: the WPF temporary project (`wpftmp`) doesn't see sources generated during the build, so `{x:Static res:Strings.…}` in XAML wouldn't compile. After changing a `.resx`, run `powershell -NoProfile -ExecutionPolicy Bypass -File tools\gen-strings.ps1`; a test fails if a class is out of date.
- **Culture**: `App.OnStartup` sets the UI and formatting culture (`ru-RU` or `en-US`) for all threads before the host starts, and the XAML language. Numbers follow it: `−3,0 дБ` and `−3.0 dB`, always with a real minus sign (`Fmt`). Plurals: `Plural_*` keys hold the forms separated by `|`, two in English and three in Russian (`Fmt.Plural`).
- **Not translated**: node names the user typed (`graph.json`), device and application names from Windows. Default node names ("Default output") come from resources; when the device of such a node changes, a name in either language is recognised as the mixer's own.
- **Log** messages are English literals in the code in both languages. Exceptions: messages that are also shown in the UI (sink check results, Take over states, node errors) go to the log in the UI language.
- The audio threads don't touch `ResourceManager`: stream names used in the render loop are read once when the node is created.
- **Tests** run in Russian (`Culture` module initializer in `tests/Mixer.Tests/LocalizationTests.cs`), so text checks don't depend on the machine. `LocalizationTests` check that both languages have the same keys and placeholders, the plural form counts, the generated classes, English numbers and plurals, language selection, the main window and palette in English, saving the language choice, when a restart is offered, and the restart switches.
- `tools/Mixer.Run`, `tools/Mixer.Probe` and `tools/Mixer.PluginBench` are developer consoles and stay in Russian.
