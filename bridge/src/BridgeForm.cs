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
        private readonly Button startForza = Button("Start Forza"), start = Button("Start bridge"), stop = Button("Stop / restore devices"), watch = Button("Monitor inputs"), refresh = Button("Refresh devices"), save = Button("Save profile"), learn = Button("Learn selected output"), autoButtons = Button("Map all buttons"), remove = Button("Remove mapping"), recover = Button("Restore visibility");
        private readonly ComboBox forzaTarget = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
        private readonly CheckBox startBridgeWithForza = new CheckBox { Text = "Start bridge when I click Start Forza", AutoSize = true };
        private readonly Button refreshForzaTargets = Button("Find installed games");
        private readonly Label forzaDiscoveryStatus = new Label { AutoSize = true };
        private readonly Button checkUpdates = Button("Check for updates");
        private readonly Label updateCheckStatus = new Label { AutoSize = true, Tag = "Muted" };
        private readonly ToolTip updateToolTips = new ToolTip();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 60 };
        private readonly string previewPath;
        private List<PhysicalDevice> available = new List<PhysicalDevice>();
        private BridgeProfile profile = new BridgeProfile();
        private BridgeRuntime runtime;
        private bool scanning, closing, learning, profileConfigBlocked, optionsConfigBlocked;
        private DateTime learnDeadline;
        private string learnKind;
        private int learnIndex;
        private Dictionary<string, InputState> baseline;
        private readonly string logPath;
        private readonly List<Control> setupActions = new List<Control>();
        private readonly List<UpdateRowControls> updateRows = new List<UpdateRowControls>();
        private readonly Button setupWizard = Button("Set up my rig");
        private readonly StrengthSlider strength = new StrengthSlider { Dock = DockStyle.Fill };
        private readonly Label strengthValue = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 36) };
        private readonly Label gainHint = new Label { AutoSize = true, Tag = "Muted" };
        private readonly Label bridgeState = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 22) };
        private readonly Label packetCount = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 22), Text = "0" };
        private readonly Label wheelStarts = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 22), Text = "0" };
        private readonly Label rigSummary = new Label { AutoSize = true, Tag = "Muted" }, wheelName = new Label { AutoSize = true, Tag = "Muted" };
        private readonly Label pageTitle = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 25) }, pageHint = new Label { AutoSize = true, Tag = "Muted" };
        private readonly FlowLayoutPanel rigDevices = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        private readonly Button day = Button("Day"), night = Button("Night");
        private readonly Panel deck = new Panel { Dock = DockStyle.Fill, Tag = "Page" };
        private readonly Dictionary<string, Control> views = new Dictionary<string, Control>();
        private readonly Dictionary<string, Button> navigation = new Dictionary<string, Button>();
        private readonly string previewPage;
        private readonly string profilePath;
        private readonly string optionsPath;
        private ShimOptions options = new ShimOptions();
        private bool launchingForza, scanningForza, checkingUpdates, updatingComponent;
        private bool syncingGain, pendingGainSave;
        private DateTime gainSaveAt;
        private string currentPage = "Drive";
        public BridgeForm(string previewPath, string previewMode = null, string previewPage = null)
            : this(previewPath, previewMode, previewPage, Store.ProfilePath, Store.OptionsPath) { }
        internal BridgeForm(string previewPath, string previewMode, string previewPage, string profilePath)
            : this(previewPath, previewMode, previewPage, profilePath, Store.OptionsPath) { }
        internal BridgeForm(string previewPath, string previewMode, string previewPage, string profilePath, string optionsPath)
        {
            this.previewPath = previewPath; this.previewPage = previewPage; this.profilePath = profilePath; this.optionsPath = optionsPath;
            Appearance.Load(); if (previewMode != null) Appearance.Set(previewMode, false);
            Text = "Shim"; Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); Font = new Font("Segoe UI", 10);
            ClientSize = new Size(1280, 860); MinimumSize = new Size(1120, 790); AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen; DoubleBuffered = true;
            logPath = Path.Combine(Store.DirectoryPath, "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            if (File.Exists(profilePath))
                try { profile = Store.LoadProfile(profilePath, new BridgeProfile()); }
                catch (Exception ex) { profileConfigBlocked = true; Log("Could not load the saved profile; Shim will not overwrite it. " + ex.Message); }
            if (File.Exists(optionsPath))
                try { options = Store.LoadOptions(optionsPath); }
                catch (Exception ex) { optionsConfigBlocked = true; Log("Could not load saved Options; Shim will not overwrite them. " + ex.Message); }
            Build();
            enableFfb.Checked = profile.EnableFfb; invert.Checked = profile.InvertFfb;
            virtualId.Value = Math.Max(1, Math.Min(16, profile.VirtualId));
            strength.Value = Math.Max(0, Math.Min(100, profile.FfbGain)); strengthValue.Text = strength.Value + "%";
            dll.Text = String.IsNullOrEmpty(profile.VjoyDll) ? Vjoy.DefaultDll : profile.VjoyDll;
            startBridgeWithForza.Checked = options.StartBridgeWithForza;
            refresh.Click += async (s, e) => await RefreshAsync();
            save.Click += (s, e) => SaveProfile();
            setupWizard.Click += async (s, e) => await RunSetup();
            startForza.Click += async (s, e) => await StartForzaAsync();
            checkUpdates.Click += async (s, e) => await CheckForUpdatesAsync();
            startBridgeWithForza.CheckedChanged += (s, e) => SaveOptions();
            forzaTarget.SelectedIndexChanged += (s, e) => SaveOptions();
            refreshForzaTargets.Click += async (s, e) => await RefreshForzaTargets();
            strength.ValueChanged += (s, e) => GainChanged(strength.Value);
            day.Click += (s, e) => ChangeMode("Day"); night.Click += (s, e) => ChangeMode("Night");
            Appearance.Changed += ApplyAppearance;
            enableFfb.CheckedChanged += (s, e) => UpdateOverview();
            watch.Click += (s, e) => Start(true);
            start.Click += (s, e) => Start(false);
            stop.Click += (s, e) => { if (runtime != null) { runtime.RequestStop(); status.Text = "Stopping forces and restoring device visibility..."; } };
            learn.Click += (s, e) => BeginLearn();
            autoButtons.Click += (s, e) => AutoMapButtons();
            remove.Click += (s, e) => { if (mappings.SelectedRows.Count > 0) { profile.Bindings.Remove((Binding)mappings.SelectedRows[0].Tag); RenderMappings(); } };
            recover.Click += (s, e) => {
                try
                {
                    if (runtime != null) throw new InvalidOperationException("Stop the bridge before restoring visibility.");
                    HideSession.RecoverFile(Store.RecoveryPath, true); Log("Device visibility restored.");
                }
                catch (Exception ex) { Log(ex.Message); }
                UpdateButtons();
            };
            devices.ItemCheck += (s, e) => { if (!scanning && runtime == null) BeginInvoke(new Action(CaptureSelections)); };
            timer.Tick += (s, e) => TickRuntime(); timer.Start();
            Shown += async (s, e) => {
                await RefreshAsync();
                if (previewPath == null) await RefreshForzaTargets();
                if (previewPath != null)
                {
                    if (this.previewPage != null) ShowView(this.previewPage);
                    using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(previewPath, System.Drawing.Imaging.ImageFormat.Png); }
                    Close();
                }
                else if (profile.SetupVersion < SetupLogic.Version) await RunSetup();
            };
            FormClosing += (s, e) => { if (runtime != null) { e.Cancel = true; closing = true; runtime.RequestStop(); status.Text = "Stopping the bridge before closing..."; } else FlushGain(true); };
            FormClosed += (s, e) => { timer.Dispose(); updateToolTips.Dispose(); Appearance.Changed -= ApplyAppearance; };
            if (previewPath != null) { ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Location = new Point(-20000, -20000); }
            ApplyAppearance(); UpdateButtons();
        }
        private static Button Button(string text) { return new ModernButton { Text = text, AutoSize = true, MinimumSize = new Size(90, 40), Padding = new Padding(13, 5, 13, 5), Margin = new Padding(0, 0, 8, 0) }; }
        private static Label Label(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 5, 10, 5) }; }
        private static FlowLayoutPanel Flow(params Control[] controls)
        { var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) }; panel.Controls.AddRange(controls); return panel; }
        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 204)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var rail = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 30, 18, 20), RowCount = 3, ColumnCount = 1, Tag = "Surface" };
            rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 112)); rail.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 146));
            var brand = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 7, 0, 0) };
            var brandImage = Appearance.BrandImage;
            if (brandImage != null) brand.Controls.Add(new PictureBox { Image = brandImage, Size = new Size(82, 82), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent, Margin = new Padding(0, 0, 10, 0) });
            var brandCopy = Stack(); brandCopy.Margin = new Padding(0, 17, 0, 0);
            brandCopy.Controls.Add(new Label { Text = "Shim", Font = new Font("Segoe UI Semibold", 21), AutoSize = true, Tag = "Accent" });
            brandCopy.Controls.Add(new Label { Text = "FORZA BRIDGE", Font = new Font("Segoe UI Semibold", 8), AutoSize = true, Tag = "Muted" }); brand.Controls.Add(brandCopy); rail.Controls.Add(brand, 0, 0);
            var menu = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            foreach (string name in new[] { "Drive", "Mappings", "Live inputs", "Drivers", "Options", "Updates", "Activity" })
            {
                string target = name; var button = Button(name); button.AutoSize = false; button.Size = new Size(168, 44); button.TextAlign = ContentAlignment.MiddleLeft; button.Margin = new Padding(0, 0, 0, 8);
                button.Click += (s, e) => ShowView(target); navigation.Add(name, button); menu.Controls.Add(button);
            }
            rail.Controls.Add(menu, 0, 1);
            var railFooter = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            var help = Button("Help & reports"); help.AutoSize = false; help.Size = new Size(168, 44); help.TextAlign = ContentAlignment.MiddleLeft; help.Margin = new Padding(0, 0, 0, 8);
            help.Click += (s, e) => ShowView("Help"); navigation.Add("Help", help); railFooter.Controls.Add(help);
            setupWizard.Width = 168; setupWizard.AutoSize = false; setupWizard.Tag = "Active"; railFooter.Controls.Add(setupWizard);
            var version = Label("Version 0.8.0"); version.Tag = "Muted"; version.Font = new Font("Segoe UI", 9); railFooter.Controls.Add(version); rail.Controls.Add(railFooter, 0, 2); root.Controls.Add(rail, 0, 0);
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26, 24, 26, 20), ColumnCount = 1, RowCount = 3, Tag = "Page" };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 88)); content.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); content.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            var titles = Stack(); titles.Controls.Add(pageTitle); titles.Controls.Add(pageHint); header.Controls.Add(titles, 0, 0);
            day.AutoSize = night.AutoSize = false; day.MinimumSize = night.MinimumSize = new Size(82, 36); day.Size = night.Size = new Size(82, 36);
            day.Padding = night.Padding = new Padding(3, 0, 3, 0);
            header.Controls.Add(Flow(day, night), 1, 0); content.Controls.Add(header, 0, 0); content.Controls.Add(deck, 0, 1);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 18, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
            status.Tag = "Muted"; status.AutoEllipsis = true; footer.Controls.Add(status, 0, 0);
            start.Tag = "Primary"; stop.Tag = "Danger"; start.Text = "Start bridge"; stop.Text = "Stop bridge"; start.MinimumSize = new Size(124, 42); stop.MinimumSize = new Size(124, 42);
            startForza.Tag = "Primary"; startForza.Text = "Start Forza"; startForza.MinimumSize = new Size(124, 42);
            footer.Controls.Add(Flow(startForza, start, stop), 1, 0); content.Controls.Add(footer, 0, 2); root.Controls.Add(content, 1, 0); Controls.Add(root);
            BuildDrive(); BuildMappings(); BuildDrivers(); BuildOptions(); BuildUpdates(); BuildHelp();
            var liveCard = new SurfaceCard { Dock = DockStyle.Fill }; liveCard.Controls.Add(monitor); AddView("Live inputs", liveCard);
            var activityCard = new SurfaceCard { Dock = DockStyle.Fill }; activityCard.Controls.Add(log); AddView("Activity", activityCard);
            status.Text = "Ready. Start the bridge, then launch Forza."; ShowView("Drive");
        }
        private static FlowLayoutPanel Stack()
        { return new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) }; }
        private static Label Heading(string text) { return new Label { Text = text, Font = new Font("Segoe UI Semibold", 13), AutoSize = true, Margin = new Padding(0, 0, 0, 8) }; }
        private void AddView(string name, Control view) { view.Dock = DockStyle.Fill; view.Visible = false; deck.Controls.Add(view); views.Add(name, view); }
        private void ShowView(string name)
        {
            if (!views.ContainsKey(name)) return; currentPage = name;
            foreach (var item in views) item.Value.Visible = item.Key == name;
            views[name].BringToFront(); pageTitle.Text = name == "Drive" ? "Your rig" : name;
            pageHint.Text = name == "Drive" ? "One virtual wheel. Ready for the next drive." : name == "Mappings" ? "Connect each physical control to your virtual wheel." : name == "Live inputs" ? "Readings from your selected devices." : name == "Drivers" ? "One-time setup and advanced connection settings." : name == "Options" ? "Choose how Shim launches your Forza game." : name == "Updates" ? "Check current versions against published GitHub releases." : name == "Activity" ? "Session events and force feedback diagnostics." : name == "Help" ? "Things to try when Forza has no force feedback." : "";
            foreach (var item in navigation) item.Value.Tag = item.Key == name ? "Active" : "Nav";
            Appearance.Apply(this);
        }
        private void BuildDrive()
        {
            var drive = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Tag = "Page" };
            drive.RowStyles.Add(new RowStyle(SizeType.Absolute, 120)); drive.RowStyles.Add(new RowStyle(SizeType.Absolute, 18)); drive.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); drive.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            var stats = new SurfaceCard { Dock = DockStyle.Fill }; var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            foreach (var value in new[] { new { Title = "BRIDGE", Value = bridgeState }, new { Title = "FFB PACKETS", Value = packetCount }, new { Title = "WHEEL STARTS ACCEPTED", Value = wheelStarts } })
            {
                var item = Stack(); var label = Label(value.Title); label.Tag = "Muted"; label.Font = new Font("Segoe UI Semibold", 8.5f); item.Controls.Add(label); item.Controls.Add(value.Value);
                columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f)); columns.Controls.Add(item);
            }
            stats.Controls.Add(columns); drive.Controls.Add(stats, 0, 0);
            var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58)); cards.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18)); cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            var forceCard = new SurfaceCard { Dock = DockStyle.Fill }; var forceLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
            foreach (int height in new[] { 36, 32, 82, 52, 40, 48 }) forceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); forceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            forceLayout.Controls.Add(Heading("Force feedback"), 0, 0); forceLayout.Controls.Add(wheelName, 0, 1); forceLayout.Controls.Add(strengthValue, 0, 2); forceLayout.Controls.Add(strength, 0, 3);
            var endpoints = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 }; endpoints.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); endpoints.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            endpoints.Controls.Add(new Label { Text = "0% / Off", Dock = DockStyle.Fill, Tag = "Muted" }, 0, 0); endpoints.Controls.Add(new Label { Text = "100% / Full", Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopRight, Tag = "Muted" }, 1, 0);
            forceLayout.Controls.Add(endpoints, 0, 4); forceLayout.Controls.Add(gainHint, 0, 5);
            var enableArea = Stack(); enableFfb.Text = "Forward game force feedback"; enableArea.Controls.Add(enableFfb); var note = Label("0% mutes forces. Change strength while driving."); note.Tag = "Muted"; note.MaximumSize = new Size(440, 0); enableArea.Controls.Add(note); forceLayout.Controls.Add(enableArea, 0, 6);
            forceCard.Controls.Add(forceLayout); cards.Controls.Add(forceCard, 0, 0);
            var rigCard = new SurfaceCard { Dock = DockStyle.Fill }; var rigLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            rigLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); rigLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); rigLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            rigLayout.Controls.Add(Heading("Included devices"), 0, 0); rigLayout.Controls.Add(rigDevices, 0, 1); rigLayout.Controls.Add(rigSummary, 0, 2); rigCard.Controls.Add(rigLayout); cards.Controls.Add(rigCard, 2, 0); drive.Controls.Add(cards, 0, 2);
            var guide = Label("Start the bridge before Forza. Stop bridge restores device visibility."); guide.Tag = "Muted"; guide.Margin = new Padding(0, 14, 0, 0); drive.Controls.Add(guide, 0, 3); AddView("Drive", drive);
        }
        private void BuildMappings()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var sourceCard = new SurfaceCard { Dock = DockStyle.Fill, Padding = new Padding(14, 20, 14, 16) }; var sources = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            sources.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            sources.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); sources.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); sources.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            var sourceActions = Flow(refresh, watch); sourceActions.AutoSize = false;
            sources.Controls.Add(Heading("Physical devices"), 0, 0); sources.Controls.Add(devices, 0, 1); sources.Controls.Add(sourceActions, 0, 2); sourceCard.Controls.Add(sources); layout.Controls.Add(sourceCard, 0, 0);
            var mappingCard = new SurfaceCard { Dock = DockStyle.Fill, Padding = new Padding(14, 20, 14, 14) }; var area = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            area.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); area.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); area.RowStyles.Add(new RowStyle(SizeType.Absolute, 96)); area.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            area.Controls.Add(Heading("Control mappings"), 0, 0);
            mappings.Columns.Add("output", "Virtual output"); mappings.Columns.Add("device", "Physical device"); mappings.Columns.Add("input", "Input"); mappings.Columns.Add("calibration", "Options"); area.Controls.Add(mappings, 0, 1);
            for (int i = 0; i < 8; i++) outputControl.Items.Add(new OutputChoice { Kind = "Axis", Index = i, Name = Mapper.AxisNames[i] });
            for (int i = 0; i < 128; i++) outputControl.Items.Add(new OutputChoice { Kind = "Button", Index = i, Name = "Button " + (i + 1) });
            for (int i = 0; i < 4; i++) outputControl.Items.Add(new OutputChoice { Kind = "POV", Index = i, Name = "POV " + (i + 1) });
            outputControl.SelectedIndex = 0;
            learn.Text = "Learn control"; autoButtons.Text = "Auto-map buttons"; remove.Text = "Remove"; refresh.Text = "Refresh devices";
            area.Controls.Add(Flow(outputControl, invertAxis, Label("Deadzone %"), deadzone, learn), 0, 2); area.Controls.Add(Flow(autoButtons, remove, save), 0, 3); mappingCard.Controls.Add(area); layout.Controls.Add(mappingCard, 2, 0); AddView("Mappings", layout);
        }
        private void BuildDrivers()
        {
            var card = new SurfaceCard { Dock = DockStyle.Fill }; var setup = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            setup.Controls.Add(Heading("Driver health")); prerequisites.MaximumSize = new Size(820, 0); setup.Controls.Add(prerequisites);
            Button getVjoy = Button(File.Exists(DriverPackages.PathFor(true)) ? "Install bundled vJoy" : "Get official vJoy driver"),
                getHide = Button(File.Exists(DriverPackages.PathFor(false)) ? "Install bundled HidHide" : "Get official HidHide driver"),
                choose = Button("Select vJoy DLL"), check = Button("Check drivers");
            getVjoy.Click += (s, e) => InstallDriver(true);
            getHide.Click += (s, e) => InstallDriver(false);
            choose.Click += async (s, e) => { using (var dialog = new OpenFileDialog { Filter = "vJoyInterface.dll|vJoyInterface.dll", FileName = "vJoyInterface.dll" }) if (dialog.ShowDialog(this) == DialogResult.OK) { dll.Text = dialog.FileName; await CheckDrivers(); } };
            check.Click += async (s, e) => await CheckDrivers();
            setupActions.AddRange(new Control[] { getVjoy, getHide, choose, check });
            setup.Controls.Add(Flow(getVjoy, getHide, check));
            var intro = Label("Use Set up my rig for guided installation, virtual wheel configuration and calibration."); intro.Tag = "Muted"; intro.MaximumSize = new Size(810, 0); setup.Controls.Add(intro);
            setup.Controls.Add(Heading("Bridge settings")); setup.Controls.Add(Flow(Label("FFB wheel"), wheel)); setup.Controls.Add(invert); setup.Controls.Add(Flow(Label("Virtual device"), virtualId));
            setup.Controls.Add(Heading("Advanced")); setup.Controls.Add(Flow(Label("vJoy library"), dll, choose)); setup.Controls.Add(recover);
            var hint = Label("Device visibility is restored on stop. If Pit House needs access while bridging, add it to HidHide's Applications list. Keep Forza off that list."); hint.Tag = "Muted"; hint.MaximumSize = new Size(810, 0); setup.Controls.Add(hint);
            card.Controls.Add(setup); AddView("Drivers", card);
        }
        private void BuildOptions()
        {
            var card = new SurfaceCard { Dock = DockStyle.Fill };
            var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            layout.Controls.Add(Heading("Forza launch"));
            var targetLabel = Label("Game to start"); targetLabel.Tag = "Muted";
            layout.Controls.Add(Flow(targetLabel, forzaTarget, refreshForzaTargets));
            layout.Controls.Add(startBridgeWithForza);
            var note = Label("When enabled, Start Forza starts the bridge if needed, waits for it to report Running, then opens the selected game. If bridge startup fails, the game is left closed.");
            note.Tag = "Muted"; note.MaximumSize = new Size(830, 0); note.Margin = new Padding(0, 0, 0, 18); layout.Controls.Add(note);
            forzaDiscoveryStatus.Text = "Shim can find installed Forza games from the Xbox app, Steam, and Start Menu shortcuts.";
            forzaDiscoveryStatus.Tag = "Muted"; forzaDiscoveryStatus.MaximumSize = new Size(830, 0); layout.Controls.Add(forzaDiscoveryStatus);
            card.Controls.Add(layout); AddView("Options", card);
        }
        private void BuildUpdates()
        {
            var card = new SurfaceCard { Dock = DockStyle.Fill };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(Heading("Software updates"), 0, 0);
            var actions = Flow(checkUpdates, updateCheckStatus); actions.WrapContents = false; layout.Controls.Add(actions, 0, 1);
            var note = Label("Shim updates apply and restart. Driver installers are signature checked and use Windows approval. vJoy updates its matching SDK.");
            note.Tag = "Muted"; note.MaximumSize = new Size(900, 0); note.Margin = new Padding(0, 10, 0, 2); layout.Controls.Add(note, 0, 2);
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 5, RowCount = 4, Margin = new Padding(0, 8, 0, 0) };
            foreach (float width in new[] { 18f, 19f, 17f, 27f, 19f }) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            foreach (string text in new[] { "Component", "Current version", "Latest release", "Status", "Action" })
            { var header = Label(text); header.Tag = "Muted"; header.Font = new Font("Segoe UI Semibold", 9); header.Dock = DockStyle.Fill; table.Controls.Add(header, table.Controls.Count, 0); }
            AddUpdateRow(table, "Shim");
            AddUpdateRow(table, "vJoy");
            AddUpdateRow(table, "HidHide");
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; scroll.Controls.Add(table); layout.Controls.Add(scroll, 0, 3);
            card.Controls.Add(layout); AddView("Updates", card);
        }
        private void BuildHelp()
        {
            var card = new SurfaceCard { Dock = DockStyle.Fill };
            var checklist = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Tag = "Surface", Padding = new Padding(2, 2, 8, 8) };
            checklist.Controls.Add(Heading("If Forza has no force feedback"));
            checklist.Controls.Add(HelpText("Try these checks in order. Steering working with no force usually means the game is not sending FFB to Shim's virtual wheel, or the bridge cannot send it to the physical wheel."));
            var createReport = Button("Create / submit report"); createReport.Tag = "Primary";
            createReport.Click += async (s, e) => await CreateSupportReportAsync();
            createReport.Margin = new Padding(0, 0, 0, 16); checklist.Controls.Add(createReport);

            checklist.Controls.Add(Heading("1. Start the bridge before Forza"));
            checklist.Controls.Add(HelpText("Close Forza completely. In Shim, start the bridge and wait for Running. Then start Forza, preferably with Start Forza or the Start bridge when I click Start Forza option. Restart the game any time you stop or restart the bridge."));

            checklist.Controls.Add(Heading("2. Check the device Forza is using"));
            checklist.Controls.Add(HelpText("In Shim's Drivers page, confirm vJoy and HidHide are ready. In Forza, use a custom wheel profile bound to Shim's vJoy virtual wheel. While the bridge is running, HidHide should conceal the physical devices selected in Shim so the virtual wheel is the game-facing controller. Temporarily disconnect or disable other game controllers if they take Device 1."));

            checklist.Controls.Add(Heading("3. Confirm the FFB route and gain"));
            checklist.Controls.Add(HelpText("On Shim's Drive page, select the MOZA base as the FFB wheel, enable Forward game force feedback, and set Shim strength above 0%. In Forza's wheel settings, make sure force feedback is enabled and its scale is above 0. Check that the base is powered and recognized by its driver software."));

            checklist.Controls.Add(Heading("4. Read Shim's counters"));
            checklist.Controls.Add(HelpText("FFB packets received staying at 0 means the game has not sent force data to this vJoy session. Recheck the custom wheel profile and device assignment, then restart Forza after the bridge is running."));
            checklist.Controls.Add(HelpText("Packets increasing but no wheel starts means the game has sent effects, but they have not started. Check the in-game FFB settings and test while driving, not only in menus."));
            checklist.Controls.Add(HelpText("Wheel starts increasing with no force points to the physical wheel route. Confirm the selected FFB wheel, power, cables and driver status. Open Activity for the exact driver error."));

            checklist.Controls.Add(Heading("5. Check app permissions and competing software"));
            checklist.Controls.Add(HelpText("Shim normally does not need to run as administrator after setup; Windows will request approval for driver installation and vJoy configuration when needed. Launch Shim and Forza normally. If either is explicitly set to Run this program as an administrator in its Compatibility properties, turn that off for both and retry. Close other bridges, wheel testers or apps that may exclusively own the wheel. If Activity reports DIERR_NOTEXCLUSIVEACQUIRED or access denied, stop those apps before restarting the bridge. 0x800704DF is a different, already-initialized effect error fixed in current Shim builds; update Shim if it appears."));

            checklist.Controls.Add(Heading("6. Share useful details if it still fails"));
            checklist.Controls.Add(HelpText("Include FH5 or FH6 and whether it is Steam or Xbox app, your wheel model, Shim/vJoy/HidHide versions, the FFB packets and wheel starts counters, and the exact Activity error. Do not post serial numbers or account details."));

            var openDrivers = Button("Open Drivers"); openDrivers.Tag = "Ghost";
            var openActivity = Button("Open Activity"); openActivity.Tag = "Ghost";
            var actions = Flow(openDrivers, openActivity);
            actions.Controls[0].Click += (s, e) => ShowView("Drivers");
            actions.Controls[1].Click += (s, e) => ShowView("Activity");
            checklist.Controls.Add(actions);
            checklist.Controls.Add(Heading("What a report includes"));
            checklist.Controls.Add(HelpText("Shim collects your Windows and Shim versions, CPU model/core counts, installed memory, graphics adapters and driver versions, racing wheel/controller names and capabilities, relevant wheel/controller driver details, limited Shim settings (FFB enabled/strength, selected wheel name and device count), bridge state and recent FFB counters, and up to three recent Activity logs plus startup/watchdog diagnostics. Controller names and log messages can identify your equipment and may contain troubleshooting details."));
            checklist.Controls.Add(HelpText("Shim does not collect your Forza account data, button mappings, calibration, or unrelated files. It does not intentionally collect your Windows account name or hardware serial numbers; diagnostic logs may contain details from driver errors, so inspect the ZIP before sharing. Reports are created on your PC first."));
            checklist.Controls.Add(HelpText("If you submit a report, it is sent over HTTPS to Shim support and visible only to the Shim maintainer for troubleshooting. It is not sold, published, or shared with other people. Cloudflare provides the private network and storage service and may process normal request metadata. Reports expire at 30 days; daily cleanup removes expired reports within 24 hours. Uploading is optional."));
            checklist.Controls.Add(HelpText("Email me a copy creates a draft in your email app with the ZIP attached and addressed to you. Shim does not send that email; review it and press Send yourself. Your email app/provider will handle it if you choose to send."));
            card.Controls.Add(checklist); AddView("Help", card);
        }
        private async Task CreateSupportReportAsync()
        {
            string reportPath = null;
            try
            {
                string generatedPath = SupportReports.CreateBundle(profile, runtime, lastPackets, lastStarts);
                using (var dialog = new SaveFileDialog {
                    Title = "Save Shim support report",
                    Filter = "Shim support report (*.zip)|*.zip",
                    FileName = Path.GetFileName(generatedPath),
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    OverwritePrompt = true
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK)
                    {
                        Log("Support report saved locally: " + generatedPath);
                        return;
                    }
                    reportPath = dialog.FileName;
                    if (!String.Equals(Path.GetFullPath(generatedPath), Path.GetFullPath(reportPath), StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(generatedPath, reportPath, true);
                        File.Delete(generatedPath);
                    }
                }
                var answer = ChooseSupportReportAction(reportPath);
                if (answer == "Email")
                {
                    string address = PromptForReportEmail();
                    if (address == null) return;
                    string draft = SupportReports.CreateEmailDraft(reportPath, address);
                    Log("Support report email draft created locally: " + draft);
                    MessageBox.Show(this, "Your email app opened a draft with the report attached. Shim has not sent it. Check the recipient and attachment, then press Send if you want to email it.", "Email draft ready", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (answer != "Upload") { Log("Support report kept locally: " + reportPath); return; }
                status.Text = "Uploading support report...";
                string reportId = await SupportReports.UploadAsync(reportPath);
                Log("Support report uploaded. Reference: " + reportId);
                MessageBox.Show(this, "Report uploaded over HTTPS to private Shim support storage. It is used for troubleshooting and expires at 30 days; daily cleanup removes expired reports within 24 hours. Reference: " + reportId + "\r\n\r\nYour local copy remains at:\r\n" + reportPath, "Support report submitted", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("Support report: " + ex.Message);
                MessageBox.Show(this, "The report could not be uploaded. The local copy was kept if it was created.\r\n\r\n" + ex.Message + (reportPath == null ? "" : "\r\n\r\n" + reportPath), "Support report", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        private string ChooseSupportReportAction(string path)
        {
            using (var dialog = new Form { Text = "Review your support report", StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, ClientSize = new Size(640, 380), AutoScaleMode = AutoScaleMode.Dpi, BackColor = Appearance.Colors.Surface, ForeColor = Appearance.Colors.Text })
            {
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(18) };
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
                var textArea = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
                var text = new Label { AutoSize = true, MaximumSize = new Size(570, 0), ForeColor = Appearance.Colors.Text, Text = "Your report is saved locally and ready to inspect:\r\n" + path + "\r\n\r\nSubmitting sends it over HTTPS to private Shim support storage, visible only to the Shim maintainer for troubleshooting. It is not published or shared with other people. Cloudflare provides the network and storage service. It expires at 30 days and daily cleanup removes expired reports within 24 hours. Upload is optional." };
                textArea.Controls.Add(text); layout.Controls.Add(textArea, 0, 0);
                var local = Button("Keep local"); local.DialogResult = DialogResult.No;
                var email = Button("Email me a copy"); email.DialogResult = DialogResult.Retry;
                var upload = Button("Submit to support"); upload.Tag = "Primary"; upload.DialogResult = DialogResult.Yes;
                var choices = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
                choices.Controls.AddRange(new Control[] { upload, email, local }); layout.Controls.Add(choices, 0, 1);
                dialog.Controls.Add(layout); dialog.CancelButton = local; Appearance.Apply(dialog);
                DialogResult result = dialog.ShowDialog(this);
                return result == DialogResult.Retry ? "Email" : result == DialogResult.Yes ? "Upload" : "Local";
            }
        }
        private string PromptForReportEmail()
        {
            using (var dialog = new Form { Text = "Email yourself a report copy", StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, ClientSize = new Size(460, 150), BackColor = Color.FromArgb(25, 28, 32), ForeColor = Color.White })
            {
                var label = new Label { Left = 16, Top = 15, Width = 426, Height = 38, Text = "Enter your address. Shim will open a draft; it will not send it." };
                var address = new TextBox { Left = 16, Top = 57, Width = 426 };
                var ok = new Button { Text = "Open email draft", Left = 281, Top = 98, Width = 160, Height = 36, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Left = 190, Top = 98, Width = 80, Height = 36, DialogResult = DialogResult.Cancel };
                dialog.Controls.AddRange(new Control[] { label, address, ok, cancel }); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                return address.Text.Trim();
            }
        }
        private static Label HelpText(string text)
        {
            var label = Label(text); label.MaximumSize = new Size(780, 0); label.Margin = new Padding(0, 0, 12, 12); return label;
        }
        private void AddUpdateRow(TableLayoutPanel table, string name)
        {
            int rowIndex = updateRows.Count + 1;
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            var row = new UpdateRowControls { Name = name };
            row.Component = Label(name); row.Component.Font = new Font("Segoe UI Semibold", 10);
            row.Current = Cell("Not checked"); row.Latest = Cell("Not checked"); row.Status = Cell("Not checked"); row.Status.Tag = "Muted";
            row.Action = Button("Check first"); row.Action.Tag = "Ghost"; row.Action.MinimumSize = new Size(130, 40); row.Action.Enabled = false;
            row.Action.Click += async (s, e) => await RunUpdateActionAsync(row);
            table.Controls.Add(row.Component, 0, rowIndex); table.Controls.Add(row.Current, 1, rowIndex); table.Controls.Add(row.Latest, 2, rowIndex);
            table.Controls.Add(row.Status, 3, rowIndex); table.Controls.Add(row.Action, 4, rowIndex); updateRows.Add(row);
        }
        private static Label Cell(string text)
        { return new Label { Text = text, Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 4, 8, 4) }; }
        private async Task CheckForUpdatesAsync()
        {
            if (checkingUpdates || previewPath != null) return;
            checkingUpdates = true; checkUpdates.Text = "Checking..."; updateCheckStatus.Text = "Contacting GitHub for the latest published releases..."; UpdateButtons();
            try
            {
                var results = await UpdateChecker.CheckAsync();
                foreach (var result in results)
                {
                    var row = updateRows.First(x => x.Name == result.Name);
                    row.Info = result; row.Download = null;
                    row.Current.Text = result.Current; row.Latest.Text = result.Latest;
                    row.Status.Text = result.Status == "Update available" && !String.IsNullOrEmpty(result.DownloadProblem) ? "Update unavailable" : result.Status;
                    row.Status.AccessibleDescription = result.Details + (String.IsNullOrEmpty(result.DownloadProblem) ? "" : " " + result.DownloadProblem);
                    updateToolTips.SetToolTip(row.Status, row.Status.AccessibleDescription);
                    row.Status.Tag = row.Status.Text == "Update available" ? "Accent" : row.Status.Text == "Up to date" ? "Success" : row.Status.Text == "Check failed" || row.Status.Text == "Update unavailable" ? "Danger" : "Muted";
                    Appearance.Apply(row.Status);
                    row.CanUpdate = row.Status.Text == "Update available" && String.IsNullOrEmpty(result.DownloadProblem);
                    row.Action.Text = row.CanUpdate ? "Update now" : "Check first";
                    row.Action.Tag = row.CanUpdate ? "Primary" : "Ghost";
                    Appearance.Apply(row.Action);
                    row.Action.AccessibleName = "Download and apply the " + row.Name + " update";
                    row.Action.AccessibleDescription = result.Details + (String.IsNullOrEmpty(result.DownloadProblem) ? "" : " " + result.DownloadProblem);
                    row.Action.Enabled = row.Status.Text == "Update available" && !checkingUpdates && runtime == null;
                }
                updateCheckStatus.Text = "Finished checking " + results.Count + " components.";
            }
            catch (Exception ex) { updateCheckStatus.Text = "Update check failed: " + ex.Message; }
            finally { checkingUpdates = false; checkUpdates.Text = "Check for updates"; UpdateButtons(); }
        }
        private sealed class UpdateRowControls
        {
            public string Name;
            public bool CanUpdate;
            public UpdateInfo Info;
            public DownloadedUpdate Download;
            public Label Component, Current, Latest, Status;
            public Button Action;
        }
        private async Task RunUpdateActionAsync(UpdateRowControls row)
        {
            if (row == null || row.Info == null || updatingComponent) return;
            if (runtime != null) { Log("Stop the bridge and restore device visibility before updating Shim or a driver."); return; }
            updatingComponent = true; row.Action.Enabled = false;
            try
            {
                if (row.Download == null)
                {
                    row.Action.Text = "Downloading..."; row.Status.Text = "Downloading from GitHub..."; row.Status.Tag = "Muted"; Appearance.Apply(row.Status);
                    updateCheckStatus.Text = "Downloading and verifying " + row.Name + "...";
                    row.Download = await UpdateInstaller.DownloadAsync(row.Info, (received, total) =>
                    {
                        if (IsDisposed || !IsHandleCreated) return;
                        try { BeginInvoke(new Action(() => { if (!IsDisposed) row.Status.Text = "Downloading " + FormatBytes(received) + " / " + FormatBytes(total); })); }
                        catch (InvalidOperationException) { }
                    });
                    row.Status.Text = row.Name == "Shim" ? "Package SHA-256 verified" : "Publisher, signature, and version verified";
                    row.Status.Tag = "Success"; Appearance.Apply(row.Status);
                }
                if (row.Name == "Shim")
                {
                    string helper = UpdateInstaller.PrepareShimUpdater(row.Download.PrimaryPath, row.Info.Latest);
                    UpdateInstaller.StartShimUpdate(helper, row.Download.PrimaryPath, Application.StartupPath);
                    updateCheckStatus.Text = "Shim is closing to apply the update, then it will restart.";
                }
                else
                {
                    string helper = Path.Combine(Application.StartupPath, "Shim.Updater.exe");
                    if (!File.Exists(helper)) throw new FileNotFoundException("Shim's update helper is missing. Install the latest Shim package, then try again.", helper);
                    UpdateInstaller.StartDriverUpdate(helper, row.Download, row.Info, Application.StartupPath);
                    updateCheckStatus.Text = row.Name + " installer is starting. Shim will reopen after it finishes.";
                }
                Close();
                return;
            }
            catch (Exception ex)
            {
                row.Download = null; row.Action.Text = "Retry update"; row.Status.Text = ex.Message; row.Status.Tag = "Danger"; Appearance.Apply(row.Status);
                row.Status.AccessibleDescription = ex.ToString(); updateToolTips.SetToolTip(row.Status, ex.Message);
                updateCheckStatus.Text = row.Name + " update stopped. Review the row message and retry if appropriate.";
                Log(row.Name + " update: " + ex.Message);
            }
            finally { updatingComponent = false; UpdateButtons(); }
        }
        private static string FormatBytes(long bytes)
        {
            return bytes >= 1024 * 1024 ? (bytes / 1048576.0).ToString("0.0") + " MB" : Math.Max(1, bytes / 1024) + " KB";
        }
        private async Task RefreshForzaTargets()
        {
            if (scanningForza) return;
            scanningForza = true; refreshForzaTargets.Enabled = false;
            forzaDiscoveryStatus.Text = "Searching this PC for installed Forza games...";
            try
            {
                await Task.Yield();
                string previous = options.ForzaTargetId;
                var found = ForzaLauncher.Discover();
                forzaTarget.BeginUpdate(); forzaTarget.Items.Clear();
                foreach (var target in found) forzaTarget.Items.Add(target);
                forzaTarget.EndUpdate();
                ForzaGameTarget selected = found.FirstOrDefault(x => x.Id == previous) ?? found.FirstOrDefault();
                if (selected != null)
                {
                    forzaTarget.SelectedItem = selected;
                    forzaDiscoveryStatus.Text = found.Count + " installed Forza game" + (found.Count == 1 ? "" : "s") + " found. Choose the title Start Forza should open.";
                }
                else
                {
                    options.ForzaTargetId = null;
                    forzaDiscoveryStatus.Text = "No installed Forza game was found. Install it through the Xbox app or Steam, or add its shortcut to the Start Menu, then search again.";
                }
            }
            catch (Exception ex) { forzaDiscoveryStatus.Text = "Could not search for installed games: " + ex.Message; }
            finally { scanningForza = false; SaveOptions(); UpdateButtons(); }
        }
        private void SaveOptions()
        {
            if (previewPath != null || scanningForza) return;
            if (optionsConfigBlocked) { Log("Options are read-only because this Shim version could not safely load their saved format. Update Shim or choose fresh settings in its installer."); return; }
            options.StartBridgeWithForza = startBridgeWithForza.Checked;
            var target = forzaTarget.SelectedItem as ForzaGameTarget;
            options.ForzaTargetId = target == null ? null : target.Id;
            try { Store.Write(optionsPath, options); }
            catch (Exception ex) { Log("Options could not be saved: " + ex.Message); }
            UpdateButtons();
        }
        private async Task StartForzaAsync()
        {
            if (launchingForza || previewPath != null) return;
            ForzaGameTarget target = forzaTarget.SelectedItem as ForzaGameTarget;
            if (target == null) { Log("Choose an installed Forza game in Options, then click Start Forza."); return; }
            launchingForza = true; UpdateButtons();
            try
            {
                if (options.StartBridgeWithForza)
                {
                    if (runtime != null && runtime.IsMonitorOnly)
                        throw new InvalidOperationException("Stop input monitoring before using Start Forza with bridge auto-start enabled.");
                    if (runtime == null)
                    {
                        Start(false);
                        if (runtime == null) throw new InvalidOperationException("Shim could not start the bridge. Check the Activity page and resolve the bridge error first.");
                    }
                    DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                    while (runtime != null && runtime.Running && !runtime.Started && !runtime.Stopping && DateTime.UtcNow < deadline)
                        await Task.Delay(100);
                    if (runtime == null || !runtime.Running || !runtime.Started || runtime.Stopping)
                    {
                        if (runtime != null && !runtime.Started && runtime.Running) runtime.RequestStop();
                        throw new InvalidOperationException("The bridge did not reach Running state, so Forza was not launched. Check Activity for the startup error.");
                    }
                }
                ForzaLauncher.Launch(target);
                Log("Started " + target.DisplayName + (options.StartBridgeWithForza ? " after the bridge was ready." : "."));
            }
            catch (Exception ex) { Log("Forza launch: " + ex.Message); }
            finally { launchingForza = false; UpdateButtons(); }
        }
        private void ChangeMode(string mode)
        { try { Appearance.Set(mode, previewPath == null); } catch (Exception ex) { Log("Appearance could not be saved: " + ex.Message); } }
        private void ApplyAppearance()
        {
            day.Tag = Appearance.Mode == "Day" ? "Active" : "Ghost"; night.Tag = Appearance.Mode == "Night" ? "Active" : "Ghost";
            Appearance.Apply(this); UpdateOverview();
        }
        private void SyncGain(int value)
        {
            syncingGain = true;
            try { strength.Value = value; strengthValue.Text = value + "%"; }
            finally { syncingGain = false; }
        }
        private void GainChanged(int value)
        {
            if (syncingGain) return; SyncGain(value);
            if (previewPath != null) return;
            profile.FfbGain = value; pendingGainSave = true; gainSaveAt = DateTime.UtcNow.AddMilliseconds(450);
            try { if (runtime != null && runtime.CanChangeGain) runtime.UpdateGain(value); }
            catch (Exception ex) { Log("Could not change FFB strength: " + ex.Message); }
            UpdateOverview();
        }
        private void FlushGain(bool force)
        {
            if (!pendingGainSave || previewPath != null || (!force && DateTime.UtcNow < gainSaveAt)) return;
            if (runtime != null && runtime.CanChangeGain && runtime.AppliedGain != profile.FfbGain) return;
            try
            {
                Store.SaveStrength(profilePath, profile.FfbGain, profile);
                pendingGainSave = false; UpdateOverview();
            }
            catch (Exception ex) { pendingGainSave = false; Log("FFB strength changed, but saving failed: " + ex.Message); }
        }
        private void RenderRig()
        {
            rigDevices.SuspendLayout();
            while (rigDevices.Controls.Count > 0) { var child = rigDevices.Controls[0]; rigDevices.Controls.RemoveAt(0); child.Dispose(); }
            foreach (var device in available.Where(d => profile.Devices.Contains(d.Id)))
            {
                var row = new FlowLayoutPanel { Width = 300, Height = 67, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
                row.Controls.Add(new Label { Text = device.Name, AutoSize = true, MaximumSize = new Size(280, 30), Font = new Font("Segoe UI Semibold", 10), AutoEllipsis = true });
                string role = device.Id == profile.FfbDevice ? "Steering & force feedback" : "Included in your virtual wheel";
                row.Controls.Add(new Label { Text = role, AutoSize = true, Tag = "Muted", Font = new Font("Segoe UI", 9), Margin = new Padding(0, 4, 0, 0) }); rigDevices.Controls.Add(row);
            }
            if (rigDevices.Controls.Count == 0) { var empty = Label("Choose Set up my rig to add your devices."); empty.Tag = "Muted"; empty.MaximumSize = new Size(260, 0); rigDevices.Controls.Add(empty); }
            rigSummary.Text = profile.Devices.Count + " devices / one virtual wheel";
            rigDevices.ResumeLayout(); Appearance.Apply(rigDevices); UpdateOverview();
        }
        private long lastPackets, lastStarts;
        private void UpdateOverview()
        {
            bool active = runtime != null && runtime.Running, forwarding = active && runtime.CanChangeGain;
            bridgeState.Text = scanning ? "Checking" : active ? runtime.Started ? runtime.IsMonitorOnly ? "Monitoring" : "Running" : "Starting" : profile.Bindings.Count == 0 ? "Setup needed" : "Ready";
            bridgeState.ForeColor = active && runtime.Started ? Appearance.Colors.Success : Appearance.Colors.Text;
            packetCount.Text = (runtime == null ? lastPackets : runtime.FfbPackets).ToString("N0"); wheelStarts.Text = (runtime == null ? lastStarts : runtime.WheelStarts).ToString("N0");
            var source = available.FirstOrDefault(d => d.Id == profile.FfbDevice); wheelName.Text = source == null ? "Choose your wheel in Set up my rig" : source.Name;
            gainHint.Text = forwarding && runtime.AppliedGain != strength.Value ? "Applying live strength..." : pendingGainSave ? "Saving strength..." : forwarding ? "Applied live / saved automatically" : "Saved for your next drive";
            gainHint.ForeColor = forwarding ? Appearance.Colors.Success : Appearance.Colors.Muted;
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
                RenderMappings(); RenderRig();
                await CheckDrivers();
                Log("Detected " + available.Count + " physical controllers. Driver health is on Drivers.");
                if (File.Exists(Store.RecoveryPath)) Log("A previous hiding session needs recovery. Click Restore visibility.");
            }
            catch (Exception ex) { Log(ex.Message); }
            finally { scanning = false; UpdateButtons(); UpdateOverview(); }
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
            if (profileConfigBlocked) { MessageBox.Show(this, "Shim could not safely read the saved rig profile, so setup is disabled to protect it. Update Shim or select fresh settings in the installer. The original file has not been changed.", "Profile kept safe", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            CaptureSelections();
            using (var wizard = new SetupWizard(profile, available, null, 0))
            {
                if (wizard.ShowDialog(this) != DialogResult.OK) return;
                profile = wizard.Result;
                enableFfb.Checked = profile.EnableFfb; invert.Checked = profile.InvertFfb;
                pendingGainSave = false; SyncGain(profile.FfbGain); virtualId.Value = profile.VirtualId;
                dll.Text = String.IsNullOrEmpty(profile.VjoyDll) ? Vjoy.DefaultDll : profile.VjoyDll;
            }
            await RefreshAsync(); Log("Setup saved. Start the bridge, then launch Forza and bind your virtual wheel controls.");
        }
        private void CaptureSelections()
        {
            profile.Devices = devices.CheckedItems.Cast<PhysicalDevice>().Select(d => d.Id).ToList();
            profile.FfbDevice = wheel.SelectedItem == null ? null : ((PhysicalDevice)wheel.SelectedItem).Id;
            profile.VirtualId = (uint)virtualId.Value; profile.EnableFfb = enableFfb.Checked; profile.InvertFfb = invert.Checked; profile.FfbGain = strength.Value; profile.VjoyDll = dll.Text;
            RenderRig();
        }
        private bool SaveProfile()
        { if (profileConfigBlocked) { Log("Profile is read-only because this Shim version could not safely load its saved format."); return false; } try { CaptureSelections(); Store.Write(profilePath, profile); Log("Saved profile."); return true; } catch (Exception ex) { Log(ex.Message); return false; } }
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
                lastPackets = lastStarts = 0;
                Log(monitorOnly ? "Starting read-only input monitor. No hiding, virtual output or FFB commands." : "Starting virtual wheel bridge. Launch Forza after the bridge reports running.");
                UpdateButtons();
            }
            catch (Exception ex) { Log(ex.Message); }
        }
        private void BeginLearn()
        {
            if (runtime == null || !runtime.Started) { Log("Start Monitor inputs first."); return; }
            if (!runtime.IsMonitorOnly) { Log("Use Monitor inputs to learn controls while the bridge is stopped."); return; }
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
            FlushGain(false);
            if (runtime == null) return;
            foreach (string message in runtime.TakeMessages()) Log(message);
            if (!runtime.Running)
            {
                var error = runtime.Error; lastPackets = runtime.FfbPackets; lastStarts = runtime.WheelStarts; runtime.Dispose(); runtime = null; learning = false;
                Log(error == null ? "Stopped. Session device visibility restored." : "Bridge stopped: " + error.Message);
                if (error != null) File.AppendAllText(logPath, error + Environment.NewLine);
                UpdateButtons(); UpdateOverview(); if (closing) Close(); return;
            }
            var states = runtime.Snapshot();
            learn.Enabled = runtime.IsMonitorOnly && runtime.Started;
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
            if (runtime.Started) status.Text = (learning ? "Learning input... " : "Running. ") + runtime.Frames + " frames; " + runtime.FfbPackets + " FFB packets. " + runtime.FfbStatus;
            UpdateOverview();
        }
        private static string OutputName(Binding binding) { return binding.OutputKind == "Axis" ? Mapper.AxisNames[binding.OutputIndex] : (String.IsNullOrEmpty(binding.Label) ? "" : binding.Label + " / ") + binding.OutputKind + " " + (binding.OutputIndex + 1); }
        private void RenderMappings()
        {
            mappings.Rows.Clear();
            foreach (var binding in profile.Bindings.OrderBy(b => b.OutputKind).ThenBy(b => b.OutputIndex))
            {
                var device = available.FirstOrDefault(d => d.Id == binding.DeviceId);
                var control = device == null ? null : device.Controls.FirstOrDefault(c => c.Kind == binding.InputKind && c.Index == binding.InputIndex);
                string inputName = control == null || binding.InputKind != "Axis" ? binding.InputKind + " " + (binding.InputIndex + 1) : control.Name;
                string options = binding.OutputKind == "Axis" ? (binding.Invert ? "Inverted; " : "") + (binding.Deadzone == 0 ? "Standard" : "Deadzone " + (binding.Deadzone * 100).ToString("0") + "%") : "";
                int row = mappings.Rows.Add(OutputName(binding), device == null ? "Missing device" : device.Name, inputName, options);
                mappings.Rows[row].Tag = binding;
            }
        }
        private void UpdateButtons()
        {
            bool idle = runtime == null && !scanning;
            devices.Enabled = wheel.Enabled = enableFfb.Enabled = invert.Enabled = virtualId.Enabled = dll.Enabled = refresh.Enabled = autoButtons.Enabled = remove.Enabled = idle && !profileConfigBlocked;
            save.Enabled = idle && !profileConfigBlocked;
            strength.Enabled = !scanning && (runtime == null || runtime.CanChangeGain);
            start.Enabled = idle && !profileConfigBlocked && previewPath == null && profile.Devices.Count > 0 && profile.Bindings.Count > 0 && !File.Exists(Store.RecoveryPath);
            bool canAutoStartBridge = runtime != null ? !runtime.IsMonitorOnly && !runtime.Stopping : idle && !profileConfigBlocked && profile.Devices.Count > 0 && profile.Bindings.Count > 0 && !File.Exists(Store.RecoveryPath);
            startForza.Enabled = previewPath == null && !launchingForza && !scanningForza && forzaTarget.SelectedItem != null && (!options.StartBridgeWithForza || canAutoStartBridge);
            startBridgeWithForza.Enabled = idle && !launchingForza && previewPath == null;
            forzaTarget.Enabled = !launchingForza && !scanningForza && previewPath == null;
            refreshForzaTargets.Enabled = !launchingForza && !scanningForza && previewPath == null;
            watch.Enabled = idle && previewPath == null;
            stop.Enabled = runtime != null;
            recover.Enabled = idle && previewPath == null && File.Exists(Store.RecoveryPath);
            checkUpdates.Enabled = !checkingUpdates && !updatingComponent && previewPath == null;
            foreach (var row in updateRows)
                row.Action.Enabled = !checkingUpdates && !updatingComponent && runtime == null && row.Info != null &&
                    row.CanUpdate;
            learn.Enabled = runtime != null && runtime.IsMonitorOnly && runtime.Started;
            foreach (var action in setupActions) action.Enabled = idle && !profileConfigBlocked && previewPath == null;
            setupWizard.Enabled = idle && !profileConfigBlocked && previewPath == null;
            UpdateOverview();
        }
        private void InstallDriver(bool vjoy)
        {
            try
            {
                if (!File.Exists(DriverPackages.PathFor(vjoy))) { Log("The bundled installer is missing. Re-extract the latest Shim package and reopen setup."); return; }
                DriverSetup.Install(vjoy);
                Log("Started the official " + (vjoy ? "vJoy" : "HidHide") + " installer. Follow its setup wizard; restart if requested, then click Check drivers.");
            }
            catch (Exception ex) { Log("Driver setup: " + ex.Message); }
        }
        private void Log(string message)
        {
            string level = message.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0 ? "ERROR" : "INFO";
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            log.AppendText(line + Environment.NewLine); status.Text = message;
            try
            {
                File.AppendAllText(logPath, line + Environment.NewLine);
                var entry = new { TimestampUtc = DateTime.UtcNow.ToString("o"), Level = level, Message = message };
                File.AppendAllText(logPath + ".jsonl", new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(entry) + Environment.NewLine);
            }
            catch (IOException ex) { log.AppendText("Log file error: " + ex.Message + Environment.NewLine); }
            catch (UnauthorizedAccessException ex) { log.AppendText("Log file error: " + ex.Message + Environment.NewLine); }
        }
        private sealed class OutputChoice { public string Kind; public int Index; public string Name; public override string ToString() { return Name; } }
    }
}
