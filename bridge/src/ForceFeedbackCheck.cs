using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RigBridge
{
    internal static class ForceFeedbackCheck
    {
        // Opt-in driver interoperability check. Gain is zero and no effect Start is ever called.
        internal static void Run(string reportPath)
        {
            if (File.Exists(Store.RecoveryPath)) throw new InvalidOperationException("Stop the bridge and restore visibility before checking FFB updates.");
            var profile = Store.Read<BridgeProfile>(Store.ProfilePath);
            using (var window = new Form())
            using (var direct = new DirectInput())
            {
                var device = direct.Enumerate().FirstOrDefault(d => d.Id == profile.FfbDevice && d.ForceFeedback && !d.Virtual);
                if (device == null) throw new InvalidOperationException("The profile's physical FFB wheel is unavailable.");
                var steering = profile.Bindings.FirstOrDefault(b => b.OutputKind == "Axis" && b.OutputIndex == 0 && b.DeviceId == device.Id);
                if (steering == null) throw new InvalidOperationException("Map steering to the FFB wheel before running this check.");
                uint axis = device.Controls.First(c => c.Kind == "Axis" && c.Index == steering.InputIndex).ObjectId;
                using (var reader = direct.Open(device, window.Handle, true))
                {
                    var native = new PhysicalForceDevice(reader.Device);
                    reader.Device.Unacquire(); int gainBefore = Read(native, 7), autocenterBefore = Read(native, 9);
                    var passed = new List<string>(); NativeForceSink sink;
                    Store.Write(reportPath, new { Completed = false, Wheel = device.Name, TestGain = 0, PlaybackRequested = false });
                    using (sink = new NativeForceSink(native, 0, false, axis))
                    {
                        var constant = new ForceEffect { Id = 1, Type = 1, Constant = 0 };
                        sink.Update(constant); constant.Constant = 1000; sink.Update(constant); constant.Constant = -1000; sink.Update(constant);
                        passed.Add("Constant effect: created and magnitude updated in both directions"); sink.Free(1);
                        var spring = new ForceEffect { Id = 2, Type = 8, PositiveCoefficient = 0, NegativeCoefficient = 0 };
                        sink.Update(spring); spring.PositiveCoefficient = 1000; spring.NegativeCoefficient = -1000; sink.Update(spring);
                        passed.Add("Spring effect: created and condition parameters updated"); sink.Free(2);
                        if (sink.Starts != 0) throw new InvalidOperationException("The no-playback check unexpectedly requested playback.");
                    }
                    int gainAfter = Read(native, 7), autocenterAfter = Read(native, 9);
                    bool settingsRestored = (gainBefore < 0 || gainBefore == gainAfter) && (autocenterBefore < 0 || autocenterBefore == autocenterAfter);
                    Store.Write(reportPath, new { Completed = true, Wheel = device.Name, TestGain = 0, PlaybackRequested = false,
                        Passed = passed, Created = sink.Created, Updated = sink.Updated, Starts = sink.Starts,
                        GainBefore = gainBefore, GainAfter = gainAfter, AutocenterBefore = autocenterBefore, AutocenterAfter = autocenterAfter, SettingsRestored = settingsRestored });
                    if (!settingsRestored) throw new InvalidOperationException("The wheel did not confirm restoration of its original gain/autocenter settings.");
                }
            }
        }
        private static int Read(IPhysicalForceDevice device, int property)
        {
            IntPtr memory = Marshal.AllocHGlobal(20);
            try { Marshal.Copy(new[] { 20, 16, 0, 0, 0 }, 0, memory, 5); return device.GetProperty(new IntPtr(property), memory) < 0 ? -1 : Marshal.ReadInt32(memory, 16); }
            finally { Marshal.FreeHGlobal(memory); }
        }
    }
}
