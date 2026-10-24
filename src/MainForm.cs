using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FHMozaFix
{
    public sealed class MainForm : Form
    {
        private readonly ListView devices = new ListView();
        private readonly TextBox details = new TextBox();
        private readonly TextBox output = new TextBox();
        private readonly Label status = new Label();
        private readonly Label privilege = new Label();
        private readonly Button refresh = MakeButton("Refresh devices");
        private readonly Button save = MakeButton("Save order");
        private readonly Button up = MakeButton("Move up");
        private readonly Button down = MakeButton("Move down");
        private readonly Button first = MakeButton("Make first");
        private readonly Button reconnect = MakeButton("Reconnect selected");
        private readonly Button resume = MakeButton("Connect accessories");
        private readonly Button cancel = MakeButton("Cancel and restore");
        private readonly Button restore = MakeButton("Restore devices");
        private readonly Button admin = MakeButton("Reopen as administrator");
        private readonly ComboBox mode = new ComboBox();
        private readonly NumericUpDown delay = new NumericUpDown();
        private readonly CheckBox forceReconnect = new CheckBox();
        private readonly IRecoveryStore recovery;
        private readonly string profilePath;
        private readonly string logPath;
        private readonly string previewPath;
        private bool busy;
        private bool loading;
        private bool closeRequested;
        private bool logFailed;
        private CancellationTokenSource cancellation;
        private TaskCompletionSource<bool> continuation;

        public MainForm(string previewPath)
        {
            this.previewPath = previewPath;
            Directory.CreateDirectory(Program.DataDirectory);
            profilePath = Path.Combine(Program.DataDirectory, "profile.json");
            recovery = new RecoveryStore(Path.Combine(Program.DataDirectory, "recovery.json"));
            logPath = Path.Combine(Program.DataDirectory, "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            Text = "FH MOZA Fix - controller reconnect order";
            Font = new Font("Segoe UI", 10);
            ClientSize = new Size(1120, 834);
            MinimumSize = new Size(980, 794);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(245, 247, 250);
            BuildLayout();
            refresh.Click += async (s, e) => await RefreshDevicesAsync();
            save.Click += (s, e) => TrySaveProfile();
            up.Click += (s, e) => MoveSelection(-1);
            down.Click += (s, e) => MoveSelection(1);
            first.Click += (s, e) => MoveSelection(-devices.Items.Count);
            reconnect.Click += async (s, e) => await ReconnectAsync();
            restore.Click += async (s, e) => await RestoreAsync();
            cancel.Click += (s, e) => { if (cancellation != null) cancellation.Cancel(); };
            resume.Click += (s, e) => { if (continuation != null) continuation.TrySetResult(true); };
            admin.Click += (s, e) => {
                if (!TrySaveProfile()) return;
                try { Program.Elevate(); Close(); }
                catch (Win32Exception ex) { Report("Administrator access was not granted: " + ex.Message); }
            };
            devices.ItemCheck += (s, e) => {
                if (loading) return;
                var controller = (Controller)devices.Items[e.Index].Tag;
                if (!controller.Allowed) e.NewValue = CheckState.Unchecked;
                BeginInvoke(new Action(UpdateOrderNumbers));
            };
            devices.SelectedIndexChanged += (s, e) => {
                if (devices.SelectedItems.Count == 0) return;
                var controller = (Controller)devices.SelectedItems[0].Tag;
                details.Text = controller.Name + Environment.NewLine + controller.TargetId + Environment.NewLine +
                    (controller.Allowed ? "Reconnects this controller's USB device, including its own configuration port if present." : controller.BlockedReason);
            };
            Shown += async (s, e) => {
                await RefreshDevicesAsync();
                if (previewPath != null)
                {
                    using (var bitmap = new Bitmap(Width, Height))
                    {
                        DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                        bitmap.Save(previewPath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    Close();
                }
                else if (recovery.Exists)
                    Report("A previous reconnect needs recovery. Reopen as administrator if needed, then click Restore devices before starting another sequence.");
            };
            FormClosing += (s, e) => {
                if (!busy) return;
                e.Cancel = true;
                closeRequested = true;
                if (cancellation != null) cancellation.Cancel();
                status.Text = "Restoring controllers before closing...";
            };
            if (previewPath != null)
            {
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-20000, -20000);
            }
            UpdateButtons();
        }

        private static Button MakeButton(string text)
        {
            return new Button { Text = text, AutoSize = true, Height = 34, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(3) };
        }

        private static FlowLayoutPanel Flow(params Control[] controls)
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            panel.Controls.AddRange(controls);
            return panel;
        }

        private void BuildLayout()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 11 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 83));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 69));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 153));
            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(0) };
            heading.Controls.Add(new Label { Text = "Put your wheel first.", Font = new Font("Segoe UI", 23, FontStyle.Bold), AutoSize = true }, 0, 0);
            heading.Controls.Add(new Label { Text = "Select the controllers to reconnect, then move the wheel base to the top.", AutoSize = true }, 0, 1);
            layout.Controls.Add(heading, 0, 0);
            privilege.AutoSize = true;
            privilege.Margin = new Padding(3, 9, 16, 3);
            privilege.Text = Program.IsAdministrator ? "Administrator access active" : "Browsing devices. Reconnecting requires administrator access.";
            privilege.ForeColor = Program.IsAdministrator ? Color.DarkGreen : Color.FromArgb(124, 75, 0);
            layout.Controls.Add(Flow(privilege, admin), 0, 1);
            layout.Controls.Add(new Label { Text = "Your selected reconnect order (Forza assigns its own device numbers)", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 0, 2);
            devices.Dock = DockStyle.Fill;
            devices.View = View.Details;
            devices.CheckBoxes = true;
            devices.FullRowSelect = true;
            devices.MultiSelect = false;
            devices.HideSelection = false;
            devices.Columns.Add("Order", 72);
            devices.Columns.Add("Controller", 240);
            devices.Columns.Add("Availability", 150);
            devices.Columns.Add("USB instance (specific to this port)", 560);
            var listArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            listArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            listArea.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
            listArea.Controls.Add(devices, 0, 0);
            var moves = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            moves.Controls.AddRange(new Control[] { up, down, first });
            listArea.Controls.Add(moves, 1, 0);
            layout.Controls.Add(listArea, 0, 3);
            details.Multiline = true;
            details.ReadOnly = true;
            details.BorderStyle = BorderStyle.None;
            details.BackColor = BackColor;
            details.Dock = DockStyle.Fill;
            details.Text = "Include other game controllers that may claim Device 1. Unsupported controllers must be disconnected manually.";
            layout.Controls.Add(details, 0, 4);
            var joy = MakeButton("Windows game controllers");
            joy.Click += (s, e) => {
                try { Process.Start(new ProcessStartInfo("control.exe", "joy.cpl") { UseShellExecute = true }); }
                catch (Exception ex) { Report(ex.Message); }
            };
            layout.Controls.Add(Flow(refresh, save, joy), 0, 5);
            mode.DropDownStyle = ComboBoxStyle.DropDownList;
            mode.Width = 315;
            mode.Items.AddRange(new object[] { "Reconnect all automatically", "Pause after the wheel (recommended)", "Pause before each accessory" });
            mode.SelectedIndex = 1;
            delay.Minimum = 1;
            delay.Maximum = 15;
            delay.Value = 3;
            delay.Width = 52;
            layout.Controls.Add(Flow(new Label { Text = "Sequence:", AutoSize = true, Margin = new Padding(3, 7, 6, 3) }, mode,
                new Label { Text = "Wait between devices (seconds):", AutoSize = true, Margin = new Padding(16, 7, 6, 3) }, delay), 0, 6);
            forceReconnect.Text = "Force reconnect even if Forza is running";
            forceReconnect.AutoSize = true;
            forceReconnect.Margin = new Padding(3, 5, 3, 3);
            layout.Controls.Add(forceReconnect, 0, 7);
            reconnect.BackColor = Color.FromArgb(25, 100, 186);
            reconnect.ForeColor = Color.White;
            reconnect.FlatStyle = FlatStyle.Flat;
            layout.Controls.Add(Flow(reconnect, resume, cancel, restore), 0, 8);
            status.Dock = DockStyle.Fill;
            status.Text = "No devices are changed until you click Reconnect selected.";
            status.ForeColor = Color.FromArgb(36, 62, 89);
            status.Padding = new Padding(3, 5, 3, 0);
            layout.Controls.Add(status, 0, 9);
            output.Multiline = true;
            output.ReadOnly = true;
            output.ScrollBars = ScrollBars.Both;
            output.WordWrap = false;
            output.Font = new Font("Consolas", 9);
            output.Dock = DockStyle.Fill;
            layout.Controls.Add(output, 0, 10);
            Controls.Add(layout);
        }

        private async Task RefreshDevicesAsync()
        {
            busy = true;
            UpdateButtons();
            try
            {
                Profile profile = CaptureProfile();
                if (devices.Items.Count == 0 && File.Exists(profilePath)) profile = Json.Read<Profile>(profilePath) ?? new Profile();
                var controllers = await Task.Run(() => DeviceSafety.Discover(NativeDevices.Snapshot(true)));
                controllers = controllers.OrderBy(c => { int index = profile.Order.IndexOf(c.TargetId); return index < 0 ? Int32.MaxValue : index; }).ToList();
                loading = true;
                devices.Items.Clear();
                foreach (var controller in controllers)
                {
                    var item = new ListViewItem("") { Tag = controller, Checked = controller.Allowed && profile.Order.Contains(controller.TargetId) };
                    item.SubItems.Add(controller.Name);
                    item.SubItems.Add(controller.Allowed ? "Ready" : "Manual reconnect");
                    item.SubItems.Add(controller.TargetId);
                    if (!controller.Allowed) item.ForeColor = Color.Gray;
                    devices.Items.Add(item);
                }
                mode.SelectedIndex = Math.Max(0, Math.Min(2, profile.Mode));
                delay.Value = Math.Max(delay.Minimum, Math.Min(delay.Maximum, profile.DelaySeconds));
                forceReconnect.Checked = profile.ForceReconnect;
                UpdateOrderNumbers();
                ActiveControl = devices;
                Log("Found " + controllers.Count + " controller device(s); " + controllers.Count(c => c.Allowed) + " can be reconnected by this app.");
                status.Text = controllers.Count == 0 ? "No supported HID game controllers detected. Connect and power on your wheel, then refresh." :
                    "Select the controllers, put the wheel base first, then reconnect. Other connected controllers can still affect Forza's numbering.";
            }
            catch (Exception ex) { Report("Could not scan devices: " + ex.Message); }
            finally
            {
                loading = false;
                busy = false;
                UpdateButtons();
                if (closeRequested) Close();
            }
        }

        private Profile CaptureProfile()
        {
            return new Profile { Order = devices.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => ((Controller)i.Tag).TargetId).ToList(),
                Mode = mode.SelectedIndex, DelaySeconds = (int)delay.Value, ForceReconnect = forceReconnect.Checked };
        }

        private bool TrySaveProfile()
        {
            try { Json.WriteAtomic(profilePath, CaptureProfile()); Log("Saved controller order for this Windows account."); return true; }
            catch (Exception ex) { Report("Could not save order: " + ex.Message); return false; }
        }

        private void MoveSelection(int offset)
        {
            if (devices.SelectedIndices.Count == 0) return;
            int oldIndex = devices.SelectedIndices[0];
            int newIndex = Math.Max(0, Math.Min(devices.Items.Count - 1, oldIndex + offset));
            if (oldIndex == newIndex) return;
            loading = true;
            var item = devices.Items[oldIndex];
            devices.Items.RemoveAt(oldIndex);
            devices.Items.Insert(newIndex, item);
            item.Selected = true;
            item.EnsureVisible();
            loading = false;
            UpdateOrderNumbers();
        }

        private void UpdateOrderNumbers()
        {
            int order = 0;
            foreach (ListViewItem item in devices.Items) item.Text = item.Checked ? (++order).ToString() : "-";
        }

        private async Task ReconnectAsync()
        {
            try
            {
                if (!forceReconnect.Checked && Program.ForzaIsRunning())
                {
                    Report("Close Forza Horizon 6, or tick Force reconnect even if Forza is running to continue with the game open.");
                    return;
                }
                var selected = devices.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => (Controller)i.Tag).ToList();
                if (selected.Count == 0) { Report("Select at least one controller and put the wheel base first."); return; }
                if (!TrySaveProfile()) return;
                if (forceReconnect.Checked) Log("Forza running check skipped: force reconnect is enabled.");
                busy = true;
                cancellation = new CancellationTokenSource();
                UpdateButtons();
                var engine = new ReconnectEngine(new WindowsBackend(Log), recovery, Log);
                await engine.RunAsync(selected, (int)delay.Value * 1000, mode.SelectedIndex, PauseAsync, cancellation.Token);
                status.Text = "All selected controllers are connected. Confirm Device 1 and FFB in Forza. Software reconnection cannot guarantee the game's device order.";
            }
            catch (OperationCanceledException) { status.Text = "Sequence cancelled. Controllers restored."; Log(status.Text); }
            catch (Exception ex) { Report(ex.Message); }
            finally { FinishOperation(); }
        }

        private async Task PauseAsync(int index, Controller controller)
        {
            continuation = new TaskCompletionSource<bool>();
            resume.Text = mode.SelectedIndex == 2 ? "Connect next device" : "Connect accessories";
            resume.Enabled = true;
            string text = index == 0
                ? "Wheel connected; selected accessories are disconnected. " + (forceReconnect.Checked ? "Switch to or launch Forza" : "Launch Forza") + ", bind the wheel as Device 1 and test FFB. Then click " + resume.Text + "."
                : controller.Name + " is connected. Bind/test it in Forza, then click Connect next device.";
            status.Text = text;
            Log(text);
            using (cancellation.Token.Register(() => continuation.TrySetCanceled()))
            {
                try { await continuation.Task; }
                finally { resume.Enabled = false; }
            }
        }

        private async Task RestoreAsync()
        {
            busy = true;
            UpdateButtons();
            try
            {
                var engine = new ReconnectEngine(new WindowsBackend(Log), recovery, Log);
                bool success = await engine.RestoreAsync();
                status.Text = success ? "All controllers recorded by this app are enabled. Refresh devices to rebuild the list." : "Recovery needs attention. See the log and retry Restore devices.";
            }
            catch (Exception ex) { Report(ex.Message); }
            finally { FinishOperation(); }
        }

        private void FinishOperation()
        {
            busy = false;
            if (cancellation != null) { cancellation.Dispose(); cancellation = null; }
            continuation = null;
            UpdateButtons();
            if (closeRequested && !recovery.Exists) Close();
            else if (closeRequested) { closeRequested = false; Report("Some controllers still need recovery. See the log and click Restore devices."); }
        }

        private void UpdateButtons()
        {
            bool interactive = previewPath == null;
            devices.Enabled = refresh.Enabled = save.Enabled = up.Enabled = down.Enabled = first.Enabled = mode.Enabled = delay.Enabled = forceReconnect.Enabled = !busy;
            admin.Visible = !Program.IsAdministrator;
            admin.Enabled = !busy && interactive;
            reconnect.Enabled = !busy && !recovery.Exists && Program.IsAdministrator && interactive;
            restore.Enabled = !busy && recovery.Exists && Program.IsAdministrator && interactive;
            cancel.Enabled = busy && cancellation != null;
            if (!busy) resume.Enabled = false;
        }

        private void Report(string message)
        {
            status.Text = message;
            Log(message);
        }

        private void Log(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            output.AppendText(line + Environment.NewLine);
            if (!logFailed)
            {
                try { File.AppendAllText(logPath, line + Environment.NewLine); }
                catch (IOException ex) { LogWriteFailure(ex); }
                catch (UnauthorizedAccessException ex) { LogWriteFailure(ex); }
            }
            if (busy && continuation == null) status.Text = message;
        }

        private void LogWriteFailure(Exception ex)
        {
            logFailed = true;
            output.AppendText("Could not write the session log: " + ex.Message + Environment.NewLine);
        }
    }
}
