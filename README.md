<div align="center">
  <img src="assets/shim-repo-icon-512.png" alt="Shim transmission check-engine warning icon" width="240">
</div>

# Shim

[![Shim build](https://github.com/Rephl3x/Shim/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/Rephl3x/Shim/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/Rephl3x/Shim)](https://github.com/Rephl3x/Shim/releases/latest)

**Shim gives Forza one wheel to see when your rig has several USB controllers.** It combines a MOZA wheel, pedals, shifters, handbrake, and other selected controls into one vJoy controller. It then forwards the game's force feedback to the physical wheel.

This addresses rigs where steering works but force feedback goes missing because Forza binds a different device. Shim uses HidHide while the bridge runs so the selected physical controllers do not compete with the virtual wheel. You still create and bind a custom wheel profile in Forza. Shim does not change USB device order, patch the game, or inject into it.

## Download Shim 0.8.0

| Download | Use it for |
| --- | --- |
| [Windows installer](https://github.com/Rephl3x/Shim/releases/download/v0.8.0/Shim-Setup-0.8.0.exe) ([SHA-256](https://github.com/Rephl3x/Shim/releases/download/v0.8.0/Shim-Setup-0.8.0.exe.sha256)) | Installing or upgrading Shim. It includes the vJoy and HidHide installers and can back up existing settings. |
| [Portable ZIP](https://github.com/Rephl3x/Shim/releases/download/v0.8.0/Shim-0.8.0.zip) ([SHA-256](https://github.com/Rephl3x/Shim/releases/download/v0.8.0/Shim-0.8.0.zip.sha256)) | Running from an extracted folder. Keep `Shim.exe` beside its `drivers` and `runtime` folders. |

**Requirements:** Windows 10/11 x64 and .NET Framework 4.8 or later. Driver installation and vJoy configuration need Windows administrator approval; restart if a driver installer asks. The vJoy and HidHide vendor installers are checked for signatures and hashes before packaging. Shim's own app and setup executables are currently unsigned.

## Get driving

1. Run the installer, or extract the entire portable ZIP and open `Shim.exe`. The installer upgrades an existing installation and offers to keep or reset settings with a timestamped backup.
2. Follow **Set up my rig**. Choose your devices, learn the controls, and calibrate axes and gears. The final Save step configures and checks the chosen vJoy slot, then saves the profile; other vJoy slots are left alone.
3. Start the bridge, wait for **Running**, then launch Forza. **Start Forza** on the Drive page can do the launch; enable **Start bridge when I click Start Forza** under Options to do both in order.
4. In Forza, create a custom wheel profile and bind the virtual wheel's steering, pedals, gears, and buttons. Test at a low force strength first.

Close and reopen Forza if you stop or restart the bridge. The running game can keep an old device or force-feedback session open. The [full setup guide](bridge/README.md) covers mapping, driver checks, HidHide, and recovery in detail.

## What is included

| Area | What Shim does |
| --- | --- |
| Controls | Merges selected devices through DirectInput, with a six-step wizard, editable mappings, input monitor, and calibration. |
| Force feedback | Forwards game effects from vJoy to the selected physical wheel. Change strength while driving without restarting the bridge. |
| Device visibility | Hides selected physical game-controller interfaces during a bridge session and restores visibility on stop or recovery. |
| Everyday use | Drive dashboard, live FFB counters, Start Forza, Day/Night modes, driver status, and activity logs. |
| Updates | Checks GitHub releases for Shim, vJoy, and HidHide. Shim updates apply in the app; driver updates require signature checks and Windows approval. |
| Support | Built-in no-FFB checklist and optional, user-reviewed diagnostic reports. |

## If steering works but there is no FFB

Open **Help & reports** at the bottom of Shim's sidebar for the full checklist. Start with these checks:

1. Close Forza, start the bridge, wait for **Running**, and launch the game again.
2. Confirm vJoy and HidHide are ready on **Drivers**, and that Forza's custom wheel profile uses Shim's virtual wheel.
3. Select the physical wheel as the FFB destination. Turn on forwarding and set both Shim and in-game force strength above zero.
4. Check the **FFB packets** and **Wheel starts** counters on Drive. Zero packets points to the game-to-vJoy route; packets without force point to effect playback or the physical-wheel route. **Activity** records driver errors.

Shim normally runs without administrator rights after setup. If Shim or Forza has **Run this program as an administrator** enabled in Compatibility settings, turn that off for both while troubleshooting. Close other FFB tools that may hold the wheel exclusively. See the [detailed no-FFB checklist](bridge/README.md#no-force-feedback-checklist).

## Support reports and privacy

Open **Help & reports**, then click **Create / submit report** to create a ZIP on your PC. It contains Windows and Shim versions, CPU and memory details, GPU and relevant controller-driver versions, controller names and capabilities, limited FFB settings and counters, and recent Shim logs. It does not include button mappings, calibration, Forza account data, or unrelated files. Device names and diagnostic logs can still contain identifying details, so inspect the ZIP before sharing.

You can keep the report locally, open an email draft addressed to yourself, or explicitly upload it to Shim support. Shim never sends the email draft for you. Uploads use HTTPS, are visible only to the Shim maintainer through a private portal that displays each report file on one page, and expire after 30 days. Cloudflare provides the network and private storage service. No report is uploaded unless you choose **Submit to support**.

## Build from source

Shim uses the Windows .NET Framework C# compiler and needs no NuGet packages. `PackageBridge.ps1` fetches pinned vendor packages, verifies their signatures and hashes, builds Shim, and creates the portable ZIP. `BuildInstaller.ps1` needs Inno Setup 6.7.3 or later and creates the Windows setup from that payload.

```powershell
.\PackageBridge.ps1
.\BuildInstaller.ps1 -PayloadDirectory .\build\payload
```

Local builds write temporary files to `build` and the current installer, ZIP, and checksums to `dist/latest`. Both folders are ignored by Git; download published builds from the release links above. The [GitHub Actions workflow](.github/workflows/build.yml) builds and tests Shim on Windows. The [support service](report-service/README.md) is a separate Cloudflare Worker.

For help or a bug report, use [GitHub Issues](https://github.com/Rephl3x/Shim/issues).

## Say thanks

Shim is free and open source. If it has helped with your rig, you can [buy me a coffee](https://buymeacoffee.com/amslabs). Support is optional; feedback and bug reports help too.

