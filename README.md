# FH MOZA Fix

Forza Motorsport/Horizon Adapter for Force Feedback

The newer **FH Rig Bridge 0.3.0** combines physical controls into one virtual wheel using our own mapper and FFB bridge, with official vJoy/HidHide installers bundled. It includes guided setup, a cleaner Drive dashboard, saved Day/Night mode and live FFB strength adjustment without resetting the bridge. See [the bridge guide](bridge/README.md). Build/package it with `BuildBridge.ps1 -Test` and `PackageBridge.ps1`. The reconnect utility below remains a separate tool.

A portable Windows utility that disconnects selected USB game controllers and reconnects them in your chosen order. Put the wheel base first to try the MOZA / Forza Horizon 6 force-feedback workaround without crawling behind the PC.

**This is an experimental reconnect workaround. It cannot guarantee that Forza will assign the wheel to Device 1.** Device disable/enable is a logical reconnect, not a physical USB power cycle. Windows, drivers, and the game can retain their own device ordering. You must verify Device 1 and force feedback in the game.

## Quick start

1. Extract `FH-Moza-Fix-portable.zip` to a folder. Run `FH-Moza-Fix.exe`.
2. Close **Forza Horizon 6** before starting a sequence, or tick **Force reconnect even if Forza is running** to reconnect with the game open. This option skips the game's running-process check and is saved with your order, including when reopening as administrator. It is off by default. If a driver refuses to disconnect, close MOZA Pit House and other controller utilities too.
3. Tick the wheel base and the other game controllers you need to disconnect: separate pedals, shifters, handbrakes, gamepads, flight/sim panels, and so on. A controller left connected can still take priority. Select the wheel's row and click **Make first**. Use **Move up / Move down** to arrange the accessories.
4. Keep **Pause after the wheel** selected for the first attempt. Click **Save order**, then **Reopen as administrator**. Windows will request permission to manage these devices. If using a different administrator account, select/save the order again in that account.
5. Click **Reconnect selected**. The app disconnects the selected devices in reverse order, then enables the first device and waits for its game-controller interfaces to return.
6. At the pause, launch or switch to Forza, select/bind the wheel as **Device 1**, and confirm FFB. Leave the app open. Once FFB works, click **Connect accessories**, then bind/test the accessories in Forza. **Pause before each accessory** lets you connect and test each one separately. If the running game does not pick up the returning controllers, restart Forza.
7. On later runs, reuse the saved order. **Reconnect all automatically** restores every controller without pausing; use it only after confirming that this sequence works with your game and drivers.

The numbers in the app are the **requested reconnect sequence**, not the current device numbers in Windows or Forza. The app does not measure FFB or read the game's assigned device numbers.

You can also use **Windows game controllers** to open `joy.cpl`. MOZA recommends selecting the base as the preferred controller under **Advanced**. This preference does not guarantee Forza's device numbering.

## Requirements and distribution

- Windows 10 version 2004 or newer, or Windows 11, **x64**, with .NET Framework 4.8. No additional package downloads or installer are used.
- Administrator access is required for reconnecting or restoring devices. Discovery and diagnosis work without it.
- USB devices using the Windows HID / USB composite drivers are supported. Bluetooth controllers, virtual controllers without a supported physical USB parent, and controllers using other drivers are listed for manual handling when detected.
- The EXE is unsigned. Review the supplied source and obtain it from someone you trust. Respect any device-management or application policies on managed PCs.

Share the ZIP with friends. It includes the EXE, this guide, an optional PowerShell launcher, and the source/build scripts. The EXE also works on its own. Each PC/account chooses its own devices and order; your hardware profile is not included in the ZIP. Keep devices on the same USB ports after saving a profile.

## What the app changes

The app uses Windows' built-in `pnputil.exe /disable-device "<exact instance ID>"` and `/enable-device "<exact instance ID>"`. It disconnects **all selected devices before enabling the wheel**, then restores devices one by one. It checks the Windows device state, waits for every original game-controller interface to return, and applies an adjustable settling delay.

It targets the individual controller's physical USB device, including its own serial configuration interface where supported. This more closely approximates unplugging the controller than restarting just its game-controller HID collection. MOZA's USB serial ports and vendor software components are supported. All game-controller collections on the same physical USB device move together.

Devices sharing keyboard, mouse, system-control, storage, network, or other unsupported functions are excluded. USB hubs and host controllers are never targets. The app does not uninstall devices/drivers, edit game files or controller-order registry keys, install a driver, stop services, or restart the PC. There are no network requests, telemetry, updates, or scheduled actions.

An ordinary USB HID game controller reports the identifiers documented by Microsoft. A device that does not report those identifiers may not appear here. Other connected controllers, including unsupported devices, can still interfere with Forza's ordering.

## Cancel, interruption, and recovery

**Cancel and restore** stops the sequence and attempts to enable the session's controllers. Closing the window during a reconnect also requests recovery before exit. A currently running Windows device command is allowed to finish before cancellation is handled; a hung command has a 45-second timeout.

Before the first disconnect, the app durably saves the exact devices it may need to restore. That record is updated after each successful reconnect. If the app is killed, the PC loses power, or recovery fails, reopen it **as administrator under the same Windows account** and click **Restore devices**. Recovery only enables recorded devices; it does not disconnect anything. A new sequence is blocked while recovery is pending.

If a device was physically unplugged or moved to another port, reconnect it to its original port and retry. Failed/missing devices stay in the recovery file. You can also enable a disabled controller in Device Manager. If Windows reports that a restart is required, the app reports it and attempts recovery; you decide when to restart Windows. Recovery cannot run while the app is stopped.

The profile, recovery record, and logs are stored in:

```text
%LOCALAPPDATA%\FHMozaFix\profile.json
%LOCALAPPDATA%\FHMozaFix\recovery.json
%LOCALAPPDATA%\FHMozaFix\session-YYYYMMDD-HHMMSS.log
```

Do not delete a pending `recovery.json` until your controllers are working. The journal contains devices that were healthy when the session started; the app refuses to start with a selected device that was already disabled or faulted.

## PowerShell and diagnostics

`FH-Moza-Fix.ps1` is an optional **launcher for the EXE**, not a separate implementation. It works beside the EXE in the extracted ZIP or with the `dist` folder in the source checkout.

```powershell
# Open the app. Browsing does not change devices.
.\FH-Moza-Fix.ps1

# Open the app as administrator; reconnect still requires clicking the button.
.\FH-Moza-Fix.ps1 -Administrator

# Write a read-only hardware report, without elevation or device changes.
.\FH-Moza-Fix.ps1 -Diagnose .\device-report.json
```

The EXE also accepts `--diagnose "C:\path\device-report.json"`. The report contains hardware identifiers, USB serials, parent relationships, and driver information. Check it before sharing. Nothing is uploaded automatically. Diagnostic startup errors are written to `%LOCALAPPDATA%\FHMozaFix\startup-error.txt` and return a nonzero process exit code.

## Build and verify

Run these commands in Windows PowerShell from the source folder:

```powershell
.\Build.ps1 -Test
.\Package.ps1
```

`Build.ps1` uses the C# compiler included with Windows' .NET Framework. It writes `dist\FH-Moza-Fix.exe`. There is no NuGet restore or third-party dependency. C# 5 syntax is intentional so the included compiler can build the app.

The automated tests use a fake device backend and **never change hardware**. They cover safe target selection, composite MOZA devices, reconnect order, wheel-only/manual pauses, cancellation, partial command failures, recovery-file failures, missing devices, changed device identities, and recovery after interruption.

For a read-only UI rendering check, run the EXE with `--preview "C:\path\preview.png"`. This captures the app offscreen; reconnect/elevation/recovery actions are disabled in preview mode.

Validation during development: 36 automated checks passed; a read-only scan correctly identified the connected MOZA R21 base, HGP shifter, SGP shifter, Saitek panel, and USB gamepad. The UI rendered successfully. **Physical disconnect/reconnect and in-game FFB were not tested during development.**

## Why this workaround exists

[MOZA's FH6 setup guide](https://support.mozaracing.com/en/support/solutions/articles/70000683812/) recommends binding the base by itself as Device 1, confirming force feedback, and then connecting the accessories. This app attempts that connection sequence in software.

[Microsoft's DirectInput enumeration documentation](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ee417804(v=vs.85)) says enumeration order is not guaranteed. Consequently, completing a reconnect sequence is not proof that FFB is fixed. If the wheel still is not Device 1, try the wheel-only pause and rebind it before restoring accessories. If the driver/game still keeps the wrong order, use MOZA's physical reconnect procedure.

Implementation references: [PnPUtil command syntax](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax), [HID collection identifiers](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/hidclass-hardware-ids-for-top-level-collections), [device status API](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_status).

