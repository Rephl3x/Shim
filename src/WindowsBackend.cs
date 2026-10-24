using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FHMozaFix
{
    public sealed class WindowsBackend : IDeviceBackend
    {
        private readonly Action<string> log;
        public WindowsBackend(Action<string> log) { this.log = log; }
        public List<DeviceNode> Snapshot() { return NativeDevices.Snapshot(false); }

        public async Task SetEnabledAsync(Controller controller, bool enabled)
        {
            if (!Program.IsAdministrator) throw new InvalidOperationException("Reopen the app as administrator to reconnect controllers.");
            if (!DeviceSafety.IsPhysicalUsb(controller.TargetId)) throw new InvalidOperationException("Invalid USB device instance ID.");
            var start = new ProcessStartInfo {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "pnputil.exe"),
                Arguments = (enabled ? "/enable-device \"" : "/disable-device \"") + controller.TargetId + "\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = new Process { StartInfo = start })
            {
                if (!process.Start()) throw new InvalidOperationException("Windows could not start PnPUtil.");
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                // Do not cancel a running PnP command halfway through a device change.
                bool exited = await Task.Run(() => process.WaitForExit(45000));
                if (!exited)
                {
                    process.Kill();
                    await Task.Run(() => process.WaitForExit());
                    throw new TimeoutException("Windows took too long to change " + controller.Name + ".");
                }
                string output = ((await stdout) + " " + (await stderr)).Trim();
                log("PnPUtil exit " + process.ExitCode + ": " + output.Replace("\r", "").Replace("\n", " "));
                if (process.ExitCode == 3010 || process.ExitCode == 1641)
                    throw new InvalidOperationException("Windows reports that a restart is required for " + controller.Name + ". The app will not restart your PC.");
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("Windows could not " + (enabled ? "enable " : "disable ") + controller.Name + " (exit " + process.ExitCode + "). See the log.");
            }
        }

        public async Task WaitAsync(Controller controller, bool enabled, CancellationToken cancellation)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(20))
            {
                cancellation.ThrowIfCancellationRequested();
                if (NativeDevices.IsState(controller.TargetId, enabled) &&
                    (!enabled || controller.GameIds.All(id => NativeDevices.IsState(id, true)))) return;
                await Task.Delay(250, cancellation);
            }
            throw new TimeoutException(controller.Name + (enabled
                ? " did not return with all its game-controller interfaces ready."
                : " was not confirmed disabled. Windows may require a restart."));
        }
    }
}
