using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace FHMozaFix
{
    public static class Program
    {
        public static string DataDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FHMozaFix"); }
        }
        public static bool IsAdministrator
        {
            get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (args.Length == 2 && args[0] == "--diagnose")
                {
                    var nodes = NativeDevices.Snapshot(true);
                    Json.WriteAtomic(args[1], new {
                        CapturedUtc = DateTime.UtcNow, Controllers = DeviceSafety.Discover(nodes),
                        Devices = nodes
                    });
                    return 0;
                }
                string preview = args.Length == 2 && args[0] == "--preview" ? args[1] : null;
                if (args.Length != 0 && preview == null)
                    throw new ArgumentException("Usage: FH-Moza-Fix.exe [--diagnose output.json | --preview output.png]");
                using (var mutex = new Mutex(false, "Local\\FHMozaFix-" + WindowsIdentity.GetCurrent().User.Value))
                {
                    bool acquired = false;
                    try
                    {
                        try { acquired = mutex.WaitOne(2000); }
                        catch (AbandonedMutexException) { acquired = true; }
                        if (!acquired) { MessageBox.Show("FH MOZA Fix is already open in this Windows session.", "FH MOZA Fix"); return 1; }
                        Application.Run(new MainForm(preview));
                        return 0;
                    }
                    finally { if (acquired) mutex.ReleaseMutex(); }
                }
            }
            catch (Exception ex)
            {
                if (args.Length > 0) // Noninteractive diagnostics must never prompt for device actions.
                {
                    string errorPath = Path.Combine(DataDirectory, "startup-error.txt");
                    try { Directory.CreateDirectory(DataDirectory); File.WriteAllText(errorPath, ex.ToString()); }
                    catch (IOException) { return 2; }
                    return 1;
                }
                MessageBox.Show(ex.Message, "FH MOZA Fix", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        public static bool ForzaIsRunning()
        {
            foreach (var process in Process.GetProcesses())
                using (process)
                    if (process.ProcessName.IndexOf("ForzaHorizon6", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        process.ProcessName.IndexOf("Forza_Horizon_6", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static void Elevate()
        {
            Process.Start(new ProcessStartInfo {
                FileName = Application.ExecutablePath, UseShellExecute = true, Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath)
            });
        }
    }
}
