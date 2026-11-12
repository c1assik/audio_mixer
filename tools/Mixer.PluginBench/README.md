# Mixer.PluginBench: VST stage 0

A console tool for stage 0 of «Микшер для Windows: поддержка VST»: how much the exchange with `Mixer.PluginHost.exe` costs, and how much memory one host takes. `HostChannel.cs` is the mixer side of the audio channel (the section, events, deadline, host in a Job Object). It is the prototype of the transport behind the future `PluginSlot`.

Build: `./tools/build-pluginhost.ps1` (host and AGain), then `dotnet build tools/Mixer.PluginBench -c Release`. Run:

```
tools\Mixer.PluginBench\bin\Release\net8.0-windows\Mixer.PluginBench.exe --frames 480,128 --minutes 60
```

| Argument | Meaning |
| --- | --- |
| `--plugin <path>` / `--builtin` | plug-in (default: `again.vst3` from the host build) / no plug-in, the host copies input to output |
| `--frames 480,128` | block sizes; each one gets its own thread and its own host, and they run in parallel |
| `--minutes`, `--rate` | duration (default 1), sample rate (48000) |
| `--deadline-pct`, `--spin-us`, `--gate-us` | deadline in % of the period (40), spin before the kernel wait (50 µs), stage gate (100 µs) |
| `--stall-every N --stall-ms M` | the host sleeps M ms on every N-th block (deadline check) |
| `--out` | result JSON, default `probe-out/pluginbench-*.json` |

Each run is a thread registered with MMCSS as "Pro Audio", like a device thread in the engine. A high-resolution waitable timer wakes it once per period. The thread sends a 440/660 Hz sine through the host with a deadline of 40% of the period and compares the output with the input bit for bit (AGain at its default gain of 1.0 and the copy are both exact). It reports:

- **exchange**: the time the audio thread spends on a block minus the host's own `process` time. It covers the input copy, the signal, the host waking up, the answer and the output copy. This is the gated figure.
- **→ host** (`Request` set → host thread awake) and **← back** (host before `Done` → mixer sees the answer) are the two halves of it.
- **timer**: how late the bench's own timer woke the thread. This is context only. The engine is woken by WASAPI events, not by this timer.
- CPU of the host process and of the audio thread inside the exchange. The latter is wall time, so it is an upper bound that includes spinning. Memory of the host: after load, the maximum over the run (sampled every 5 s) and at the end.

The exit code is 0 when every run passes the gate: p99 exchange ≤ 100 µs, no mismatches, and the host still alive.

## Results: Windows 11 25H2 (26200), Ryzen 5 5600G, 12 logical cores, 12 November 2026

AGain from SDK 3.8.1, 48 kHz. The 480-frame (10 ms) and 128-frame (2.67 ms) runs went in parallel for one hour each, with two hosts and two audio threads. The machine was in normal use, and the user's mixer may have been running. Raw data: [results/](results/).

| µs | 480, AGain, 1 h | 128, AGain, 1 h | 480, no plug-in, 5 min | 128, no plug-in, 5 min |
| --- | --- | --- | --- | --- |
| exchange p50 | 12.0 | 9.1 | 12.7 | 8.7 |
| **exchange p99** | **28.9** | **31.0** | 41.3 | 28.9 |
| exchange p99.9 | 256 | 256 | 72 | 82 |
| exchange max | 487 | 516 | 315 | 494 |
| → host p50 / p99 | 10.5 / 27.1 | 8.4 / 25.9 | 11.0 / 29.0 | 8.0 / 26.3 |
| ← back p99 | 0.7 | 8.3 | 18.1 | 7.0 |
| AGain `process` p99 | 3.9 | 3.5 | 1.7 | 1.1 |
| blocks / missed | 360,000 / 0 | 1,350,000 / 0 | 30,000 / 0 | 112,500 / 0 |

1. **The gate is passed.** Exchange p99 is 29–31 µs, against a gate of 100 µs. No deadline was missed in 1.71 million blocks, and every output matched its input bit for bit. The protocol stays synchronous; the fallback (a ring with one block of latency) is not needed.
2. **Almost all of the cost is waking the host thread.** A cross-process `SetEvent` to a sleeping MMCSS thread takes 8–11 µs at the median and about 26 µs at p99. The way back is almost free: the 50 µs spin catches the answer in 99.7% of blocks, so the mixer thread almost never sleeps. The rest (both copies and the `SetEvent` call) is 1–2 µs on average: exchange mean 14.0 µs against 12.5 + 0.3 µs for the two halves at 480 frames.
3. **The tail is system-wide.** 0.22% of blocks exceeded 100 µs. They are clustered in a few minutes (minutes 8–9 and 33 held 72% of them at 480 frames and 82% at 128), at the same moments in both runs, and the host wake-up accounts for almost all of the time. The worst block took 516 µs. That is half of the 1.07 ms deadline at the 2.67 ms low-latency period, and an eighth of the 4 ms deadline at 10 ms. A plug-in on a low-latency node therefore has roughly 0.5 ms of `process` time before tail events start to cause misses. This is the input for the risk «Обмен с хостом дороже ожидаемого при низкой задержке»: nodes with plug-ins can stay in low latency, but light plug-ins should be the norm there.
4. **CPU per plug-in.** The host process takes 0.09% (480) and 0.18% (128) of one core. The mixer's audio thread spends at most 0.15% and 0.46% of one core in the exchange (wall time, spin included). Out of all 12 cores that is ≤ 0.02% and ≤ 0.05% per plug-in, well under the 0.5% acceptance criterion, which is measured the way the mixer's header measures CPU: as a share of all cores. Measured against one core, the 128-frame case is 0.64%, mostly spin.
5. **Memory per host.** Without a plug-in: 1.2 MB private and 7.6 MB working set. With AGain: 25–27 MB private and 36–38 MB working set. It did not grow over the hour (+0.1 MB). Most of AGain's cost is its own module (VSTGUI, a second static CRT), so real plug-ins will cost more. 16 small plug-ins come to about 0.6 GB of working set. The ceiling of 16 and the "one host for all" fallback from the specification stand.
6. **Startup.** From process start to "ready", 40 ms without a plug-in and 100–150 ms with AGain. Eight warm-up blocks follow; without them, the first `process` at 128 frames missed its deadline once in a 15 s test.
7. **The deadline logic works.** With `--stall-every 300 --stall-ms 50` at 480 frames, the misses fell on exactly the stalled requests (blocks 291, 596, 901, 1206; the first 8 requests are the warm-up). Each miss was followed by 5 "busy" blocks (50 ms of stall ÷ 10 ms period), and the late answers were dropped by block number. There were no mismatches.
