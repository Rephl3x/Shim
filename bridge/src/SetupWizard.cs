using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RigBridge
{
    public sealed class SetupWizard : Form
    {
        private readonly bool preview;
        private readonly string previewPath;
        private readonly int previewStep;
        private readonly BridgeProfile original;
        private BridgeProfile draft;
        private List<PhysicalDevice> devices;
        private RigChoices choices;
        private readonly Label steps = new Label { AutoSize = true, ForeColor = Color.FromArgb(50, 99, 166) };
        private readonly Label heading = new Label { AutoSize = true, Font = new Font("Segoe UI", 23, FontStyle.Bold) };
        private readonly Label message = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(166, 56, 25), AutoEllipsis = true };
        private readonly Panel body = new SurfaceCard { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(22) };
        private readonly Button back = MakeButton("Back"), next = MakeButton("Next"), cancel = MakeButton("Save progress and close");
        private readonly Button themeDay = MakeButton("Day"), themeNight = MakeButton("Night");
        private readonly ComboBox wheel = Choice(), pedals = Choice(), shifter = Choice(), sequential = Choice(), handbrake = Choice();
        private readonly CheckBox clutch = Check("My pedals include a clutch"), reverse = Check("Include reverse gear"), automatic = Check("Automatically include remaining buttons and hat switches");
        private readonly NumericUpDown gears = new NumericUpDown { Minimum = 1, Maximum = 10, Value = 7, Width = 65 };
        private readonly CheckedListBox extras = new CheckedListBox { Width = 760, Height = 115, CheckOnClick = true, BorderStyle = BorderStyle.FixedSingle };
        private readonly NumericUpDown slot = new NumericUpDown { Minimum = 1, Maximum = 16, Value = 1, Width = 65 };
        private readonly CheckBox feedback = Check("Forward game force feedback to my wheel"), invert = Check("Invert steering forces (only if the force direction needs reversing)");
        private readonly NumericUpDown gain = new NumericUpDown { Minimum = 0, Maximum = 100, Value = 25, Width = 65 };
        private readonly ListBox controls = new ListBox { Width = 230, Height = 340, IntegralHeight = false };
        private readonly Label learnTitle = new Label { Font = new Font("Segoe UI", 17, FontStyle.Bold), AutoSize = true };
        private readonly Label instruction = Copy(""), mapping = Copy(""), live = Copy("");
        private readonly Button capture = MakeButton("Capture"), retry = MakeButton("Relearn this control");
        private readonly CheckBox keepExisting = Check("Reuse existing axis and named gear mappings"), resume = Check("Resume my saved setup progress");
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 80 };
        private Label driverInfo;
        private Button installVjoy, installHide, configure, recheck;
        private Label testInfo;
        private Button retest;
        private SetupStatus driverStatus;
        private bool checking, navigating, roleControlsReady, monitorFailed;
        private int page, phase, omitted;
        private InputState first, second;
        private BridgeRuntime monitor;
        private List<SetupControl> plan = new List<SetupControl>();
        private Binding pending;
        private bool armed;
        private DateTime armedAt;
        private List<Binding> explicitBindings;
        private static string DraftPath { get { return Path.Combine(Store.DirectoryPath, "setup-draft.json"); } }
        public BridgeProfile Result { get; private set; }

        public SetupWizard(BridgeProfile current, IList<PhysicalDevice> found, string previewPath, int previewStep)
        {
            Appearance.Load();
            original = SetupLogic.Clone(current); draft = SetupLogic.Clone(current);
            this.previewPath = previewPath; this.previewStep = previewStep; preview = previewPath != null;
            devices = found.Where(d => !d.Virtual).ToList();
            Text = "FH Rig Bridge - set up your rig"; Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(243, 246, 250);
            ClientSize = new Size(1000, 790); MinimumSize = new Size(920, 740); AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 5 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            steps.Tag = "Accent"; steps.Font = new Font("Segoe UI Semibold", 9); top.Controls.Add(steps, 0, 0);
            themeDay.AutoSize = themeNight.AutoSize = false; themeDay.MinimumSize = themeNight.MinimumSize = new Size(82, 32); themeDay.Size = themeNight.Size = new Size(82, 32);
            themeDay.Padding = themeNight.Padding = new Padding(3, 0, 3, 0);
            themeDay.Margin = themeNight.Margin = new Padding(0, 0, 8, 0);
            var modes = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) }; modes.Controls.AddRange(new Control[] { themeDay, themeNight }); top.Controls.Add(modes, 1, 0);
            layout.Controls.Add(top, 0, 0); layout.Controls.Add(heading, 0, 1); layout.Controls.Add(body, 0, 2); layout.Controls.Add(message, 0, 3);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            next.Tag = "Primary"; cancel.Tag = "Ghost"; message.Tag = "Danger";
            footer.Controls.AddRange(new Control[] { next, back, cancel }); layout.Controls.Add(footer, 0, 4); Controls.Add(layout);
            back.Click += (s, e) => { if (!navigating) { StopMonitor(); ShowPage(page - 1); } };
            next.Click += async (s, e) => await NextAsync(); cancel.Click += (s, e) => Close();
            capture.Click += (s, e) => CaptureControl(); retry.Click += (s, e) => ResetCapture();
            controls.SelectedIndexChanged += (s, e) => ResetCapture();
            themeDay.Click += (s, e) => SetMode("Day"); themeNight.Click += (s, e) => SetMode("Night");
            Appearance.Changed += ApplyAppearance;
            slot.ValueChanged += (s, e) => { draft.VirtualId = (uint)slot.Value; driverStatus = null; UpdateDriverText(); };
            timer.Tick += (s, e) => Tick(); timer.Start();
            Shown += async (s, e) => {
                if (preview)
                {
                    choices = SetupLogic.Suggest(draft, devices); SetRoleControls();
                    if (previewStep >= 3 && !String.IsNullOrEmpty(choices.Wheel)) plan = SetupLogic.Plan(choices, devices);
                    ShowPage(previewStep);
                    using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(previewPath, System.Drawing.Imaging.ImageFormat.Png); }
                    Close();
                }
                else { ShowPage(0); await Task.FromResult(0); }
            };
            FormClosing += (s, e) => {
                if (navigating || checking) { e.Cancel = true; message.Text = "Wait for the current check to finish, then close setup."; return; }
                StopMonitor();
                if (!preview && Result == null && page > 0) { try { SaveDraft(); } catch (Exception ex) { message.Text = "Could not save setup progress: " + ex.Message; } }
            };
            FormClosed += (s, e) => { timer.Dispose(); Appearance.Changed -= ApplyAppearance; };
            if (preview) { ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Location = new Point(-20000, -20000); }
            ApplyAppearance();
        }
        private static Button MakeButton(string text) { return new ModernButton { Text = text, AutoSize = true, MinimumSize = new Size(110, 40), Padding = new Padding(12, 5, 12, 5), Margin = new Padding(0, 4, 10, 4) }; }
        private void SetMode(string mode) { try { Appearance.Set(mode, !preview); } catch (Exception ex) { message.Text = "Appearance could not be saved: " + ex.Message; } }
        private void ApplyAppearance()
        {
            themeDay.Tag = Appearance.Mode == "Day" ? "Active" : "Ghost"; themeNight.Tag = Appearance.Mode == "Night" ? "Active" : "Ghost";
            Appearance.Apply(this);
        }
        private static ComboBox Choice() { return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 580 }; }
        private static CheckBox Check(string text) { return new CheckBox { Text = text, AutoSize = true, Margin = new Padding(0, 8, 0, 8) }; }
        private static Label Copy(string text) { return new Label { Text = text, AutoSize = true, MaximumSize = new Size(840, 0), Margin = new Padding(0, 4, 0, 12) }; }
        private static FlowLayoutPanel Stack() { return new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) }; }
        private static FlowLayoutPanel Row(params Control[] items) { var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) }; row.Controls.AddRange(items); return row; }
        private static Label Caption(string text) { return new Label { Text = text, Width = 190, Height = 30, Padding = new Padding(0, 5, 0, 0) }; }
        private void ShowPage(int value)
        {
            page = Math.Max(0, Math.Min(5, value)); armed = false; message.Text = "";
            body.Controls.Clear(); body.AutoScrollPosition = Point.Empty;
            string[] titles = { "Let's set up your rig", "Drivers and virtual wheel", "Which devices make up your rig?", "Teach the bridge your controls", "Choose force feedback", "Check your inputs and save" };
            steps.Text = "RIG SETUP / STEP " + (page + 1) + " OF 6"; heading.Text = titles[page];
            back.Enabled = page > 0; next.Text = page == 5 ? "Finish setup" : "Next"; next.Enabled = !preview;
            var content = Stack(); body.Controls.Add(content);
            if (page == 0)
            {
                content.Controls.Add(Copy("Connect your wheel, pedals, shifters, handbrake and button boxes. Close Forza while setting up."));
                content.Controls.Add(Copy("We will check the drivers, create one virtual wheel, identify your devices and learn each driving control. Your friends can run the same wizard on their own PCs."));
                content.Controls.Add(Copy("Setup reads your inputs without sending motor forces. Your current profile stays in place until you click Finish setup. You can close the wizard and resume later."));
                content.Controls.Add(Copy("Driver installers may ask for administrator access or a Windows restart. If an installer is stuck at its final step, finish or restart it before configuring the virtual wheel."));
                resume.Checked = File.Exists(DraftPath); resume.Visible = File.Exists(DraftPath); content.Controls.Add(resume);
                content.Controls.Add(Copy(devices.Count + " physical controllers detected. You will choose which ones to include."));
            }
            else if (page == 1)
            {
                content.Controls.Add(Copy("Install each driver once, finish its installer, then configure your virtual wheel. You can continue choosing devices while an installer finishes; the final check will verify everything."));
                driverInfo = Copy(driverStatus == null ? "Checking driver installation and virtual controller layout..." : driverStatus.Summary); content.Controls.Add(driverInfo);
                installVjoy = MakeButton("Install vJoy"); installHide = MakeButton("Install HidHide"); recheck = MakeButton("Recheck drivers");
                installVjoy.Click += (s, e) => LaunchInstaller(true); installHide.Click += (s, e) => LaunchInstaller(false); recheck.Click += async (s, e) => await CheckAsync();
                content.Controls.Add(Row(installVjoy, installHide, recheck));
                slot.Value = Math.Max(1, Math.Min(16, draft.VirtualId)); content.Controls.Add(Row(Caption("Virtual wheel slot"), slot));
                configure = MakeButton("Configure virtual wheel automatically"); configure.Click += (s, e) => {
                    try { DriverSetup.Configure((uint)slot.Value, driverStatus == null ? null : driverStatus.Virtual); driverStatus = null; message.Text = "Applying configuration. Recheck drivers when the Windows prompt/tool has finished."; UpdateDriverText(); }
                    catch (Exception ex) { message.Text = ex.Message; }
                }; content.Controls.Add(configure);
                content.Controls.Add(Copy("This sets the chosen vJoy slot to 8 axes, 128 buttons, 4 continuous hats and the 11 supported force effects. It replaces that slot's layout and refreshes vJoy. Close games and other vJoy feeders first."));
                content.Controls.Add(Copy("If an installer still shows its final step, the driver may already be visible while setup remains incomplete. Do not launch it again. Complete its window or restart Windows if it stays stuck, then choose Recheck drivers."));
                if (!preview) BeginInvoke(new Action(async () => await CheckAsync()));
            }
            else if (page == 2)
            {
                if (!roleControlsReady) SetRoleControls();
                content.Controls.Add(Copy("Choose a device for each role. Pedals connected through the wheelbase appear under the wheelbase; a separate USB pedal set appears as its own device."));
                content.Controls.Add(Row(Caption("Steering wheel *"), wheel)); content.Controls.Add(Row(Caption("Pedals"), pedals)); content.Controls.Add(clutch);
                content.Controls.Add(Row(Caption("H-pattern shifter"), shifter)); content.Controls.Add(Row(Caption("Forward gears"), gears, reverse));
                content.Controls.Add(Row(Caption("Sequential / paddles"), sequential)); content.Controls.Add(Row(Caption("Analog handbrake"), handbrake));
                content.Controls.Add(Copy("Include other controllers below. Devices chosen above are already included; wheel buttons are included with the wheel."));
                content.Controls.Add(extras); content.Controls.Add(keepExisting);
                var scan = MakeButton("Refresh connected devices"); scan.Click += (s, e) => { try { choices = ReadChoices(); using (var input = new DirectInput()) devices = input.Enumerate().Where(d => !d.Virtual).ToList(); roleControlsReady = false; ShowPage(2); } catch (Exception ex) { message.Text = ex.Message; } }; content.Controls.Add(scan);
            }
            else if (page == 3)
            {
                content.Controls.Add(Copy("Select a control, then follow its capture instructions. Move only that control. Already mapped controls can be kept or relearned. Every requested control must have a mapping before continuing."));
                controls.Items.Clear(); foreach (var item in plan) controls.Items.Add(item);
                var detail = Stack(); detail.Width = 560; learnTitle.MaximumSize = instruction.MaximumSize = mapping.MaximumSize = live.MaximumSize = new Size(550, 0);
                detail.Controls.Add(learnTitle); detail.Controls.Add(instruction); detail.Controls.Add(Row(capture, retry)); detail.Controls.Add(mapping); detail.Controls.Add(live);
                content.Controls.Add(Row(controls, detail)); content.Controls.Add(automatic);
                if (controls.Items.Count > 0) controls.SelectedIndex = 0;
                StartMonitor();
            }
            else if (page == 4)
            {
                var source = devices.FirstOrDefault(d => d.Id == choices.Wheel);
                feedback.Enabled = source != null && source.ForceFeedback;
                if (!feedback.Enabled) feedback.Checked = false;
                content.Controls.Add(Copy("Force feedback returns the game's effects to " + (source == null ? "your wheel" : source.Name) + ". Steering and feedback use the same physical wheel."));
                content.Controls.Add(feedback); content.Controls.Add(Row(Caption("Maximum game gain %"), gain)); content.Controls.Add(invert);
                content.Controls.Add(Copy("Start with a low gain. The default is 25%. This is a game-gain limit, not a measured torque limit. Setup never starts a motor-force test."));
                content.Controls.Add(Copy("During play, the selected physical controllers are hidden from Forza and our bridge continues reading them. Stopping the bridge restores visibility. If Pit House needs access, add it to HidHide's Applications list; keep Forza off that list."));
                content.Controls.Add(Copy("After finishing: start the bridge, launch Forza, then create a custom wheel profile and bind the virtual controls once."));
            }
            else if (page == 5)
            {
                content.Controls.Add(Copy(draft.Devices.Count + " devices / " + draft.Bindings.Count(b => b.OutputKind == "Axis") + " axes / " + draft.Bindings.Count(b => b.OutputKind == "Button") + " buttons / " + draft.Bindings.Count(b => b.OutputKind == "POV") + " hats. Force feedback: " + (draft.EnableFfb ? draft.FfbGain + "% to your steering wheel." : "off.")));
                testInfo = Copy("Checking drivers..."); content.Controls.Add(testInfo);
                retest = MakeButton("Recheck drivers"); retest.Click += async (s, e) => await CheckAsync(); content.Controls.Add(retest);
                live.MaximumSize = new Size(840, 0); live.Text = "Starting read-only live check..."; content.Controls.Add(live);
                if (omitted > 0) content.Controls.Add(Copy(omitted + " extra declared buttons did not fit in the 128-button limit. Your explicitly learned gears are reserved. Use advanced mappings to substitute any extra buttons you need."));
                content.Controls.Add(Copy("Turn the wheel, press each pedal and select every gear. Check the live mapped values above. Finish saves a backup of your previous profile, then opens the ready-to-start main window."));
                content.Controls.Add(Copy("Forza still needs a custom wheel profile bound to this virtual controller. The wizard cannot verify in-game feedback until you start the bridge and the game."));
                StartMonitor(); if (!preview) BeginInvoke(new Action(async () => await CheckAsync()));
                UpdateFinish();
            }
            ApplyAppearance();
        }
        private void SetRoleControls()
        {
            choices = choices ?? SetupLogic.Suggest(draft, devices);
            Fill(wheel, choices.Wheel); Fill(pedals, choices.Pedals); Fill(shifter, choices.Shifter); Fill(sequential, choices.Sequential); Fill(handbrake, choices.Handbrake);
            clutch.Checked = choices.Clutch; reverse.Checked = choices.Reverse; gears.Value = Math.Max(1, Math.Min(10, choices.Gears));
            extras.Items.Clear(); foreach (var device in devices) extras.Items.Add(device, choices.Extras.Contains(device.Id));
            keepExisting.Checked = draft.Bindings.Count > 0; automatic.Checked = true;
            feedback.Checked = draft.SetupVersion == 0 && draft.Bindings.Count == 0 ? devices.Any(d => d.Id == choices.Wheel && d.ForceFeedback) : draft.EnableFfb;
            invert.Checked = draft.InvertFfb; gain.Value = Math.Max(0, Math.Min(100, draft.FfbGain)); roleControlsReady = true;
        }
        private void Fill(ComboBox box, string selected)
        {
            box.Items.Clear(); box.Items.Add(new DeviceChoice { Name = "None / not fitted" });
            foreach (var device in devices) box.Items.Add(new DeviceChoice { Id = device.Id, Name = device.Name });
            box.SelectedIndex = 0;
            for (int i = 1; i < box.Items.Count; i++) if (((DeviceChoice)box.Items[i]).Id == selected) box.SelectedIndex = i;
        }
        private static string Selected(ComboBox box) { return box.SelectedItem == null ? null : ((DeviceChoice)box.SelectedItem).Id; }
        private RigChoices ReadChoices()
        {
            return new RigChoices { Wheel = Selected(wheel), Pedals = Selected(pedals), Clutch = clutch.Checked, Shifter = Selected(shifter), Gears = (int)gears.Value, Reverse = reverse.Checked,
                Sequential = Selected(sequential), Handbrake = Selected(handbrake), Extras = extras.CheckedItems.Cast<PhysicalDevice>().Select(d => d.Id).ToList() };
        }
        private async Task NextAsync()
        {
            if (navigating || preview) return;
            navigating = true; message.Text = "";
            try
            {
                if (page == 0)
                {
                    draft = resume.Checked && File.Exists(DraftPath) ? Store.Read<BridgeProfile>(DraftPath) : SetupLogic.Clone(original);
                    if (draft == null) draft = SetupLogic.Clone(original);
                    choices = null; roleControlsReady = false;
                }
                else if (page == 2)
                {
                    choices = ReadChoices(); var requested = SetupLogic.Plan(choices, devices);
                    draft.Rig = choices; draft.Devices = choices.Selected(); draft.FfbDevice = choices.Wheel;
                    SetupLogic.KeepRequestedMappings(draft, requested, keepExisting.Checked);
                    plan = requested; explicitBindings = null; omitted = 0;
                }
                else if (page == 3)
                {
                    if (monitor == null || !monitor.Started || !monitor.Running) throw new InvalidOperationException("Wait for the live input monitor, or reconnect the device and return to Devices.");
                    if (plan.Any(target => !HasMapping(target))) throw new InvalidOperationException("Learn every requested control first. Select an unfinished control in the list, or return to Devices to remove an optional role.");
                    if (explicitBindings == null) explicitBindings = SetupLogic.Clone(draft).Bindings;
                    draft.Bindings = SetupLogic.Clone(new BridgeProfile { Bindings = explicitBindings }).Bindings;
                    omitted = automatic.Checked ? SetupLogic.AddButtons(draft, devices) : 0;
                    if (automatic.Checked) SetupLogic.AddHats(draft, devices);
                    draft.EnableFfb = false; // The following page applies the user's feedback choice.
                    draft.Validate(devices);
                }
                else if (page == 4) { draft.EnableFfb = feedback.Checked; draft.FfbGain = (int)gain.Value; draft.InvertFfb = invert.Checked; draft.Validate(devices); }
                else if (page == 5)
                {
                    if (monitor == null || !monitor.Started || !monitor.Running) throw new InvalidOperationException("The live input check is not running. Return to Devices and reconnect missing controllers.");
                    StopMonitor(); await CheckAsync();
                    if (driverStatus == null || !driverStatus.Ready || DriverSetup.Configuring) throw new InvalidOperationException("Finish driver setup first. Use Back to return to Drivers, then configure/recheck the virtual wheel.");
                    draft.Validate(devices); draft.SetupVersion = SetupLogic.Version;
                    if (File.Exists(Store.ProfilePath)) File.Copy(Store.ProfilePath, Path.Combine(Store.DirectoryPath, "profile-before-setup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json"), false);
                    Store.Write(Store.ProfilePath, draft); if (File.Exists(DraftPath)) File.Delete(DraftPath);
                    Result = draft; DialogResult = DialogResult.OK; navigating = false; Close(); return;
                }
                StopMonitor(); SaveDraft(); ShowPage(page + 1);
            }
            catch (Exception ex) { message.Text = ex.Message; if (page == 5) StartMonitor(); }
            finally { navigating = false; }
        }
        private bool HasMapping(SetupControl target) { return draft.Bindings.Any(b => b.OutputKind == target.Kind && b.OutputIndex == target.Output && b.DeviceId == target.DeviceId); }
        private void SaveDraft()
        {
            if (preview) return;
            if (page == 2 && roleControlsReady) { choices = ReadChoices(); draft.Rig = choices; }
            if (page == 4) { draft.EnableFfb = feedback.Checked; draft.FfbGain = (int)gain.Value; draft.InvertFfb = invert.Checked; }
            Store.Write(DraftPath, draft);
        }
        private async Task CheckAsync()
        {
            if (checking || preview) return;
            checking = true; UpdateDriverText();
            try { driverStatus = await DriverSetup.CheckAsync(draft.VjoyDll, draft.VirtualId); }
            catch (Exception ex) { message.Text = ex.Message; }
            finally { checking = false; if (!IsDisposed) { UpdateDriverText(); UpdateFinish(); } }
        }
        private void UpdateDriverText()
        {
            if (page != 1 || driverInfo == null || driverInfo.IsDisposed) return;
            bool busy = DriverSetup.InstallerRunning(true) || DriverSetup.InstallerRunning(false) || DriverSetup.Configuring;
            driverInfo.Text = checking ? "Checking actual driver and controller capabilities..." : driverStatus == null ? "Click Recheck drivers to refresh installation and controller readiness." : driverStatus.Summary;
            if (installVjoy != null) installVjoy.Enabled = installHide.Enabled = !checking && !busy && !preview;
            if (configure != null) configure.Enabled = !checking && !busy && !preview && File.Exists(DriverSetup.ConfigPath);
            if (recheck != null) recheck.Enabled = !checking && !preview;
            slot.Enabled = !checking && !DriverSetup.Configuring;
        }
        private void LaunchInstaller(bool vjoy)
        {
            try { DriverSetup.Install(vjoy); driverStatus = null; message.Text = "The official installer is open. Finish its steps, then recheck drivers. Your device setup can continue while it runs."; UpdateDriverText(); }
            catch (Exception ex) { message.Text = ex.Message; }
        }
        private SetupControl Target { get { return controls.SelectedItem as SetupControl; } }
        private void ResetCapture()
        {
            armed = false; first = null; second = null; pending = null; phase = 0; capture.Enabled = true;
            var target = Target; if (target == null) return;
            learnTitle.Text = target.Name; instruction.Text = target.Kind == "Button" ? "Return the shifter to neutral / release all buttons. Click Listen, then engage " + target.Name + "." : target.Output == 0 ? "Turn the wheel to your full left steering endpoint and hold it there, then click Capture left." : "Release " + target.Name.ToLowerInvariant() + " fully, then click Capture released.";
            capture.Text = target.Kind == "Button" ? "Listen for " + target.Name : target.Output == 0 ? "Capture left" : "Capture released";
            var existing = draft.Bindings.FirstOrDefault(b => b.OutputKind == target.Kind && b.OutputIndex == target.Output && b.DeviceId == target.DeviceId);
            mapping.Text = existing == null ? "Not mapped yet." : "Mapped: " + Describe(existing) + ". Relearn only if needed.";
        }
        private InputState Current(SetupControl target)
        {
            if (monitor == null || !monitor.Started || !monitor.Running) throw new InvalidOperationException("Waiting for live inputs. If this persists, return to Devices and check the connection.");
            InputState state; if (!monitor.Snapshot().TryGetValue(target.DeviceId, out state)) throw new InvalidOperationException("No input sample from this device yet."); return state;
        }
        private void CaptureControl()
        {
            message.Text = "";
            try
            {
                var target = Target; if (target == null) return; var current = Current(target);
                if (target.Kind == "Button")
                {
                    first = current; armed = true; armedAt = DateTime.UtcNow; capture.Enabled = false; instruction.Text = "Listening: engage " + target.Name + " now. Return other controls to rest."; return;
                }
                if (phase == 0)
                {
                    first = current; phase = 1; instruction.Text = target.Output == 0 ? "Turn the wheel to your full right steering endpoint and hold it there. Click Capture right." : "Press / pull " + target.Name.ToLowerInvariant() + " fully and hold it. Click Capture full.";
                    capture.Text = target.Output == 0 ? "Capture right" : "Capture full"; return;
                }
                if (phase == 1)
                {
                    second = current; pending = SetupLogic.Calibrate(target, devices.First(d => d.Id == target.DeviceId), first, second);
                    if (target.Output == 0) { phase = 2; instruction.Text = "Return the wheel to straight ahead, then click Verify centre."; capture.Text = "Verify centre"; return; }
                }
                else
                {
                    double center = (current.Axes[pending.InputIndex] - pending.Minimum) / (pending.Maximum - pending.Minimum);
                    if (Math.Abs(center - 0.5) > 0.08) throw new InvalidOperationException("The centre is too far from halfway between the captured endpoints. Click Relearn and capture equal left/right steering travel.");
                }
                Commit(pending);
            }
            catch (Exception ex) { message.Text = ex.Message; }
        }
        private void Commit(Binding value)
        {
            SetupLogic.Put(draft, value); explicitBindings = null; armed = false; capture.Enabled = false;
            mapping.Text = "Mapped: " + Describe(value); instruction.Text = "Captured. Return to rest and select the next control, or click Next when all are mapped.";
            SaveDraft();
            int unfinished = plan.Count(t => !HasMapping(t)); message.Text = unfinished == 0 ? "All requested controls are mapped. Check their live values, then continue." : unfinished + " controls still need mapping.";
        }
        private static string Describe(Binding binding) { return binding.InputKind + " " + (binding.InputIndex + 1) + " -> " + (binding.OutputKind == "Axis" ? Mapper.AxisNames[binding.OutputIndex] : "virtual button " + (binding.OutputIndex + 1)) + (binding.Invert ? " (automatically inverted)" : ""); }
        private void StartMonitor()
        {
            if (preview || monitor != null || draft.Devices.Count == 0) return;
            monitorFailed = false; monitor = new BridgeRuntime(SetupLogic.Clone(draft), Handle, true); monitor.Start();
        }
        private void StopMonitor() { armed = false; if (monitor != null) { monitor.RequestStop(); monitor.Dispose(); monitor = null; } }
        private void UpdateFinish()
        {
            if (page != 5) return;
            if (testInfo != null) testInfo.Text = checking ? "Checking driver and virtual wheel readiness..." : driverStatus == null ? "Drivers have not been verified yet." : driverStatus.Summary;
            if (retest != null) retest.Enabled = !checking && !preview;
            next.Enabled = !preview && !checking && !monitorFailed && monitor != null && monitor.Started && monitor.Running && driverStatus != null && driverStatus.Ready && !DriverSetup.Configuring;
        }
        private int tickCount;
        private void Tick()
        {
            if (preview) return;
            if (++tickCount % 25 == 0 && page == 1)
            {
                if (driverStatus != null) { driverStatus.VjoyInstalling = DriverSetup.InstallerRunning(true); driverStatus.HideInstalling = DriverSetup.InstallerRunning(false); }
                string configuration = DriverSetup.ConfigurationMessage;
                if (configuration != null)
                {
                    message.Text = configuration;
                    if (!DriverSetup.Configuring && !checking) BeginInvoke(new Action(async () => await CheckAsync()));
                }
                UpdateDriverText();
            }
            if (monitor != null && !monitor.Running)
            {
                message.Text = monitor.Error == null ? "Live inputs stopped. Return to Devices and reconnect the controllers." : "Input check stopped: " + monitor.Error.Message;
                monitor.Dispose(); monitor = null; monitorFailed = true; armed = false; UpdateFinish(); return;
            }
            if (monitor == null || !monitor.Started) return;
            try
            {
                var states = monitor.Snapshot();
                if (page == 3 && Target != null)
                {
                    InputState current; if (!states.TryGetValue(Target.DeviceId, out current)) return;
                    var device = devices.First(d => d.Id == Target.DeviceId);
                    live.Text = "Live " + device.Name + ":\n" + String.Join("  ", device.Controls.Where(c => c.Kind == "Axis").Select(c => c.Name + " " + (current.Axes[c.Index] * 100).ToString("0") + "%")) + "\nButtons down: " + String.Join(", ", Enumerable.Range(0, 128).Where(i => current.Buttons[i]).Select(i => i + 1));
                    if (armed)
                    {
                        if ((DateTime.UtcNow - armedAt).TotalSeconds > 20) { armed = false; capture.Enabled = true; message.Text = "No new button detected. Return to neutral, click Listen and try again."; }
                        else { var found = SetupLogic.LearnButton(Target, device, first, current); if (found != null) Commit(found); }
                    }
                }
                else if (page == 5)
                {
                    var output = Mapper.Map(draft.Bindings, states);
                    live.Text = "LIVE MAPPED INPUTS (read-only preview)\n\n" + String.Join("\n", draft.Bindings.Where(b => b.OutputKind == "Axis").OrderBy(b => b.OutputIndex).Select(b => (b.Label ?? Mapper.AxisNames[b.OutputIndex]) + ": " + (output.Axes[b.OutputIndex] * 100).ToString("0") + "%")) +
                        "\n\nActive buttons / gears: " + String.Join(", ", draft.Bindings.Where(b => b.OutputKind == "Button" && output.Buttons[b.OutputIndex]).Select(b => (b.Label ?? "Button") + " [" + (b.OutputIndex + 1) + "]"));
                    UpdateFinish();
                }
            }
            catch (Exception ex) { armed = false; capture.Enabled = true; message.Text = ex.Message; }
        }
        private sealed class DeviceChoice { public string Id, Name; public override string ToString() { return Name; } }
    }
}
