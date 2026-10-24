using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace RigBridge
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DiInstance
    {
        public uint Size; public Guid Instance, Product; public uint Type;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string InstanceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ProductName;
        public Guid FfbDriver; public ushort UsagePage, Usage;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DiObject
    {
        public uint Size; public Guid TypeGuid; public uint Offset, Type, Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        public uint MaxForce, Resolution; public ushort Collection, Designator, UsagePage, Usage;
        public uint Dimension; public ushort Exponent, Report;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct DiCaps
    { public uint Size, Flags, Type, Axes, Buttons, Pov, FfbRate, FfbResolution, Revision, Firmware, Hardware; }
    [StructLayout(LayoutKind.Sequential)] internal struct DiFormat
    { public uint Size, ObjectSize, Flags, DataSize, Count; public IntPtr Objects; }
    [StructLayout(LayoutKind.Sequential)] internal struct DiObjectFormat
    { public IntPtr Guid; public uint Offset, Type, Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct DiEffectDescription
    {
        public uint Size, Flags, Duration, SamplePeriod, Gain, TriggerButton, RepeatInterval, AxesCount;
        public IntPtr Axes, Direction, Envelope; public uint TypeSize; public IntPtr TypeData; public uint StartDelay;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int EnumDeviceCallback(IntPtr instance, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int EnumObjectCallback(IntPtr instance, IntPtr context);
    [ComImport, Guid("BF798031-483A-4DA2-AA99-5D64ED369700"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDi8
    {
        [PreserveSig] int CreateDevice(ref Guid id, out IDiDevice device, IntPtr outer);
        [PreserveSig] int EnumDevices(uint type, [MarshalAs(UnmanagedType.FunctionPtr)] EnumDeviceCallback callback, IntPtr context, uint flags);
    }
    [ComImport, Guid("54D41081-DC15-4833-A41B-748F73A38179"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDiDevice
    {
        [PreserveSig] int GetCapabilities(ref DiCaps caps);
        [PreserveSig] int EnumObjects([MarshalAs(UnmanagedType.FunctionPtr)] EnumObjectCallback callback, IntPtr context, uint flags);
        [PreserveSig] int GetProperty(IntPtr property, IntPtr value);
        [PreserveSig] int SetProperty(IntPtr property, IntPtr value);
        [PreserveSig] int Acquire();
        [PreserveSig] int Unacquire();
        [PreserveSig] int GetDeviceState(uint size, IntPtr data);
        [PreserveSig] int GetDeviceData(uint size, IntPtr data, IntPtr count, uint flags);
        [PreserveSig] int SetDataFormat(ref DiFormat format);
        [PreserveSig] int SetEventNotification(IntPtr handle);
        [PreserveSig] int SetCooperativeLevel(IntPtr window, uint flags);
        [PreserveSig] int GetObjectInfo(IntPtr data, uint obj, uint how);
        [PreserveSig] int GetDeviceInfo(ref DiInstance instance);
        [PreserveSig] int RunControlPanel(IntPtr window, uint flags);
        [PreserveSig] int Initialize(IntPtr module, uint version, ref Guid id);
        [PreserveSig] int CreateEffect(ref Guid type, ref DiEffectDescription effect, out IDiEffect result, IntPtr outer);
        [PreserveSig] int EnumEffects(IntPtr callback, IntPtr context, uint flags);
        [PreserveSig] int GetEffectInfo(IntPtr info, ref Guid id);
        [PreserveSig] int GetForceFeedbackState(out uint state);
        [PreserveSig] int SendForceFeedbackCommand(uint command);
        [PreserveSig] int EnumCreatedEffectObjects(IntPtr callback, IntPtr context, uint flags);
        [PreserveSig] int Escape(IntPtr escape);
        [PreserveSig] int Poll();
    }
    [ComImport, Guid("E7E1F7C0-88D2-11D0-9AD0-00A0C9A06E35"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDiEffect
    {
        [PreserveSig] int Initialize(IntPtr module, uint version, ref Guid id);
        [PreserveSig] int GetEffectGuid(out Guid id);
        [PreserveSig] int GetParameters(IntPtr effect, uint flags);
        [PreserveSig] int SetParameters(ref DiEffectDescription effect, uint flags);
        [PreserveSig] int Start(uint iterations, uint flags);
        [PreserveSig] int Stop();
        [PreserveSig] int GetEffectStatus(out uint flags);
        [PreserveSig] int Download();
        [PreserveSig] int Unload();
    }

    public sealed class DirectInput : IDisposable
    {
        public static Action<string> Trace;
        private static void Mark(string stage) { if (Trace != null) Trace(stage); }
        private IntPtr api;
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumNative(IntPtr self, uint type, EnumDeviceCallback callback, IntPtr context, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateNative(IntPtr self, ref Guid id, out IntPtr device, IntPtr outer);
        [DllImport("dinput8.dll")] private static extern int DirectInput8Create(IntPtr module, uint version, ref Guid iid, out IntPtr api, IntPtr outer);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        public DirectInput()
        {
            Guid iid = new Guid("BF798031-483A-4DA2-AA99-5D64ED369700");
            Mark("DirectInput8Create");
            Check(DirectInput8Create(GetModuleHandle(null), 0x800, ref iid, out api, IntPtr.Zero), "Initialize DirectInput");
        }
        public List<PhysicalDevice> Enumerate()
        {
            var instances = new List<DiInstance>();
            Mark("EnumDevices");
            EnumDeviceCallback callback = delegate(IntPtr info, IntPtr data) { instances.Add((DiInstance)Marshal.PtrToStructure(info, typeof(DiInstance))); return 1; };
            var enumerate = (EnumNative)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(api), 4 * IntPtr.Size), typeof(EnumNative));
            Check(enumerate(api, 4, callback, IntPtr.Zero, 1), "List game controllers");
            GC.KeepAlive(callback);
            var result = new List<PhysicalDevice>();
            foreach (var info in instances)
            {
                Mark("CreateDevice: " + info.InstanceName);
                IDiDevice device; Guid id = info.Instance;
                device = Create(ref id);
                try
                {
                    var caps = new DiCaps { Size = (uint)Marshal.SizeOf(typeof(DiCaps)) };
                    Mark("GetCapabilities");
                    Check(device.GetCapabilities(ref caps), "Read device capabilities");
                    string path = DevicePath(device);
                    Mark("EnumObjects");
                    var controls = BuildControls(device);
                    result.Add(new PhysicalDevice {
                        Id = info.Instance.ToString(), Name = info.InstanceName, ForceFeedback = (caps.Flags & 0x100) != 0,
                        Virtual = info.InstanceName.IndexOf("vJoy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            path.IndexOf("VID_1234&PID_BEAD", StringComparison.OrdinalIgnoreCase) >= 0,
                        HidId = HidInstanceId(path), Controls = controls
                    });
                }
                finally { Marshal.FinalReleaseComObject(device); }
            }
            return result;
        }
        public InputReader Open(PhysicalDevice info, IntPtr window, bool forceFeedback)
        {
            IDiDevice device; Guid id = new Guid(info.Id);
            device = Create(ref id);
            try { return new InputReader(device, info, window, forceFeedback); }
            catch { Marshal.FinalReleaseComObject(device); throw; }
        }
        internal static List<InputControl> BuildControls(IDiDevice device)
        {
            var objects = new List<DiObject>();
            EnumObjectCallback callback = delegate(IntPtr value, IntPtr context) { objects.Add((DiObject)Marshal.PtrToStructure(value, typeof(DiObject))); return 1; };
            Check(device.EnumObjects(callback, IntPtr.Zero, 0), "Read controller controls");
            GC.KeepAlive(callback);
            string[] axes = { "a36d02e0", "a36d02e1", "a36d02e2", "a36d02f4", "a36d02f5", "a36d02e3" };
            var result = new List<InputControl>(); int sliders = 0, buttons = 0, pov = 0;
            foreach (var obj in objects)
            {
                int index; string kind;
                if ((obj.Type & 3) != 0)
                {
                    string type = obj.TypeGuid.ToString().Substring(0, 8);
                    index = Array.IndexOf(axes, type);
                    if (type == "a36d02e4") index = 6 + sliders++;
                    if (index < 0 || index >= 8 || result.Any(c => c.Kind == "Axis" && c.Index == index)) continue;
                    kind = "Axis";
                }
                else if ((obj.Type & 12) != 0) { kind = "Button"; index = buttons++; if (index >= 128) continue; }
                else if ((obj.Type & 16) != 0) { kind = "POV"; index = pov++; if (index >= 4) continue; }
                else continue;
                result.Add(new InputControl { Kind = kind, Index = index, Name = obj.Name, ObjectId = obj.Type });
            }
            return result;
        }
        private static string DevicePath(IDiDevice device)
        {
            IntPtr memory = Marshal.AllocHGlobal(552);
            Mark("GetProperty GUIDANDPATH");
            try
            {
                for (int i = 0; i < 552; i++) Marshal.WriteByte(memory, i, 0);
                Marshal.WriteInt32(memory, 552); Marshal.WriteInt32(memory, 4, 16);
                int hr = device.GetProperty(new IntPtr(12), memory);
                return hr < 0 ? "" : Marshal.PtrToStringUni(new IntPtr(memory.ToInt64() + 32));
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        public static string HidInstanceId(string path)
        {
            if (String.IsNullOrEmpty(path) || !path.StartsWith(@"\\?\hid#", StringComparison.OrdinalIgnoreCase)) return "";
            int end = path.LastIndexOf("#{", StringComparison.Ordinal);
            return end < 0 ? "" : path.Substring(4, end - 4).Replace('#', '\\').ToUpperInvariant();
        }
        internal static void Check(int hr, string action)
        { if (hr < 0) throw new InvalidOperationException(action + " failed (0x" + hr.ToString("X8") + "). Close other apps that exclusively own this controller."); }
        private IDiDevice Create(ref Guid id)
        {
            var create = (CreateNative)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(api), 3 * IntPtr.Size), typeof(CreateNative));
            IntPtr pointer; Check(create(api, ref id, out pointer, IntPtr.Zero), "Create DirectInput controller");
            try { return (IDiDevice)Marshal.GetTypedObjectForIUnknown(pointer, typeof(IDiDevice)); }
            finally { Marshal.Release(pointer); }
        }
        public void Dispose() { if (api != IntPtr.Zero) { Marshal.Release(api); api = IntPtr.Zero; } }
    }

    public sealed class InputReader : IDisposable
    {
        internal IDiDevice Device;
        public readonly PhysicalDevice Info;
        private readonly IntPtr state = Marshal.AllocHGlobal(176);
        private readonly List<IntPtr> formatMemory = new List<IntPtr>();
        private readonly Dictionary<int, int[]> ranges = new Dictionary<int, int[]>();
        internal InputReader(IDiDevice device, PhysicalDevice info, IntPtr window, bool ffb)
        {
            Device = device; Info = info;
            try
            {
                var formats = info.Controls.Select(control => new DiObjectFormat {
                    Offset = (uint)(control.Kind == "Axis" ? control.Index * 4 : control.Kind == "Button" ? 48 + control.Index : 32 + control.Index * 4),
                    Type = control.ObjectId
                }).ToArray();
                int size = Marshal.SizeOf(typeof(DiObjectFormat));
                IntPtr objects = Marshal.AllocHGlobal(size * formats.Length); formatMemory.Add(objects);
                for (int i = 0; i < formats.Length; i++) Marshal.StructureToPtr(formats[i], new IntPtr(objects.ToInt64() + i * size), false);
                var format = new DiFormat { Size = (uint)Marshal.SizeOf(typeof(DiFormat)), ObjectSize = (uint)size, Flags = 1, DataSize = 176, Count = (uint)formats.Length, Objects = objects };
                DirectInput.Check(device.SetDataFormat(ref format), "Set controller input format");
                DirectInput.Check(device.SetCooperativeLevel(window, ffb ? 9u : 10u), "Set controller access");
                foreach (var control in info.Controls.Where(c => c.Kind == "Axis"))
                {
                    IntPtr range = Marshal.AllocHGlobal(24);
                    try
                    {
                        Marshal.Copy(new[] { 24, 16, (int)control.ObjectId, 2, 0, 65535 }, 0, range, 6);
                        device.SetProperty(new IntPtr(4), range);
                        DirectInput.Check(device.GetProperty(new IntPtr(4), range), "Read axis range");
                        ranges[control.Index] = new[] { Marshal.ReadInt32(range, 16), Marshal.ReadInt32(range, 20) };
                    }
                    finally { Marshal.FreeHGlobal(range); }
                }
                DirectInput.Check(device.Acquire(), "Acquire " + info.Name);
            }
            catch { foreach (var memory in formatMemory) Marshal.FreeHGlobal(memory); Marshal.FreeHGlobal(state); throw; }
        }
        public InputState Read()
        {
            Device.Poll();
            int hr = Device.GetDeviceState(176, state);
            if (hr < 0)
            {
                DirectInput.Check(Device.Acquire(), "Reacquire " + Info.Name);
                Device.Poll(); DirectInput.Check(Device.GetDeviceState(176, state), "Read " + Info.Name);
            }
            var result = new InputState();
            foreach (var control in Info.Controls)
            {
                int index = control.Index;
                if (control.Kind == "Axis")
                {
                    int[] range = ranges[index];
                    result.Axes[index] = range[0] == range[1] ? 0 : Math.Max(0, Math.Min(1, (Marshal.ReadInt32(state, index * 4) - (double)range[0]) / (range[1] - (double)range[0])));
                }
                else if (control.Kind == "Button") result.Buttons[index] = (Marshal.ReadByte(state, 48 + index) & 128) != 0;
                else { int value = Marshal.ReadInt32(state, 32 + index * 4); result.Pov[index] = (value & 0xffff) == 0xffff ? -1 : value; }
            }
            return result;
        }
        public void Dispose()
        {
            if (Device == null) return;
            Device.Unacquire(); Marshal.FinalReleaseComObject(Device); Device = null;
            foreach (var memory in formatMemory) Marshal.FreeHGlobal(memory);
            Marshal.FreeHGlobal(state);
        }
    }
}
