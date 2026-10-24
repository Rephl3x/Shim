using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FHMozaFix
{
    // Read-only SetupAPI / Configuration Manager / HID interop.
    public static class NativeDevices
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInfo
        {
            public uint Size;
            public Guid ClassGuid;
            public uint DevInst;
            public UIntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(IntPtr guid, string enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DeviceInfo info, StringBuilder id, uint size, out uint required);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref DeviceInfo info, uint property, out uint type, byte[] buffer, uint size, out uint required);
        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_Parent(out uint parent, uint child, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_IDW(uint devInst, StringBuilder id, uint size, uint flags);
        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Locate_DevNodeW(out uint devInst, string id, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid guid, string id, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_Interface_ListW(ref Guid guid, string id, [Out] char[] buffer, uint length, uint flags);
        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] buffer, uint length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        public static List<DeviceNode> Snapshot(bool productNames)
        {
            IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, 0x02 | 0x04);
            if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var nodes = new List<DeviceNode>();
                for (uint index = 0; ; index++)
                {
                    var info = new DeviceInfo { Size = (uint)Marshal.SizeOf(typeof(DeviceInfo)) };
                    if (!SetupDiEnumDeviceInfo(set, index, ref info))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == 259) break;
                        throw new Win32Exception(error);
                    }
                    uint needed;
                    var id = new StringBuilder(1024);
                    if (!SetupDiGetDeviceInstanceIdW(set, ref info, id, (uint)id.Capacity, out needed))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    uint parent;
                    var parentId = new StringBuilder(1024);
                    if (CM_Get_Parent(out parent, info.DevInst, 0) == 0 && CM_Get_Device_IDW(parent, parentId, (uint)parentId.Capacity, 0) != 0)
                        throw new InvalidOperationException("Could not read the parent of " + id);
                    uint status, problem;
                    bool known = CM_Get_DevNode_Status(out status, out problem, info.DevInst, 0) == 0;
                    string name = Property(set, ref info, 12);
                    if (String.IsNullOrEmpty(name)) name = Property(set, ref info, 0);
                    var node = new DeviceNode {
                        Id = id.ToString(), ParentId = parentId.ToString(), Name = name,
                        ClassName = Property(set, ref info, 7), Service = Property(set, ref info, 4),
                        HardwareIds = Split(Property(set, ref info, 1)),
                        CompatibleIds = Split(Property(set, ref info, 2)),
                        StatusKnown = known, Started = known && (status & 0x08) != 0, Problem = problem
                    };
                    if (productNames && DeviceSafety.IsGame(node)) node.Product = Product(node.Id);
                    nodes.Add(node);
                }
                return nodes;
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        private static string Property(IntPtr set, ref DeviceInfo info, uint property)
        {
            uint type, needed;
            var buffer = new byte[32768];
            if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out type, buffer, (uint)buffer.Length, out needed))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 13 || error == 1168) return ""; // Property not supplied by this driver.
                throw new Win32Exception(error, "Could not read device property " + property);
            }
            return Encoding.Unicode.GetString(buffer, 0, (int)needed).TrimEnd('\0');
        }

        private static string[] Split(string value) { return value.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries); }

        public static bool IsState(string id, bool enabled)
        {
            uint devInst, status, problem;
            if (CM_Locate_DevNodeW(out devInst, id, 0) != 0 ||
                CM_Get_DevNode_Status(out status, out problem, devInst, 0) != 0) return false;
            return enabled ? problem == 0 && (status & 0x08) != 0 : problem == 22;
        }

        private static string Product(string id)
        {
            Guid guid;
            HidD_GetHidGuid(out guid);
            uint length;
            if (CM_Get_Device_Interface_List_SizeW(out length, ref guid, id, 0) != 0 || length <= 1 || length > 32768) return "";
            var buffer = new char[length];
            if (CM_Get_Device_Interface_ListW(ref guid, id, buffer, length, 0) != 0) return "";
            foreach (string path in new string(buffer).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
            {
                using (var handle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                {
                    if (handle.IsInvalid) continue;
                    var name = new byte[512];
                    if (HidD_GetProductString(handle, name, (uint)name.Length))
                        return Encoding.Unicode.GetString(name).Split('\0')[0];
                }
            }
            return ""; // Some drivers do not expose a readable product string.
        }
    }
}
