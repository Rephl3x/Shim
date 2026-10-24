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
    public sealed class BridgeForm : Form
    {
        private readonly CheckedListBox devices = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true };
        private readonly ComboBox wheel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
        private readonly CheckBox enableFfb = new CheckBox { Text = "Forward game force feedback", AutoSize = true };
        private readonly CheckBox invert = new CheckBox { Text = "Invert steering forces", AutoSize = true };
        private readonly NumericUpDown gain = new NumericUpDown { Minimum = 0, Maximum = 100, Value = 25, Width = 60 };
        private readonly NumericUpDown virtualId = new NumericUpDown { Minimum = 1, Maximum = 16, Value = 1, Width = 50 };
        private readonly TextBox dll = new TextBox { Width = 470 };
        private readonly Label prerequisites = new Label { AutoSize = true, MaximumSize = new Size(1000, 0) };
        private readonly Label status = new Label { Dock = DockStyle.Fill, Padding = new Padding(3, 6, 3, 0) };
        private readonly DataGridView mappings = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        private readonly TextBox monitor = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9) };
        private readonly TextBox log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9) };
        private readonly ComboBox outputControl = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175 };
        private readonly CheckBox invertAxis = new CheckBox { Text = "Invert axis", AutoSize = true };
        private readonly NumericUpDown deadzone = new NumericUpDown { Minimum = 0, Maximum = 40, Width = 50 };
        private readonly Button start = Button("Start bridge"), stop = Button("Stop / restore devices"), watch = Button("Monitor inputs"), refresh = Button("Refresh devices"), save = Button("Save profile"), learn = Button("Learn selected output"), autoButtons = Button("Map all buttons"), remove = Button("Remove mapping"), recover = Button("Restore visibility");
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 60 };
        private readonly string previewPath;
        private List<PhysicalDevice> available = new List<PhysicalDevice>();
        private BridgeProfile profile = new BridgeProfile();
        private BridgeRuntime runtime;
        private bool scanning, closing, learning;
        private DateTime learnDeadline;
        private string learnKind;
        private int learnIndex;
        private Dictionary<string, InputState> baseline;
        private readonly string logPath;
        private readonly List<Control> setupActions = new List<Control>();
        private readonly Button setupWizard = Button("Set up my rig");
        public BridgeForm(string previewPath)
        {
            this.previewPath = previewPath;
            Text = "FH Rig Bridge - one virtual wheel"; Font = new Font("Segoe UI", 10);
            ClientSize = new Size(1180, 880); MinimumSize = new Size(1080, 800); AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(245, 247, 250);
            logPath = Path.Combine(Store.DirectoryPath, "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            if (File.Exists(Store.ProfilePath))
                try { profile = Store.Read<BridgeProfile>(Store.ProfilePath) ?? new BridgeProfile(); }
                catch (Exception ex) { Log("Could not load the profile: " + ex.Message); }
            Build();
            enableFfb.Checked = profile.EnableFfb; invert.Checked = profile.InvertFfb;
            gain.Value = Math.Max(0, Math.Min(100, profile.FfbGain)); virtualId.Value = Math.Max(1, Math.Min(16, profile.VirtualId));
            dll.Text = String.IsNullOrEmpty(profile.VjoyDll) ? Vjoy.DefaultDll : profile.VjoyDll;
            refresh.Click += async (s, e) => await RefreshAsync();
            save.Click += (s, e) => SaveProfile();
            setupWizard.Click += async (s, e) => await RunSetup();
            watch.Click += (s, e) => Start(true);
            start.Click += (s, e) => Start(false);
            stop.Click += (s, e) => { if (runtime != null) { runtime.RequestStop(); status.Text = "Stopping forces and restoring device visibility..."; } };
            learn.Click += (s, e) => BeginLearn();
            autoButtons.Click += (s, e) => AutoMapButtons();
            remove.Click += (s, e) => { if (mappings.SelectedRows.Count > 0) { profile.Bindings.Remove((Binding)mappings.SelectedRows[0].Tag); RenderMappings(); } };
            recover.Click += (s, e) => { try { HideSession.RecoverFile(Store.RecoveryPath); Log("Device visibility restored."); } catch (Exception ex) { Log(ex.Message); } UpdateButtons(); };
            devices.ItemCheck += (s, e) => { if (!scanning && runtime == null) BeginInvoke(new Action(CaptureSelections)); };
            timer.Tick += (s, e) => TickRuntime(); timer.Start();
            Shown += async (s, e) => {
                await RefreshAsync();
                if (previewPath != null)
                {
                    using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(previewPath, System.Drawing.Imaging.ImageFormat.Png); }
                    Close();
                }
                else if (profile.SetupVersion < SetupLogic.Version) await RunSetup();
            };
            FormClosing += (s, e) => { if (runtime != null) { e.Cancel = true; closing = true; runtime.RequestStop(); status.Text = "Stopping the bridge before closing..."; } };
            FormClosed += (s, e) => timer.Dispose();
            if (previewPath != null) { ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Location = new Point(-20000, -20000); }
            UpdateButtons();
        }
        private static Button Button(string text) { return new Button { Text = text, AutoSize = true, Padding = new Padding(7, 3, 7, 3), Margin = new Padding(3) }; }
        private static Label Label(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(5, 7, 6, 3) }; }
        private static FlowLayoutPanel Flow(params Control[] controls)
        { var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) }; panel.Controls.AddRange(controls); return panel; }
        private void Build()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 6 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 104)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 53)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
            var title = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            title.Controls.Add(new Label { Text = "One rig. One virtual wheel.", Font = new Font("Segoe UI", 22, FontStyle.Bold), AutoSize = true });
            title.Controls.Add(Flow(setupWizard, Label("Start the bridge before launching Forza. Use Set up my rig for guided configuration.")));
            layout.Controls.Add(title, 0, 0);
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var mappingTab = new TabPage("Devices and mappings");
            var split = new SplitContainer { Width = 1120, Dock = DockStyle.Fill, SplitterDistance = 300, FixedPanel = FixedPanel.Panel1 };
            var sources = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            sources.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); sources.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); sources.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            sources.Controls.Add(Label("Physical input devices"), 0, 0); sources.Controls.Add(devices, 0, 1); sources.Controls.Add(Flow(refresh, watch), 0, 2);
            split.Panel1.Controls.Add(sources);
            var mappingArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            mappingArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); mappingArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); mappingArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 75)); mappingArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            mappingArea.Controls.Add(Label("Learn: start Monitor inputs, choose an output, then move or press a physical control."), 0, 0);
            mappings.Columns.Add("output", "Virtual output"); mappings.Columns.Add("device", "Physical device"); mappings.Columns.Add("input", "Physical input"); mappings.Columns.Add("calibration", "Options");
            mappingArea.Controls.Add(mappings, 0, 1);
            for (int i = 0; i < 8; i++) outputControl.Items.Add(new OutputChoice { Kind = "Axis", Index = i, Name = Mapper.AxisNames[i] });
            for (int i = 0; i < 128; i++) outputControl.Items.Add(new OutputChoice { Kind = "Button", Index = i, Name = "Button " + (i + 1) });
            for (int i = 0; i < 4; i++) outputControl.Items.Add(new OutputChoice { Kind = "POV", Index = i, Name = "POV " + (i + 1) });
            outputControl.SelectedIndex = 0;
            mappingArea.Controls.Add(Flow(outputControl, invertAxis, Label("Deadzone %:"), deadzone, learn), 0, 2);
            mappingArea.Controls.Add(Flow(autoButtons, remove, save), 0, 3);
            split.Panel2.Controls.Add(mappingArea); mappingTab.Controls.Add(split); tabs.TabPages.Add(mappingTab);
            var monitorTab = new TabPage("Live inputs"); monitorTab.Controls.Add(monitor); tabs.TabPages.Add(monitorTab);
            var setupTab = new TabPage("Driver setup");
            var setup = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14), AutoScroll = true };
            setup.Controls.Add(prerequisites);
            Button getVjoy = Button(File.Exists(DriverPackages.PathFor(true)) ? "Install bundled vJoy" : "Get official vJoy driver"),
                getHide = Button(File.Exists(DriverPackages.PathFor(false)) ? "Install bundled HidHide" : "Get official HidHide driver"),
                choose = Button("Select vJoy DLL"), check = Button("Check drivers");
            getVjoy.Click += (s, e) => InstallDriver(true);
            getHide.Click += (s, e) => InstallDriver(false);
            choose.Click += async (s, e) => { using (var dialog = new OpenFileDialog { Filter = "vJoyInterface.dll|vJoyInterface.dll", FileName = "vJoyInterface.dll" }) if (dialog.ShowDialog(this) == DialogResult.OK) { dll.Text = dialog.FileName; await CheckDrivers(); } };
            check.Click += async (s, e) => await CheckDrivers();
            setupActions.AddRange(new Control[] { getVjoy, getHide, choose, check });
            setup.Controls.Add(Flow(getVjoy, getHide, check));
            setup.Controls.Add(Label("Install the official drivers once. Restart Windows if requested. Configure one vJoy device with:"));
            setup.Controls.Add(Label("Use Set up my rig to configure vJoy automatically and learn your devices."));
            setup.Controls.Add(Label("Enable FFB on X with Constant, Ramp, Periodic and Condition effects. Leave Custom Force disabled."));
            setup.Controls.Add(Flow(Label("x64 SDK DLL:"), dll, choose));
            setup.Controls.Add(Label("HidHide must use normal mode. The bridge temporarily hides selected gaming interfaces and restores its changes on stop."));
            setup.Controls.Add(Label("The package includes unchanged signed installers. Click each Install button and follow the publisher's setup wizard."));
            setup.Controls.Add(Label("Installers are checked against pinned SHA-256 hashes. Windows handles elevation and any required restart."));
            setup.Controls.Add(Label("If Pit House needs access while bridging, add it to HidHide's Applications list. Never whitelist Forza."));
            setupTab.Controls.Add(setup); tabs.TabPages.Add(setupTab); layout.Controls.Add(tabs, 0, 1);
            layout.Controls.Add(Flow(Label("FFB wheel:"), wheel, enableFfb, Label("Gain %:"), gain, invert), 0, 2);
            layout.Controls.Add(Flow(Label("vJoy device:"), virtualId, start, stop, recover), 0, 3);
            start.BackColor = Color.FromArgb(25, 100, 186); start.ForeColor = Color.White; start.FlatStyle = FlatStyle.Flat;
            status.Text = "Monitor inputs works without vJoy or HidHide. FFB stays off until you enable it and start the bridge.";
            layout.Controls.Add(status, 0, 4); layout.Controls.Add(log, 0, 5); Controls.Add(layout);
        }
        private async Task RefreshAsync()
        {
            scanning = true; UpdateButtons();
            try
            {
                using (var direct = new DirectInput()) available = direct.Enumerate().Where(d => !d.Virtual).ToList();
                devices.Items.Clear(); wheel.Items.Clear();
                foreach (var device in available) { devices.Items.Add(device, profile.Devices.Contains(device.Id)); if (device.ForceFeedback) wheel.Items.Add(device); }
                wheel.SelectedItem = wheel.Items.Cast<PhysicalDevice>().FirstOrDefault(d => d.Id == profile.FfbDevice);
                if (wheel.SelectedIndex < 0 && wheel.Items.Count > 0) wheel.SelectedIndex = 0;
                RenderMappings();
                await CheckDrivers();
                Log("Detected " + available.Count + " physical controllers. Driver status is on the Driver setup tab.");
                if (File.Exists(Store.RecoveryPath)) Log("A previous hiding session needs recovery. Click Restore visibility.");
            }
            catch (Exception ex) { Log(ex.Message); }
            finally { scanning = false; UpdateButtons(); }
        }
        private async Task CheckDrivers()
        {
            prerequisites.Text = "Checking driver installation and virtual controller layout...";
            var result = await DriverSetup.CheckAsync(dll.Text, (uint)virtualId.Value);
            prerequisites.Text = result.Summary;
        }
        private async Task RunSetup()
        {
            if (runtime != null || scanning || previewPath != null) return;
            CaptureSelections();
            using (var wizard = new SetupWizard(profile, available, null, 0))
            {
                if (wizard.ShowDialog(this) != DialogResult.OK) return;
                profile = wizard.Result;
                enableFfb.Checked = profile.EnableFfb; invert.Checked = profile.InvertFfb;
                gain.Value = profile.FfbGain; virtualId.Value = profile.VirtualId;
                dll.Text = String.IsNullOrEmpty(profile.VjoyDll) ? Vjoy.DefaultDll : profile.VjoyDll;
            }
            await RefreshAsync(); Log("Setup saved. Start the bridge, then launch Forza and bind your virtual wheel controls.");
        }
        private void CaptureSelections()
        {
            profile.Devices = devices.CheckedItems.Cast<PhysicalDevice>().Select(d => d.Id).ToList();
            profile.FfbDevice = wheel.SelectedItem == null ? null : ((PhysicalDevice)wheel.SelectedItem).Id;
            profile.VirtualId = (uint)virtualId.Value; profile.EnableFfb = enableFfb.Checked; profile.InvertFfb = invert.Checked; profile.FfbGain = (int)gain.Value; profile.VjoyDll = dll.Text;
        }
        private bool SaveProfile()
        { try { CaptureSelections(); Store.Write(Store.ProfilePath, profile); Log("Saved profile."); return true; } catch (Exception ex) { Log(ex.Message); return false; } }
        private void Start(bool monitorOnly)
        {
            try
            {
                CaptureSelections();
                if (profile.Devices.Count == 0) throw new InvalidOperationException("Select the physical devices first.");
                if (!monitorOnly) { profile.Validate(available); if (!SaveProfile()) return; }
                // Snapshot the profile so editing the UI cannot change a running session.
                var copy = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<BridgeProfile>(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(profile));
                runtime = new BridgeRuntime(copy, Handle, monitorOnly); runtime.Start();
                Log(monitorOnly ? "Starting read-only input monitor. No hiding, virtual output or FFB commands." : "Starting virtual wheel bridge. Launch Forza after the bridge reports running.");
                UpdateButtons();
            }
            catch (Exception ex) { Log(ex.Message); }
        }
        private void BeginLearn()
        {
            if (runtime == null || !runtime.Started) { Log("Start Monitor inputs first."); return; }
            var output = (OutputChoice)outputControl.SelectedItem;
            learnKind = output.Kind; learnIndex = output.Index; baseline = runtime.Snapshot();
            if (baseline.Count == 0) { Log("Waiting for initial input samples. Try Learn again."); return; }
            learning = true; learnDeadline = DateTime.UtcNow.AddSeconds(12);
            Log("Learning " + output.Name + ": " + (output.Kind == "Axis" ? "move the desired axis through its travel." : output.Kind == "POV" ? "move the desired hat switch." : "press the desired button or engage a gear."));
        }
        private void AutoMapButtons()
        {
            CaptureSelections();
            var selected = available.Where(d => profile.Devices.Contains(d.Id)).OrderBy(d => d.Name.IndexOf("HGP", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1).ToList();
            selected = selected.OrderBy(d => d.Name.IndexOf("HGP", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : d.ForceFeedback ? 2 : 1).ToList();
            int required = selected.Sum(d => d.Controls.Count(c => c.Kind == "Button"));
            profile.Bindings.RemoveAll(b => b.OutputKind == "Button"); int next = 0;
            foreach (var device in selected) foreach (var control in device.Controls.Where(c => c.Kind == "Button").OrderBy(c => c.Index))
                if (next < 128) profile.Bindings.Add(new Binding { OutputKind = "Button", OutputIndex = next++, DeviceId = device.Id, InputKind = "Button", InputIndex = control.Index });
            RenderMappings(); Log("Mapped " + next + " physical buttons, with HGP gear inputs first. " +
                (required > 128 ? (required - 128) + " excess declared buttons were left unmapped; use Learn to replace outputs with the buttons you need." : "Bind the virtual buttons in Forza once."));
        }
        private void TickRuntime()
        {
            if (runtime == null) return;
            if (!runtime.Running)
            {
                var error = runtime.Error; runtime.Dispose(); runtime = null; learning = false;
                Log(error == null ? "Stopped. Session device visibility restored." : "Bridge stopped: " + error.Message);
                if (error != null) File.AppendAllText(logPath, error + Environment.NewLine);
                UpdateButtons(); if (closing) Close(); return;
            }
            var states = runtime.Snapshot();
            monitor.Text = String.Join(Environment.NewLine + Environment.NewLine, available.Where(d => states.ContainsKey(d.Id)).Select(d => {
                InputState state = states[d.Id];
                return d.Name + Environment.NewLine + String.Join("  ", d.Controls.Where(c => c.Kind == "Axis").Select(c => c.Name + "=" + state.Axes[c.Index].ToString("0.000"))) +
                    Environment.NewLine + "Buttons down: " + String.Join(", ", Enumerable.Range(0, 128).Where(i => state.Buttons[i]).Select(i => i + 1));
            }));
            if (learning)
            {
                Binding learned = null; double best = 0.15;
                foreach (var device in available.Where(d => states.ContainsKey(d.Id) && baseline.ContainsKey(d.Id)))
                    foreach (var control in device.Controls.Where(c => c.Kind == learnKind))
                    {
                        var current = states[device.Id]; var before = baseline[device.Id]; int i = control.Index;
                        double change = learnKind == "Axis" ? Math.Abs(current.Axes[i] - before.Axes[i]) : learnKind == "Button" ? current.Buttons[i] && !before.Buttons[i] ? 1 : 0 : current.Pov[i] >= 0 && current.Pov[i] != before.Pov[i] ? 1 : 0;
                        if (change <= best) continue;
                        best = change; learned = new Binding { DeviceId = device.Id, InputKind = learnKind, InputIndex = i, OutputKind = learnKind, OutputIndex = learnIndex, Invert = invertAxis.Checked, Deadzone = (double)deadzone.Value / 100 };
                    }
                if (learned != null)
                {
                    profile.Bindings.RemoveAll(b => b.OutputKind == learned.OutputKind && b.OutputIndex == learned.OutputIndex); profile.Bindings.Add(learned);
                    learning = false; RenderMappings(); Log("Mapped " + OutputName(learned) + ".");
                }
                else if (DateTime.UtcNow >= learnDeadline) { learning = false; Log("Learn timed out. Return the control to rest and try again."); }
            }
            if (runtime.Started) status.Text = (learning ? "Learning input... " : "Running. ") + runtime.Frames + " input frames; " + runtime.FfbPackets + " FFB packets received.";
        }
        private static string OutputName(Binding binding) { return (String.IsNullOrEmpty(binding.Label) ? "" : binding.Label + " / ") + (binding.OutputKind == "Axis" ? Mapper.AxisNames[binding.OutputIndex] : binding.OutputKind + " " + (binding.OutputIndex + 1)); }
        private void RenderMappings()
        {
            mappings.Rows.Clear();
            foreach (var binding in profile.Bindings.OrderBy(b => b.OutputKind).ThenBy(b => b.OutputIndex))
            {
                var device = available.FirstOrDefault(d => d.Id == binding.DeviceId);
                var control = device == null ? null : device.Controls.FirstOrDefault(c => c.Kind == binding.InputKind && c.Index == binding.InputIndex);
                int row = mappings.Rows.Add(OutputName(binding), device == null ? "Missing device" : device.Name, control == null ? binding.InputKind + " " + (binding.InputIndex + 1) : control.ToString(), (binding.Invert ? "Inverted; " : "") + "Deadzone " + (binding.Deadzone * 100).ToString("0") + "%");
                mappings.Rows[row].Tag = binding;
            }
        }
        private void UpdateButtons()
        {
            bool idle = runtime == null && !scanning;
            devices.Enabled = wheel.Enabled = enableFfb.Enabled = gain.Enabled = invert.Enabled = virtualId.Enabled = dll.Enabled = refresh.Enabled = save.Enabled = autoButtons.Enabled = remove.Enabled = idle;
            start.Enabled = idle && previewPath == null && !File.Exists(Store.RecoveryPath);
            watch.Enabled = idle && previewPath == null;
            stop.Enabled = runtime != null;
            recover.Enabled = idle && previewPath == null && File.Exists(Store.RecoveryPath);
            learn.Enabled = runtime != null;
            foreach (var action in setupActions) action.Enabled = idle && previewPath == null;
            setupWizard.Enabled = idle && previewPath == null;
        }
        private void InstallDriver(bool vjoy)
        {
            try
            {
                if (!File.Exists(DriverPackages.PathFor(vjoy))) { Open(vjoy ? "https://github.com/BrunnerInnovation/vJoy/releases" : "https://github.com/nefarius/HidHide/releases"); return; }
                DriverSetup.Install(vjoy);
                Log("Started the official " + (vjoy ? "vJoy" : "HidHide") + " installer. Follow its setup wizard; restart if requested, then click Check drivers.");
            }
            catch (Exception ex) { Log("Driver setup: " + ex.Message); }
        }
        private void Open(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { Log(ex.Message); } }
        private void Log(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            log.AppendText(line + Environment.NewLine); status.Text = message;
            try { File.AppendAllText(logPath, line + Environment.NewLine); }
            catch (IOException ex) { log.AppendText("Log file error: " + ex.Message + Environment.NewLine); }
            catch (UnauthorizedAccessException ex) { log.AppendText("Log file error: " + ex.Message + Environment.NewLine); }
        }
        private sealed class OutputChoice { public string Kind; public int Index; public string Name; public override string ToString() { return Name; } }
    }
}
