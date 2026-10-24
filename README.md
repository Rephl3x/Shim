# Shim

[![Shim build](https://github.com/Rephl3x/Shim/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/Rephl3x/Shim/actions/workflows/build.yml)

**Shim is a Windows controller aggregator and force-feedback adapter for Forza Motorsport and Forza Horizon.** It combines a MOZA wheel, pedals, shifters, handbrake and other selected controls into one vJoy virtual wheel, then forwards the game's force feedback to the physical steering wheel.

The bridge reads physical controls through DirectInput, maps them to a configurable virtual controller and uses HidHide to keep the selected physical devices out of the game's controller list while the bridge runs. Forza binds the virtual wheel once in a custom profile. This avoids relying on the order in which several physical controllers happen to enumerate.

## Current capabilities

- A six-step setup wizard detects devices, lets you choose control roles, and guides input learning and calibration.
- The final Save step configures the chosen vJoy slot, verifies its layout, then saves the profile. It does not overwrite other vJoy slots.
- Live input monitoring and editable mappings help diagnose and tune a rig before driving.
- Game force feedback is forwarded from the virtual controller to the selected physical wheel. Gain can be adjusted while the bridge is running without resetting the connection or effects.
- HidHide visibility recovery restores selected physical devices when the bridge stops or recovers from an interrupted session.
- Day and Night appearance modes, a Drive dashboard, driver status and activity logs are included.

## Download and setup

The latest build is [Shim v0.3.3](https://github.com/Rephl3x/Shim/releases/tag/v0.3.3). Each release includes its versioned ZIP and SHA-256 checksum. Extract the ZIP to a permanent folder and keep `Shim.exe` beside its `drivers` and `runtime` folders.

Windows 10/11 x64 with .NET Framework 4.8 or later is required; .NET Framework 4.8.1 is recommended where available. vJoy and HidHide are included for one-time installation; the wizard checks their status and explains when a restart or repair is needed. For the full first-run guide, controls, troubleshooting and safety details, see [the bridge guide](bridge/README.md).

After setup, start the bridge before launching Forza. Create a custom wheel profile in Forza and bind it to the virtual controller. The app does not inject into the game, patch game files or change USB device order.

## Build from source

The source uses the Windows .NET Framework C# compiler and does not require NuGet packages. `PackageBridge.ps1` fetches pinned official driver packages, verifies their signatures and hashes, builds the app and creates a portable ZIP. Run it from Windows PowerShell:

```powershell
.\PackageBridge.ps1 -OutputDirectory .\dist\bridge
```

