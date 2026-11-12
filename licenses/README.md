# Third-party licenses

Components built into `Mixer.exe` and its plug-in host `Mixer.PluginHost.exe`. The texts are embedded in the program and shown in Settings → «О программе»; the host's folder `%LocalAppData%\Mixer\bin\<version>\` gets a copy as well.

| Component | Where | License |
| --- | --- | --- |
| VST 3 SDK 3.8 (Steinberg Media Technologies GmbH) | `Mixer.PluginHost.exe` | MIT, [VST3_SDK.txt](VST3_SDK.txt) |
| JSON for Modern C++ 3.11 (Niels Lohmann) | `Mixer.PluginHost.exe` | MIT, [nlohmann_json.txt](nlohmann_json.txt) |
| Oswald font (The Oswald Project Authors) | `Mixer.exe`, Modular theme | SIL OFL 1.1, [src/Mixer.App/Fonts/Oswald-OFL.txt](../src/Mixer.App/Fonts/Oswald-OFL.txt) |

VST is a trademark of Steinberg Media Technologies GmbH. The mixer does not use the VST logo; the UI says "VST3" in plain text.
