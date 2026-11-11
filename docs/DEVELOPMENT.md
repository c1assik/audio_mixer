# Mixer for Windows: developer notes

The user guide is in the [README](../README.md). The specification and plan (in Russian) is `Микшер для Windows спецификация и план.md` in the repository root; section names from it are quoted below in Russian. Stage 0 (risk check): [tools/Mixer.Probe](../tools/Mixer.Probe/README.md). The v2 specification is `Микшер для Windows v2 спецификация и план.md`; its stage 0 results (captures, new applications, direct route, `IAudioClient3`) are in the same Mixer.Probe README; stages 1 (buses), 2 (processing) and 3 (applications) are described below in «v2 stage 1», «v2 stage 2» and «v2 stage 3».

The UI is in English, or in Russian when Windows is in Russian (see "Localization"). Log messages are in English; code comments are in Russian. Russian strings quoted below are shown as they appear in the Russian UI.

## Layout

| Project | Contents |
| --- | --- |
| `src/Mixer.Audio` | Audio engine and WASAPI interop, no WPF: source and output nodes, wires, buses (per-output instances and mix plans), resamplers, PI controller, ramps, limiter, processing chain (gate, EQ, compressor), peak meters; process loopback, `IAudioPolicyConfig`, audio sessions with their volume, meter and new-session notifications, probe stream, device change notifications (`IMMNotificationClient`) |
| `src/Mixer.Core` | Graph model and `graph.json`, device lookup by ID, name and default role, the feedback rule, paths through buses, building the engine snapshot; process snapshot, Take over with the `boundApps` journal, sink validation, watchdog, the `MixerHost` control layer with hot-plug and sleep handling; a WPF-free editor: history, edits (including grouping into a bus), wire validation, layout, store with backups, chain presets, application catalog; file log |
| `src/Mixer.App` | UI (WPF, CommunityToolkit.Mvvm): graph canvas, cards, wires, palette, sink settings, processing side panel with the EQ curve. Builds `Mixer.exe` |
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
