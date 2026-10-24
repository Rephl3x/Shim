using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace RigBridge.Tests
{
    public static class TestProgram
    {
        private static int passed;
        public static int Main()
        {
            try { Run(); Console.WriteLine("PASS: " + passed + " bridge tests. No drivers, hiding, virtual output or physical FFB were used."); return 0; }
            catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        }
        private static void Run()
        {
            Test("Five physical devices contribute to one output state", () => {
                var input = new Dictionary<string, InputState>(); var bindings = new List<Binding>();
                for (int i = 0; i < 5; i++)
                {
                    input["d" + i] = new InputState(); input["d" + i].Buttons[i] = true;
                    bindings.Add(new Binding { DeviceId = "d" + i, InputKind = "Button", InputIndex = i, OutputKind = "Button", OutputIndex = 20 + i });
                }
                input["d0"].Axes[0] = 0.75; bindings.Add(Axis("d0", 0, 0));
                var output = Mapper.Map(bindings, input);
                Assert(output.Axes[0] == 0.75 && output.Buttons.Skip(20).Take(5).All(b => b), "Aggregation lost a source");
            });
            Test("Released gears and buttons do not remain stuck", () => {
                var state = new InputState(); var bindings = new List<Binding> { new Binding { DeviceId = "hgp", InputKind = "Button", OutputKind = "Button" } };
                var input = new Dictionary<string, InputState> { { "hgp", state } };
                state.Buttons[0] = true; Assert(Mapper.Map(bindings, input).Buttons[0], "Gear press lost");
                state.Buttons[0] = false; Assert(!Mapper.Map(bindings, input).Buttons[0], "Gear stayed pressed");
            });
            Test("Unmapped controls use neutral steering and released pedals", () => {
                var state = Mapper.Map(new List<Binding>(), new Dictionary<string, InputState>());
                Assert(state.Axes[0] == 0.5 && state.Axes.Skip(1).All(a => a == 0) && state.Buttons.All(b => !b) && state.Pov.All(p => p == -1), "Unsafe neutral state");
            });
            Test("Axis inversion maps endpoints", () => {
                var binding = Axis("wheel", 0, 0); binding.Invert = true;
                var state = new InputState(); var input = new Dictionary<string, InputState> { { "wheel", state } };
                Assert(Mapper.Map(new[] { binding }, input).Axes[0] == 1, "Low endpoint not inverted");
                state.Axes[0] = 1; Assert(Mapper.Map(new[] { binding }, input).Axes[0] == 0, "High endpoint not inverted");
            });
            Test("Steering deadzone is symmetric around center", () => {
                var binding = Axis("wheel", 0, 0); binding.Deadzone = 0.1;
                var state = new InputState(); var input = new Dictionary<string, InputState> { { "wheel", state } };
                foreach (double value in new[] { 0.46, 0.5, 0.54 }) { state.Axes[0] = value; Assert(Mapper.Map(new[] { binding }, input).Axes[0] == 0.5, "Center deadzone failed"); }
            });
            Test("Pedal calibration clamps and preserves full travel", () => {
                var binding = Axis("pedals", 0, 1); binding.Minimum = 0.2; binding.Maximum = 0.8;
                var state = new InputState(); var input = new Dictionary<string, InputState> { { "pedals", state } };
                state.Axes[0] = 0.1; Assert(Mapper.Map(new[] { binding }, input).Axes[1] == 0, "Pedal rest not clamped");
                state.Axes[0] = 0.9; Assert(Mapper.Map(new[] { binding }, input).Axes[1] == 1, "Pedal full travel not clamped");
            });
            Test("Missing input aborts instead of retaining throttle", () => Throws(() => Mapper.Map(new[] { Axis("missing", 0, 1) }, new Dictionary<string, InputState>())));
            Test("POV center and directions pass through", () => {
                var source = new InputState(); var binding = new Binding { DeviceId = "box", InputKind = "POV", OutputKind = "POV" };
                var input = new Dictionary<string, InputState> { { "box", source } };
                source.Pov[0] = 9000; Assert(Mapper.Map(new[] { binding }, input).Pov[0] == 9000, "POV direction lost");
                source.Pov[0] = -1; Assert(Mapper.Map(new[] { binding }, input).Pov[0] == -1, "POV stayed pressed");
            });
            Test("Profile rejects duplicate virtual outputs", () => { var profile = Profile(); profile.Bindings.Add(Axis("wheel", 0, 0)); Throws(() => profile.Validate(Devices())); });
            Test("Profile rejects a virtual input feedback loop", () => { var devices = Devices(); devices[0].Virtual = true; Throws(() => Profile().Validate(devices)); });
            Test("Profile rejects missing HID identity before hiding", () => { var devices = Devices(); devices[0].HidId = ""; Throws(() => Profile().Validate(devices)); });
            Test("Profile requires steering from the selected FFB wheel", () => { var profile = Profile(); profile.EnableFfb = true; profile.FfbDevice = "other"; Throws(() => profile.Validate(Devices())); });
            Test("Profile rejects out-of-range FFB gain", () => { var profile = Profile(); profile.FfbGain = 101; Throws(() => profile.Validate(Devices())); });
            Test("Profile rejects invalid calibration including NaN", () => { var profile = Profile(); profile.Bindings[0].Maximum = Double.NaN; Throws(() => profile.Validate(Devices())); });
            Test("Profile accepts a complete wheel mapping", () => { var profile = Profile(); profile.EnableFfb = true; profile.FfbDevice = "wheel"; profile.Validate(Devices()); });
            Test("HID interface paths are converted to exact instance IDs", () => {
                Assert(DirectInput.HidInstanceId(@"\\?\hid#vid_346e&pid_001e&mi_02#d&123&0&0000#{12345678-0000-0000-0000-000000000000}") == @"HID\VID_346E&PID_001E&MI_02\D&123&0&0000", "Wrong HID identity");
                Assert(DirectInput.HidInstanceId(@"\\?\root#virtual#1#{guid}") == "", "Non-HID identity accepted");
            });
            Test("Native DirectInput structures match the Windows x64 ABI", () => {
                Assert(Marshal.SizeOf(typeof(DiInstance)) == 1100 && Marshal.SizeOf(typeof(DiObject)) == 576 && Marshal.SizeOf(typeof(DiCaps)) == 44, "Device ABI mismatch");
                Assert(Marshal.SizeOf(typeof(DiEffectDescription)) == 80 && Marshal.SizeOf(typeof(DiFormat)) == 32 && Marshal.SizeOf(typeof(DiObjectFormat)) == 24, "Pointer alignment mismatch");
            });
            Test("Constant FFB preserves negative signed magnitude", () => {
                var sink = new FakeForce(); var router = new FfbRouter(sink); router.Process(1, Definition(1));
                byte[] packet = { 0x15, 1, 0, 0 }; Put16(packet, 2, -7500); router.Process(5, packet);
                Assert(sink.Last.Constant == -7500 && sink.Starts == 0, "Signed force was corrupted or started early");
                router.Process(10, new byte[] { 0x1a, 1, 1, 1 }); Assert(sink.Starts == 1 && sink.Iterations == 1, "Effect did not start");
            });
            Test("Finite duration and infinite duration use DirectInput units", () => {
                var sink = new FakeForce(); var router = new FfbRouter(sink); byte[] packet = Definition(1); Put16(packet, 3, 125); router.Process(1, packet);
                Assert(sink.Last.Duration == 125000, "Milliseconds not converted");
                Put16(packet, 3, 65535); router.Process(1, packet); Assert(sink.Last.Duration == UInt32.MaxValue, "Infinite duration lost");
            });
            Test("Spring and damper coefficients preserve negative values", () => {
                var sink = new FakeForce(); var router = new FfbRouter(sink); router.Process(1, Definition(9));
                var packet = new byte[15]; packet[0] = 0x13; packet[1] = 1;
                Put16(packet, 3, -100); Put16(packet, 5, -2000); Put16(packet, 7, -3000); Put16(packet, 9, 8000); Put16(packet, 11, 9000); router.Process(3, packet);
                Assert(sink.Last.Center == -100 && sink.Last.PositiveCoefficient == -2000 && sink.Last.NegativeCoefficient == -3000 && sink.Last.PositiveSaturation == 8000, "Condition values corrupted");
            });
            Test("Y-axis conditions do not affect steering", () => {
                var sink = new FakeForce(); var router = new FfbRouter(sink); router.Process(1, Definition(8)); int updates = sink.Updates;
                var packet = new byte[15]; packet[1] = 1; packet[2] = 1; router.Process(3, packet); Assert(sink.Updates == updates, "Y condition forwarded to wheel");
            });
            Test("Periodic FFB converts period and signed offset", () => {
                var sink = new FakeForce(); var router = new FfbRouter(sink); router.Process(1, Definition(4)); var packet = new byte[12]; packet[1] = 1;
                Put16(packet, 2, 1000); Put16(packet, 4, -500); Put16(packet, 6, 9000); Put32(packet, 8, 20); router.Process(4, packet);
                Assert(sink.Last.Magnitude == 1000 && sink.Last.Offset == -500 && sink.Last.Period == 20000 && sink.Last.Phase == 9000, "Periodic parameters corrupted");
            });
            Test("Ramp FFB retains both signed endpoints", () => {
                var sink = new FakeForce(); var router = new FfbRouter(sink); router.Process(1, Definition(2)); var packet = new byte[6]; packet[1] = 1;
                Put16(packet, 2, -1000); Put16(packet, 4, 2000); router.Process(6, packet); Assert(sink.Last.RampStart == -1000 && sink.Last.RampEnd == 2000, "Ramp sign lost");
            });
            Test("Envelope timing uses microseconds without overflow", () => {
                var sink = new FakeForce(); var router = new FfbRouter(sink); router.Process(1, Definition(1)); var packet = new byte[14]; packet[1] = 1;
                Put16(packet, 2, 100); Put16(packet, 4, 200); Put32(packet, 6, 50); Put32(packet, 10, UInt32.MaxValue); router.Process(2, packet);
                Assert(sink.Last.Envelope && sink.Last.AttackTime == 50000 && sink.Last.FadeTime == UInt32.MaxValue, "Envelope time overflow");
            });
            Test("FFB device gain is normalized", () => { var sink = new FakeForce(); new FfbRouter(sink).Process(13, new byte[] { 0x1d, 255 }); Assert(sink.DeviceGain == 10000, "Gain normalization failed"); });
            Test("Stop, free, reset and actuator commands are routed", () => {
                var sink = new FakeForce(); var router = new FfbRouter(sink); router.Process(1, Definition(1)); router.Process(10, new byte[] { 0x1a, 1, 3, 1 });
                router.Process(11, new byte[] { 0x1b, 1 }); router.Process(12, new byte[] { 0x1c, 2 });
                Assert(sink.Stops == 1 && sink.Frees == 1 && sink.Command == 2, "Lifecycle command lost");
                Throws(() => router.Process(10, new byte[] { 0x1a, 1, 1, 1 }));
            });
            Test("An infinite repeat does not become zero iterations", () => {
                var sink = new FakeForce(); var router = new FfbRouter(sink); router.Process(1, Definition(1)); router.Process(10, new byte[] { 0x1a, 1, 2, 0 });
                Assert(sink.Iterations == UInt32.MaxValue && sink.Solo, "Loop count lost");
            });
            Test("Two-axis forces project onto steering", () => {
                var sink = new FakeForce(); var router = new FfbRouter(sink); var packet = Definition(1); packet[13] = 3;
                Put16(packet, 14, -10000); Put16(packet, 16, 0); router.Process(1, packet); Assert(sink.Last.Projection == -1, "Cartesian projection wrong");
            });
            foreach (int report in new[] { 1, 2, 3, 4, 5, 6, 10 })
            { int type = report; Test("Truncated FFB report " + type + " is rejected", () => Throws(() => new FfbRouter(new FakeForce()).Process(type, new byte[] { 0x10, 1 }))); }
            Test("Custom force is rejected explicitly", () => Throws(() => new FfbRouter(new FakeForce()).Process(1, Definition(12))));
            Test("Invalid effect IDs are rejected", () => Throws(() => new FfbRouter(new FakeForce()).Process(5, new byte[] { 0x15, 0, 1, 0 })));
            Test("Unsupported reports cannot silently drive the wheel", () => Throws(() => new FfbRouter(new FakeForce()).Process(7, new byte[] { 0x17, 1, 0, 0 })));
            Test("Hiding restores only the entries this session added", () => {
                using (var driver = new FakeHide())
                {
                    driver.Active = true; driver.Black.Add("HID\\EXISTING"); driver.White.Add("EXISTING_APP");
                    var recovery = HideSession.Prepare(driver, new[] { "HID\\WHEEL", "HID\\HGP" }, "OUR_APP", null);
                    HideSession.Apply(driver, recovery); Assert(driver.Black.Count == 3 && driver.White.Count == 2, "Session rules missing");
                    HideSession.Restore(driver, recovery);
                    Assert(driver.Black.SequenceEqual(new[] { "HID\\EXISTING" }) && driver.White.SequenceEqual(new[] { "EXISTING_APP" }) && driver.Active, "Existing rules were lost");
                }
            });
            Test("An initially inactive empty configuration becomes inactive again", () => {
                using (var driver = new FakeHide()) { var recovery = HideSession.Prepare(driver, new[] { "HID\\WHEEL" }, "OUR_APP", null); HideSession.Apply(driver, recovery); HideSession.Restore(driver, recovery); Assert(!driver.Active && driver.Black.Count == 0 && driver.White.Count == 0, "Original visibility not restored"); }
            });
            Test("Other applications' concurrent additions survive recovery", () => {
                using (var driver = new FakeHide()) { var recovery = HideSession.Prepare(driver, new[] { "HID\\WHEEL" }, "OUR_APP", null); HideSession.Apply(driver, recovery); driver.Black.Add("HID\\OTHER"); driver.White.Add("OTHER_APP"); HideSession.Restore(driver, recovery); Assert(driver.Active && driver.Black.SequenceEqual(new[] { "HID\\OTHER" }) && driver.White.Contains("OTHER_APP"), "Concurrent rules overwritten"); }
            });
            Test("Partial hiding failure can restore its earlier whitelist change", () => {
                using (var driver = new FakeHide()) { var recovery = HideSession.Prepare(driver, new[] { "HID\\WHEEL" }, "OUR_APP", null); driver.FailOnce = 2051; Throws(() => HideSession.Apply(driver, recovery)); HideSession.Restore(driver, recovery); Assert(driver.White.Count == 0 && driver.Black.Count == 0 && !driver.Active, "Partial hiding was not rolled back"); }
            });
            Test("Inverse-mode configurations are not overwritten", () => { using (var driver = new FakeHide { Inverse = true }) Throws(() => HideSession.Prepare(driver, new[] { "HID\\WHEEL" }, "APP", null)); });
            Test("Existing inactive hiding rules need user review", () => { using (var driver = new FakeHide()) { driver.Black.Add("HID\\OTHER"); Throws(() => HideSession.Prepare(driver, new[] { "HID\\WHEEL" }, "APP", null)); Assert(!driver.Active, "Unrelated rules activated"); } });
            Test("Non-HID device identities cannot be hidden", () => { using (var driver = new FakeHide()) Throws(() => HideSession.Prepare(driver, new[] { "USB\\ROOT_HUB30" }, "APP", null)); });
            Test("MULTI_SZ preserves Unicode and has a double terminator", () => {
                byte[] data = HideDriver.EncodeMultiString(new[] { "HID\\ONE", "HID\\游戏", "hid\\one" });
                Assert(data.Skip(data.Length - 4).All(b => b == 0) && HideDriver.DecodeMultiString(data).Count == 2, "MULTI_SZ encoding failed");
                Assert(HideDriver.EncodeMultiString(new string[0]).Length == 4, "Empty MULTI_SZ missing terminators");
            });
            Test("Recovery does not trust a reused process ID", () => { using (var process = Process.GetCurrentProcess()) Assert(!HideSession.ParentIsAlive(new HideRecovery { ProcessId = process.Id, ProcessStartTicks = 1 }), "PID reuse ignored"); });
            Test("Profile and hiding recovery records survive atomic replacement", () => {
                string directory = Path.Combine(Path.GetTempPath(), "FHRigBridge-tests-" + Guid.NewGuid().ToString("N")); string path = Path.Combine(directory, "state.json");
                try { Store.Write(path, Profile()); var copy = Store.Read<BridgeProfile>(path); copy.FfbGain = 40; Store.Write(path, copy); Assert(Store.Read<BridgeProfile>(path).FfbGain == 40, "Atomic replacement lost state"); }
                finally { if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); if (Directory.Exists(directory)) Directory.Delete(directory); }
            });
        }
        private static Binding Axis(string device, int source, int target) { return new Binding { DeviceId = device, InputKind = "Axis", InputIndex = source, OutputKind = "Axis", OutputIndex = target }; }
        private static BridgeProfile Profile() { return new BridgeProfile { Devices = new List<string> { "wheel" }, Bindings = new List<Binding> { Axis("wheel", 0, 0) } }; }
        private static List<PhysicalDevice> Devices() { return new List<PhysicalDevice> { new PhysicalDevice { Id = "wheel", Name = "Wheel", HidId = "HID\\WHEEL", ForceFeedback = true, Controls = new List<InputControl> { new InputControl { Kind = "Axis", Index = 0 } } } }; }
        private static byte[] Definition(int type) { var packet = new byte[18]; packet[0] = 0x11; packet[1] = 1; packet[2] = (byte)type; Put16(packet, 3, 65535); packet[11] = 255; packet[13] = 1; return packet; }
        private static void Put16(byte[] packet, int index, int value) { packet[index] = (byte)value; packet[index + 1] = (byte)(value >> 8); }
        private static void Put32(byte[] packet, int index, uint value) { for (int i = 0; i < 4; i++) packet[index + i] = (byte)(value >> (i * 8)); }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Throws(Action action) { bool threw = false; try { action(); } catch (Exception) { threw = true; } Assert(threw, "Expected operation to fail"); }
        private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
        private sealed class FakeForce : IForceSink
        {
            public ForceEffect Last; public int Starts, Stops, Frees, Command, DeviceGain, Updates; public uint Iterations; public bool Solo;
            public void Update(ForceEffect effect) { Last = effect; Updates++; }
            public void Start(int id, uint iterations, bool solo) { Starts++; Iterations = iterations; Solo = solo; }
            public void Free(int id) { Frees++; } public void Stop(int id) { Stops++; } public void Control(int command) { Command = command; }
            public void Gain(int gain) { DeviceGain = gain; } public void Dispose() { }
        }
        private sealed class FakeHide : IHideDriver
        {
            public List<string> White = new List<string>(), Black = new List<string>(); public bool Active, Inverse; public int FailOnce;
            public List<string> GetList(int function) { return new List<string>(function == 2048 ? White : Black); }
            public void SetList(int function, IEnumerable<string> entries)
            { if (FailOnce == function) { FailOnce = 0; throw new IOException("Simulated HidHide failure"); } var values = entries.Distinct(StringComparer.OrdinalIgnoreCase).ToList(); if (function == 2049) White = values; else Black = values; }
            public bool GetFlag(int function) { return function == 2054 ? Inverse : Active; }
            public void SetFlag(int function, bool value) { if (function == 2053) Active = value; else Inverse = value; }
            public void Dispose() { }
        }
    }
}
