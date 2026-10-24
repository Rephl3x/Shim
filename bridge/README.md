# FH Rig Bridge 0.2.2

Our Windows app combines physical racing controls into one virtual force-feedback controller. It contains our own DirectInput reader, input mapper, vJoy force-feedback packet decoder, and DirectInput effect forwarding code. It does not run Forza EmuWheel, UCR, or another mapper.

This build adds guided first-run setup. Physical input reading has been checked with the connected MOZA R21, HGP, Saitek panel and USB gamepad. Automated tests cover setup, calibration, the mapper, FFB protocol and hiding recovery. **Virtual output, actual motor forces and Forza Horizon 6 compatibility still need hands-on verification.**

Version 0.2.1 fixes the false **HidHide returned an oversized response** error when starting or restoring a session. HidHide list writes report bytes consumed despite having no output buffer; those acknowledgements are now handled separately from reads. List reads query the required size and retry if it changes. Recovery verifies the actual driver state before clearing its record. Restore visibility also works in the same app window after its bridge worker has stopped. Profiles and calibration remain compatible.

Version 0.2.2 fixes **Update physical FFB effect failed (0x800704DF)**. That code means `DIERR_ALREADYINITIALIZED`: the previous updater resent the effect's fixed axis assignment on every change. Updates now send only changed fields, leaving the initialized axis/direction alone. Unchanged updates are skipped. If the driver cannot apply a change while playing, the updater retries once with DirectInput's normal playback-preserving behavior. Error messages now distinguish this from an exclusive-access conflict. A nonzero Cartesian steering direction and explicit actuator enable are set at initialization; game mute/pause commands and the gain cap remain effective.

## What is in the package

- `FH-Rig-Bridge.exe`: our app, including the setup wizard, automatic vJoy configuration, a read-only input monitor, learnable mappings, profile storage, FFB forwarding and device visibility recovery.
- `drivers/vJoySetup_v2.2.2.0_Win10_Win11.exe`: the unchanged official signed vJoy installer.
- `drivers/HidHide_1.5.230_x64.exe`: the unchanged official signed HidHide installer.
- `runtime/x64/vJoyInterface.dll`: the matching signed x64 library from the official vJoy SDK.
- `licenses`: upstream license texts. `THIRD-PARTY-NOTICES.md` identifies the components and sources.
- Our source, tests, dependency lock file and rebuild/package scripts.

Windows 10/11 **x64** with .NET Framework 4.8 is required. The app itself needs no installer. Drivers need a one-time installation and may request a Windows restart. Driver installation/configuration starts when you click its button in setup. The bridge does not disable USB devices, patch game files, inject into the game, or change USB controller order.

## First setup

1. Extract the entire ZIP to a permanent local folder. Keep the EXE beside the `drivers` and `runtime` folders.
2. Open the app. The **six-step setup wizard** opens automatically for a new or older profile. You can reopen it any time with **Set up my rig**.
3. On **Drivers**, install vJoy and HidHide if needed. Their pinned hashes are checked before Windows launches the official installers. Finish each installer and restart Windows if requested. Click **Configure virtual wheel automatically**, accept Windows' administrator prompt, then **Recheck drivers**. This uses vJoy's installed `vJoyConfig.exe` to replace only your selected slot's layout: all eight axes, 128 buttons, four continuous POV hats and the eleven supported FFB effects. It refreshes vJoy, so close games and other feeders first. Other vJoy slots are not deleted/reset.
4. On **Devices**, choose the wheel, pedal source, clutch, H-pattern shifter/gear count/reverse, sequential shifter/paddles and analog handbrake. Choose **None / not fitted** for optional equipment you do not use. Pedals plugged into the wheelbase use the wheelbase as their source. Tick additional button boxes/controllers you want included and hidden. Devices chosen for a role are already included.
5. On **Controls**, select each requested control. Steering captures full left, full right and verifies centre. Pedals/handbrake capture released and fully pressed/pulled positions; inversion is detected automatically. For gears, return to neutral, click **Listen**, then engage the requested gear. Existing axis and named gear mappings can be reused or relearned. Generic button maps are rebuilt so one device cannot consume all outputs. Remaining buttons/hats can be assigned automatically, with learned gears reserved first and remaining button space shared across devices.
6. On **Feedback**, choose whether to forward game FFB, its gain cap and force inversion. The default cap is 25%. Setup only reads inputs and never sends a motor test.
7. On **Finish**, move each control and check the live mapped values. Finish stays disabled until driver configuration and live input checks pass. **Finish setup** backs up your previous profile and saves the new one. It does not start the bridge. Start it from the main window, then launch Forza and bind your virtual wheel in a custom profile.

**If vJoy's installer appears stuck at the end:** the driver can be present while its installer/helper is still running. The wizard reports these separately and prevents duplicate installation/configuration attempts. Finish/close the installer's window if possible; if it stays stuck, restart Windows and reopen setup. No installer is forcibly killed. Driver presence alone is insufficient: a default vJoy device may have only eight buttons and no hats. **Configure virtual wheel automatically** fixes the layout after installation has finished. Checks run in a fresh helper with a ten-second limit so a driver check cannot freeze the setup screen indefinitely.

You can continue selecting/calibrating devices while installation finishes. **Save progress and close** stores a separate setup draft so you can resume after a restart. The active profile stays unchanged until Finish. Cancelling setup does not roll back driver installations/configuration you explicitly started.

HidHide must use normal mode. Review any old inactive hiding rules in **HidHide Configuration Client**. If Pit House needs access while bridging, add Pit House to its Applications list. Keep Forza off that list. If driver/DLL versions differ, finish installation/restart and use a matching pair; the bundled DLL matches vJoy 2.2.2.0.

The installers' Windows signatures were verified during packaging. The bridge EXE itself is unsigned. Upstream driver installers retain their own publisher identities and licenses.

## Advanced mapping (optional after the wizard)

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

**After an FFB error or bridge stop/restart, close and restart Forza after the bridge is running.** Stopping resets vJoy and removes the game's effects. An already-running game can keep steering working without recreating its force effects.

The status line separates incoming **FFB packets** from **wheel effects created / updates / starts accepted**, and shows effective gain and game mute/pause state. These counters confirm accepted software/driver calls; they do not measure motor torque. Session logs record the first report of each type, a five-second FFB summary, and final totals, so a later restart with no effect definitions is distinguishable from a physical-driver update failure. Zero packets means the game has not sent FFB to this vJoy session. Packets with no starts mean playback has not been requested/accepted yet; restart the game after the bridge if it retained old effects. Gain 0%, muted or paused state can also explain no force.

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
%LOCALAPPDATA%\FHRigBridge\setup-draft.json
%LOCALAPPDATA%\FHRigBridge\profile-before-setup-*.json
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

The build uses Windows' built-in .NET Framework C# compiler. No NuGet packages are required. Dependency fetching uses pinned official release URLs, SHA-256 hashes and Authenticode verification. Fetching/package creation downloads files but never runs their installers. `PackageBridge.ps1` writes `dist\FH-Rig-Bridge-0.2.2.zip`. All three scripts accept `-OutputDirectory` to build a new release beside an open older copy, for example `PackageBridge.ps1 -OutputDirectory .\dist\bridge-0.2.2`.

An optional desktop smoke check uses a connected FFB wheel for read-only input polling and exercises the real WinForms wizard, including back navigation, required-control validation, final readiness, cancellation and resume. It restores any previous setup draft and checks that the active profile stayed unchanged:

```powershell
.\dist\bridge\FH-Rig-Bridge.Tests.exe --desktop
```

Read-only diagnostics and offscreen UI preview:

```powershell
.\FH-Rig-Bridge.exe --diagnose C:\Temp\rig-report.json
.\FH-Rig-Bridge.exe --preview C:\Temp\rig-preview.png
.\FH-Rig-Bridge.exe --preview-wizard C:\Temp\wizard-preview.png 2
```

With the bridge stopped, `FH-Rig-Bridge.exe --check-ffb-update C:\Temp\ffb-update-report.json` creates/updates/frees constant and spring effects at **zero gain without any effect Start calls**. It opens the profile's physical FFB wheel exclusively, checks gain/autocentre restoration and does not start the bridge, hide devices, or write vJoy inputs. This validates driver interoperability; it is not a motor-force test.

To recover a stopped session without opening the UI, run `FH-Rig-Bridge.exe --restore-visibility C:\Temp\visibility-report.json`. It applies the saved session rollback and records before/after state; it refuses to recover a session owned by another running process. The driver control handle is closed between snapshot, recovery and verification because HidHide allows one control client at a time.

Diagnostics include controller instance identifiers; review before sharing. Preview mode disables driver setup, bridge start and visibility recovery. Wizard preview takes a page index from 0 to 5. Neither mode sends motor forces or saves a profile.

Validation: 84 automated checks cover setup, mapping, FFB and recovery, including the exact already-initialized failure through a simulated physical driver, changed-field updates, playback-preserving fallback and native adapter/packet integration. The desktop smoke flow uses read-only R21 polling. The physical R21 accepted two effect creations and three updates (constant magnitude in both directions and spring condition updates) at zero gain with **zero Start calls**; gain/autocentre readback matched their original values. Native HidHide recovery was also verified in 0.2.1. All six wizard screens and the main window were rendered for inspection. Both bundled installers and the SDK DLL have valid publisher signatures. In-game motor feedback remains a hands-on test.

API references: [Microsoft DirectInput](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ee418998(v=vs.85)), [effect parameters](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ee416616(v=vs.85)), [vJoy SDK](https://github.com/BrunnerInnovation/vJoy), [vJoy configuration command source](https://github.com/BrunnerInnovation/vJoy/blob/v2.2.2.0/apps/vJoyConf/vJoyConfig/vJoyConfig.cpp), [HidHide](https://github.com/nefarius/HidHide).
