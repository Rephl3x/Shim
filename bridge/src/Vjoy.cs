using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace RigBridge
{
    public sealed class FfbPacket { public int Type; public byte[] Data; }
    public sealed class Vjoy : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FfbCallback(IntPtr packet, IntPtr context);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("vJoyInterface.dll", CallingConvention = CallingConvention.Cdecl)] private static extern bool vJoyEnabled();
        [DllImport("vJoyInterface.dll", CallingConvention = CallingConvention.Cdecl)] private static extern bool DriverMatch(out ushort dll, out ushort driver);
        [DllImport("vJoyInterface.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int GetVJDStatus(uint id);
        [DllImport("vJoyInterface.dll", CallingConvention = CallingConvention.Cdecl)] private static extern bool AcquireVJD(uint id);
        [DllImport("vJoyInterface.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void RelinquishVJD(uint id);
        [DllImport("vJoyInterface.dll", CallingConvention = CallingConvention.Cdecl)] private static extern bool ResetVJD(uint id);
        [DllImport("vJoyInterface.dll", CallingConvention = CallingConvention.Cdecl)] private static extern bool GetVJDAxisExist(uint id, uint usage);
        [DllImport("vJoyInterface.dll", CallingConvention = CallingConvention.Cdecl)] private static extern bool GetVJDAxisMin(uint id, uint usage, out int value);
        [DllImport("vJoyInterface.dll", CallingConvention = CallingConvention.Cdecl)] private static extern bool GetVJDAxisMax(uint id, uint usage, out int value);
        [DllImport("vJoyInterface.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int GetVJDButtonNumber(uint id);
        [DllImport("vJoyInterface.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int GetVJDContPovNumber(uint id);
        [DllImport("vJoyInterface.dll", CallingConvention = CallingConvention.Cdecl)] private static extern bool SetAxis(int value, uint id, uint usage);
        [DllImport("vJoyInterface.dll", CallingConvention = CallingConvention.Cdecl)] private static extern bool SetBtn(bool value, uint id, byte button);
        [DllImport("vJoyInterface.dll", CallingConvention = CallingConvention.Cdecl)] private static extern bool SetContPov(uint value, uint id, byte pov);
        [DllImport("vJoyInterface.dll", CallingConvention = CallingConvention.Cdecl)] private static extern bool IsDeviceFfb(uint id);
        [DllImport("vJoyInterface.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void FfbRegisterGenCB(FfbCallback callback, IntPtr context);
        [DllImport("vJoyInterface.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint Ffb_h_DeviceID(IntPtr packet, out uint id);
        [DllImport("vJoyInterface.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint Ffb_h_Type(IntPtr packet, out uint type);
        private readonly uint id;
        private readonly int[] minimum = new int[8], maximum = new int[8];
        private readonly bool[] axes = new bool[8];
        private readonly int buttons, povs;
        private readonly FfbCallback callback;
        private InputState last;
        private bool owned;
        private static readonly object loadLock = new object();
        private static string loadedPath;
        public static string DefaultDll
        {
            get
            {
                string bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime", "x64", "vJoyInterface.dll");
                return File.Exists(bundled) ? bundled : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy", "x64", "vJoyInterface.dll");
            }
        }
        public static string Probe(string path)
        {
            try { Load(path); ushort dll, driver; return !vJoyEnabled() ? "vJoy driver is not enabled" : !DriverMatch(out dll, out driver) ? "vJoy DLL/driver version mismatch" : "vJoy is ready"; }
            catch (Exception ex) { return "vJoy: " + ex.Message; }
        }
        private static void Load(string path)
        {
            path = String.IsNullOrEmpty(path) ? DefaultDll : Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("Install vJoy, then select its x64 vJoyInterface.dll in Setup.");
            lock (loadLock)
            {
                if (loadedPath != null)
                {
                    if (!String.Equals(loadedPath, path, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Restart the app to use a different vJoy DLL.");
                    return;
                }
                if (LoadLibraryEx(path, IntPtr.Zero, 8) == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                loadedPath = path;
            }
        }
        public Vjoy(BridgeProfile profile, Action<FfbPacket> onPacket, Action<Exception> onError)
        {
            Load(profile.VjoyDll); ushort dll, driver;
            if (!vJoyEnabled() || !DriverMatch(out dll, out driver)) throw new InvalidOperationException("vJoy is unavailable or its driver and DLL versions do not match.");
            id = profile.VirtualId;
            if (GetVJDStatus(id) != 1) throw new InvalidOperationException("vJoy device " + id + " is missing or is owned by another feeder. Configure it and close other feeders.");
            buttons = GetVJDButtonNumber(id); povs = GetVJDContPovNumber(id);
            for (int i = 0; i < 8; i++)
            {
                axes[i] = GetVJDAxisExist(id, (uint)(0x30 + i));
                if (axes[i] && (!GetVJDAxisMin(id, (uint)(0x30 + i), out minimum[i]) || !GetVJDAxisMax(id, (uint)(0x30 + i), out maximum[i])))
                    throw new InvalidOperationException("Cannot read vJoy axis limits.");
            }
            foreach (var binding in profile.Bindings)
                if ((binding.OutputKind == "Axis" && !axes[binding.OutputIndex]) ||
                    (binding.OutputKind == "Button" && binding.OutputIndex >= buttons) ||
                    (binding.OutputKind == "POV" && binding.OutputIndex >= povs))
                    throw new InvalidOperationException("Configure vJoy with all 8 axes, 128 buttons and 1 continuous POV (up to 4 if mapped).");
            if (profile.EnableFfb && !IsDeviceFfb(id)) throw new InvalidOperationException("Enable force feedback on vJoy device " + id + " in Configure vJoy.");
            if (!AcquireVJD(id)) throw new InvalidOperationException("Could not acquire the virtual controller.");
            owned = true;
            try
            {
                if (!ResetVJD(id)) throw new InvalidOperationException("Could not reset vJoy.");
                callback = delegate(IntPtr packet, IntPtr context) {
                    try
                    {
                        uint device, type;
                        if (Ffb_h_DeviceID(packet, out device) != 0 || device != id || Ffb_h_Type(packet, out type) != 0) return;
                        int length = Marshal.ReadInt32(packet) - 8;
                        if (length < 2 || length > 1024) throw new InvalidOperationException("Invalid vJoy FFB packet length.");
                        var data = new byte[length]; Marshal.Copy(Marshal.ReadIntPtr(packet, 8), data, 0, length);
                        onPacket(new FfbPacket { Type = (int)type, Data = data });
                    }
                    catch (Exception ex) { onError(ex); } // Never unwind an exception into the native callback.
                };
                FfbRegisterGenCB(callback, IntPtr.Zero);
                Write(new InputState { Axes = new[] { 0.5, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 } });
            }
            catch { Dispose(); throw; }
        }
        public void Write(InputState state)
        {
            for (int i = 0; i < 8; i++) if (axes[i] && (last == null || Math.Abs(last.Axes[i] - state.Axes[i]) > 0.00001))
                Check(SetAxis((int)(minimum[i] + Math.Max(0, Math.Min(1, state.Axes[i])) * (maximum[i] - (double)minimum[i])), id, (uint)(0x30 + i)));
            for (int i = 0; i < Math.Min(128, buttons); i++) if (last == null || last.Buttons[i] != state.Buttons[i]) Check(SetBtn(state.Buttons[i], id, (byte)(i + 1)));
            for (int i = 0; i < Math.Min(4, povs); i++) if (last == null || last.Pov[i] != state.Pov[i]) Check(SetContPov(unchecked((uint)state.Pov[i]), id, (byte)(i + 1)));
            last = state;
        }
        private static void Check(bool success) { if (!success) throw new InvalidOperationException("Writing to vJoy failed. The bridge has stopped."); }
        public void Dispose()
        {
            if (!owned) return;
            FfbRegisterGenCB(null, IntPtr.Zero);
            try { ResetVJD(id); last = null; Write(new InputState { Axes = new[] { 0.5, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 } }); }
            finally { RelinquishVJD(id); owned = false; GC.KeepAlive(callback); }
        }
    }
}
