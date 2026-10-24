# FH Rig Bridge 0.1

Our Windows app combines physical racing controls into one virtual force-feedback controller. It contains our own DirectInput reader, input mapper, vJoy force-feedback packet decoder, and DirectInput effect forwarding code. It does not run Forza EmuWheel, UCR, or another mapper.

This first build is ready for hardware testing. Physical input reading has been checked with the connected MOZA R21, HGP, Saitek panel and USB gamepad. Automated tests cover the mapper, FFB protocol and hiding recovery. **Virtual output, actual motor forces and Forza Horizon 6 compatibility have not yet been verified on this PC.**

## What is in the package

- `FH-Rig-Bridge.exe`: our app, including a read-only input monitor, learnable mappings, profile storage, FFB forwarding and device visibility recovery.
- `drivers/vJoySetup_v2.2.2.0_Win10_Win11.exe`: the unchanged official signed vJoy installer.
- `drivers/HidHide_1.5.230_x64.exe`: the unchanged official signed HidHide installer.
- `runtime/x64/vJoyInterface.dll`: the matching signed x64 library from the official vJoy SDK.
- `licenses`: upstream license texts. `THIRD-PARTY-NOTICES.md` identifies the components and sources.
- Our source, tests, dependency lock file and rebuild/package scripts.

Windows 10/11 **x64** with .NET Framework 4.8 is required. The app itself needs no installer. Drivers need a one-time installation and may request a Windows restart. Driver installation is initiated by you through the setup tab. Nothing installs silently, disables USB devices, patches game files, injects into the game, or changes USB controller order.

## First setup

1. Extract the entire ZIP to a permanent local folder. Keep the EXE beside the `drivers` and `runtime` folders.
2. Open the app and choose **Driver setup**. Click **Install bundled vJoy**, then **Install bundled HidHide**. The app verifies the installers' pinned SHA-256 hashes before asking Windows to launch them. Follow each publisher's setup wizard. Restart Windows if requested. Restarting also clears the reboot-pending USB operations recorded by the earlier reconnect tool.
3. Open **Configure vJoy** from Windows' Start menu. Configure only the virtual device you intend to use, normally device **1**, with:
   - X, Y, Z, Rx, Ry, Rz and both sliders.
   - 128 buttons and one **continuous** POV. Configure up to four continuous POVs if you map additional hats.
   - Force feedback enabled, with the standard constant, ramp, periodic and condition effects. Leave **Custom Force** disabled. Steering will be mapped to X.
4. Use normal mode in **HidHide Configuration Client**; whitelist inverse mode is unsupported. If you have old inactive hiding rules, review them there first. If MOZA Pit House needs access during bridging, add Pit House to HidHide's Applications list. Do not whitelist Forza: it must see the virtual controller rather than the physical inputs.
5. Reopen FH Rig Bridge and click **Check drivers**. Both should report ready. If a driver was already installed at a different version, use a matching driver/DLL pair. The bundled DLL matches vJoy 2.2.2.0.

The installers' Windows signatures were verified during packaging. The bridge EXE itself is unsigned. Upstream driver installers retain their own publisher identities and licenses.

## Map your rig

1. In **Devices and mappings**, tick the physical controllers you want merged/hidden. Include other connected controllers that could compete with the virtual wheel in Forza. Unselected physical controllers remain visible to the game.
2. Click **Monitor inputs**. This mode reads selected controllers without hiding devices, writing to vJoy, or sending FFB commands. **Live inputs** shows axes and pressed button numbers.
3. Choose **Steering (X)** in the output selector and click **Learn selected output**. Turn the physical wheel far enough to change its normalized value by more than 0.15. The first qualifying input is assigned. Learn throttle, brake, clutch and handbrake the same way. Return other controls to rest while learning.
4. Use **Invert axis** if a pedal reports the opposite direction. Choose the deadzone before learning. To change those options for an existing mapping, learn that output again. Steering deadzone is centered; pedal deadzone is applied at the released end.
5. Click **Stop / restore devices** to stop monitoring. Click **Map all buttons** to assign physical buttons to virtual buttons. HGP inputs are assigned first, other accessories next, and the FFB wheel last. There are 128 virtual button outputs. If the hardware declares more, the app tells you how many were left unmapped. Use Learn with a specific Button output to replace an unwanted assignment with a needed control. You can also learn POV hats.
6. Choose the R21 under **FFB wheel**. Initially leave FFB off, save the profile and verify all virtual inputs first. For the force test, enable **Forward game force feedback** and start with a low gain; the default cap is **25%**. This caps the game's device gain; it is not a calibrated torque measurement.

**Steering and the FFB destination must be the same physical wheel.** A selected device with no mapped controls is hidden during bridging but contributes no inputs. The app never invents H-pattern gear pulses: a physical gear button stays pressed only while the corresponding input is pressed.

The output axis names are our suggested assignments. Bind those virtual axes and buttons to their intended functions in the game. The app does not edit Forza's saved mappings.

## Start and play

1. Close Forza for the first setup. Click **Start bridge** and wait for **Running** and the input-frame counter.
2. Launch Forza. The selected physical HID inputs are hidden; the virtual vJoy controller remains visible. Create a custom wheel profile and bind its axes, individual gears and buttons once.
3. Confirm steering, pedals and every H-pattern gear. Then test FFB at a low gain. The app's **FFB packets received** counter shows whether the game is sending feedback to the virtual device.
4. Keep the bridge running while playing. On later launches, load the same profile, start the bridge, then launch Forza. Keeping a stable virtual device and hiding its physical sources removes competition between those selected physical sources for Device 1. Other unhidden controllers can still affect enumeration.
5. Click **Stop / restore devices** when done. Restart Forza before switching between the virtual rig and native physical-device profiles. An already running game may retain handles it opened before hiding was enabled.

The vJoy device number in this app identifies a vJoy slot; it is not a readout of Forza's Device 1/2 assignment. The bridge does not inspect the game's device list or verify FFB in the game automatically.

## Force-feedback implementation

The input direction is:

```text
Physical devices -> our DirectInput reader/mapper -> vJoy -> Forza
```

The return direction is:

```text
Forza -> vJoy FFB reports -> our decoder -> DirectInput effects -> selected wheel motor
```

The bridge forwards the game's actual effect commands, rather than synthesizing forces from telemetry. It handles constant, ramp, square, sine, triangle, sawtooth up/down, spring, damper, inertia and friction effects, along with envelopes, gain, start/stop, free/reset, pause/continue and actuator commands. It decodes signed fields, converts HID milliseconds to DirectInput microseconds, and forwards effects to one steering axis. Y-only conditions are ignored. Two-axis directional forces are projected onto steering where specified.

**Custom force sample data is unsupported.** Malformed or unsupported packets stop the session and trigger cleanup. The bridge's current handling of trigger buttons is untriggered playback: effects start through the explicit operation reports. Hardware-specific unsupported effect parameters can cause the bridge to stop with a DirectInput error. Real-game effect compatibility needs testing.

**Invert steering forces** reverses signed constant/ramp forces and directional periodic forces. Condition coefficients are forwarded unchanged, preserving spring/damper behavior. The wheel's original readable global gain and autocenter settings are restored on normal shutdown. On failure/disconnection, the bridge attempts to stop effects, release the virtual device, close physical handles and restore visibility. Driver/device failures can prevent a cleanup command from completing.

Inputs are processed in a worker thread with a nominal 4 ms wait between iterations. Actual scheduling and USB/game latency depend on Windows and the drivers; no latency figure has been measured or guaranteed.

## Device visibility and recovery

HidHide hides the selected **game-controller HID interfaces**, rather than disabling entire USB devices or their serial configuration ports. Our process is added to its allowed-app list so it can continue reading those devices and sending FFB. HidHide's normal allowlist model means other non-allowlisted applications also lose access to the hidden interfaces during the session.

Before changing hiding rules, the app durably records its additions and starts a separate copy of itself as a recovery watchdog. Normal stop removes the session's additions while preserving existing HidHide rules and other applications' new additions. If the app exits unexpectedly, the watchdog attempts to stop the selected wheel's forces and restore device visibility. A watchdog cannot guarantee recovery from OS/driver failure or loss of power; if recovery remains pending, reopen the app and click **Restore visibility**. HidHide Configuration Client also provides manual control over hiding.

The released HidHide version bundled here uses persistent hiding rules, so this rollback/watchdog is necessary. We do not depend on newer, unreleased session-hiding APIs.

Keep the app in the same folder once configured. These files are stored per Windows account:

```text
%LOCALAPPDATA%\FHRigBridge\profile.json
%LOCALAPPDATA%\FHRigBridge\hiding-recovery.json
%LOCALAPPDATA%\FHRigBridge\session-*.log
```

If a controller changes instance identity after a USB-port or firmware change, refresh and relearn its mappings. Profiles are local to each PC and are not included in the shared ZIP.

## Rebuild and package

From the extracted source/package root, use Windows PowerShell:

```powershell
.\BuildBridge.ps1 -Test
.\FetchBridgeDependencies.ps1
.\PackageBridge.ps1
```

The build uses Windows' built-in .NET Framework C# compiler. No NuGet packages are required. Dependency fetching uses pinned official release URLs, SHA-256 hashes and Authenticode verification. Fetching/package creation downloads files but never runs their installers. `PackageBridge.ps1` writes `dist\FH-Rig-Bridge-0.1.0.zip`.

Read-only diagnostics and offscreen UI preview:

```powershell
.\FH-Rig-Bridge.exe --diagnose C:\Temp\rig-report.json
.\FH-Rig-Bridge.exe --preview C:\Temp\rig-preview.png
```

Diagnostics include controller instance identifiers; review before sharing. Preview mode disables driver setup, bridge start and visibility recovery. Neither mode sends motor forces.

Validation for this build: 48 automated checks pass using fake FFB/hiding backends; native controller discovery and nonexclusive input polling succeeded on the connected R21, HGP, Saitek panel and gamepad. The app and setup UI rendered. Both driver installers and the SDK DLL have valid publisher signatures. The drivers were **not installed** and physical motor output was **not tested** during development.

API references: [Microsoft DirectInput](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ee418998(v=vs.85)), [effect parameters](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ee416616(v=vs.85)), [vJoy SDK](https://github.com/BrunnerInnovation/vJoy), [HidHide](https://github.com/nefarius/HidHide).
