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
