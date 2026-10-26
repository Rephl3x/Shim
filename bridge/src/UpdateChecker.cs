using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace RigBridge
{
    public sealed class UpdateInfo
    {
        public string Name { get; set; }
        public string Current { get; set; }
        public string CurrentVersion { get; set; }
        public string Latest { get; set; }
        public string Status { get; set; }
        public string Details { get; set; }
        public string ReleaseUrl { get; set; }
    }

    public static class UpdateChecker
    {
        private sealed class Source
        {
            public string Name, Repository, FallbackUrl, Current, ComparisonVersion;
        }

        private sealed class Release
        {
            public string Tag, Url, Error;
        }

        public static Task<List<UpdateInfo>> CheckAsync()
        { return Task.Run(() => Check()); }

        public static List<UpdateInfo> Check()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var sources = LocalSources();
            var requests = sources.Select(source => Task.Run(() => Fetch(source))).ToArray();
            Task.WaitAll(requests);
            return sources.Select((source, index) => BuildInfo(source, requests[index].Result)).ToList();
        }

        public static string CompareStatus(string current, string latest)
        {
            if (String.IsNullOrWhiteSpace(current)) return "Not detected";
            if (String.IsNullOrWhiteSpace(latest)) return "No published release";
            Version installed, available;
            if (TryVersion(current, out installed) && TryVersion(latest, out available))
            {
                int comparison = installed.CompareTo(available);
                return comparison < 0 ? "Update available" : comparison == 0 ? "Up to date" : "Installed version is newer";
            }
            return String.Equals(current.Trim(), latest.Trim().TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase)
                ? "Up to date" : "Version format differs";
        }

        private static List<Source> LocalSources()
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            string shimVersion = version == null ? null : new Version(version.Major, version.Minor, Math.Max(0, version.Build)).ToString();
            string vjoyPath = Path.Combine(Environment.SystemDirectory, "drivers", "vjoy.sys");
            string hidhidePath = FindDriverPath("HidHide", "HidHide.sys");
            string vjoyVersion = FileVersion(vjoyPath);
            string vjoySdk = FileVersion(Vjoy.DefaultDll);
            string vjoyCurrent = vjoyVersion == null
                ? (vjoySdk == null ? null : "Driver not detected (bundled SDK " + vjoySdk + ")")
                : vjoyVersion + (vjoySdk == null ? " driver" : " driver (SDK " + vjoySdk + ")");
            return new List<Source>
            {
                new Source { Name = "Shim", Repository = "Rephl3x/Shim", FallbackUrl = "https://github.com/Rephl3x/Shim/releases", Current = shimVersion, ComparisonVersion = shimVersion },
                new Source { Name = "vJoy", Repository = "BrunnerInnovation/vJoy", FallbackUrl = "https://github.com/BrunnerInnovation/vJoy/releases", Current = vjoyCurrent, ComparisonVersion = vjoyVersion },
                new Source { Name = "HidHide", Repository = "nefarius/HidHide", FallbackUrl = "https://github.com/nefarius/HidHide/releases", Current = FileVersion(hidhidePath), ComparisonVersion = FileVersion(hidhidePath) }
            };
        }

        private static UpdateInfo BuildInfo(Source source, Release release)
        {
            var result = new UpdateInfo { Name = source.Name, Current = source.Current ?? "Not detected", CurrentVersion = source.ComparisonVersion,
                Latest = release.Tag ?? "Unavailable", ReleaseUrl = release.Url ?? source.FallbackUrl };
            if (!String.IsNullOrEmpty(release.Error))
            {
                result.Status = "Check failed"; result.Details = release.Error;
            }
            else if (String.IsNullOrWhiteSpace(release.Tag))
            {
                result.Status = "No published release found"; result.Details = "GitHub did not return a latest published release for this repository.";
            }
            else
            {
                result.Status = CompareStatus(source.ComparisonVersion, release.Tag);
                result.Details = result.Status == "Update available" ? "A newer published version is available on GitHub." :
                    result.Status == "Installed version is newer" ? "The local version is newer than the latest release tag." :
                    result.Status == "Not detected" ? "No installed driver version was found. The release page contains download details." :
                    result.Status == "Version format differs" ? "Could not compare these version strings safely. Review the release page." : "The installed version matches the latest published release.";
            }
            return result;
        }

        private static Release Fetch(Source source)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + source.Repository + "/releases/latest");
                request.Method = "GET"; request.Accept = "application/vnd.github+json"; request.UserAgent = "Shim-Update-Checker";
                request.Timeout = 12000; request.ReadWriteTimeout = 12000;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                    string tag = Value(json, "tag_name"), url = Value(json, "html_url");
                    if (String.IsNullOrWhiteSpace(tag)) return new Release { Error = "GitHub returned a release without a version tag." };
                    if (!IsGitHubReleaseUrl(url)) url = source.FallbackUrl;
                    return new Release { Tag = tag, Url = url };
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                string reason = response == null ? ex.Status.ToString() : ((int)response.StatusCode) + " " + response.StatusDescription;
                if (response != null) response.Dispose();
                return new Release { Error = "GitHub request failed (" + reason + "). Check your internet connection and try again." };
            }
            catch (Exception ex)
            { return new Release { Error = "Could not read the GitHub release: " + ex.Message }; }
        }

        private static string Value(IDictionary<string, object> json, string key)
        { object value; return json != null && json.TryGetValue(key, out value) ? Convert.ToString(value) : null; }

        private static bool IsGitHubReleaseUrl(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps &&
                String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Contains("/releases/");
        }

        private static bool TryVersion(string value, out Version version)
        {
            version = null;
            if (String.IsNullOrWhiteSpace(value)) return false;
            string normalized = Regex.Replace(value.Trim(), "^[vV]", "");
            Version parsed;
            if (!Regex.IsMatch(normalized, @"^\d+(?:\.\d+){1,3}$") || !Version.TryParse(normalized, out parsed)) return false;
            version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
            return true;
        }

        private static string FileVersion(string path)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                string value = FileVersionInfo.GetVersionInfo(path).FileVersion;
                return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
        }

        private static string FindDriverPath(string service, string fallbackName)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service))
                {
                    string image = key == null ? null : Convert.ToString(key.GetValue("ImagePath"));
                    if (!String.IsNullOrWhiteSpace(image))
                    {
                        image = Environment.ExpandEnvironmentVariables(image.Trim().Trim('"'));
                        if (image.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                            image = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), image.Substring(12));
                        else if (image.StartsWith(@"\??\", StringComparison.Ordinal)) image = image.Substring(4);
                        if (File.Exists(image)) return image;
                    }
                }
            }
            catch (System.Security.SecurityException) { }
            return Path.Combine(Environment.SystemDirectory, "drivers", fallbackName);
        }
    }
}
