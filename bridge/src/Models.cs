using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace RigBridge
{
    public sealed class PhysicalDevice
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string HidId { get; set; }
        public bool ForceFeedback { get; set; }
        public bool Virtual { get; set; }
        public List<InputControl> Controls { get; set; }
        public override string ToString() { return Name; }
        public PhysicalDevice() { Controls = new List<InputControl>(); }
    }
    public sealed class InputControl
    {
        public string Kind { get; set; }
        public int Index { get; set; }
        public string Name { get; set; }
        public uint ObjectId { get; set; }
        public override string ToString() { return Kind + " " + (Index + 1) + ": " + Name; }
    }
    public sealed class InputState
    {
        public double[] Axes = new double[8];
        public bool[] Buttons = new bool[128];
        public int[] Pov = new[] { -1, -1, -1, -1 };
    }
    public sealed class Binding
    {
        public string Label { get; set; }
        public string OutputKind { get; set; }
        public int OutputIndex { get; set; }
        public string DeviceId { get; set; }
        public string InputKind { get; set; }
        public int InputIndex { get; set; }
        public bool Invert { get; set; }
        public double Deadzone { get; set; }
        public double Minimum { get; set; }
        public double Maximum { get; set; }
        public Binding() { Minimum = 0; Maximum = 1; }
    }
    public sealed class BridgeProfile
    {
        public int SetupVersion { get; set; }
        public RigChoices Rig { get; set; }
        public List<string> Devices { get; set; }
        public List<Binding> Bindings { get; set; }
        public string FfbDevice { get; set; }
        public uint VirtualId { get; set; }
        public bool EnableFfb { get; set; }
        public bool InvertFfb { get; set; }
        public int FfbGain { get; set; }
        public string VjoyDll { get; set; }
        public BridgeProfile()
        {
            Devices = new List<string>(); Bindings = new List<Binding>();
            VirtualId = 1; FfbGain = 25;
        }
        public void Validate(IList<PhysicalDevice> devices)
        {
            if (VirtualId < 1 || VirtualId > 16) throw new InvalidOperationException("Choose vJoy device 1 to 16.");
            if (Devices == null || Devices.Count == 0 || Devices.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Devices.Count)
                throw new InvalidOperationException("Select at least one physical device, without duplicates.");
            foreach (string id in Devices)
            {
                var device = devices.FirstOrDefault(d => d.Id == id);
                if (device == null || device.Virtual) throw new InvalidOperationException("A selected physical device is unavailable. Refresh devices.");
                if (String.IsNullOrEmpty(device.HidId) || !device.HidId.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Cannot safely hide " + device.Name + ". No HID instance ID was reported.");
            }
            if (Bindings == null || Bindings.Count == 0) throw new InvalidOperationException("Map at least one control before starting.");
            var targets = new HashSet<string>();
            foreach (var binding in Bindings)
            {
                if (binding == null || !Devices.Contains(binding.DeviceId)) throw new InvalidOperationException("A mapping refers to an unselected device.");
                if (!targets.Add(binding.OutputKind + binding.OutputIndex)) throw new InvalidOperationException("Two mappings use the same virtual output.");
                int count = binding.OutputKind == "Axis" ? 8 : binding.OutputKind == "Button" ? 128 : binding.OutputKind == "POV" ? 4 : 0;
                if (binding.OutputIndex < 0 || binding.OutputIndex >= count || binding.InputKind != binding.OutputKind)
                    throw new InvalidOperationException("A mapping contains an invalid control type or index.");
                var source = devices.First(d => d.Id == binding.DeviceId);
                if (!source.Controls.Any(c => c.Kind == binding.InputKind && c.Index == binding.InputIndex))
                    throw new InvalidOperationException("Mapped input is missing on " + source.Name + ".");
                if (!Finite(binding.Minimum) || !Finite(binding.Maximum) || !Finite(binding.Deadzone) ||
                    binding.Minimum >= binding.Maximum || binding.Deadzone < 0 || binding.Deadzone >= 0.5)
                    throw new InvalidOperationException("Mapping calibration/deadzone is invalid.");
            }
            if (FfbGain < 0 || FfbGain > 100) throw new InvalidOperationException("FFB gain must be between 0 and 100 percent.");
            if (EnableFfb && !devices.Any(d => Devices.Contains(d.Id) && d.Id == FfbDevice && d.ForceFeedback))
                throw new InvalidOperationException("Select an FFB-capable physical wheel as the force-feedback destination.");
            if (EnableFfb && !Bindings.Any(b => b.OutputKind == "Axis" && b.OutputIndex == 0 && b.DeviceId == FfbDevice))
                throw new InvalidOperationException("Map virtual Steering (X) to the selected FFB wheel.");
        }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    }
    public static class Mapper
    {
        public static readonly string[] AxisNames = { "Steering (X)", "Throttle (Y)", "Brake (Z)", "Clutch (Rx)", "Handbrake (Ry)", "Spare (Rz)", "Slider 1", "Slider 2" };
        public static InputState Map(IList<Binding> bindings, IDictionary<string, InputState> inputs)
        {
            var result = new InputState(); result.Axes[0] = 0.5;
            foreach (var binding in bindings)
            {
                InputState state;
                if (!inputs.TryGetValue(binding.DeviceId, out state)) throw new InvalidOperationException("A mapped device stopped providing input.");
                if (binding.OutputKind == "Button") result.Buttons[binding.OutputIndex] = state.Buttons[binding.InputIndex];
                else if (binding.OutputKind == "POV") result.Pov[binding.OutputIndex] = state.Pov[binding.InputIndex];
                else
                {
                    double value = Math.Max(0, Math.Min(1, (state.Axes[binding.InputIndex] - binding.Minimum) / (binding.Maximum - binding.Minimum)));
                    if (binding.Invert) value = 1 - value;
                    double deadzone = binding.Deadzone;
                    if (binding.OutputIndex == 0)
                    {
                        double signed = value * 2 - 1;
                        value = 0.5 + Math.Sign(signed) * Math.Max(0, (Math.Abs(signed) - deadzone) / (1 - deadzone)) * 0.5;
                    }
                    else value = Math.Max(0, (value - deadzone) / (1 - deadzone));
                    result.Axes[binding.OutputIndex] = value;
                }
            }
            return result;
        }
    }
    public static class Store
    {
        public static string DirectoryPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FHRigBridge"); } }
        public static string ProfilePath { get { return Path.Combine(DirectoryPath, "profile.json"); } }
        public static string RecoveryPath { get { return Path.Combine(DirectoryPath, "hiding-recovery.json"); } }
        public static T Read<T>(string path) { return new JavaScriptSerializer().Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)); }
        public static void Write(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(value));
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(path + ".tmp", path, null); else File.Move(path + ".tmp", path);
        }
    }
}
