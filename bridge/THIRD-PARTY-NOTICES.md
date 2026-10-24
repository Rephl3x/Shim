# Third-party components

FH Rig Bridge's mapper and force-feedback bridge are our own implementation. The package includes these unchanged upstream components:

| Component | Version | Included files | Source and release |
| --- | --- | --- | --- |
| BRUNNER vJoy | 2.2.2.0 | Official installer and x64 `vJoyInterface.dll` from its SDK | [Source](https://github.com/BrunnerInnovation/vJoy/tree/v2.2.2.0), [release](https://github.com/BrunnerInnovation/vJoy/releases/tag/v2.2.2.0) |
| Nefarius HidHide | 1.5.230.0 | Official x64 installer | [Source](https://github.com/nefarius/HidHide/tree/v1.5.230.0), [release](https://github.com/nefarius/HidHide/releases/tag/v1.5.230.0) |

Their upstream MIT license notices are included in `licenses/vJoy-MIT.txt` and `licenses/HidHide-MIT.txt`. The original installers also retain their own bundled notices and setup terms. Installer publishers and signatures are unchanged. The bridge does not claim authorship of these drivers, rename their publisher identities, or build replacement kernel drivers.

Exact download URLs and SHA-256 hashes are recorded in `dependencies.lock.json`. The runtime DLL is extracted from the pinned official SDK ZIP; the package's `SHA256SUMS.txt` also records its hash.

Microsoft Windows, .NET Framework and DirectInput are platform dependencies and are not redistributed here. Forza EmuWheel and other mapper applications are not included or used.
