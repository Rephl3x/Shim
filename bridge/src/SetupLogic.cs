using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace RigBridge
{
    public sealed class RigChoices
    {
        public string Wheel { get; set; }
        public string Pedals { get; set; }
        public bool Clutch { get; set; }
        public string Shifter { get; set; }
        public int Gears { get; set; }
        public bool Reverse { get; set; }
        public string Sequential { get; set; }
        public string Handbrake { get; set; }
        public List<string> Extras { get; set; }
        public RigChoices() { Gears = 7; Reverse = true; Extras = new List<string>(); }
        public List<string> Selected()
        { return new[] { Wheel, Pedals, Shifter, Sequential, Handbrake }.Concat(Extras ?? new List<string>()).Where(x => !String.IsNullOrEmpty(x)).Distinct().ToList(); }
    }
    public sealed class SetupControl
    {
        public string Name, DeviceId, Kind;
        public int Output;
        public override string ToString() { return Name; }
    }
    public static class SetupLogic
    {
        public const int Version = 1;
        public static BridgeProfile Clone(BridgeProfile source)
        { var json = new JavaScriptSerializer(); return json.Deserialize<BridgeProfile>(json.Serialize(source)); }
        public static RigChoices Suggest(BridgeProfile profile, IList<PhysicalDevice> devices)
        {
            if (profile.Rig != null) return Clone(profile).Rig;
            var wheel = devices.FirstOrDefault(d => d.Id == profile.FfbDevice) ?? devices.FirstOrDefault(d => d.ForceFeedback);
            var pedal = profile.Bindings.FirstOrDefault(b => b.OutputKind == "Axis" && b.OutputIndex == 1);
            var result = new RigChoices { Wheel = wheel == null ? null : wheel.Id, Pedals = pedal == null ? null : pedal.DeviceId };
            var shifter = devices.FirstOrDefault(d => Contains(d.Name, "HGP") || Contains(d.Name, "H-pattern"));
            if (shifter != null) result.Shifter = shifter.Id;
            var handbrake = devices.FirstOrDefault(d => Contains(d.Name, "HBP") || Contains(d.Name, "handbrake"));
            if (handbrake != null) result.Handbrake = handbrake.Id;
            result.Clutch = profile.Bindings.Any(b => b.OutputKind == "Axis" && b.OutputIndex == 3);
            result.Extras = profile.Devices.Where(id => !result.Selected().Contains(id)).ToList();
            return result;
        }
        private static bool Contains(string name, string token) { return name != null && name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0; }
        public static List<SetupControl> Plan(RigChoices rig, IList<PhysicalDevice> devices)
        {
            if (String.IsNullOrEmpty(rig.Wheel)) throw new InvalidOperationException("Choose your steering wheel.");
            if (rig.Clutch && String.IsNullOrEmpty(rig.Pedals)) throw new InvalidOperationException("Choose the device providing the clutch pedal.");
            foreach (string id in rig.Selected())
                if (!devices.Any(d => d.Id == id && !d.Virtual && !String.IsNullOrEmpty(d.HidId)))
                    throw new InvalidOperationException("A selected device is unavailable or cannot be hidden. Refresh and choose it again.");
            if (rig.Gears < 1 || rig.Gears > 10) throw new InvalidOperationException("Choose between 1 and 10 forward gears.");
            var plan = new List<SetupControl>();
            plan.Add(new SetupControl { Name = "Steering", DeviceId = rig.Wheel, Kind = "Axis", Output = 0 });
            if (!String.IsNullOrEmpty(rig.Pedals))
            {
                plan.Add(new SetupControl { Name = "Throttle", DeviceId = rig.Pedals, Kind = "Axis", Output = 1 });
                plan.Add(new SetupControl { Name = "Brake", DeviceId = rig.Pedals, Kind = "Axis", Output = 2 });
                if (rig.Clutch) plan.Add(new SetupControl { Name = "Clutch", DeviceId = rig.Pedals, Kind = "Axis", Output = 3 });
            }
            if (!String.IsNullOrEmpty(rig.Handbrake)) plan.Add(new SetupControl { Name = "Handbrake", DeviceId = rig.Handbrake, Kind = "Axis", Output = 4 });
            int button = 0;
            if (!String.IsNullOrEmpty(rig.Shifter))
            {
                for (int i = 1; i <= rig.Gears; i++) plan.Add(new SetupControl { Name = "Gear " + i, DeviceId = rig.Shifter, Kind = "Button", Output = button++ });
                if (rig.Reverse) plan.Add(new SetupControl { Name = "Reverse", DeviceId = rig.Shifter, Kind = "Button", Output = button++ });
            }
            if (!String.IsNullOrEmpty(rig.Sequential))
            {
                plan.Add(new SetupControl { Name = "Shift up", DeviceId = rig.Sequential, Kind = "Button", Output = button++ });
                plan.Add(new SetupControl { Name = "Shift down", DeviceId = rig.Sequential, Kind = "Button", Output = button++ });
            }
            foreach (var control in plan)
                if (!devices.First(d => d.Id == control.DeviceId).Controls.Any(c => c.Kind == control.Kind))
                    throw new InvalidOperationException(devices.First(d => d.Id == control.DeviceId).Name + " has no " + control.Kind.ToLowerInvariant() + " inputs for " + control.Name + ". Choose another device or omit this role.");
            return plan;
        }
        public static Binding Calibrate(SetupControl target, PhysicalDevice device, InputState first, InputState second)
        {
            var candidates = device.Controls.Where(c => c.Kind == "Axis").Select(c => new { c.Index, Delta = Math.Abs(first.Axes[c.Index] - second.Axes[c.Index]) }).OrderByDescending(c => c.Delta).ToList();
            if (candidates.Count == 0 || candidates[0].Delta < 0.15) throw new InvalidOperationException("Too little movement. Try again, moving only " + target.Name.ToLowerInvariant() + " through its full travel.");
            if (candidates.Count > 1 && candidates[1].Delta > candidates[0].Delta * 0.8) throw new InvalidOperationException("Several axes moved together. Move only this control, or disable combined pedals in your device software.");
            int index = candidates[0].Index;
            return new Binding { Label = target.Name, DeviceId = target.DeviceId, InputKind = "Axis", InputIndex = index, OutputKind = "Axis", OutputIndex = target.Output,
                Minimum = Math.Min(first.Axes[index], second.Axes[index]), Maximum = Math.Max(first.Axes[index], second.Axes[index]), Invert = first.Axes[index] > second.Axes[index], Deadzone = target.Output == 0 ? 0 : 0.02 };
        }
        public static Binding LearnButton(SetupControl target, PhysicalDevice device, InputState rest, InputState current)
        {
            var pressed = device.Controls.Where(c => c.Kind == "Button" && current.Buttons[c.Index] && !rest.Buttons[c.Index]).ToList();
            if (pressed.Count == 0) return null;
            if (pressed.Count > 1) throw new InvalidOperationException("Several buttons changed. Return to neutral, then try that gear or button alone.");
            return new Binding { Label = target.Name, DeviceId = target.DeviceId, InputKind = "Button", InputIndex = pressed[0].Index, OutputKind = "Button", OutputIndex = target.Output };
        }
        public static void Put(BridgeProfile draft, Binding binding)
        {
            if (draft.Bindings.Any(b => b.OutputKind == binding.OutputKind && b.OutputIndex != binding.OutputIndex && b.DeviceId == binding.DeviceId && b.InputKind == binding.InputKind && b.InputIndex == binding.InputIndex))
                throw new InvalidOperationException("That physical control is already mapped. Use a separate axis, gear or button.");
            draft.Bindings.RemoveAll(b => b.OutputKind == binding.OutputKind && b.OutputIndex == binding.OutputIndex);
            draft.Bindings.Add(binding);
        }
        public static void KeepRequestedMappings(BridgeProfile draft, IList<SetupControl> plan, bool reuse)
        {
            // Old bulk button maps have no gear semantics and can fill all 128 slots with one shifter.
            // Reuse axes and explicitly named gears; rebuild remaining buttons after guided learning.
            draft.Bindings.RemoveAll(binding => !reuse || !plan.Any(target => target.DeviceId == binding.DeviceId && target.Kind == binding.OutputKind && target.Output == binding.OutputIndex &&
                (binding.OutputKind == "Axis" || binding.Label == target.Name)));
        }
        // Reserve explicit gears first, then distribute remaining outputs fairly across sources.
        public static int AddButtons(BridgeProfile draft, IList<PhysicalDevice> devices)
        {
            var sources = devices.Where(d => draft.Devices.Contains(d.Id)).ToList();
            var queues = sources.Select(d => new Queue<InputControl>(d.Controls.Where(c => c.Kind == "Button" && !draft.Bindings.Any(b => b.DeviceId == d.Id && b.InputKind == "Button" && b.InputIndex == c.Index)).OrderBy(c => c.Index))).ToList();
            int next = 0;
            while (queues.Any(q => q.Count > 0))
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    while (next < 128 && draft.Bindings.Any(b => b.OutputKind == "Button" && b.OutputIndex == next)) next++;
                    if (next >= 128) return queues.Sum(q => q.Count);
                    if (queues[i].Count == 0) continue;
                    var control = queues[i].Dequeue();
                    draft.Bindings.Add(new Binding { Label = sources[i].Name + " / " + control.Name, DeviceId = sources[i].Id, InputKind = "Button", InputIndex = control.Index, OutputKind = "Button", OutputIndex = next++ });
                }
            }
            return 0;
        }
        public static void AddHats(BridgeProfile draft, IList<PhysicalDevice> devices)
        {
            int next = 0;
            foreach (var device in devices.Where(d => draft.Devices.Contains(d.Id)))
                foreach (var control in device.Controls.Where(c => c.Kind == "POV"))
                {
                    if (draft.Bindings.Any(b => b.DeviceId == device.Id && b.InputKind == "POV" && b.InputIndex == control.Index)) continue;
                    while (next < 4 && draft.Bindings.Any(b => b.OutputKind == "POV" && b.OutputIndex == next)) next++;
                    if (next < 4) draft.Bindings.Add(new Binding { Label = device.Name + " / hat", DeviceId = device.Id, InputKind = "POV", InputIndex = control.Index, OutputKind = "POV", OutputIndex = next++ });
                }
        }
    }
}
