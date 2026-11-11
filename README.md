# Mixer for Windows

Mixer collects audio from different places and routes it where you need it. Your microphone, a game, Discord and music go in on one side; headphones and your stream come out on the other. Every connection is a wire, and every wire has its own volume. You can see at a glance what goes where, with no need to keep buses A1–B3 in your head as in Voicemeeter.

![Main window: four sources on the left, headphones and a stream output on the right, wires with per-wire volume in between](docs/images/main-window.png)


What it does:

- takes audio from microphones and other inputs, from any output (whatever is playing in your headphones or speakers) and **from a single application**: Discord, a browser, a game, a music player;
- plays to any playback device, including the VB-Cable virtual cable, so you can hand the mix to OBS or Discord;
- sets the volume of every source, every output and every wire separately, with a level meter on each;
- groups sources into **buses**: one fader for "game + chat", fewer wires;
- cleans up your voice with a **gate, equalizer and compressor** on any node, with ready-made presets;
- sends an application straight to its output when it needs nothing from the mixer (**direct route**: no added latency or CPU);
- opens devices with **low latency** where the sound card driver allows it;
- saves your layout automatically and restores it on start, picks up unplugged and reconnected devices, and survives sleep.

## What's new in 2.0

- **Buses**: a middle column between sources and outputs. Select sources and group them in one click; levels stay the same.
- **Processing**: gate, EQ and compressor on every source, bus and output, with an EQ curve you drag with the mouse.
- **All system audio** can leave out chosen programs (say, a messenger), and shows which processes it skipped and why.
- New applications are picked up within tens of milliseconds, so the first notification sound isn't lost.
- **Direct routes**: an application that goes to exactly one output, unprocessed, plays there directly.
- **Low latency** on outputs (and, experimentally, on inputs) where the driver supports small buffers.
- Your 1.0 layout is moved over automatically (see "Upgrading from 1.0").

## Requirements

- **Windows 11**, 64-bit. On Windows 10 (version 1809 or later) Mixer starts, but capturing individual applications may be unavailable; applications are then greyed out in the list with an explanation.
- **VB-Cable**, if you want to hand the mix to another program (OBS, Discord, Zoom). It is a free download from [vb-audio.com/Cable](https://vb-audio.com/Cable/). You don't need it to mix into your headphones.
- No .NET installation: everything is inside `Mixer.exe`.

## Getting started

1. Download `Mixer.exe` and put it in any folder.
2. Run it. There is nothing to install.
3. If Windows shows "Windows protected your PC", click **More info → Run anyway**. Windows does this for any program without a digital signature, and asks only once.

Mixer works while its window is open. Close the window and audio through Mixer stops; applications play as usual again. Only one copy runs at a time: starting it again brings the open window to the front.

To start Mixer with Windows, put a shortcut to `Mixer.exe` in the Startup folder: press Win+R, type `shell:startup`, press Enter.

## Your first layout

The window is a canvas: **sources on the left, outputs on the right**, wires in between. Audio always flows left to right.

Example for streaming: you hear the game and Discord in your headphones, while your microphone, the game (a bit quieter) and Discord go to the stream.

1. Click **+ Source** (`+ Источник`) and pick your microphone. Add the game and Discord the same way: they are under "Applications playing audio" (`Приложения со звуком`), or under "Other running applications" (`Другие запущенные приложения`) if they are silent right now. The list has a search box.

   ![The "Add source" list: applications playing audio, other applications, inputs, output audio](docs/images/palette.png)

2. Click **+ Output** (`+ Выход`) and add your headphones and **CABLE Input**, the input of the virtual cable; you will pick it in OBS later.
3. Drag wires from the dot on the right of a source to an output:
   - game → headphones, game → CABLE Input;
   - Discord → headphones, Discord → CABLE Input;
   - microphone → CABLE Input.
4. Hover over the "0 dB" label in the middle of the game → CABLE Input wire and scroll down to −10 dB. Now the game doesn't drown out your voice on stream, and still sounds the same in your headphones.
5. In OBS, add an "Audio Input Capture" source and select **CABLE Output** in it.

Discord takes your microphone directly, as usual: there is no need to route it through Mixer.

The layout saves itself a second after every change. Undo is Ctrl+Z.

Four sources going to two outputs make eight wires. To tidy that up, Ctrl+click the game and Discord, then click **Group into bus** (`Сгруппировать в шину`): Mixer adds a bus "Bus 1" in the middle column, wires both sources into it and the bus into both outputs. Every level stays exactly as it was, and now one fader turns the whole group up or down.

## Sources and outputs

| Source | What it is |
| --- | --- |
| **Input** (`Входы`) | a microphone, line input, CABLE Output |
| **Output audio** (`Звук выходов (loopback)`) | everything currently playing on the chosen device (headphones, speakers) |
| **Application** | the audio of one program, matched by its exe name. If the program isn't running, the node waits and connects by itself when it starts |
| **All system audio** (`Весь звук системы`, under `Особые`) | all programs except Mixer itself. Only one per layout |
| **Default device** (`по умолчанию`) | the microphone or output selected as default in Windows. Change the device in Windows and the node follows. The "communications" variant (`для связи`) follows the default communications device, the one Discord and calls use |

An output is any playback device: headphones, speakers, CABLE Input.

A **bus** (**+ Bus**, `+ Шина`) sits in the middle column. Several sources (or another bus) go in, and the sum goes on to outputs. It has its own fader, mute, meter and processing. A bus can feed another bus, but no more than two in a row. A bus with no inputs or no outputs is drawn faded: nothing is heard through it. "Ungroup" (`Разгруппировать`) in the bus menu turns it back into direct wires at the same levels.

Every node has a volume from −60 to +12 dB, an **M** button (mute) and a level meter. A source's menu also has **mono** (sum the left and right channels, handy for a microphone that records into one channel) and balance. If an output gets too loud, a limiter gently holds it back, and a badge lights up on the card.

## Controls

| To do this | Do this |
| --- | --- |
| Add a node | **+ Source** / **+ Output**, or right-click an empty spot in a column |
| Connect | drag from the dot on a node to a node in the other column; or node menu → "Send to…" (`Вывести в…`) / "Receive from…" (`Принимать из…`) |
| Wire volume | scroll over the "dB" label on the wire (with Shift, in 0.1 dB steps), or drag the label up and down |
| Turn a wire off | click the "dB" label; double-click to reset to 0 dB |
| Node volume | the fader, or scroll over it; double-click for 0 dB |
| Move or delete a wire | drag the wire by its end (near the node) to another node or into empty space; or select it and press Delete; or right-click → "Delete" (`Удалить провод`) |
| Rename | double-click the name, or F2 |
| Mono, balance, colour, another device | node menu: "⋯" or right-click |
| Group sources into a bus | Ctrl+click two or more sources, then **Group into bus**; or right-click one of them |
| Processing | click **+ FX** or a Gate · EQ · Comp chip on the card |
| See routes | hover over a node: its wires light up |
| Undo / redo | Ctrl+Z / Ctrl+Y |
| Zoom | Ctrl + mouse wheel, Ctrl +/−/0 |
| Scroll | drag with the middle mouse button, or with Space held down |

Hover over a wire to see its total gain, sample rate conversion and latency.

## "Take over": so an application doesn't play twice

When you add an application to Mixer, it keeps playing to its own speakers too. Without extra setup you would hear it twice: directly and through Mixer.

That is why an Application node has a **Take over** switch (`Забрать себе`, in the node menu, on by default). Mixer asks Windows to move the application to a **sink**: a device nobody listens to. The application plays there, and Mixer still captures its audio and sends it along your wires.

Choose the sink once in Settings (⚙ in the top right corner). Good choices:

- **a separate virtual cable**, the best option. Just not the one that goes to OBS or Discord: free VB-Cable has a single cable, additional ones (A, B) are sold on the same site;
- **an unused output** on your sound card with nothing plugged in, such as a digital output or the HDMI output of a monitor without speakers.

Mixer tests the device with a short probe stream before accepting it. If the device isn't suitable, you can't assign it, and Settings explains why.

Good to know:

- When you close Mixer, running applications return to their devices immediately.
- Windows remembers the sink per program, and a program can only be moved back while it is running. So an application you closed before Mixer (or every application, if Mixer crashed) may be silent the next time you start it. Just start Mixer again: it moves such an application back within a couple of seconds of the application starting.
- To fix it by hand, without Mixer: **Settings → System → Sound → Volume mixer**, pick the application and set its output device to "Default". The "Reset" button there does this for all applications at once.
- Some applications move to the sink only when they start playback again. The card then asks you to "restart playback" (`перезапустите воспроизведение`).
- If the sink has fewer channels than your main device (say, 7.1 headphones and a stereo cable), some applications get quieter. Settings warns you about this. Restarting playback in the application helps, or set the cable to 7.1 in its device properties.
- "All system audio" also hears applications in the sink. If it and such an application go to the same output, that application plays twice; the card warns you.

## Processing: gate, EQ, compressor

Every source, bus and output has a chain of three blocks in a fixed order: **gate → EQ → compressor**, then the fader. Click **+ FX** on a card (or a Gate · EQ · Comp chip once a block is on) to open the side panel.

- **Gate** silences the background between phrases: threshold, attack, hold, release and how deep it cuts.
- **EQ**: a high-pass filter (40–300 Hz) against rumble, and four bands (peak, low shelf, high shelf). Drag a point on the curve to change frequency and gain, scroll over it for Q, right-click for the band type, double-click for 0 dB.
- **Compressor** evens out quiet and loud phrases: threshold, ratio, attack, release, knee and makeup. The card shows how much it is reducing right now.

Presets: "Voice — clean", "Voice — broadcast", "Music — quieter and smoother". "Save as…" keeps your own. A block that is off costs nothing, and turning knobs never clicks. The "In" and "Out" meters at the bottom compare the level before and after processing.

## Applications: exclusions and direct routes

**All system audio** captures every program except Mixer. To keep a program out of it (say, a messenger out of your stream) without giving it its own node, open the node menu → **Exclude apps** (`Исключить приложения`): tick a program that is playing now, or type any exe name. Programs that have their own node are left out automatically so you don't hear them twice. If you want such a program in both (Discord on its own wire to the stream, and together with everything else in your headphones), turn on **Keep apps that have their own node**.

Windows can only capture a program together with its whole process tree. When a tree contains Mixer or an excluded program, it can't be captured, and the card shows **skipped: N** (`пропущено: N`); hover over it to see which processes and why.

**Direct route.** When an Application node goes to exactly one output, has no processing and its level is at most 0 dB, Mixer doesn't capture it at all: it sends the application straight to that output and sets its volume there. The wire shows a lightning bolt and "direct" (`напрямую`). There is no added latency or CPU. Add a second wire, turn on processing or raise the level above 0 dB, and the node goes back through Mixer by itself; the switch causes a gap of a few tens of milliseconds. A direct route needs Take over (below). It can be turned off in the node menu: **Direct route** (`Прямой маршрут`).

## Low latency

Outputs open with the smallest buffer the sound card driver allows (**Low latency**, `Низкая задержка`, in the node menu, on by default). On a driver that supports it, the period drops from 10 ms to as little as 2.7 ms, and the whole path through Mixer gets shorter. Many drivers only allow 10 ms; then nothing changes, and the card's tooltip says so.

- The small buffer applies to the whole Windows audio engine on that device, for every program, and slightly raises system CPU use.
- If the device glitches more than 3 times a minute, the node goes back to 10 ms by itself, and the card explains why. To try again, turn the mode off and on.
- On inputs (microphones) the mode is off by default and marked experimental: with some drivers a capture stream with a small buffer glitches.
- It is off by default on VB-Cable outputs: the cable adds its own buffer, and a small one makes it glitch.

## What the cards show

![Node and wire states: normal, muted, waiting, error, limiter; wire on, off, being dragged, forbidden](docs/images/states.png)

| On the card | Meaning |
| --- | --- |
| faded, "device disconnected" (`устройство отключено`) | the device was unplugged or turned off. Wires and volume are kept, and audio resumes by itself when the device comes back |
| "waiting for discord.exe" (`ждёт запуска discord.exe`) | the program isn't running; the node connects by itself when it starts |
| "in use by another application" (`занято другим приложением`) | another program has taken the device in exclusive mode. Mixer retries every 2 seconds |
| "waiting for Voicemeeter" (`ждёт Voicemeeter`) | this is a Voicemeeter device and Voicemeeter isn't running. Without it, such devices can hang Windows audio, so Mixer leaves them alone and connects when Voicemeeter starts |
| red frame with text | an error; the reason is written on the card |
| red dashed wire, "loop" (`петля`) | the wire would feed audio back into itself (e.g. headphones audio back into the headphones, or CABLE Output into CABLE Input). Such a wire doesn't play; Mixer won't let you create it and explains why |

The top bar shows the total latency, CPU load and the number of audio glitches. The bottom bar shows how many nodes are on the canvas, whether VB-Cable was found and whether application capture is available.

## Limitations

- **Latency is about 40–50 ms** on most sound cards. That is unnoticeable for streaming, calls and listening, but monitoring your own voice through Mixer in real time is uncomfortable: you will hear a slight echo. With low latency on a card that supports small buffers it is lower; VB-Cable always adds its own buffer.
- Games with anti-cheat and programs running as administrator may refuse to give up their audio or to move to the sink. The card shows an error.
- No noise suppression, VST, tray icon, scenes or hotkeys yet.

## Where your data lives

| What | Where |
| --- | --- |
| layout | `%AppData%\Mixer\graph.json` |
| layout backups (the last 5) | same folder, `graph.json.bak-…` |
| your 1.0 layout, as it was before the upgrade | same folder, `graph.v1.json` |
| settings (language, your processing presets) | same folder, `settings.json` |
| log | `%LocalAppData%\Mixer\logs` |

To open a folder, paste the path into the File Explorer address bar. If the layout file gets corrupted, Mixer restores it from the most recent backup and moves the broken file aside as `graph.json.broken-…`.

**To uninstall**, close Mixer and delete `Mixer.exe` and both `Mixer` folders.

## Upgrading from 1.0

Just replace `Mixer.exe`. On first start 2.0 moves your layout over as it is: nodes, wires, names, levels and positions. Processing is off, so everything sounds as before. The original file is kept as `graph.v1.json` next to the layout; to go back to 1.0, put that file back as `graph.json`.

If two or more sources go to the same outputs, Mixer offers to group them into a bus. Click **Group into a bus** (`Сгруппировать в шину`) or **No thanks** (`Не нужно`); nothing changes until you choose.

## Troubleshooting

- **No sound from an application.** Read what the card says. If you just started the application, give it a second. If the card asks you to restart playback, pause and play again.
- **An application plays twice.** Turn on Take over for its node and assign a sink in ⚙.
- **Sound disappeared after Mixer crashed.** Start Mixer again: it moves applications back to their devices. Or fix it by hand in the Windows Volume mixer (see above).
- **Crackling or dropouts.** Check the glitch counter in the top bar and the wire's tooltip. Close programs that take the audio device in exclusive mode. If it started with low latency, turn **Low latency** off on that node.
- **Something else.** Attach the log from `%LocalAppData%\Mixer\logs` to your question: it records what happened with devices and applications.

## For developers

Architecture, stage history, measurements and tests are in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md). The specification and plan (in Russian) is `Микшер для Windows v2 спецификация и план.md`.

```
dotnet build Mixer.sln -c Release
dotnet test tests/Mixer.Tests -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File tools\publish.ps1
```

The last command builds `dist\Mixer.exe` for release: tests, a single self-contained exe, signing (if you pass a certificate), a launch check and a checksum.

GitHub Actions ([.github/workflows/build.yml](.github/workflows/build.yml)) builds and tests every push and pull request and keeps `Mixer.exe` as a build artifact. Pushing a `v*` tag (for example `git tag v1.1.0 && git push origin v1.1.0`) also creates a GitHub Release with the exe and its checksum, versioned from the tag. To sign it, add the repository secrets `SIGN_PFX_BASE64` (the .pfx file in base64) and `SIGN_PFX_PASSWORD`.

The screenshots in `docs/images` are cut from the design mockup (`Windows Mixer — mockup.html`).
