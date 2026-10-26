using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RigBridge
{
    public sealed class DownloadedUpdate
    {
        public string PrimaryPath { get; set; }
        public string CompanionPath { get; set; }
    }

    public static class UpdateInstaller
    {
        private const long MaxShimBytes = 512L * 1024 * 1024;
        private const long MaxDriverBytes = 200L * 1024 * 1024;

        public static Task<DownloadedUpdate> DownloadAsync(UpdateInfo update, Action<long, long> progress)
        { return Task.Run(() => Download(update, progress)); }

        public static string PrepareShimUpdater(string packagePath, string version)
        {
            string directory = Path.Combine(Store.DirectoryPath, "updates"); Directory.CreateDirectory(directory);
            string helper = Path.Combine(directory, "Shim.Updater-" + SafeVersion(version) + ".exe");
            string temp = helper + ".part";
            try
            {
                using (var archive = ZipFile.OpenRead(packagePath))
                {
                    var entry = archive.GetEntry("Shim.Updater.exe");
                    if (entry == null || entry.Length < 1024 || entry.Length > 20 * 1024 * 1024) throw new InvalidDataException("The update package is missing its updater helper.");
                    using (var input = entry.Open()) using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                }
                if (File.Exists(helper)) File.Delete(helper);
                File.Move(temp, helper); return helper;
            }
            catch { if (File.Exists(temp)) File.Delete(temp); throw; }
        }

        public static void StartShimUpdate(string helperPath, string packagePath, string installDirectory)
        {
            var start = new ProcessStartInfo { FileName = helperPath,
                Arguments = JoinArguments("--apply-shim", packagePath, installDirectory, Process.GetCurrentProcess().Id.ToString()),
                WorkingDirectory = Path.GetDirectoryName(helperPath), UseShellExecute = false, CreateNoWindow = true };
            Process.Start(start);
        }

        public static void StartDriverUpdate(string helperPath, DownloadedUpdate download, UpdateInfo update, string installDirectory)
        {
            string sdk = update.Name == "vJoy" ? download.CompanionPath : "-";
            var start = new ProcessStartInfo { FileName = helperPath,
                Arguments = JoinArguments("--install-driver", download.PrimaryPath, update.Name, installDirectory,
                    Process.GetCurrentProcess().Id.ToString(), update.Latest, sdk),
                WorkingDirectory = Path.GetDirectoryName(helperPath), UseShellExecute = false, CreateNoWindow = true };
            Process.Start(start);
        }

        private static DownloadedUpdate Download(UpdateInfo update, Action<long, long> progress)
        {
            if (update == null || String.IsNullOrWhiteSpace(update.DownloadUrl) || String.IsNullOrWhiteSpace(update.AssetName))
                throw new InvalidOperationException(String.IsNullOrWhiteSpace(update == null ? null : update.DownloadProblem) ? "No compatible update package is available." : update.DownloadProblem);
            long limit = update.Name == "Shim" ? MaxShimBytes : MaxDriverBytes;
            string root = Path.Combine(Store.DirectoryPath, "updates"); Directory.CreateDirectory(root);
            string primary = SafePath(root, update.AssetName);
            DownloadFile(update.DownloadUrl, primary, update.AssetSize, limit, update.Sha256, progress);
            string companion = null;
            if (update.Name == "vJoy")
            {
                if (String.IsNullOrWhiteSpace(update.CompanionDownloadUrl) || String.IsNullOrWhiteSpace(update.CompanionAssetName))
                    throw new InvalidOperationException("The vJoy release did not provide its matching SDK archive.");
                companion = SafePath(root, update.CompanionAssetName);
                DownloadFile(update.CompanionDownloadUrl, companion, update.CompanionAssetSize, MaxDriverBytes, null, progress);
            }
            if (update.Name == "Shim") VerifySha256(primary, update.Sha256);
            else
            {
                VerifyInstaller(primary, update.Name);
                string fileVersion = FileVersionInfo.GetVersionInfo(primary).FileVersion;
                if (!SameVersion(fileVersion, update.Latest)) throw new InvalidDataException("The signed installer version does not match the GitHub release tag.");
                if (update.Name == "vJoy") VerifyVjoySdk(companion, update.Latest);
            }
            return new DownloadedUpdate { PrimaryPath = primary, CompanionPath = companion };
        }

        private static void DownloadFile(string url, string destination, long expectedSize, long limit, string digest, Action<long, long> progress)
        {
            if (!IsGitHubDownloadUrl(url)) throw new InvalidDataException("The release contains a download URL outside GitHub.");
            if (expectedSize < 1 || expectedSize > limit) throw new InvalidDataException("The release asset size is missing or exceeds the update limit.");
            string partial = destination + ".part";
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET"; request.Accept = "application/octet-stream"; request.UserAgent = "Shim-Updater";
                request.Timeout = 30000; request.ReadWriteTimeout = 30000; request.AllowAutoRedirect = true;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.ResponseUri.Scheme != Uri.UriSchemeHttps ||
                        !(String.Equals(response.ResponseUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) || response.ResponseUri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("GitHub redirected the download to an unexpected host.");
                    if (response.ContentLength > 0 && response.ContentLength != expectedSize) throw new InvalidDataException("The release asset size did not match GitHub's metadata.");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    using (var input = response.GetResponseStream()) using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[65536]; long received = 0; int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            received += read; if (received > limit || received > expectedSize) throw new InvalidDataException("The downloaded file exceeded its expected size.");
                            output.Write(buffer, 0, read); if (progress != null) progress(received, expectedSize);
                        }
                        output.Flush();
                        if (received != expectedSize) throw new InvalidDataException("The download ended before the complete release asset arrived.");
                    }
                }
                if (IsSha256(digest)) VerifySha256(partial, digest);
                else if (!String.IsNullOrWhiteSpace(digest)) throw new InvalidDataException("GitHub returned an unsupported asset digest format.");
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(partial, destination);
            }
            catch { if (File.Exists(partial)) File.Delete(partial); throw; }
        }

        private static void VerifyInstaller(string path, string component)
        {
            string publisher = component == "vJoy" ? "Brunner Elektronik AG" : component == "HidHide" ? "Nefarius Software Solutions e.U." : null;
            if (publisher == null) throw new InvalidOperationException("Unsupported driver update.");
            string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell)) throw new InvalidOperationException("Windows PowerShell is required to verify the driver signature.");
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFullPath(path)));
            string script = "$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encoded + "'));$s=Get-AuthenticodeSignature -LiteralPath $p;if($s.Status -ne 'Valid'){[Console]::Error.WriteLine('Signature status: '+$s.Status);exit 21};if($s.SignerCertificate.Subject.IndexOf('" + publisher + "',[StringComparison]::OrdinalIgnoreCase)-lt 0){[Console]::Error.WriteLine('Unexpected publisher: '+$s.SignerCertificate.Subject);exit 22};exit 0";
            using (var process = Process.Start(new ProcessStartInfo { FileName = powershell, Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + Quote(script),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true }))
            {
                if (!process.WaitForExit(30000)) { try { process.Kill(); } catch { } throw new TimeoutException("The downloaded driver's signature check timed out."); }
                string error = process.StandardError.ReadToEnd();
                if (process.ExitCode != 0) throw new InvalidDataException("Shim blocked the driver update because its signature or publisher did not verify. " + error.Trim());
            }
        }

        private static void VerifyVjoySdk(string archivePath, string version)
        {
            string output = Path.Combine(Path.GetDirectoryName(archivePath), "vJoyInterface-verify-" + Guid.NewGuid().ToString("N") + ".dll");
            try
            {
                using (var archive = ZipFile.OpenRead(archivePath))
                {
                    var entry = archive.GetEntry("SDK/lib/x64/vJoyInterface.dll");
                    if (entry == null || entry.Length < 1024 || entry.Length > 20 * 1024 * 1024) throw new InvalidDataException("The vJoy SDK archive does not contain the expected x64 library.");
                    using (var input = entry.Open()) using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(file);
                }
                if (!SameVersion(FileVersionInfo.GetVersionInfo(output).FileVersion, version)) throw new InvalidDataException("The vJoy SDK library does not match the release tag.");
                VerifyInstaller(output, "vJoy");
            }
            finally { if (File.Exists(output)) File.Delete(output); }
        }

        private static void VerifySha256(string path, string expected)
        {
            if (!IsSha256(expected)) throw new InvalidDataException("GitHub did not provide a valid SHA-256 digest for this update.");
            using (var sha = SHA256.Create()) using (var file = File.OpenRead(path))
            {
                string actual = "sha256:" + BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
                if (!String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The downloaded package did not match GitHub's SHA-256 digest.");
            }
        }

        private static bool SameVersion(string actual, string expected)
        {
            Version left, right;
            string a = (actual ?? "").Trim().TrimStart('v', 'V').Split('-')[0], b = (expected ?? "").Trim().TrimStart('v', 'V').Split('-')[0];
            if (!Version.TryParse(a, out left) || !Version.TryParse(b, out right)) return String.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            return new Version(left.Major, left.Minor, Math.Max(0, left.Build), Math.Max(0, left.Revision)) ==
                new Version(right.Major, right.Minor, Math.Max(0, right.Build), Math.Max(0, right.Revision));
        }

        private static bool IsSha256(string value)
        { return !String.IsNullOrWhiteSpace(value) && System.Text.RegularExpressions.Regex.IsMatch(value, @"^sha256:[a-fA-F0-9]{64}$"); }

        private static bool IsGitHubDownloadUrl(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps &&
                String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Contains("/releases/download/");
        }

        private static string SafePath(string root, string name)
        {
            if (String.IsNullOrWhiteSpace(name) || !String.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("GitHub returned an unsafe update asset name.");
            return Path.Combine(root, name);
        }

        private static string SafeVersion(string version)
        { return new string((version ?? "").Where(c => Char.IsLetterOrDigit(c) || c == '.' || c == '-').ToArray()); }

        private static string JoinArguments(params string[] values) { return String.Join(" ", values.Select(Quote)); }
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
    }
}
