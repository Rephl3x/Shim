using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace RigBridge.Tests
{
    // Opt-in smoke check against real WinForms controls and read-only physical input.
    // Never invokes installers, configuration, hiding, virtual output, or FFB.
    public static class DesktopTests
    {
        public static int Run()
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            int result = 1;
            using (var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) })
            {
                host.Shown += (s, e) => { result = RunChecks(); host.Close(); };
                Application.Run(host);
            }
            return result;
        }
        private static int RunChecks()
        {
            string draftPath = Path.Combine(Store.DirectoryPath, "setup-draft.json");
            byte[] previousDraft = File.Exists(draftPath) ? File.ReadAllBytes(draftPath) : null;
            byte[] previousProfile = File.Exists(Store.ProfilePath) ? File.ReadAllBytes(Store.ProfilePath) : null;
            byte[] previousAppearance = File.Exists(Appearance.Path) ? File.ReadAllBytes(Appearance.Path) : null;
            Appearance.Load(); string previousMode = Appearance.Mode;
            try
            {
                MainWindowChecks();
                using (var direct = new DirectInput())
                {
                    var devices = direct.Enumerate().Where(d => !d.Virtual).ToList();
                    var physical = devices.FirstOrDefault(d => d.ForceFeedback && d.Controls.Any(c => c.Kind == "Axis"));
                    if (physical == null) throw new InvalidOperationException("Desktop smoke check needs a connected FFB wheel. Pure tests do not require hardware.");
                    using (var wizard = new SetupWizard(new BridgeProfile(), devices, null, 0))
                    {
                        wizard.ShowInTaskbar = false; wizard.StartPosition = FormStartPosition.Manual; wizard.Location = new Point(-20000, -20000);
                        wizard.Show(); Pump(() => Get<int>(wizard, "page") == 0, "welcome");
                        Get<CheckBox>(wizard, "resume").Checked = false;
                        Click(wizard, "next", 1); Pump(() => Get<SetupStatus>(wizard, "driverStatus") != null && !Get<bool>(wizard, "checking"), "driver probe");
                        Assert(Get<Label>(wizard, "driverInfo").Text.Contains("vJoy"), "Driver page did not show its result");
                        Console.WriteLine("PASS Desktop: welcome and fresh native driver status");
                        Click(wizard, "next", 2);
                        Pick(Get<ComboBox>(wizard, "wheel"), physical.Id);
                        Pick(Get<ComboBox>(wizard, "pedals"), null); Pick(Get<ComboBox>(wizard, "shifter"), null);
                        Pick(Get<ComboBox>(wizard, "handbrake"), null); Pick(Get<ComboBox>(wizard, "sequential"), null);
                        Get<CheckBox>(wizard, "clutch").Checked = false; Get<CheckBox>(wizard, "keepExisting").Checked = false;
                        var extra = Get<CheckedListBox>(wizard, "extras"); for (int i = 0; i < extra.Items.Count; i++) extra.SetItemChecked(i, false);
                        Click(wizard, "next", 3);
                        Pump(() => { var runtime = Get<BridgeRuntime>(wizard, "monitor"); return runtime != null && runtime.Started && runtime.Frames > 2; }, "read-only wheel samples");
                        var monitorBeforeTheme = Get<BridgeRuntime>(wizard, "monitor");
                        Get<Button>(wizard, "themeNight").PerformClick();
                        Assert(Appearance.Mode == "Night" && Object.ReferenceEquals(monitorBeforeTheme, Get<BridgeRuntime>(wizard, "monitor")) && monitorBeforeTheme.Running, "Wizard theme change interrupted live input monitoring");
                        Get<Button>(wizard, "themeDay").PerformClick();
                        Assert(Get<BridgeProfile>(wizard, "draft").Devices.SequenceEqual(new[] { physical.Id }), "Role selection duplicated or omitted the wheel");
                        Get<Button>(wizard, "next").PerformClick(); Pump(() => !Get<bool>(wizard, "navigating"), "required-control validation");
                        Assert(Get<int>(wizard, "page") == 3 && Get<Label>(wizard, "message").Text.Contains("Learn every"), "Unmapped required control was accepted");
                        Console.WriteLine("PASS Desktop: role selection, real input samples, and required mapping gate");
                        int axis = physical.Controls.First(c => c.Kind == "Axis").Index;
                        SetupLogic.Put(Get<BridgeProfile>(wizard, "draft"), new Binding { Label = "Steering", DeviceId = physical.Id, InputKind = "Axis", InputIndex = axis, OutputKind = "Axis", OutputIndex = 0 });
                        Click(wizard, "next", 4);
                        Get<CheckBox>(wizard, "feedback").Checked = false;
                        Click(wizard, "next", 5); Pump(() => Get<SetupStatus>(wizard, "driverStatus") != null && !Get<bool>(wizard, "checking"), "final driver status");
                        Pump(() => Get<Label>(wizard, "live").Text.Contains("LIVE MAPPED INPUTS"), "live mapped inputs");
                        var status = Get<SetupStatus>(wizard, "driverStatus");
                        if (!status.Ready) Assert(!Get<Button>(wizard, "next").Enabled, "Finish was enabled with incomplete drivers");
                        Console.WriteLine("PASS Desktop: feedback preferences, live mapped inputs, and finish readiness gate");
                        Click(wizard, "back", 4); Click(wizard, "back", 3);
                        Pump(() => { var runtime = Get<BridgeRuntime>(wizard, "monitor"); return runtime != null && runtime.Started; }, "monitor restarted on back navigation");
                        Assert(Get<BridgeProfile>(wizard, "draft").Bindings.Any(b => b.OutputKind == "Axis"), "Back navigation lost calibration");
                        Get<Button>(wizard, "cancel").PerformClick(); Pump(() => wizard.IsDisposed, "cancel");
                    }
                    var savedDraft = Store.Read<BridgeProfile>(draftPath);
                    Assert(savedDraft.Bindings.Any(b => b.OutputKind == "Axis") && savedDraft.Rig.Wheel == physical.Id, "Resume draft lost setup");
                    using (var resumed = new SetupWizard(new BridgeProfile(), devices, null, 0))
                    {
                        resumed.ShowInTaskbar = false; resumed.StartPosition = FormStartPosition.Manual; resumed.Location = new Point(-20000, -20000); resumed.Show();
                        Pump(() => Get<CheckBox>(resumed, "resume").Checked, "offer saved setup progress");
                        Assert(Get<CheckBox>(resumed, "resume").Checked, "Resume was not offered");
                        Click(resumed, "next", 1); Pump(() => !Get<bool>(resumed, "checking"), "resume driver probe");
                        Assert(Get<BridgeProfile>(resumed, "draft").Bindings.Any(b => b.Label == "Steering"), "Resume discarded calibration");
                        resumed.Close();
                    }
                    Assert(Same(previousProfile, File.Exists(Store.ProfilePath) ? File.ReadAllBytes(Store.ProfilePath) : null), "Active profile was modified before Finish");
                    Console.WriteLine("PASS Desktop: back navigation, cancellation, saved resume, and unchanged active profile");
                }
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("FAIL Desktop: " + ex); return 1; }
            finally
            {
                if (previousDraft == null) { if (File.Exists(draftPath)) File.Delete(draftPath); }
                else File.WriteAllBytes(draftPath, previousDraft);
                if (previousAppearance == null) { if (File.Exists(Appearance.Path)) File.Delete(Appearance.Path); }
                else File.WriteAllBytes(Appearance.Path, previousAppearance);
                Appearance.Set(previousMode, false);
            }
        }
        private static void MainWindowChecks()
        {
            string directory = Path.Combine(Path.GetTempPath(), "FHRigBridge-desktop-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "profile.json");
            var original = File.Exists(Store.ProfilePath) ? Store.Read<BridgeProfile>(Store.ProfilePath) : new BridgeProfile(); original.SetupVersion = SetupLogic.Version;
            Store.Write(path, original);
            try
            {
                using (var main = new BridgeForm(null, null, null, path))
                {
                    main.ShowInTaskbar = false; main.StartPosition = FormStartPosition.Manual; main.Location = new Point(-20000, -20000); main.Show();
                    Pump(() => !Field<bool>(main, "scanning") && Field<System.Collections.Generic.List<PhysicalDevice>>(main, "available").Count > 0, "main window discovery");
                    var navigation = Field<System.Collections.Generic.Dictionary<string, Button>>(main, "navigation");
                    var views = Field<System.Collections.Generic.Dictionary<string, Control>>(main, "views");
                    foreach (string name in new[] { "Mappings", "Drivers", "Live inputs", "Activity", "Drive" })
                    { navigation[name].PerformClick(); Assert(views[name].Visible, "Navigation did not reveal " + name); }
                    Field<Button>(main, "night").PerformClick();
                    Assert(Appearance.Mode == "Night" && Store.Read<UiPreferences>(Appearance.Path).Mode == "Night", "Night preference not saved");
                    Assert(Field<DataGridView>(main, "mappings").BackgroundColor == Appearance.Colors.Surface, "Hidden mappings page did not receive Night colors");
                    Field<Button>(main, "day").PerformClick(); Assert(Appearance.Mode == "Day", "Day mode did not apply");
                    int target = original.FfbGain == 43 ? 44 : 43; Field<StrengthSlider>(main, "strength").Value = target;
                    Pump(() => Store.Read<BridgeProfile>(path).FfbGain == target, "debounced strength save");
                    Assert(Store.Read<BridgeProfile>(path).Bindings.Count == original.Bindings.Count, "Strength save changed mappings");
                    Assert(Field<Label>(main, "strengthValue").Text == target + "%", "Displayed strength out of sync");
                    Assert(Field<StrengthSlider>(main, "strength").AccessibilityObject.Value == target + "%", "Accessible strength value out of sync");
                    main.Close();
                }
                Console.WriteLine("PASS Desktop: navigation, Day/Night persistence, contrast, strength display and isolated automatic saving");
            }
            finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(directory)) Directory.Delete(directory); }
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(owner); }
        private static bool Same(byte[] left, byte[] right) { return left == null ? right == null : right != null && left.SequenceEqual(right); }
        private static T Get<T>(SetupWizard wizard, string name) { return (T)typeof(SetupWizard).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(wizard); }
        private static void Pick(ComboBox box, string id)
        {
            for (int i = 0; i < box.Items.Count; i++) if ((string)box.Items[i].GetType().GetField("Id").GetValue(box.Items[i]) == id) { box.SelectedIndex = i; return; }
            throw new InvalidOperationException("Device choice missing: " + id);
        }
        private static void Click(SetupWizard wizard, string button, int page)
        { Get<Button>(wizard, button).PerformClick(); Pump(() => Get<int>(wizard, "page") == page && !Get<bool>(wizard, "navigating"), "navigate to step " + (page + 1)); }
        private static void Pump(Func<bool> done, string action)
        {
            var time = Stopwatch.StartNew();
            do { Application.DoEvents(); if (done()) return; Thread.Sleep(20); } while (time.ElapsedMilliseconds < 16000);
            throw new TimeoutException("Timed out: " + action);
        }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
