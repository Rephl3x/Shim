using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RigBridge
{
    // Reads physical controllers only. It never changes HidHide, vJoy, or motor forces.
    public sealed class InputDiagnostics : Form
    {
        private readonly List<PhysicalDevice> devices;
        private readonly Dictionary<string, InputReader> readers = new Dictionary<string, InputReader>();
        private readonly Dictionary<string, InputState> previous = new Dictionary<string, InputState>();
        private readonly Dictionary<string, string> results = new Dictionary<string, string>();
        private readonly Dictionary<string, double> wheelMotion = new Dictionary<string, double>();
        private readonly TextBox output = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 10) };
        private readonly Label hint = new Label { Dock = DockStyle.Fill, AutoSize = false };
        private readonly Button identify = new ModernButton { Text = "Identify steering wheel", AutoSize = true };
        private readonly Button useWheel = new ModernButton { Text = "Use detected wheel", AutoSize = true, Enabled = false };
        private readonly Timer timer = new Timer { Interval = 100 };
        private DirectInput direct;
        private DateTime listenUntil;
        private string candidate;
        public string SelectedWheelId { get; private set; }

        public InputDiagnostics(IEnumerable<PhysicalDevice> found)
        {
            devices = found.Where(d => !d.Virtual).ToList();
            Text = "Shim - check physical inputs"; Font = new Font("Segoe UI", 10);
            ClientSize = new Size(900, 620); MinimumSize = new Size(740, 500);
            StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), RowCount = 3, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            hint.Text = "Move the wheel, pedals and shifter one at a time. The device that changes is the physical source. This check sends no forces and changes no device settings.";
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            actions.Controls.Add(identify); actions.Controls.Add(useWheel);
            layout.Controls.Add(hint, 0, 0); layout.Controls.Add(output, 0, 1); layout.Controls.Add(actions, 0, 2);
            Controls.Add(layout);
            identify.Click += (s, e) => { wheelMotion.Clear(); candidate = null; useWheel.Enabled = false; listenUntil = DateTime.UtcNow.AddSeconds(8); hint.Text = "Turn only the steering wheel left and right for eight seconds. Keep pedals and other controllers still."; };
            useWheel.Click += (s, e) => { SelectedWheelId = candidate; DialogResult = DialogResult.OK; Close(); };
            Shown += (s, e) => OpenReaders();
            timer.Tick += (s, e) => ReadInputs();
            FormClosed += (s, e) => { timer.Stop(); timer.Dispose(); foreach (var reader in readers.Values) reader.Dispose(); if (direct != null) direct.Dispose(); };
            Appearance.Apply(this);
        }
        private void OpenReaders()
        {
            try
            {
                direct = new DirectInput();
                foreach (var device in devices)
                {
                    try { readers.Add(device.Id, direct.Open(device, Handle, false)); results[device.Id] = "Waiting for movement"; }
                    catch (Exception ex) { results[device.Id] = "Cannot read: " + ex.Message; }
                }
                timer.Start(); Render();
            }
            catch (Exception ex) { hint.Text = "Input check could not start: " + ex.Message; }
        }
        private void ReadInputs()
        {
            foreach (var device in devices.Where(d => readers.ContainsKey(d.Id)))
            {
                try
                {
                    var state = readers[device.Id].Read(); InputState before;
                    if (previous.TryGetValue(device.Id, out before))
                    {
                        var axes = device.Controls.Where(c => c.Kind == "Axis" && c.Index < state.Axes.Length && Math.Abs(state.Axes[c.Index] - before.Axes[c.Index]) >= 0.03)
                            .Select(c => c.Name + " " + (state.Axes[c.Index] * 100).ToString("0") + "%").ToList();
                        var buttons = device.Controls.Where(c => c.Kind == "Button" && c.Index < state.Buttons.Length && state.Buttons[c.Index] && !before.Buttons[c.Index])
                            .Select(c => "button " + (c.Index + 1)).ToList();
                        if (axes.Count + buttons.Count > 0) results[device.Id] = "Responding: " + String.Join(", ", axes.Concat(buttons).Take(4));
                        if (DateTime.UtcNow < listenUntil)
                        {
                            double movement = device.Controls.Where(c => c.Kind == "Axis" && c.Index < state.Axes.Length)
                                .Select(c => Math.Abs(state.Axes[c.Index] - before.Axes[c.Index])).DefaultIfEmpty(0).Max();
                            if (movement >= 0.03) wheelMotion[device.Id] = (wheelMotion.ContainsKey(device.Id) ? wheelMotion[device.Id] : 0) + movement;
                        }
                    }
                    previous[device.Id] = state;
                }
                catch (Exception ex) { results[device.Id] = "Read failed: " + ex.Message; readers[device.Id].Dispose(); readers.Remove(device.Id); }
            }
            if (listenUntil != DateTime.MinValue && DateTime.UtcNow >= listenUntil)
            {
                listenUntil = DateTime.MinValue;
                var moved = wheelMotion.Where(p => p.Value >= 0.15).OrderByDescending(p => p.Value).ToList();
                if (moved.Count == 1)
                {
                    candidate = moved[0].Key; useWheel.Enabled = true;
                    hint.Text = "Wheel input detected on " + devices.First(d => d.Id == candidate).Name + ". Click Use detected wheel to select it in setup.";
                }
                else hint.Text = moved.Count == 0 ? "No wheel axis changed enough. Check its power, USB connection and Device Manager status, then retry." :
                    "Several devices reported axis movement. Keep pedals and other controllers still, then retry. Check wheel software for combined or mirrored inputs.";
            }
            Render();
        }
        private void Render()
        {
            output.Text = devices.Count == 0 ? "No physical controllers were detected. Check USB, power and Device Manager, then refresh devices in Shim." :
                String.Join(Environment.NewLine + Environment.NewLine, devices.Select(d => d.Name + "\r\n" + (results.ContainsKey(d.Id) ? results[d.Id] : "Not opened")));
        }
    }
}
