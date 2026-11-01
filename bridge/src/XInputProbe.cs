using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RigBridge
{
    internal static class XInputProbe
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Gamepad
        {
            public ushort Buttons;
            public byte LeftTrigger, RightTrigger;
            public short LeftX, LeftY, RightX, RightY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct State
        {
            public uint PacketNumber;
            public Gamepad Pad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState", ExactSpelling = true)]
        private static extern uint GetState(uint userIndex, out State state);

        public static int[] ConnectedSlots()
        {
            var slots = new List<int>();
            for (uint i = 0; i < 4; i++)
            {
                State state;
                uint result = GetState(i, out state);
                if (result == 0) slots.Add((int)i + 1);
                else if (result != 1167) throw new InvalidOperationException("XInput check failed (" + result + ").");
            }
            return slots.ToArray();
        }
    }
}
