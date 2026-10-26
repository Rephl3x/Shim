using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RigBridge.Update
{
    internal static class Updater
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 4 && args[0] == "--apply-shim")
                {
                    WaitFor(args[3]);
                    if (!Writable(args[2])) { RelaunchElevated(args); return 0; }
                    ApplyPackage(args[1], args[2]);
                    LaunchShim(args[2]);
                    return 0;
                }
                if (args.Length == 7 && args[0] == "--install-driver")
                {
                    WaitFor(args[4]);
                    if (args[2] == "vJoy" && args[6] != "-" && !Writable(args[3])) { RelaunchElevated(args); return 0; }
                    InstallDriver(args[1], args[2], args[3], args[5], args[6]);
                    LaunchShim(args[3]);
                    return 0;
                }
                throw new ArgumentException("Unsupported update operation.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Shim could not finish the update. No reboot was started.\n\n" + ex.Message,
                    "Shim updater", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static void WaitFor(string value)
        {
            int id;
            if (!Int32.TryParse(value, out id) || id < 1) throw new ArgumentException("The Shim process ID is invalid.");
            try
            {
                using (Process process = Process.GetProcessById(id))
                    if (!process.WaitForExit(120000)) throw new TimeoutException("Shim is still running. Close it and retry the update.");
            }
            catch (ArgumentException) { }
        }

        private static void ApplyPackage(string zipPath, string targetDirectory)
        {
            zipPath = Path.GetFullPath(zipPath); targetDirectory = Path.GetFullPath(targetDirectory);
            if (!File.Exists(zipPath) || !Directory.Exists(targetDirectory)) throw new FileNotFoundException("The downloaded Shim package or install folder is missing.");
            string work = Path.Combine(Path.GetTempPath(), "ShimUpdate-" + Guid.NewGuid().ToString("N"));
            string stage = Path.Combine(work, "stage"), backup = Path.Combine(work, "backup");
            Directory.CreateDirectory(stage); Directory.CreateDirectory(backup);
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0; int count = 0;
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (String.IsNullOrEmpty(entry.Name)) continue;
                        string relative = SafeRelative(entry.FullName);
                        if (!Allowed(relative)) throw new InvalidDataException("The package contains an unexpected path: " + relative);
                        if (!seen.Add(relative)) throw new InvalidDataException("The package contains a duplicate path: " + relative);
                        total += entry.Length; if (total > 512L * 1024 * 1024 || ++count > 2000) throw new InvalidDataException("The Shim package is larger than expected.");
                        string destination = Path.Combine(stage, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        using (var input = entry.Open()) using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                    }
                }
                if (!File.Exists(Path.Combine(stage, "Shim.exe"))) throw new InvalidDataException("The downloaded package does not contain Shim.exe.");
                string rollbackRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FHRigBridge", "updates", "rollback", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
                var applied = new List<AppliedFile>();
                try
                {
                    foreach (string staged in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
                    {
                        string relative = staged.Substring(stage.Length + 1);
                        string target = Path.GetFullPath(Path.Combine(targetDirectory, relative));
                        EnsureInside(targetDirectory, target);
                        string saved = Path.Combine(rollbackRoot, relative);
                        bool existed = File.Exists(target);
                        if (existed) { Directory.CreateDirectory(Path.GetDirectoryName(saved)); File.Copy(target, saved, true); }
                        applied.Add(new AppliedFile { Target = target, Backup = saved, Existed = existed });
                        Replace(staged, target);
                    }
                }
                catch
                {
                    for (int i = applied.Count - 1; i >= 0; i--)
                    {
                        try
                        {
                            var file = applied[i];
                            if (file.Existed && File.Exists(file.Backup)) Replace(file.Backup, file.Target);
                            else if (!file.Existed && File.Exists(file.Target)) File.Delete(file.Target);
                        }
                        catch { }
                    }
                    throw;
                }
            }
            finally { try { Directory.Delete(work, true); } catch { } }
        }

        private static void InstallDriver(string installer, string component, string targetDirectory, string version, string sdkArchive)
        {
            if (component != "vJoy" && component != "HidHide") throw new InvalidOperationException("Unknown driver update.");
            VerifySignature(installer, component == "vJoy" ? "Brunner Elektronik AG" : "Nefarius Software Solutions e.U.");
            var info = FileVersionInfo.GetVersionInfo(installer);
            if (!VersionMatches(info.FileVersion, version) && !VersionMatches(info.ProductVersion, version))
                throw new InvalidDataException("The signed installer version does not match the GitHub release tag.");
            using (Process process = Process.Start(new ProcessStartInfo { FileName = installer, UseShellExecute = true, Verb = IsAdministrator() ? "" : "runas" }))
            {
                if (process == null || !process.WaitForExit(900000)) throw new TimeoutException("The driver installer did not finish within 15 minutes. Finish its setup window, then reopen Shim.");
                if (process.ExitCode != 0 && process.ExitCode != 3010 && process.ExitCode != 1641)
                    throw new InvalidOperationException("The driver installer returned exit code " + process.ExitCode + ". The bundled device library was not changed.");
            }
            if (component == "vJoy" && sdkArchive != "-") InstallVjoySdk(sdkArchive, targetDirectory, version);
        }

        private static void InstallVjoySdk(string archivePath, string targetDirectory, string version)
        {
            string sdkPath = Path.Combine(Path.GetTempPath(), "Shim-vJoy-SDK-" + Guid.NewGuid().ToString("N") + ".dll");
            try
            {
                using (var archive = ZipFile.OpenRead(archivePath))
                {
                    var entry = archive.GetEntry("SDK/lib/x64/vJoyInterface.dll");
                    if (entry == null || entry.Length < 1024 || entry.Length > 20 * 1024 * 1024) throw new InvalidDataException("The SDK archive does not contain the expected x64 vJoy library.");
                    using (var input = entry.Open()) using (var output = new FileStream(sdkPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                }
                string fileVersion = FileVersionInfo.GetVersionInfo(sdkPath).FileVersion;
                if (!VersionMatches(fileVersion, version)) throw new InvalidDataException("The vJoy SDK library version does not match the release tag.");
                string destination = Path.Combine(targetDirectory, "runtime", "x64", "vJoyInterface.dll");
                Replace(sdkPath, destination);
            }
            finally { if (File.Exists(sdkPath)) File.Delete(sdkPath); }
        }

        private static void VerifySignature(string path, string publisher)
        {
            string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell)) throw new InvalidOperationException("Windows PowerShell is required to verify the driver signature.");
            string encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFullPath(path)));
            string script = "$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encodedPath + "'));$s=Get-AuthenticodeSignature -LiteralPath $p;if($s.Status -ne 'Valid'){[Console]::Error.WriteLine('Signature status: '+$s.Status);exit 21};if($s.SignerCertificate.Subject.IndexOf('" + publisher + "',[StringComparison]::OrdinalIgnoreCase)-lt 0){[Console]::Error.WriteLine('Unexpected publisher: '+$s.SignerCertificate.Subject);exit 22};exit 0";
            using (var process = Process.Start(new ProcessStartInfo { FileName = powershell, Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + Quote(script), UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true }))
            {
                if (!process.WaitForExit(30000)) { try { process.Kill(); } catch { } throw new TimeoutException("The downloaded driver's signature check timed out."); }
                string error = process.StandardError.ReadToEnd();
                if (process.ExitCode != 0) throw new InvalidDataException("Shim blocked this driver update because its signature or publisher did not verify. " + error.Trim());
            }
        }

        private static bool IsAdministrator()
        { using (var identity = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }

        private static bool Writable(string directory)
        {
            string probe = Path.Combine(directory, ".shim-write-test-" + Guid.NewGuid().ToString("N"));
            try { File.WriteAllBytes(probe, new byte[] { 1 }); File.Delete(probe); return true; }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
        }

        private static void RelaunchElevated(string[] args)
        {
            string[] elevatedArgs = (string[])args.Clone();
            int parentArgument = args[0] == "--apply-shim" ? 3 : 4;
            elevatedArgs[parentArgument] = Process.GetCurrentProcess().Id.ToString();
            try
            {
                Process.Start(new ProcessStartInfo { FileName = Application.ExecutablePath, Arguments = JoinArguments(elevatedArgs), UseShellExecute = true, Verb = "runas", WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory });
            }
            catch (System.ComponentModel.Win32Exception ex) { throw new InvalidOperationException("Writing to the Shim folder needs administrator access. The update was not applied. " + ex.Message); }
        }

        private static void LaunchShim(string directory)
        {
            string app = Path.Combine(directory, "Shim.exe");
            if (!File.Exists(app)) throw new FileNotFoundException("The updated Shim.exe is missing.", app);
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = Quote(app), UseShellExecute = true, WorkingDirectory = directory });
        }

        private static void Replace(string source, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temporary = destination + ".shim-update-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, temporary, false);
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static string SafeRelative(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) throw new InvalidDataException("The update package contains an unsafe path.");
            string normalized = path.Replace('/', '\\');
            foreach (string part in normalized.Split('\\'))
                if (String.IsNullOrWhiteSpace(part) || part == "." || part == ".." || part.IndexOf(':') >= 0) throw new InvalidDataException("The update package contains an unsafe path.");
            return normalized;
        }

        private static bool Allowed(string relative)
        {
            string first = relative.Split('\\')[0];
            if (new[] { "drivers", "runtime", "licenses", "assets", "bridge" }.Contains(first, StringComparer.OrdinalIgnoreCase)) return true;
            return new[] { "Shim.exe", "Shim.Updater.exe", "README.md", "THIRD-PARTY-NOTICES.md", "dependencies.lock.json", "SHA256SUMS.txt", "BuildBridge.ps1", "FetchBridgeDependencies.ps1", "PackageBridge.ps1" }.Contains(relative, StringComparer.OrdinalIgnoreCase);
        }

        private static void EnsureInside(string root, string path)
        { string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update package contains a path outside the Shim folder."); }

        private static bool VersionMatches(string actual, string expected)
        {
            Version left, right;
            if (Version.TryParse(UpdateVersion(actual), out left) && Version.TryParse(UpdateVersion(expected), out right))
                return new Version(left.Major, left.Minor, Math.Max(0, left.Build), Math.Max(0, left.Revision)) ==
                    new Version(right.Major, right.Minor, Math.Max(0, right.Build), Math.Max(0, right.Revision));
            return String.Equals(UpdateVersion(actual), UpdateVersion(expected), StringComparison.OrdinalIgnoreCase);
        }

        private static string UpdateVersion(string value)
        { return (value ?? "").Trim().TrimStart('v', 'V').Split('-')[0]; }

        private static string JoinArguments(IEnumerable<string> args) { return String.Join(" ", args.Select(Quote)); }
        private static string Quote(string value)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value ?? "")
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1).Append('"');
                else result.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            result.Append('\\', slashes * 2).Append('"'); return result.ToString();
        }

        private sealed class AppliedFile { public string Target, Backup; public bool Existed; }
    }
}
