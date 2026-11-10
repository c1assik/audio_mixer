# Mixer.Probe: stage 0

A console tool for checking the risks from the «Риски» (risks) section of the specification. Its own interop without NAudio: process loopback (`ActivateAudioInterfaceAsync`), `IAudioPolicyConfig` through the vtable (slots 25/26, same layout as EarTrumpet), WASAPI capture/render.

Build: `dotnet build -c Release`; run: `bin\Release\net8.0-windows\Mixer.Probe.exe help`.

| Command | What it does |
| --- | --- |
| `list` | devices, audio sessions, availability of process loopback and `IAudioPolicyConfig` |
| `test --tone --sink <device>` | the stage 0 gate on the built-in tone generator: capture → Take over → reset |
| `test --exe discord --sink <device>` | the same on a real application (it must play continuously) |
| `formats --exe <name>` | pcm16/float32 × 48000/44100 × flags, simultaneously |
| `capture --exe <name>` / `capture --system` | capture an application / all audio except the tool itself to WAV |
| `redirect`, `reset`, `status --exe <name>`, `status --self` | manual control of the binding |
| `tone`, `wavstat --file <wav>` | 1 kHz sine generator; RMS per second and per channel |
| `multi --tone --clients N` | N process loopback clients in one process; call tracing, a watchdog that dumps stacks (`dotnet-stack`) |
| `capture --device <name>` | WASAPI loopback of a device, for comparison |
| `many --device <name> [--clients 24]` | v2: N tone generators (one process each) and N include captures, opened in steps; CPU, memory, open and stop time, glitches per step |
| `newapp --device <name> [--detect poll\|notify\|both]` | v2: time from a new application's first sound to the first packet in its capture; detection by a 1 s poll (as `MixerHost`) or by `IAudioSessionNotification` |
| `direct --tone\|--exe <name> --output <out> --sink <sink>` | v2: gap when an application switches between the direct route (bound to `<out>`) and capture (bound to the sink, forwarded to `<out>` by the tool); handoff modes `naive`, `early`, `session`, `sink` |
| `lowlat --list`, `lowlat --device <name> [--capture] [--compare]` | v2: `IAudioClient3` engine periods of all devices (no streams); a stream at the minimum period with wake-up intervals, glitches per minute and CPU |

**The sink must be a device that actually accepts streams.** Voicemeeter VAIO inputs without Voicemeeter running are not suitable (see result 4).

## Results: Windows 11 25H2, build 26200.8246, 9 November 2026

1. **The key assumption is confirmed.** After redirection to the sink, process loopback keeps receiving audio without gaps: a −33.0 dBFS tone in the phases before, during and after, about 48,000 frames/s, 0 discontinuities. Tested on the tone generator with automatic stream routing, two runs.
2. **`IAudioPolicyConfig` works**, variant `ab3d4648` (21H2+). `Set` and `Get` agree. ID format: `\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{guid}#{e6327cad-dcec-4949-ae8a-991e976a79d2}`. A stream with automatic routing moves to the sink in about 2 s and comes back after a reset.
3. **The binding is stored per application, i.e. by exe path, not by PID.** A new process of the same exe starts in the sink right away, and even its `GetDefaultAudioEndpoint` returns the sink. The binding can be reset only while a process of that exe is alive, and it must have touched audio at least once; otherwise `Set`/`Get` return `E_INVALIDARG`.
4. **A bad sink brings down Audiosrv.** On "Voicemeeter In 5" without Voicemeeter running, a new process hung in `IAudioClient.Initialize`. The Windows Audio service hung twice (`AppHangB1` / `AppHangXProcB1` with `audiodg.exe`); Windows killed and restarted it. There was no sound for about a minute.
5. **Hangs with several clients are a consequence of item 4, not a separate problem.** Between 12:48 and 13:03, while the service was suffering from the VAIO sink or had just recovered, `formats` (12 clients) and `capture` (2 clients) hung on stop (`Wait/LpcReply`). Once a single client received no packets at all, and `GetNextPacketSize` blocked until `IAudioClient.Stop`. After the service recovered, the `multi` command (tracing every call, with a watchdog) gave no failures in 22 runs: 1, 2, 4 and 12 clients on one target and 3 different targets. `formats` with 12 clients of mixed formats passed too. Takeaway for the engine: capture calls can block when the service is unhealthy. A per-node watchdog is needed: no packets for N seconds, recreate the capture. COM calls must not hold the UI thread.
6. **Formats.** All 12 variants work: pcm16 and float32 × 48,000 and 44,100 × `AUTOCONVERTPCM` / `+SRC_DEFAULT_QUALITY` / no flags. The rate matches the requested one exactly, levels match the reference (Δ 0.0 dB). Process loopback converts to the requested format by itself even without `AUTOCONVERTPCM`. So the specification's option "open the capture directly at the outputs' rate" is available.
7. **Rate.** Over 37.5 s process loopback delivered exactly 48,000.0 frames/s, device loopback 96,000.0. Sonar is virtual and clocked by the system timer, so drift here is zero. The pace of process loopback is set by the clock of the device the application plays to. The drift of a physical sound card was not measured on this machine.
8. **CABLE Input works fully as a sink.** The gate passed: −48.0 dBFS in all phases, 0 discontinuities. A process starting with an already persisted binding opens on CABLE Input normally (`Initialize` 13 ms) in auto and fixed modes.
9. **Firefox passed the gate** (17 processes, root 13624, sink CABLE Input). The session moved immediately and came back after the reset. Sonar loopback received no packets in phase B, i.e. the device was idle and there was no double sound. The test now counts this automatically.
10. **Stereo capture of a process on a 7.1 device is 13.5 dB quieter.** This is Windows' normalized downmix: a tone in FL/FR only gives −48 → −61.5 dBFS. Because of it, Firefox's level jumped by 15 dB between Sonar (7.1) and CABLE Input (stereo). The Windows volume mixer has nothing to do with it: 100% on both devices. Capture in 7.1 (`--channels 8`, `WAVEFORMATEXTENSIBLE`) returns the channels unchanged on both the 7.1 and the stereo device: stereo sits in FL/FR at its original level. Decision: capture in 7.1 and do our own ITU downmix in the engine.
11. **Loopback of the SteelSeries Sonar virtual device barely hears the application** (about −70 dBFS for a −33/−48 tone). So double sound on this machine is judged by audio sessions, not by loopback.

**Not tested:** Discord, drift of a physical sound card, builds 23H2 and 24H2.

## v2 stage 0: Windows 11 25H2, build 26200.8246, 10 November 2026

These are the four measurements from «План работ» → «Этап 0» of the v2 specification. Mixer.exe was closed during the runs. Every v2 command rejects Voicemeeter VAIO endpoints (`Safety`). Test signals are a sine at −70 dBFS (RMS −73) or uniform noise at −70 dBFS RMS. Raw output is in `probe-out\v2-*.txt`, which is not in git.

### 1. 24 simultaneous captures

Command: `many --device "CABLE Input" --clients 24 --seconds 20 --format float32`. 24 tone generators play to CABLE Input, and include captures are opened in steps of 0, 1, 6, 12, 18 and 24. Results of two clean runs:

| Captures | Tool, % of one core | audiodg, % of one core | Tool private memory | DATA_DISCONTINUITY | Starved 100 ms buckets | Level, dBFS |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | 0.9–1.2 | 1.3–2.8 | 14–16 MB | — | — | — |
| 1 | 0.2–0.9 | 0.8–1.1 | 15–19 MB | 0 | 0 | −73.0 |
| 6 | 2.0–2.4 | 2.2–3.5 | 17 MB | 0 | 0 | −73.0 |
| 12 | 2.5–7.8 | 2.7–3.8 | 19 MB | 0 | 0 | −73.0 |
| 24 | 3.8–5.3 | 2.2–4.2 | 23 MB | 0 | 0 | −73.0 |

- The machine has 12 logical cores. At 24 captures the tool takes at most 0.45% of the whole CPU and audiodg at most 0.35%.
- Opening a capture takes about 1 ms: activation 0–5 ms, `Initialize` 0–2 ms, `Start` under 1 ms. The first packet arrives 17–38 ms after `Start`.
- Stopping takes 4–11 ms per capture. There were no hangs.
- Each capture hears only its own generator: all read −73.0 dBFS.
- **Gate: the ceiling stays at 24 captures.** The tool never reached a limit, and the cost is small. More than 24 was not measured.

**Two anomalies.** Both happened in the first two runs, right after Mixer.exe was closed. Neither came back in six later runs.

- The first capture of the process received no packets at all for 11–16 s.
- The next capture's `Initialize` took 6.5 s. After that, both captures delivered normally.

This matches result 5 above: capture calls stall while the audio service is unhealthy. The engine's `CaptureWatchdog` (3 s without packets → recreate) must also cover every sub-capture of All system audio.

**A tool bug, now fixed.** Reading 48 redirected stdout and stderr pipes starved the thread pool. A generator's pipe filled up, and the generator blocked in `Console.WriteLine` inside its render loop. Its capture then received digital zeros at exactly 48,000 frames/s. `ToneProcess` now raises the thread pool minimum.

### 2. A new application in All system audio

Command: `newapp --device "CABLE Input" --runs 10`. The time runs from the generator's `IAudioClient.Start` to the first non-silent packet in a newly opened include capture.

| Detection | Lost audio, median | Min…max |
| --- | --- | --- |
| Poll every 1 s (as `MixerHost` does in v1) | 475 ms | 261…559 ms |
| `IAudioSessionNotification.OnSessionCreated` | 36 ms | 35…36 ms |

- The session is created at `Initialize`, 6–7 ms before `Start`. The notification arrives at that point, and the capture is open 1–2 ms after `Start`.
- The remaining ~35 ms is the capture's own start-up.
- One watcher tick (Toolhelp32 plus the sessions of 10 devices) takes 8–11 ms.
- **Conclusion: with the notification, the specification's «около секунды» (about a second) shrinks to ~35 ms.** Proposal for stage 3: subscribe to `OnSessionCreated` on every render endpoint and keep the 1 s tick as a fallback. Register the notification from an MTA thread, after `GetSessionEnumerator`.

### 3. Direct route ↔ capture

Command: `direct`, with the fifine speakers as the output and CABLE Input as the sink. The tool forwards the process loopback to the output through a 20 ms ring and a 15 ms output queue, as the engine does. A gap is a 5 ms window of the output's loopback that is more than 6 dB below the direct-route level. `Set` for all three roles takes 2–6 ms.

| Application and handoff | Direct → capture, longest gap | Capture → direct, longest gap | Double sound |
| --- | --- | --- | --- |
| Noise generator, `naive` (path switched together with `Set`) | 25–40 ms | 0–5 ms | none |
| Noise generator, `early` (path on 20 or 40 ms before `Set`) | 40–45 ms | 0–5 ms | 15–25 ms |
| Noise generator, `session` (wait 13–72 ms for the session on the new device) | 35–115 ms | 0 ms | up to 15 ms |
| Noise generator, `sink` (wait ~190 ms for the sound in the sink's loopback) | 200–210 ms | 0 ms | 130–140 ms |
| Firefox (YouTube white noise), `naive` | 35–40 ms | 0–15 ms | not visible above the metric's floor |
| Firefox, `session` (wait 24–26 ms) | 30–45 ms | 0 ms | not visible above the metric's floor |

- **The direct → capture gap comes from moving the application's stream.** Turning the tool's path on early (`early`) doesn't close it, and the ring runs dry during the move. So process loopback has the same hole.
- The sink's loopback shows the application 160–200 ms after `Set`. That is too late to use as a handoff signal.
- **The Firefox double-sound column is noise.** Even with the tool's path off, about 10% of 5 ms windows read more than 2 dB above the reference, because of the content itself.
- The first Firefox run was discarded: the level rose by 20 dB after 5 s.
- Firefox followed the binding without restarting playback. Its original binding (the default device) was restored after the test.
- **Gate: the direct route can be on by default, switched `naive`.** Change the binding and the engine path at the same moment. The worst case is a ~45 ms gap when a node leaves the direct route and at most 15 ms when it returns. The specification estimated 100–300 ms. The acceptance criterion «разрыв не дольше срока, зафиксированного на этапе 0» (the gap must not exceed the time fixed in stage 0) therefore becomes 50 ms.

### 4. `IAudioClient3`

Engine periods in frames at 48 kHz, from `lowlat --list`:

| Device | Default | Minimum |
| --- | --- | --- |
| Realtek Digital Output (Realtek) | 480 | 480 |
| fifine speakers and microphone (USB) | 480 | 480 |
| Mi TV (AMD HDMI) | 480 | 480 |
| Elgato virtual devices | 480 | 480 |
| CABLE Input, CABLE In 16ch (VB-Cable) | 480 | 128 (2.67 ms), step 1 |
| CABLE Output | 480 | 480 |

Each device ran for 30 s with `--compare`, against `Initialize` with a 10 ms buffer.

| Device | Period | Buffer | Wake-up interval p99 | Glitches | CPU |
| --- | --- | --- | --- | --- | --- |
| fifine, Realtek, Mi TV (playback) | 10 ms | 1056 frames | ≤ 10.9 ms | 0, same as `Initialize` | — |
| CABLE Input (playback) | 128 frames | 280 frames | 3.3 ms | 0 | 0.6% of one core |
| fifine microphone | 10 ms | — | — | 1–2 per 30 s in both modes | — |

- The microphone glitches are the device's extra packets flagged DATA_DISCONTINUITY (deviation 7 in DEVELOPMENT.md). The microphone runs at 44.1 kHz, and the engine resamples to 48 kHz.
- `GetStreamLatency` returns 0 on every device here.
- **None of the three physical cards on this machine supports small periods, so the 25 ms criterion was not verified.** It needs a card whose driver reports a minimum below 480 frames. Run `lowlat --list` on another PC to find one.

**Not tested in v2 stage 0:** Discord and a game for the direct route; `IAudioClient3` on a physical card with small buffers; more than 24 captures; builds 23H2 and 24H2.
