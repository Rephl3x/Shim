using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace RigBridge.Tests
{
    public static class TestProgram
    {
        private static int passed;
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length == 4 && args[0] == "--driver-status") { Store.Write(args[1], DriverSetup.Inspect(args[3], UInt32.Parse(args[2]))); return 0; }
            if (args.Length == 1 && args[0] == "--desktop") return DesktopTests.Run();
            try { Run(); Console.WriteLine("PASS: " + passed + " bridge tests. No drivers, hiding, virtual output or physical FFB were used."); return 0; }
            catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        }
        private static void Run()
        {
            PhysicalForceTests.Run(Test);
            Test("Setup draft changes never mutate the active profile", () => {
                var current = Profile(); current.Rig = new RigChoices { Wheel = "wheel" }; var copy = SetupLogic.Clone(current);
                copy.Devices.Clear(); copy.Bindings[0].Invert = true; copy.Rig.Wheel = "changed";
                Assert(current.Devices.Count == 1 && !current.Bindings[0].Invert && current.Rig.Wheel == "wheel", "Draft modified the original");
            });
            Test("Wizard asks for every requested axis and individual gear", () => {
                var devices = SetupDevices(); var rig = new RigChoices { Wheel = "wheel", Pedals = "wheel", Clutch = true, Shifter = "shifter", Gears = 7, Reverse = true, Sequential = "box", Handbrake = "brake" };
                var plan = SetupLogic.Plan(rig, devices);
                Assert(plan.Count == 15 && plan.Count(c => c.Kind == "Axis") == 5 && plan.Count(c => c.Kind == "Button") == 10 && plan.Any(c => c.Name == "Reverse"), "Incomplete guided controls");
                Assert(rig.Selected().Count == 4, "Shared wheel/pedals selected twice");
            });
            Test("Wizard rejects absent wheel and device roles without the required inputs", () => {
                Throws(() => SetupLogic.Plan(new RigChoices(), SetupDevices()));
                Throws(() => SetupLogic.Plan(new RigChoices { Wheel = "box" }, SetupDevices()));
                Throws(() => SetupLogic.Plan(new RigChoices { Wheel = "wheel", Clutch = true }, SetupDevices()));
            });
            Test("Released high pedal is calibrated and automatically inverted", () => {
                var first = new InputState(); var second = new InputState(); first.Axes[2] = 0.9; second.Axes[2] = 0.1;
                var target = new SetupControl { Name = "Brake", Kind = "Axis", DeviceId = "wheel", Output = 2 };
                var binding = SetupLogic.Calibrate(target, SetupDevices()[0], first, second);
                Assert(binding.InputIndex == 2 && binding.Invert && binding.Minimum == 0.1 && binding.Maximum == 0.9, "Incorrect pedal calibration");
                Assert(Mapper.Map(new[] { binding }, new Dictionary<string, InputState> { { "wheel", first } }).Axes[2] == 0, "Released pedal not zero");
                Assert(Mapper.Map(new[] { binding }, new Dictionary<string, InputState> { { "wheel", second } }).Axes[2] == 1, "Full pedal not one");
            });
            Test("Reversed steering endpoints preserve left and right", () => {
                var left = new InputState(); var right = new InputState(); left.Axes[0] = 0.8; right.Axes[0] = 0.2;
                var binding = SetupLogic.Calibrate(new SetupControl { Name = "Steering", DeviceId = "wheel", Output = 0 }, SetupDevices()[0], left, right);
                Assert(binding.InputIndex == 0 && binding.Invert && binding.Deadzone == 0, "Steering direction wrong");
            });
            Test("Axis learning rejects noise and ambiguous combined movement", () => {
                var first = new InputState(); var second = new InputState(); var target = new SetupControl { Name = "Throttle", DeviceId = "wheel", Output = 1 };
                second.Axes[0] = 0.01; Throws(() => SetupLogic.Calibrate(target, SetupDevices()[0], first, second));
                second.Axes[0] = 1; second.Axes[1] = 0.9; Throws(() => SetupLogic.Calibrate(target, SetupDevices()[0], first, second));
            });
            Test("Gear learning ignores previously held buttons and requires one new input", () => {
                var rest = new InputState(); var now = new InputState(); rest.Buttons[4] = now.Buttons[4] = true;
                var target = new SetupControl { Name = "Reverse", DeviceId = "shifter", Output = 7 };
                Assert(SetupLogic.LearnButton(target, SetupDevices()[1], rest, now) == null, "Held gear learned as new");
                now.Buttons[9] = true; var result = SetupLogic.LearnButton(target, SetupDevices()[1], rest, now);
                Assert(result.InputIndex == 9 && result.OutputIndex == 7 && result.Label == "Reverse", "Gear identity lost");
                now.Buttons[10] = true; Throws(() => SetupLogic.LearnButton(target, SetupDevices()[1], rest, now));
            });
            Test("Guided controls cannot reuse one physical axis for throttle and brake", () => {
                var profile = Profile(); SetupLogic.Put(profile, Axis("wheel", 1, 1));
                Throws(() => SetupLogic.Put(profile, Axis("wheel", 1, 2)));
                var replacement = Axis("wheel", 2, 1); SetupLogic.Put(profile, replacement);
                Assert(profile.Bindings.Count == 2 && profile.Bindings.Contains(replacement), "Relearning did not replace target");
            });
            Test("Automatic buttons preserve learned gears and share space across all devices", () => {
                var devices = SetupDevices(); var profile = new BridgeProfile { Devices = devices.Select(d => d.Id).ToList() };
                profile.Bindings.Add(new Binding { Label = "Gear 1", DeviceId = "shifter", InputKind = "Button", InputIndex = 50, OutputKind = "Button", OutputIndex = 0 });
                int omitted = SetupLogic.AddButtons(profile, devices);
                Assert(profile.Bindings.Count == 128 && omitted == 384 && profile.Bindings[0].Label == "Gear 1", "Limit or gear reservation wrong");
                Assert(devices.All(d => profile.Bindings.Any(b => b.DeviceId == d.Id)), "An accessory crowded out another device");
                Assert(profile.Bindings.Count(b => b.DeviceId == "shifter" && b.InputIndex == 50) == 1, "Explicit gear was duplicated");
                Assert(profile.Bindings.Select(b => b.OutputIndex).Distinct().Count() == 128, "Output collision");
            });
            Test("Migrating an old bulk button profile frees room for every device", () => {
                var profile = new BridgeProfile(); profile.Bindings.Add(Axis("wheel", 0, 0));
                for (int i = 0; i < 128; i++) profile.Bindings.Add(new Binding { DeviceId = "shifter", InputKind = "Button", InputIndex = i, OutputKind = "Button", OutputIndex = i });
                var plan = SetupLogic.Plan(new RigChoices { Wheel = "wheel", Shifter = "shifter", Gears = 7 }, SetupDevices());
                SetupLogic.KeepRequestedMappings(profile, plan, true);
                Assert(profile.Bindings.Count == 1 && profile.Bindings[0].OutputKind == "Axis", "Generic shifter buttons blocked explicit gear learning/other sources");
                profile.Bindings.Add(new Binding { Label = "Gear 1", DeviceId = "shifter", InputKind = "Button", InputIndex = 3, OutputKind = "Button", OutputIndex = 0 });
                SetupLogic.KeepRequestedMappings(profile, plan, true); Assert(profile.Bindings.Count == 2, "Explicitly learned gear was lost on Back");
                SetupLogic.KeepRequestedMappings(profile, plan, false); Assert(profile.Bindings.Count == 0, "Relearn option did not reset draft mappings");
            });
            Test("Automatic hats preserve mappings and stop at four outputs", () => {
                var devices = SetupDevices(); foreach (var device in devices) for (int i = 0; i < 4; i++) device.Controls.Add(new InputControl { Kind = "POV", Index = i });
                var profile = new BridgeProfile { Devices = devices.Select(d => d.Id).ToList() }; SetupLogic.AddHats(profile, devices); SetupLogic.AddHats(profile, devices);
                Assert(profile.Bindings.Count == 4 && profile.Bindings.Select(b => b.OutputIndex).Distinct().Count() == 4, "Hat limit/collision");
            });
            Test("Driver detection does not treat an incomplete layout as ready", () => {
                var status = ReadyVirtual(); status.Buttons = 8; status.Hats = 0;
                Assert(!status.LayoutReady && status.Summary.Contains("needs configuration") && status.Summary.Contains("8/128"), "Misleading ready status");
                status = ReadyVirtual(); status.Effects = 10; Assert(!status.LayoutReady, "Missing FFB effect ignored");
                status = ReadyVirtual(); status.State = 2; Assert(!status.LayoutReady && status.Summary.Contains("in use"), "Busy device called ready");
            });
            Test("An active installer blocks final readiness even if the driver is available", () => {
                var status = new SetupStatus { Virtual = ReadyVirtual(), HideReady = true, VjoyInstalling = true };
                Assert(!status.Ready && status.Summary.Contains("installer is still running"), "Incomplete installation passed final gate");
                status.VjoyInstalling = false; Assert(status.Ready, "Completed setup rejected");
            });
            Test("Automatic configuration targets one slot and explicitly enables standard effects", () => {
                var args = DriverSetup.ConfigureArguments(3);
                Assert(args.StartsWith("3 -f -a ") && args.Contains("-b 128 -p 4") && args.EndsWith("Spr Dm Inr Fric") && !args.Contains(" -r") && !args.Contains(" -d"), "Unsafe/incomplete configuration command");
                Throws(() => DriverSetup.ConfigureArguments(0)); Throws(() => DriverSetup.ConfigureArguments(17));
            });
            Test("Windows argument quoting preserves spaces quotes and trailing backslashes", () => {
                Assert(DriverSetup.Quote("C:\\Rig Folder\\") == "\"C:\\Rig Folder\\\\\"", "Trailing backslash quoting failed");
                Assert(DriverSetup.Quote("a\"b") == "\"a\\\"b\"", "Embedded quote failed");
            });
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
            Test("Native HidHide write acknowledgements are not treated as output data", () => {
                var io = new FakeHideControl();
                using (var driver = new HideDriver(io.Send))
                {
                    driver.SetList(2049, new[] { "OUR_APP" }); driver.SetList(2051, new[] { "HID\\WHEEL" }); driver.SetFlag(2053, true);
                    Assert(driver.GetList(2048).SequenceEqual(new[] { "OUR_APP" }) && driver.GetList(2050).SequenceEqual(new[] { "HID\\WHEEL" }) && driver.GetFlag(2052), "Acknowledged writes did not persist");
                    Assert(io.WritesWithoutOutput == 3, "Setter used an output buffer");
                }
            });
            Test("Real HidHide adapter can apply and restore with input-length write replies", () => {
                var io = new FakeHideControl(); io.White.Add("OTHER_APP");
                using (var driver = new HideDriver(io.Send))
                {
                    var recovery = HideSession.Prepare(driver, new[] { "HID\\WHEEL", "HID\\HGP" }, "OUR_APP", null);
                    HideSession.Apply(driver, recovery); Assert(io.Active && io.Black.Count == 2 && io.White.Count == 2, "Apply did not complete");
                    HideSession.Restore(driver, recovery);
                    Assert(!io.Active && io.Black.Count == 0 && io.White.SequenceEqual(new[] { "OTHER_APP" }), "Adapter rollback failed");
                    HideSession.Restore(driver, recovery); Assert(!io.Active && io.White.SequenceEqual(new[] { "OTHER_APP" }), "Rollback is not idempotent");
                }
            });
            Test("Native adapter recovers a partial whitelist-only session", () => {
                var io = new FakeHideControl(); io.White.Add("OTHER_APP");
                using (var driver = new HideDriver(io.Send))
                {
                    var recovery = HideSession.Prepare(driver, new[] { "HID\\WHEEL" }, "OUR_APP", null);
                    driver.SetList(2049, io.White.Concat(recovery.AddedApplications));
                    HideSession.Restore(driver, recovery);
                    Assert(io.Black.Count == 0 && io.White.SequenceEqual(new[] { "OTHER_APP" }) && !io.Active, "Partial write left stale rules");
                }
            });
            Test("HidHide list reads query their size and handle more than the old fixed capacity", () => {
                var io = new FakeHideControl(); io.White.Add(new string('a', 70000));
                using (var driver = new HideDriver(io.Send))
                {
                    Assert(driver.GetList(2048).Single().Length == 70000 && io.SizeQueries == 1, "List was truncated or size was not queried");
                    Assert(driver.GetList(2050).Count == 0, "Empty list rejected");
                }
            });
            Test("HidHide list growth between query and read is retried", () => {
                var io = new FakeHideControl { GrowNextRead = true };
                using (var driver = new HideDriver(io.Send))
                    Assert(driver.GetList(2050).SequenceEqual(new[] { "HID\\CONCURRENT" }) && io.SizeQueries == 2, "List growth was not retried");
            });
            Test("HidHide rejects implausible sizes before allocating output memory", () => {
                using (var odd = new HideDriver((function, input, output) => 3)) Throws(() => odd.GetList(2050));
                using (var oversized = new HideDriver((function, input, output) => UInt32.MaxValue)) Throws(() => oversized.GetList(2048));
            });
            Test("HidHide still checks actual read buffer lengths", () => {
                using (var oversized = new HideDriver((function, input, output) => 2)) Throws(() => oversized.GetFlag(2052));
                using (var empty = new HideDriver((function, input, output) => 0)) Throws(() => empty.GetFlag(2054));
            });
            Test("HidHide write failures still propagate", () => {
                using (var driver = new HideDriver((function, input, output) => { throw new Win32Exception(5, "Denied"); }))
                { Throws(() => driver.SetList(2049, new[] { "OUR_APP" })); Throws(() => driver.SetFlag(2053, true)); }
            });
            Test("HidHide refuses to report recovery success if acknowledged writes were ignored", () => {
                var io = new FakeHideControl();
                using (var driver = new HideDriver(io.Send))
                {
                    var recovery = HideSession.Prepare(driver, new[] { "HID\\WHEEL" }, "OUR_APP", null);
                    HideSession.Apply(driver, recovery); io.IgnoreWrites = true;
                    Throws(() => HideSession.Restore(driver, recovery));
                    Assert(io.Black.Contains("HID\\WHEEL") && io.White.Contains("OUR_APP"), "Test did not retain rules");
                    io.IgnoreWrites = false; HideSession.Restore(driver, recovery); Assert(io.Black.Count == 0 && !io.Active, "Retry failed");
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
            Test("An idle owning UI can recover its stopped worker without closing the app", () => {
                using (var process = Process.GetCurrentProcess())
                {
                    var recovery = new HideRecovery { ProcessId = process.Id, ProcessStartTicks = process.StartTime.ToUniversalTime().Ticks };
                    Assert(!HideSession.CanRecover(recovery, false), "A live worker was allowed recovery");
                    Assert(HideSession.CanRecover(recovery, true), "An explicitly stopped owning worker was blocked");
                    recovery.ProcessStartTicks = 1; Assert(HideSession.CanRecover(recovery, false), "An old session with a reused PID could not recover");
                }
            });
            Test("Profile and hiding recovery records survive atomic replacement", () => {
                string directory = Path.Combine(Path.GetTempPath(), "FHRigBridge-tests-" + Guid.NewGuid().ToString("N")); string path = Path.Combine(directory, "state.json");
                try { Store.Write(path, Profile()); var copy = Store.Read<BridgeProfile>(path); copy.FfbGain = 40; Store.Write(path, copy); Assert(Store.Read<BridgeProfile>(path).FfbGain == 40, "Atomic replacement lost state"); }
                finally { if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); if (Directory.Exists(directory)) Directory.Delete(directory); }
            });
            Test("Saving strength preserves stored mappings instead of saving unrelated UI edits", () => {
                string directory = Path.Combine(Path.GetTempPath(), "FHRigBridge-gain-" + Guid.NewGuid().ToString("N")); string path = Path.Combine(directory, "profile.json");
                try
                {
                    var saved = Profile(); saved.VirtualId = 3; saved.EnableFfb = true; saved.FfbDevice = "wheel"; saved.SetupVersion = 1; Store.Write(path, saved);
                    var editing = SetupLogic.Clone(saved); editing.Bindings.Clear(); editing.VirtualId = 8;
                    Store.SaveStrength(path, 63, editing); var actual = Store.Read<BridgeProfile>(path);
                    Assert(actual.FfbGain == 63 && actual.VirtualId == 3 && actual.Bindings.Count == 1 && actual.EnableFfb && actual.SetupVersion == 1, "Live save overwrote unrelated profile state");
                    Throws(() => Store.SaveStrength(path, 101, editing)); Assert(Store.Read<BridgeProfile>(path).FfbGain == 63, "Invalid gain altered disk profile");
                }
                finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(directory)) Directory.Delete(directory); }
            });
            Test("Day and Night palettes keep body and muted text readable", () => {
                foreach (string mode in new[] { "Day", "Night" })
                {
                    var palette = Palette.For(mode);
                    Assert(Contrast(palette.Text, palette.Surface) >= 4.5 && Contrast(palette.Muted, palette.Surface) >= 4.5, mode + " text contrast too low");
                    Assert(Contrast(palette.Text, palette.Input) >= 4.5 && palette.Night == (mode == "Night"), "Input contrast or mode mismatch");
                }
            });
        }
        private static Binding Axis(string device, int source, int target) { return new Binding { DeviceId = device, InputKind = "Axis", InputIndex = source, OutputKind = "Axis", OutputIndex = target }; }
        private static double Luminance(System.Drawing.Color color)
        {
            Func<byte, double> channel = value => { double part = value / 255.0; return part <= 0.04045 ? part / 12.92 : Math.Pow((part + 0.055) / 1.055, 2.4); };
            return 0.2126 * channel(color.R) + 0.7152 * channel(color.G) + 0.0722 * channel(color.B);
        }
        private static double Contrast(System.Drawing.Color a, System.Drawing.Color b) { double x = Luminance(a), y = Luminance(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }
        private static VirtualStatus ReadyVirtual() { return new VirtualStatus { Slot = 1, Enabled = true, Match = true, State = 1, Axes = Enumerable.Repeat(true, 8).ToArray(), Buttons = 128, Hats = 4, Ffb = true, Effects = 11 }; }
        private static List<PhysicalDevice> SetupDevices()
        {
            var result = new List<PhysicalDevice>();
            foreach (string id in new[] { "wheel", "shifter", "box", "brake" })
            {
                var device = new PhysicalDevice { Id = id, Name = id, HidId = "HID\\" + id, ForceFeedback = id == "wheel" };
                for (int i = 0; i < 128; i++) device.Controls.Add(new InputControl { Kind = "Button", Index = i, Name = "Button " + (i + 1) });
                if (id == "wheel" || id == "brake") for (int i = 0; i < 8; i++) device.Controls.Add(new InputControl { Kind = "Axis", Index = i });
                result.Add(device);
            }
            return result;
        }
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
        private sealed class FakeHideControl
        {
            public List<string> White = new List<string>(), Black = new List<string>();
            public bool Active, Inverse, GrowNextRead, IgnoreWrites;
            public int SizeQueries, WritesWithoutOutput;
            public uint Send(int function, byte[] input, byte[] output)
            {
                if (input != null)
                {
                    Assert(output == null, "Write must not request output"); WritesWithoutOutput++;
                    if (!IgnoreWrites)
                    {
                        if (function == 2049) White = HideDriver.DecodeMultiString(input);
                        else if (function == 2051) Black = HideDriver.DecodeMultiString(input);
                        else if (function == 2053) Active = input[0] != 0;
                        else if (function == 2055) Inverse = input[0] != 0;
                        else throw new InvalidOperationException("Unexpected setter");
                    }
                    return (uint)input.Length; // The driver reports consumed input bytes with no output buffer.
                }
                if (function == 2052 || function == 2054) { output[0] = (byte)((function == 2052 ? Active : Inverse) ? 1 : 0); return 1; }
                var entries = function == 2048 ? White : Black;
                if (output == null) { SizeQueries++; return (uint)HideDriver.EncodeMultiString(entries).Length; }
                if (GrowNextRead) { GrowNextRead = false; entries.Add("HID\\CONCURRENT"); }
                byte[] data = HideDriver.EncodeMultiString(entries);
                if (output.Length < data.Length) throw new Win32Exception(122, "Insufficient buffer");
                Array.Copy(data, output, data.Length); return (uint)data.Length;
            }
        }
    }
}
