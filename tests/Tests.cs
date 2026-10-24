using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FHMozaFix.Tests
{
    // No test framework or package download is needed to rebuild this portable tool.
    // Every device-changing test uses the fake backend. Tests never invoke PnPUtil.
    public static class Program
    {
        private static int passed;
        public static int Main()
        {
            try { Run().GetAwaiter().GetResult(); Console.WriteLine("PASS: " + passed + " tests. No hardware was changed."); return 0; }
            catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        }

        private static async Task Run()
        {
            Test("Dedicated USB controllers are discoverable", () => {
                var f = new Fixture();
                Assert(f.Order.Count == 3 && f.Order.All(c => c.Allowed), "Expected three safe USB controllers");
            });
            Test("Composite MOZA includes its serial configuration port and driver components", () => {
                var f = new Fixture();
                var root = f.Backend.Nodes[0]; root.ClassName = "USB"; root.Service = "usbccgp";
                f.Backend.Nodes.Add(new DeviceNode { Id = @"USB\VID_346E&PID_0001&MI_00\PORT", ParentId = root.Id,
                    ClassName = "Ports", Service = "usbser" });
                f.Backend.Nodes.Add(new DeviceNode { Id = @"SWD\DRIVERENUM\VBUS_SWC1&123", ParentId = f.Backend.Nodes[1].Id,
                    ClassName = "SoftwareComponent", HardwareIds = new[] { @"SWC\VEN_346E&DEV_FFF1&REV_01" } });
                Assert(DeviceSafety.Discover(f.Backend.Nodes).All(c => c.Allowed), "MOZA config port/component was blocked");
            });
            foreach (string dangerous in new[] { "Keyboard", "Mouse", "DiskDrive", "Net", "USB", "SoftwareComponent" })
            {
                string deviceClass = dangerous;
                Test("Shared " + deviceClass + " function is excluded", () => {
                    var f = new Fixture();
                    f.Backend.Nodes.Add(new DeviceNode { Id = "UNRELATED", ParentId = f.Order[0].TargetId, ClassName = deviceClass });
                    Assert(!DeviceSafety.Discover(f.Backend.Nodes).First(c => c.TargetId == f.Order[0].TargetId).Allowed, "Unsafe composite was allowed");
                });
            }
            Test("Keyboard HID collection is excluded even under HIDClass", () => {
                var f = new Fixture();
                f.Backend.Nodes.Add(new DeviceNode { Id = "HID\\KEYBOARD", ParentId = f.Order[0].TargetId, ClassName = "HIDClass",
                    HardwareIds = new[] { "HID_DEVICE_SYSTEM_KEYBOARD" } });
                Assert(!DeviceSafety.Discover(f.Backend.Nodes).First(c => c.TargetId == f.Order[0].TargetId).Allowed, "Keyboard collection was allowed");
            });
            Test("Protected compatible IDs are checked as well", () => {
                var f = new Fixture();
                f.Backend.Nodes.Add(new DeviceNode { Id = "HID\\MOUSE", ParentId = f.Order[0].TargetId, ClassName = "HIDClass",
                    CompatibleIds = new[] { "HID_DEVICE_SYSTEM_MOUSE" } });
                Assert(!DeviceSafety.Discover(f.Backend.Nodes).First(c => c.TargetId == f.Order[0].TargetId).Allowed, "Mouse compatible ID was allowed");
            });
            Test("Hub drivers are never accepted as reconnect targets", () => {
                var f = new Fixture(); f.Backend.Nodes[0].Service = "USBHUB3";
                Assert(!DeviceSafety.Discover(f.Backend.Nodes).First(c => c.TargetId == f.Order[0].TargetId).Allowed, "Hub was allowed");
            });
            Test("Virtual controllers are shown but not restarted", () => {
                var f = new Fixture(); f.Backend.Nodes.Add(new DeviceNode { Id = @"HID\VIRTUAL\1", ClassName = "HIDClass",
                    HardwareIds = new[] { "HID_DEVICE_SYSTEM_GAME" }, StatusKnown = true, Started = true });
                Assert(!DeviceSafety.Discover(f.Backend.Nodes).First(c => c.TargetId == @"HID\VIRTUAL\1").Allowed, "Virtual controller was allowed");
            });
            Test("Multiple game collections on one USB device form one reconnect target", () => {
                var f = new Fixture();
                f.Backend.Nodes.Add(new DeviceNode { Id = @"HID\SECOND\1", ParentId = f.Order[0].TargetId, ClassName = "HIDClass",
                    HardwareIds = new[] { "HID_DEVICE_SYSTEM_GAME" }, StatusKnown = true, Started = true });
                var groups = DeviceSafety.Discover(f.Backend.Nodes);
                Assert(groups.Count == 3 && groups.First(c => c.TargetId == f.Order[0].TargetId).GameIds.Length == 2, "Controller collections were not grouped");
            });
            foreach (string id in new[] { "USB\\ROOT_HUB30\\ABC", "USB\\VID_346E&PID_0001&MI_02\\ABC", "USB\\VID_346E&PID_0001\\A*", "USB\\VID_346E&PID_0001\\A\" /reboot", "USB\\VID_346E&PID_0001\\A\nB" })
            {
                string invalid = id;
                Test("Reject unsafe/nonphysical instance ID " + invalid.Replace("\n", "\\n"), () => Assert(!DeviceSafety.IsPhysicalUsb(invalid), "Unsafe ID accepted"));
            }
            await AsyncTest("All devices disconnect before reconnecting in the selected order", async () => {
                var f = new Fixture();
                await f.Run(0);
                Assert(f.Backend.Changes.SequenceEqual(new[] { "-Shifter", "-Pedals", "-Wheel", "+Wheel", "+Pedals", "+Shifter" }), "Wrong reconnect order");
                Assert(!f.Store.Exists && f.Backend.Nodes.All(n => n.Healthy), "Devices or recovery record were left behind");
            });
            await AsyncTest("Wheel-only mode pauses with only the wheel connected", async () => {
                var f = new Fixture(); int pauses = 0;
                await f.Engine.RunAsync(f.Order, 0, 1, (index, controller) => {
                    pauses++; Assert(index == 0 && controller.Name == "Wheel", "Wrong pause position");
                    Assert(f.Backend.Nodes[0].Healthy && !f.Backend.Nodes[2].Healthy && !f.Backend.Nodes[4].Healthy, "Accessories were connected during wheel pause");
                    Assert(f.Store.Read().Count == 2, "Wheel should be removed from pending recovery");
                    return Task.FromResult(true);
                }, CancellationToken.None);
                Assert(pauses == 1, "Expected one wheel-only pause");
            });
            await AsyncTest("Manual mode pauses before each accessory", async () => {
                var f = new Fixture(); int pauses = 0;
                await f.Engine.RunAsync(f.Order, 0, 2, (index, controller) => { pauses++; return Task.FromResult(true); }, CancellationToken.None);
                Assert(pauses == 2, "Expected two pauses");
            });
            await AsyncTest("A failed disable that already changed hardware is recovered", async () => {
                var f = new Fixture(); f.Backend.FailOnce = "-Pedals";
                await Throws(() => f.Run(0));
                Assert(f.Backend.Nodes.All(n => n.Healthy) && !f.Store.Exists, "Partial disable was not recovered");
                Assert(!f.Backend.Changes.Contains("-Wheel"), "Sequence continued after failure");
            });
            await AsyncTest("A failed enable restores every remaining controller", async () => {
                var f = new Fixture(); f.Backend.FailOnce = "+Wheel";
                await Throws(() => f.Run(0));
                Assert(f.Backend.Nodes.All(n => n.Healthy) && !f.Store.Exists, "Failed enable was not recovered");
            });
            await AsyncTest("Cancellation halfway through disconnection restores all controllers", async () => {
                var f = new Fixture(); var cancellation = new CancellationTokenSource();
                f.Backend.AfterChange = change => { if (change == "-Pedals") cancellation.Cancel(); };
                await Throws(() => f.Engine.RunAsync(f.Order, 0, 0, null, cancellation.Token));
                Assert(f.Backend.Nodes.All(n => n.Healthy) && !f.Store.Exists, "Cancellation did not restore controllers");
            });
            await AsyncTest("Cancellation during the wheel-only pause restores accessories", async () => {
                var f = new Fixture(); var cancellation = new CancellationTokenSource();
                await Throws(() => f.Engine.RunAsync(f.Order, 0, 1, (index, controller) => {
                    cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); return Task.FromResult(true);
                }, cancellation.Token));
                Assert(f.Backend.Nodes.All(n => n.Healthy) && !f.Store.Exists, "Paused sequence was not recovered");
            });
            await AsyncTest("Disabled devices are rejected before any mutation", async () => {
                var f = new Fixture(); f.Backend.Nodes[0].Started = false; f.Backend.Nodes[0].Problem = 22;
                await Throws(() => f.Run(0));
                Assert(f.Backend.Changes.Count == 0 && !f.Store.Exists, "Originally disabled device was changed");
            });
            await AsyncTest("Pending recovery prevents a new reconnect", async () => {
                var f = new Fixture(); f.Store.Write(f.Order);
                await Throws(() => f.Run(0));
                Assert(f.Backend.Changes.Count == 0, "A session started with pending recovery");
            });
            await AsyncTest("Duplicate targets are rejected before any mutation", async () => {
                var f = new Fixture(); f.Order.Add(f.Order[0]);
                await Throws(() => f.Run(0)); Assert(f.Backend.Changes.Count == 0, "Duplicate target was changed");
            });
            await AsyncTest("Recovery file must be durable before the first disable", async () => {
                var f = new Fixture(); f.Store.FailNextWrite = true;
                await Throws(() => f.Run(0)); Assert(f.Backend.Changes.Count == 0, "Disable happened before recovery record was saved");
            });
            await AsyncTest("Journal update failure after enable still recovers accessories", async () => {
                var f = new Fixture(); f.Backend.AfterChange = change => { if (change == "+Wheel") f.Store.FailNextWrite = true; };
                await Throws(() => f.Run(0));
                Assert(f.Backend.Nodes.All(n => n.Healthy) && !f.Store.Exists, "Journal write failure lost recovery");
            });
            await AsyncTest("A changed composite layout is detected before disabling it", async () => {
                var f = new Fixture();
                f.Backend.AfterChange = change => {
                    if (change == "-Shifter") f.Backend.Nodes.Add(new DeviceNode { Id = "NEW_STORAGE", ParentId = f.Order[0].TargetId, ClassName = "DiskDrive" });
                };
                await Throws(() => f.Run(0));
                Assert(!f.Backend.Changes.Contains("-Wheel"), "Changed unsafe device was disabled");
            });
            await AsyncTest("Crash recovery enables recorded disabled controllers", async () => {
                var f = new Fixture(); f.Store.Write(f.Order);
                foreach (var node in f.Backend.Nodes) { node.Started = false; node.Problem = 22; }
                Assert(await f.Engine.RestoreAsync(), "Recovery did not complete");
                Assert(!f.Store.Exists && f.Backend.Nodes.All(n => n.Healthy), "Recovery left disabled controllers");
            });
            await AsyncTest("Missing devices remain in recovery while other devices are restored", async () => {
                var f = new Fixture(); f.Store.Write(f.Order);
                f.Backend.Nodes.RemoveAll(n => n.Id == f.Order[0].TargetId || n.ParentId == f.Order[0].TargetId);
                Assert(!await f.Engine.RestoreAsync(), "Missing device should keep recovery pending");
                Assert(f.Store.Read().Count == 1 && f.Store.Read()[0].Name == "Wheel", "Wrong recovery records retained");
            });
            await AsyncTest("Device identity changes are rejected during recovery", async () => {
                var f = new Fixture(); f.Store.Write(new List<Controller> { f.Order[0] });
                f.Backend.Nodes[0].HardwareIds = new[] { "CHANGED_IDENTITY" };
                Assert(!await f.Engine.RestoreAsync() && f.Backend.Changes.Count == 0 && f.Store.Exists, "Changed identity was enabled");
            });
            await AsyncTest("An absent game interface prevents false recovery success", async () => {
                var f = new Fixture(); f.Store.Write(new List<Controller> { f.Order[0] });
                f.Backend.Nodes.RemoveAt(1);
                Assert(!await f.Engine.RestoreAsync() && f.Store.Exists, "Missing game interface was treated as recovered");
            });
            Test("Recovery JSON can be atomically replaced, loaded and cleared", () => {
                string directory = Path.Combine(Path.GetTempPath(), "FHMozaFix-tests-" + Guid.NewGuid().ToString("N"));
                string path = Path.Combine(directory, "recovery.json");
                try
                {
                    var store = new RecoveryStore(path); var f = new Fixture();
                    store.Write(f.Order); Assert(store.Read().Count == 3, "Initial journal was not saved");
                    store.Write(new List<Controller> { f.Order[0] }); Assert(store.Read().Count == 1, "Journal replacement failed");
                    store.Write(new List<Controller>()); Assert(!store.Exists, "Journal was not cleared");
                    File.WriteAllText(path, "[{\"TargetId\":\"USB\\\\ROOT_HUB30\\\\123\"}]");
                    bool rejected = false; try { store.Read(); } catch (InvalidDataException) { rejected = true; }
                    Assert(rejected && File.Exists(path), "Invalid recovery record was not preserved and rejected");
                }
                finally
                {
                    if (File.Exists(path)) File.Delete(path);
                    if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
                    if (Directory.Exists(directory)) Directory.Delete(directory); // Empty test directory only.
                }
            });
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
        private static async Task AsyncTest(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS " + name); }
        private static async Task Throws(Func<Task> action)
        {
            bool threw = false; try { await action(); } catch (Exception) { threw = true; }
            Assert(threw, "Expected the operation to fail");
        }

        private sealed class Fixture
        {
            public readonly FakeBackend Backend = new FakeBackend();
            public readonly MemoryRecovery Store = new MemoryRecovery();
            public readonly ReconnectEngine Engine;
            public readonly List<Controller> Order;
            public Fixture()
            {
                string[] names = { "Wheel", "Pedals", "Shifter" };
                for (int i = 0; i < names.Length; i++)
                {
                    string id = @"USB\VID_346E&PID_000" + (i + 1) + "\\SERIAL" + i;
                    Backend.Nodes.Add(new DeviceNode { Id = id, Name = names[i], ClassName = "HIDClass", Service = "HidUsb",
                        HardwareIds = new[] { "HW_" + names[i] }, StatusKnown = true, Started = true });
                    Backend.Nodes.Add(new DeviceNode { Id = "HID\\GAME\\" + i, ParentId = id, Name = names[i], Product = names[i], ClassName = "HIDClass",
                        HardwareIds = new[] { "HID_DEVICE_SYSTEM_GAME" }, StatusKnown = true, Started = true });
                }
                Order = DeviceSafety.Discover(Backend.Nodes).OrderBy(c => Array.IndexOf(names, c.Name)).ToList();
                Engine = new ReconnectEngine(Backend, Store, s => { });
            }
            public Task Run(int mode) { return Engine.RunAsync(Order, 0, mode, (index, controller) => Task.FromResult(true), CancellationToken.None); }
        }

        private sealed class FakeBackend : IDeviceBackend
        {
            public readonly List<DeviceNode> Nodes = new List<DeviceNode>();
            public readonly List<string> Changes = new List<string>();
            public string FailOnce;
            public Action<string> AfterChange;
            public List<DeviceNode> Snapshot() { return Nodes.ToList(); }
            public Task SetEnabledAsync(Controller controller, bool enabled)
            {
                string change = (enabled ? "+" : "-") + controller.Name;
                Changes.Add(change);
                foreach (var node in DeviceSafety.Descendants(controller.TargetId, Nodes))
                {
                    node.Started = enabled;
                    node.Problem = enabled ? 0u : 22u;
                }
                if (AfterChange != null) AfterChange(change);
                if (FailOnce == change) { FailOnce = null; throw new IOException("Simulated partial device failure"); }
                return Task.FromResult(true);
            }
            public Task WaitAsync(Controller controller, bool enabled, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                if (enabled && !controller.GameIds.All(id => Nodes.Any(n => n.Id == id && n.Healthy)))
                    throw new TimeoutException("Game interface did not return");
                return Task.FromResult(true);
            }
        }

        private sealed class MemoryRecovery : IRecoveryStore
        {
            private List<Controller> records = new List<Controller>();
            public bool FailNextWrite;
            public bool Exists { get { return records.Count > 0; } }
            public List<Controller> Read() { return records.ToList(); }
            public void Write(List<Controller> devices)
            {
                if (FailNextWrite) { FailNextWrite = false; throw new IOException("Simulated journal write failure"); }
                records = devices.ToList();
            }
        }
    }
}
