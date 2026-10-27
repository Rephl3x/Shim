using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace RigBridge
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Directory.CreateDirectory(Store.DirectoryPath);
            try
            {
                if (args.Length == 2 && args[0] == "--watchdog") { Watchdog(args[1]); return 0; }
                if (args.Length == 2 && args[0] == "--check-ffb-update") { ForceFeedbackCheck.Run(args[1]); return 0; }
                if (args.Length == 2 && args[0] == "--restore-visibility")
                {
                    object before;
                    using (var driver = new HideDriver())
                        before = new { Active = driver.GetFlag(2052), Inverse = driver.GetFlag(2054), Blacklist = driver.GetList(2050), Whitelist = driver.GetList(2048) };
                    // The control device permits one client handle at a time. Release the snapshot handle first.
                    Store.Write(args[1], new { Restored = false, Before = before });
                    HideSession.RecoverFile(Store.RecoveryPath);
                    using (var driver = new HideDriver())
                    {
                        Store.Write(args[1], new { Restored = true, Before = before,
                            After = new { Active = driver.GetFlag(2052), Inverse = driver.GetFlag(2054), Blacklist = driver.GetList(2050), Whitelist = driver.GetList(2048) },
                            RecoveryPending = File.Exists(Store.RecoveryPath) });
                    }
                    return 0;
                }
                if (args.Length == 4 && args[0] == "--driver-status")
                {
                    Store.Write(args[1], DriverSetup.Inspect(args[3], UInt32.Parse(args[2]))); return 0;
                }
                if (args.Length == 2 && args[0] == "--diagnose")
                {
                    DirectInput.Trace = message => File.AppendAllText(Path.Combine(Store.DirectoryPath, "directinput-diagnostic.log"), message + Environment.NewLine);
                    using (var direct = new DirectInput()) Store.Write(args[1], new {
                        CapturedUtc = DateTime.UtcNow, Devices = direct.Enumerate(), Vjoy = Vjoy.Probe(null), HidHide = HideDriver.Probe()
                    });
                    return 0;
                }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                if (args.Length >= 2 && args[0] == "--preview-wizard")
                {
                    Appearance.Load(); if (args.Length >= 4) Appearance.Set(args[3], false);
                    BridgeProfile saved = File.Exists(Store.ProfilePath) ? Store.LoadProfile(Store.ProfilePath, new BridgeProfile()) : new BridgeProfile();
                    using (var direct = new DirectInput()) Application.Run(new SetupWizard(saved, direct.Enumerate(), args[1], args.Length >= 3 ? Int32.Parse(args[2]) : 0));
                    return 0;
                }
                string preview = args.Length >= 2 && args.Length <= 4 && args[0] == "--preview" ? args[1] : null;
                if (args.Length > 0 && preview == null) throw new ArgumentException("Usage: Shim.exe [--diagnose report.json | --restore-visibility report.json | --preview image.png | --preview-wizard image.png page]");
                if (preview != null)
                {
                    using (var form = new BridgeForm(preview, args.Length >= 3 ? args[2] : null, args.Length >= 4 ? args[3] : null)) Application.Run(form);
                    return 0;
                }
                using (var mutex = new Mutex(false, "Local\\FHRigBridge-" + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value))
                {
                    bool owns = false;
                    try
                    {
                        try { owns = mutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                        if (!owns) throw new InvalidOperationException("Shim is already running in this Windows session.");
                        Application.Run(new BridgeForm(preview, preview != null && args.Length >= 3 ? args[2] : null, preview != null && args.Length >= 4 ? args[3] : null));
                    }
                    finally { if (owns) mutex.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(Store.DirectoryPath, "startup-error.log"), ex.ToString());
                if (args.Length == 0) MessageBox.Show(ex.Message, "Shim", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
        private static void Watchdog(string token)
        {
            if (token.Length != 32 || token.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Invalid recovery token.");
            if (!File.Exists(Store.RecoveryPath)) return;
            var recovery = Store.Read<HideRecovery>(Store.RecoveryPath);
            if (recovery.Token != token) return;
            string ready = Store.RecoveryPath + ".ready-" + token;
            File.WriteAllText(ready, "ready");
            try
            {
                while (File.Exists(Store.RecoveryPath))
                {
                    var current = Store.Read<HideRecovery>(Store.RecoveryPath);
                    if (current.Token != token) return;
                    if (!HideSession.ParentIsAlive(recovery))
                    {
                        StopWheel(recovery.WheelId);
                        HideSession.RecoverFile(Store.RecoveryPath);
                        return;
                    }
                    Thread.Sleep(250);
                }
            }
            finally { if (File.Exists(ready)) File.Delete(ready); }
        }
        private static void StopWheel(string id)
        {
            if (String.IsNullOrEmpty(id)) return;
            try
            {
                using (var window = new Form())
                using (var direct = new DirectInput())
                {
                    var wheel = direct.Enumerate().FirstOrDefault(d => d.Id == id && d.ForceFeedback && !d.Virtual);
                    if (wheel == null) return;
                    using (var reader = direct.Open(wheel, window.Handle, true))
                    { reader.Device.SendForceFeedbackCommand(2); reader.Device.SendForceFeedbackCommand(32); }
                }
            }
            catch (Exception ex) { File.AppendAllText(Path.Combine(Store.DirectoryPath, "watchdog.log"), "Wheel stop: " + ex + Environment.NewLine); }
        }
    }
}
