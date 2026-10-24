using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RigBridge
{
    public interface IHideDriver : IDisposable
    {
        List<string> GetList(int function);
        void SetList(int function, IEnumerable<string> entries);
        bool GetFlag(int function);
        void SetFlag(int function, bool value);
    }
    public sealed class HideDriver : IHideDriver
    {
        private readonly SafeFileHandle handle;
        private readonly Func<int, byte[], byte[], uint> control;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint QueryDosDevice(string name, StringBuilder buffer, int size);
        public HideDriver()
        {
            handle = CreateFile(@"\\.\HidHide", 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "HidHide is unavailable. Install its official driver, restart if requested, and retry."); }
            control = NativeControl;
        }
        internal HideDriver(Func<int, byte[], byte[], uint> control)
        {
            if (control == null) throw new ArgumentNullException("control");
            this.control = control;
        }
        private uint NativeControl(int function, byte[] input, byte[] output)
        {
            uint returned;
            uint code = (32769u << 16) | (1u << 14) | ((uint)function << 2);
            if (!DeviceIoControl(handle, code, input, input == null ? 0 : (uint)input.Length, output, output == null ? 0 : (uint)output.Length, out returned, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "HidHide command " + function + " failed");
            return returned;
        }
        private byte[] Read(int function, int capacity)
        {
            var output = new byte[capacity]; uint returned = control(function, null, output);
            if (returned > output.Length) throw new InvalidDataException("HidHide read command " + function + " returned more data than its output buffer.");
            Array.Resize(ref output, (int)returned); return output;
        }
        private void Write(int function, byte[] input)
        {
            // HidHide 1.5.230 list setters report inputBufferLength on success even with no output buffer.
            // This acknowledges bytes consumed; it must not be checked as an output-buffer length.
            control(function, input, null);
        }
        public List<string> GetList(int function)
        {
            if (function != 2048 && function != 2050) throw new ArgumentOutOfRangeException("function");
            for (int attempt = 0; attempt < 3; attempt++)
            {
                // Query the size first, as in HidHide's official client. A list can grow between calls.
                uint needed = control(function, null, null);
                if (needed > 1048576 || needed % 2 != 0) throw new InvalidDataException("HidHide reported an invalid UTF-16 list size.");
                if (needed == 0) return new List<string>();
                var output = new byte[(int)needed]; uint returned;
                try { returned = control(function, null, output); }
                catch (Win32Exception ex) { if (ex.NativeErrorCode == 122 || ex.NativeErrorCode == 234) continue; throw; }
                if (returned > output.Length) continue;
                Array.Resize(ref output, (int)returned); return DecodeMultiString(output);
            }
            throw new IOException("HidHide's list changed during the read. Close other configuration tools and retry.");
        }
        public void SetList(int function, IEnumerable<string> entries)
        {
            if (function != 2049 && function != 2051) throw new ArgumentOutOfRangeException("function");
            Write(function, EncodeMultiString(entries));
        }
        public bool GetFlag(int function)
        {
            if (function != 2052 && function != 2054) throw new ArgumentOutOfRangeException("function");
            byte[] data = Read(function, 1); if (data.Length != 1) throw new InvalidDataException("Invalid HidHide state response."); return data[0] != 0;
        }
        public void SetFlag(int function, bool value)
        {
            if (function != 2053 && function != 2055) throw new ArgumentOutOfRangeException("function");
            Write(function, new[] { (byte)(value ? 1 : 0) });
        }
        public static byte[] EncodeMultiString(IEnumerable<string> entries)
        {
            string[] values = entries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (values.Any(v => String.IsNullOrEmpty(v) || v.IndexOf('\0') >= 0)) throw new InvalidDataException("Invalid HidHide entry.");
            return Encoding.Unicode.GetBytes(values.Length == 0 ? "\0\0" : String.Join("\0", values) + "\0\0");
        }
        public static List<string> DecodeMultiString(byte[] data)
        {
            if (data.Length % 2 != 0) throw new InvalidDataException("Invalid HidHide UTF-16 data.");
            return Encoding.Unicode.GetString(data).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        public static string ImagePath(string path)
        {
            path = Path.GetFullPath(path); string root = Path.GetPathRoot(path);
            if (root.Length != 3 || root[1] != ':') throw new InvalidOperationException("Run the bridge from a local drive.");
            var volume = new StringBuilder(2048);
            if (QueryDosDevice(root.Substring(0, 2), volume, volume.Capacity) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            return volume.ToString() + path.Substring(2);
        }
        public void Dispose() { if (handle != null) handle.Dispose(); }
        public static string Probe()
        { try { using (var driver = new HideDriver()) return driver.GetFlag(2054) ? "HidHide uses inverse mode; switch to normal mode in its configuration client" : "HidHide is ready"; } catch (Exception ex) { return "HidHide: " + ex.Message; } }
    }
    public sealed class HideRecovery
    {
        public string Token { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartTicks { get; set; }
        public string WheelId { get; set; }
        public bool OriginallyActive { get; set; }
        public List<string> OriginalBlacklist { get; set; }
        public List<string> AddedDevices { get; set; }
        public List<string> AddedApplications { get; set; }
    }
    public static class HideSession
    {
        public static HideRecovery Prepare(IHideDriver driver, IEnumerable<string> deviceIds, string imagePath, string wheelId)
        {
            if (driver.GetFlag(2054)) throw new InvalidOperationException("HidHide inverse mode is enabled. Switch it off in HidHide Configuration Client first.");
            var blacklist = driver.GetList(2050); var whitelist = driver.GetList(2048);
            bool active = driver.GetFlag(2052);
            if (!active && blacklist.Count != 0)
                throw new InvalidOperationException("HidHide has inactive rules for other devices. Review and enable or clear those rules in its configuration client before starting.");
            var selected = deviceIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (selected.Count == 0 || selected.Any(id => !id.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) || id.IndexOf('\0') >= 0))
                throw new InvalidOperationException("Only identified physical game-controller HID interfaces can be hidden.");
            using (var process = Process.GetCurrentProcess())
                return new HideRecovery {
                    Token = Guid.NewGuid().ToString("N"), ProcessId = process.Id, ProcessStartTicks = process.StartTime.ToUniversalTime().Ticks,
                    WheelId = wheelId, OriginallyActive = active, OriginalBlacklist = blacklist,
                    AddedDevices = selected.Except(blacklist, StringComparer.OrdinalIgnoreCase).ToList(),
                    AddedApplications = new[] { imagePath }.Except(whitelist, StringComparer.OrdinalIgnoreCase).ToList()
                };
        }
        public static void Apply(IHideDriver driver, HideRecovery recovery)
        {
            // The caller must save recovery and start the watchdog BEFORE this call.
            driver.SetList(2049, driver.GetList(2048).Concat(recovery.AddedApplications));
            driver.SetList(2051, driver.GetList(2050).Concat(recovery.AddedDevices));
            driver.SetFlag(2053, true);
        }
        public static void Restore(IHideDriver driver, HideRecovery recovery)
        {
            if (recovery == null || recovery.AddedDevices == null || recovery.AddedApplications == null || recovery.OriginalBlacklist == null)
                throw new InvalidDataException("The hiding recovery record is damaged. Use HidHide Configuration Client to restore device visibility.");
            var remaining = driver.GetList(2050).Except(recovery.AddedDevices, StringComparer.OrdinalIgnoreCase).ToList();
            driver.SetList(2051, remaining);
            driver.SetList(2049, driver.GetList(2048).Except(recovery.AddedApplications, StringComparer.OrdinalIgnoreCase));
            // Preserve new rules another application may have added during the session.
            bool deactivate = !recovery.OriginallyActive && !remaining.Except(recovery.OriginalBlacklist, StringComparer.OrdinalIgnoreCase).Any();
            if (deactivate) driver.SetFlag(2053, false);
            // Retain the recovery record unless the actual driver state confirms rollback.
            if (driver.GetList(2050).Intersect(recovery.AddedDevices, StringComparer.OrdinalIgnoreCase).Any() ||
                driver.GetList(2048).Intersect(recovery.AddedApplications, StringComparer.OrdinalIgnoreCase).Any() ||
                (deactivate && driver.GetFlag(2052)))
                throw new IOException("HidHide has not restored the session's visibility changes. The recovery record has been kept; retry Restore visibility.");
        }
        public static bool ParentIsAlive(HideRecovery recovery)
        {
            try { using (var process = Process.GetProcessById(recovery.ProcessId)) return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == recovery.ProcessStartTicks; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
        public static void RecoverFile(string path, bool currentSessionStopped = false)
        {
            if (!File.Exists(path)) return;
            var recovery = Store.Read<HideRecovery>(path);
            if (recovery == null) throw new InvalidDataException("The hiding recovery record is empty.");
            if (!CanRecover(recovery, currentSessionStopped)) throw new InvalidOperationException("The bridge session is still running. Stop it before restoring visibility.");
            using (var driver = new HideDriver()) Restore(driver, recovery);
            File.Delete(path);
        }
        internal static bool CanRecover(HideRecovery recovery, bool currentSessionStopped)
        {
            if (!ParentIsAlive(recovery)) return true;
            // Only the owning UI can assert its worker has stopped; a different live process stays protected.
            using (var current = Process.GetCurrentProcess())
                return currentSessionStopped && recovery.ProcessId == current.Id && recovery.ProcessStartTicks == current.StartTime.ToUniversalTime().Ticks;
        }
    }
}
