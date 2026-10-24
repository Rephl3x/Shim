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
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint QueryDosDevice(string name, StringBuilder buffer, int size);
        public HideDriver()
        {
            handle = CreateFile(@"\\.\HidHide", 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "HidHide is unavailable. Install its official driver, restart if requested, and retry."); }
        }
        private byte[] Io(int function, byte[] input, int capacity)
        {
            var output = new byte[capacity]; uint returned;
            uint code = (32769u << 16) | (1u << 14) | ((uint)function << 2);
            if (!DeviceIoControl(handle, code, input, input == null ? 0 : (uint)input.Length, output, (uint)output.Length, out returned, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "HidHide command " + function + " failed");
            if (returned > output.Length) throw new InvalidDataException("HidHide returned an oversized response.");
            Array.Resize(ref output, (int)returned); return output;
        }
        public List<string> GetList(int function)
        { return DecodeMultiString(Io(function, null, 131072)); }
        public void SetList(int function, IEnumerable<string> entries) { Io(function, EncodeMultiString(entries), 0); }
        public bool GetFlag(int function)
        { byte[] data = Io(function, null, 1); if (data.Length != 1) throw new InvalidDataException("Invalid HidHide state response."); return data[0] != 0; }
        public void SetFlag(int function, bool value) { Io(function, new[] { (byte)(value ? 1 : 0) }, 0); }
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
        public void Dispose() { handle.Dispose(); }
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
            if (!recovery.OriginallyActive && !remaining.Except(recovery.OriginalBlacklist, StringComparer.OrdinalIgnoreCase).Any()) driver.SetFlag(2053, false);
        }
        public static bool ParentIsAlive(HideRecovery recovery)
        {
            try { using (var process = Process.GetProcessById(recovery.ProcessId)) return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == recovery.ProcessStartTicks; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
        public static void RecoverFile(string path)
        {
            if (!File.Exists(path)) return;
            var recovery = Store.Read<HideRecovery>(path);
            if (recovery == null || ParentIsAlive(recovery)) throw new InvalidOperationException("The bridge session is still running. Stop it before restoring visibility.");
            using (var driver = new HideDriver()) Restore(driver, recovery);
            File.Delete(path);
        }
    }
}
