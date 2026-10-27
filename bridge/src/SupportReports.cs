using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Diagnostics;
using System.Management;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RigBridge
{
    internal static class SupportReports
    {
        // HTTPS endpoint hosted by the Shim support Worker.
        internal const string UploadEndpoint = "https://dev.amslabs.net/v1/reports";
        private const int MaximumBundleBytes = 8 * 1024 * 1024;

        internal static string CreateBundle(BridgeProfile profile, BridgeRuntime runtime, long lastPackets, long lastStarts)
        {
            string directory = Path.Combine(Store.DirectoryPath, "support-reports");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "Shim-report-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                AddText(archive, "system.json", new JavaScriptSerializer().Serialize(CollectInventory(profile, runtime, lastPackets, lastStarts)));

                string[] paths = Directory.GetFiles(Store.DirectoryPath, "session-*.log")
                    .OrderByDescending(File.GetLastWriteTimeUtc).Take(3).OrderBy(File.GetLastWriteTimeUtc).ToArray();
                AddOptional(archive, Path.Combine(Store.DirectoryPath, "startup-error.log"), "startup-error.log");
                AddOptional(archive, Path.Combine(Store.DirectoryPath, "watchdog.log"), "watchdog.log");
                foreach (string log in paths)
                {
                    AddOptional(archive, log, Path.GetFileName(log));
                    string structured = log + ".jsonl";
                    AddOptional(archive, structured, Path.GetFileName(structured));
                }
            }
            if (new FileInfo(path).Length > MaximumBundleBytes)
            {
                File.Delete(path);
                throw new InvalidDataException("The support report is too large. Remove older session logs and try again.");
            }
            return path;
        }

        internal static Task<string> UploadAsync(string path)
        {
            return Task.Run(() => Upload(path));
        }

        internal static string CreateEmailDraft(string reportPath, string email)
        {
            if (String.IsNullOrWhiteSpace(email) || email.Length > 254 || !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^\s@<>]+@[^\s@<>]+\.[^\s@<>]+$"))
                throw new ArgumentException("Enter a valid email address for the draft.");
            byte[] bytes = File.ReadAllBytes(reportPath);
            if (bytes.Length > MaximumBundleBytes) throw new InvalidDataException("The report exceeds the 8 MB email attachment limit.");
            string boundary = "ShimReport_" + Guid.NewGuid().ToString("N");
            string fileName = Path.GetFileName(reportPath);
            string encodedName = Convert.ToBase64String(Encoding.UTF8.GetBytes(fileName));
            var eml = new StringBuilder();
            eml.Append("To: ").Append(email).Append("\r\nSubject: Shim support report (not sent)\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"").Append(boundary).Append("\"\r\n\r\n");
            eml.Append("--").Append(boundary).Append("\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\nThis email draft contains your Shim support report. Review the attachment and recipient, then send it yourself if you choose. Shim has not sent this email.\r\n\r\n");
            eml.Append("--").Append(boundary).Append("\r\nContent-Type: application/zip; name=\"=?UTF-8?B?").Append(encodedName).Append("?=\"\r\nContent-Transfer-Encoding: base64\r\nContent-Disposition: attachment; filename=\"=?UTF-8?B?").Append(encodedName).Append("?=\"\r\n\r\n");
            string base64 = Convert.ToBase64String(bytes);
            for (int i = 0; i < base64.Length; i += 76) eml.Append(base64.Substring(i, Math.Min(76, base64.Length - i))).Append("\r\n");
            eml.Append("--").Append(boundary).Append("--\r\n");
            string draft = Path.Combine(Path.GetDirectoryName(reportPath), Path.GetFileNameWithoutExtension(reportPath) + ".eml");
            File.WriteAllText(draft, eml.ToString(), new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo { FileName = draft, UseShellExecute = true });
            return draft;
        }

        private static object CollectInventory(BridgeProfile profile, BridgeRuntime runtime, long lastPackets, long lastStarts)
        {
            var data = new System.Collections.Generic.Dictionary<string, object>();
            data["shimVersion"] = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            data["windowsVersion"] = Environment.OSVersion.VersionString;
            data["architecture"] = Environment.Is64BitOperatingSystem ? "x64" : "x86";
            data["createdUtc"] = DateTime.UtcNow.ToString("o");
            data["cpu"] = QueryWmi("SELECT Name,NumberOfCores,NumberOfLogicalProcessors FROM Win32_Processor", new[] { "Name", "NumberOfCores", "NumberOfLogicalProcessors" });
            data["memory"] = QueryWmi("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", new[] { "TotalPhysicalMemory" });
            data["gpu"] = QueryWmi("SELECT Name,DriverVersion,VideoProcessor FROM Win32_VideoController", new[] { "Name", "DriverVersion", "VideoProcessor" });
            data["displayAndControllerDrivers"] = QueryWmi("SELECT DeviceName,DriverVersion,DriverProviderName,InfName FROM Win32_PnPSignedDriver", new[] { "DeviceName", "DriverVersion", "DriverProviderName", "InfName" }, true);
            try
            {
                using (var direct = new DirectInput())
                {
                    var devices = direct.Enumerate().Where(d => !d.Virtual).ToList();
                    data["controllers"] = devices.Select(d => new {
                        name = d.Name, forceFeedback = d.ForceFeedback,
                        axisCount = d.Controls.Count(c => c.Kind == "Axis"),
                        buttonCount = d.Controls.Count(c => c.Kind == "Button"),
                        hatCount = d.Controls.Count(c => c.Kind == "POV")
                    }).ToArray();
                    var wheel = profile == null ? null : devices.FirstOrDefault(d => d.Id == profile.FfbDevice);
                    data["shimConfiguration"] = new {
                        forceFeedbackForwarding = profile != null && profile.EnableFfb,
                        forceFeedbackStrengthPercent = profile == null ? 0 : profile.FfbGain,
                        invertSteeringForces = profile != null && profile.InvertFfb,
                        configuredPhysicalDeviceCount = profile == null ? 0 : profile.Devices.Count,
                        selectedForceFeedbackWheel = wheel == null ? null : wheel.Name,
                        bridgeRunning = runtime != null && runtime.Started && !runtime.Stopping,
                        inputMonitorOnly = runtime != null && runtime.IsMonitorOnly,
                        recentFfbPackets = runtime != null ? runtime.FfbPackets : lastPackets,
                        recentWheelStarts = runtime != null ? runtime.WheelStarts : lastStarts
                    };
                }
            }
            catch (Exception ex) { data["controllerInventoryError"] = ex.GetType().Name; }
            return data;
        }

        private static object QueryWmi(string query, string[] fields, bool driverOnly = false)
        {
            var rows = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(query))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    using (item)
                    {
                        string label = Convert.ToString(item[fields[0]]);
                        if (driverOnly && (String.IsNullOrWhiteSpace(label) || !(label.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("MOZA", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("vJoy", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("HidHide", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("controller", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("gamepad", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("shifter", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("pedal", StringComparison.OrdinalIgnoreCase) >= 0))) continue;
                        var row = new System.Collections.Generic.Dictionary<string, object>();
                        foreach (string field in fields)
                        {
                            try
                            {
                                object value = item[field];
                                if (value != null) row[field] = Convert.ToString(value);
                            }
                            catch (ManagementException) { /* A device provider may omit an optional field. */ }
                        }
                        rows.Add(row);
                        if (rows.Count >= (driverOnly ? 100 : 8)) break;
                    }
                }
            }
            catch (ManagementException ex) { return new { unavailable = "Windows Management Instrumentation", errorCode = ex.ErrorCode.ToString() }; }
            catch (Exception ex) { return new { unavailable = ex.GetType().Name }; }
            return rows;
        }

        private static string Upload(string path)
        {
            var request = (HttpWebRequest)WebRequest.Create(UploadEndpoint);
            request.Method = "POST"; request.ContentType = "application/zip";
            request.Timeout = 30000; request.ReadWriteTimeout = 30000;
            request.UserAgent = "Shim-Support/" + Assembly.GetExecutingAssembly().GetName().Version;
            using (var file = File.OpenRead(path))
            {
                if (file.Length > MaximumBundleBytes) throw new InvalidDataException("The report exceeds the 8 MB upload limit.");
                request.ContentLength = file.Length;
                using (var stream = request.GetRequestStream()) file.CopyTo(stream);
            }
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                    throw new WebException("The report server returned HTTP " + (int)response.StatusCode + ".");
                string body = reader.ReadToEnd();
                var data = new JavaScriptSerializer().Deserialize<UploadResponse>(body);
                if (data == null || String.IsNullOrWhiteSpace(data.ReportId))
                    throw new InvalidDataException("The report server returned an invalid response.");
                return data.ReportId;
            }
        }

        private static void AddOptional(ZipArchive archive, string path, string name)
        {
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            if (info.Length > 1024 * 1024) return;
            using (var input = File.OpenRead(path))
            using (var reader = new StreamReader(input, Encoding.UTF8, true))
            {
                string text = reader.ReadToEnd();
                string user = Environment.UserName;
                if (!String.IsNullOrEmpty(user)) text = text.Replace(user, "<user>");
                text = System.Text.RegularExpressions.Regex.Replace(text, @"(?i)([A-Z]:\\Users\\)[^\\\r\n]+", "$1<user>");
                AddText(archive, name, text);
            }
        }

        private static void AddText(ZipArchive archive, string name, string text)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(text);
        }

        private sealed class UploadResponse { public string ReportId { get; set; } }
    }
}
