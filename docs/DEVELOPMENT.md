# Mixer for Windows: developer notes

The user guide is in the [README](../README.md). The specification and plan (in Russian) is `Микшер для Windows спецификация и план.md` in the repository root; section names from it are quoted below in Russian. Stage 0 (risk check): [tools/Mixer.Probe](../tools/Mixer.Probe/README.md).

The UI is in English, or in Russian when Windows is in Russian (see "Localization"). Log messages are in English; code comments are in Russian. Russian strings quoted below are shown as they appear in the Russian UI.

## Layout

| Project | Contents |
| --- | --- |
| `src/Mixer.Audio` | Audio engine and WASAPI interop, no WPF: source and output nodes, wires, resamplers, PI controller, ramps, limiter, peak meters; process loopback, `IAudioPolicyConfig`, audio sessions, probe stream, device change notifications (`IMMNotificationClient`) |
| `src/Mixer.Core` | Graph model and `graph.json`, device lookup by ID, name and default role, the feedback rule, building the engine snapshot; process snapshot, Take over with the `boundApps` journal, sink validation, watchdog, the `MixerHost` control layer with hot-plug and sleep handling; a WPF-free editor: history, edits, wire validation, layout, store with backups, application catalog; file log |
| `src/Mixer.App` | UI (WPF, CommunityToolkit.Mvvm): graph canvas, cards, wires, palette, sink settings. Builds `Mixer.exe` |
| `tools/Mixer.Run` | Console host for stages 1–2: runs a JSON graph through `MixerHost` with statistics, lists applications, checks a sink, measures loop latency |
| `tests/Mixer.Tests` | xUnit tests without a sound card, including UI view models (on their own STA thread, without a window) |
| `tools/publish.ps1`, `tools/make-icon.ps1` | Release build of a single `Mixer.exe` and the program icon (stage 5) |

Build: `dotnet build Mixer.sln -c Release`; tests: `dotnet test tests/Mixer.Tests -c Release`; release: `powershell -NoProfile -ExecutionPolicy Bypass -File tools\publish.ps1` (see "Stage 5").

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
- `tools/Mixer.Run` and `tools/Mixer.Probe` are developer consoles and stay in Russian.
