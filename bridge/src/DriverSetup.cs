using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RigBridge
{
    public sealed class VirtualStatus
    {
        public uint Slot { get; set; }
        public bool Enabled { get; set; }
        public bool Match { get; set; }
        public int DllVersion { get; set; }
        public int DriverVersion { get; set; }
        public int State { get; set; }
        public bool[] Axes { get; set; }
        public int Buttons { get; set; }
        public int Hats { get; set; }
        public bool Ffb { get; set; }
        public int Effects { get; set; }
        public string Error { get; set; }
        public bool LayoutReady { get { return String.IsNullOrEmpty(Error) && Enabled && Match && State == 1 && Axes != null && Axes.Length == 8 && Axes.All(x => x) && Buttons >= 128 && Hats >= 4 && Ffb && Effects == 11; } }
        public string Summary
        {
            get
            {
                if (!String.IsNullOrEmpty(Error)) return "vJoy check: " + Error;
                if (!Enabled) return "vJoy driver is not enabled. Finish installing it and restart Windows if requested.";
                if (!Match) return "vJoy is installed, but the DLL and driver versions differ (DLL " + DllVersion + ", driver " + DriverVersion + "). Finish installation/restart, then recheck.";
                if (State == 2 || State == 0) return "vJoy " + Slot + " is in use. Close other feeders/monitors before configuring it.";
                if (State != 1) return "vJoy is installed; virtual device " + Slot + " is missing. Choose Configure virtual wheel.";
                string details = (Axes == null ? 0 : Axes.Count(x => x)) + "/8 axes, " + Buttons + "/128 buttons, " + Hats + "/4 continuous hats; FFB " + (Ffb ? Effects + "/11 effects" : "off") + ".";
                return "vJoy " + Slot + (LayoutReady ? " is configured and available. " : " needs configuration. ") + details;
            }
        }
    }
    public sealed class SetupStatus
    {
        public VirtualStatus Virtual { get; set; }
        public bool HideReady { get; set; }
        public string HideMessage { get; set; }
        public bool VjoyInstalling { get; set; }
        public bool HideInstalling { get; set; }
        public bool Ready { get { return Virtual != null && Virtual.LayoutReady && HideReady && !VjoyInstalling && !HideInstalling; } }
        public string Summary
        {
            get
            {
                return (Virtual == null ? "vJoy has not been checked." : Virtual.Summary) + Environment.NewLine + HideMessage +
                    (VjoyInstalling || HideInstalling ? Environment.NewLine + "An installer is still running. A detected driver does not mean setup has finished. Complete/close its window; if it remains stuck at the end, restart Windows, then reopen this setup. We will not start a second copy." : "");
            }
        }
    }
    public static class DriverSetup
    {
        private static Process configuration;
        private static DateTime configurationStarted;
        public static string ConfigPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy", "x64", "vJoyConfig.exe"); } }
        public static bool InstallerRunning(bool vjoy)
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        string name = process.ProcessName;
                        if (vjoy ? name.StartsWith("vJoySetup", StringComparison.OrdinalIgnoreCase) || name.Equals("vJoyInstall", StringComparison.OrdinalIgnoreCase) : name.StartsWith("HidHide_", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
            return false;
        }
        public static string ConfigurationMessage
        {
            get
            {
                if (configuration == null) return null;
                if (!configuration.HasExited) return (DateTime.UtcNow - configurationStarted).TotalSeconds > 60 ? "vJoy configuration is taking longer than expected. Complete any Windows prompt. Recheck after it finishes, or restart Windows if it stays stuck." : "Applying virtual wheel configuration. Complete the Windows administrator prompt, then recheck.";
                int code = configuration.ExitCode; configuration.Dispose(); configuration = null;
                return "Configuration tool finished (exit " + code + "). Checking the actual device layout; an exit code alone does not confirm success.";
            }
        }
        public static bool Configuring { get { return configuration != null && !configuration.HasExited; } }
        public static string ConfigureArguments(uint id)
        {
            if (id < 1 || id > 16) throw new ArgumentOutOfRangeException("id");
            return id + " -f -a x y z rx ry rz sl0 sl1 -b 128 -p 4 -e Const Ramp Sq Sine Tr StUp StDn Spr Dm Inr Fric";
        }
        public static void Configure(uint id, VirtualStatus status)
        {
            if (InstallerRunning(true) || InstallerRunning(false)) throw new InvalidOperationException("Finish the open driver installer first. If its final step stays stuck, restart Windows and reopen setup.");
            if (Configuring) throw new InvalidOperationException("The configuration tool is already running. Finish its Windows prompt and recheck.");
            if (status != null && (status.State == 0 || status.State == 2) && status.Enabled && status.Match) throw new InvalidOperationException("Close the app using this vJoy device before configuring it.");
            if (!File.Exists(ConfigPath)) throw new FileNotFoundException("The vJoy configuration tool is missing. Finish the bundled vJoy installation first.");
            configuration = Process.Start(new ProcessStartInfo { FileName = ConfigPath, Arguments = ConfigureArguments(id), UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            configurationStarted = DateTime.UtcNow;
        }
        public static void Install(bool vjoy)
        {
            if (InstallerRunning(true) || InstallerRunning(false) || Configuring) throw new InvalidOperationException("A driver installer or configuration tool is already running. Finish it before starting another.");
            DriverPackages.Verify(vjoy);
            Process.Start(new ProcessStartInfo { FileName = DriverPackages.PathFor(vjoy), UseShellExecute = true, Verb = "runas" });
        }
        public static Task<SetupStatus> CheckAsync(string dll, uint id)
        { return Task.Run(() => Check(dll, id)); }
        public static SetupStatus Check(string dll, uint id)
        {
            // Probe in a fresh, bounded helper: the native SDK caches device handles across driver installation.
            string report = Path.Combine(Store.DirectoryPath, "driver-check-" + Guid.NewGuid().ToString("N") + ".json");
            var result = new SetupStatus { VjoyInstalling = InstallerRunning(true), HideInstalling = InstallerRunning(false) };
            try
            {
                using (var process = Process.Start(new ProcessStartInfo { FileName = Application.ExecutablePath,
                    Arguments = "--driver-status " + Quote(report) + " " + id + " " + Quote(String.IsNullOrEmpty(dll) ? Vjoy.DefaultDll : dll), UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
                {
                    if (!process.WaitForExit(10000))
                    {
                        // Only our own read-only helper is terminated; never an installer or driver service.
                        try { process.Kill(); process.WaitForExit(1000); } catch (InvalidOperationException) { }
                        throw new TimeoutException("Driver check timed out. The installer may still be busy; complete it or restart Windows, then recheck.");
                    }
                    if (process.ExitCode != 0 || !File.Exists(report)) throw new IOException("Driver check did not complete. Finish installation/restart Windows, then recheck.");
                }
                var fresh = Store.Read<SetupStatus>(report);
                fresh.VjoyInstalling = result.VjoyInstalling; fresh.HideInstalling = result.HideInstalling;
                return fresh;
            }
            catch (Exception ex) { result.Virtual = new VirtualStatus { Slot = id, Error = ex.Message }; result.HideMessage = "HidHide has not been checked."; return result; }
            finally { if (File.Exists(report)) File.Delete(report); }
        }
        public static SetupStatus Inspect(string dll, uint id)
        {
            var status = new SetupStatus { Virtual = Vjoy.Inspect(dll, id) };
            try
            {
                using (var hide = new HideDriver())
                {
                    if (hide.GetFlag(2054)) status.HideMessage = "HidHide is installed but uses inverse mode. Turn inverse mode off in HidHide Configuration Client.";
                    else if (!hide.GetFlag(2052) && hide.GetList(2050).Count > 0) status.HideMessage = "HidHide has old inactive hiding rules. Review/clear them in HidHide Configuration Client before continuing.";
                    else { status.HideReady = true; status.HideMessage = "HidHide is installed and ready for temporary device hiding."; }
                }
            }
            catch (Exception ex) { status.HideMessage = "HidHide: " + ex.Message; }
            if (File.Exists(Store.RecoveryPath)) { status.HideReady = false; status.HideMessage += " Restore visibility in the main window before continuing setup."; }
            return status;
        }
        public static string Quote(string value)
        {
            // Windows command-line quoting, including backslashes immediately before quotes/end.
            var result = new System.Text.StringBuilder("\""); int slashes = 0;
            foreach (char item in value)
            {
                if (item == '\\') { slashes++; continue; }
                if (item == '"') result.Append('\\', slashes * 2 + 1).Append('"');
                else result.Append('\\', slashes).Append(item);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
}
