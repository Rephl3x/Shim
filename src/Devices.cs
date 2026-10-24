using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FHMozaFix
{
    public sealed class DeviceNode
    {
        public string Id { get; set; }
        public string ParentId { get; set; }
        public string Name { get; set; }
        public string Product { get; set; }
        public string ClassName { get; set; }
        public string Service { get; set; }
        public string[] HardwareIds { get; set; }
        public string[] CompatibleIds { get; set; }
        public bool StatusKnown { get; set; }
        public bool Started { get; set; }
        public uint Problem { get; set; }

        public DeviceNode()
        {
            Id = ParentId = Name = Product = ClassName = Service = "";
            HardwareIds = CompatibleIds = new string[0];
        }

        public IEnumerable<string> AllIds { get { return HardwareIds.Concat(CompatibleIds); } }
        public bool Healthy { get { return StatusKnown && Started && Problem == 0; } }
    }

    public sealed class Controller
    {
        public string TargetId { get; set; }
        public string Name { get; set; }
        public string BlockedReason { get; set; }
        public string[] HardwareIds { get; set; }
        public string[] GameIds { get; set; }
        public bool Allowed { get { return String.IsNullOrEmpty(BlockedReason); } }
        public override string ToString() { return Name; }
    }

    public static class DeviceSafety
    {
        private static readonly Regex UsbDevice = new Regex(
            @"^USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(?:&[0-9A-Z_]+)*\\[^\\\""\r\n*?]+$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool IsPhysicalUsb(string id)
        {
            return id != null && UsbDevice.IsMatch(id) &&
                id.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) < 0;
        }

        public static bool IsGame(DeviceNode node)
        {
            if (!Eq(node.ClassName, "HIDClass")) return false;
            return node.AllIds.Any(id => Eq(id, "HID_DEVICE_SYSTEM_GAME") ||
                Regex.IsMatch(id, @"^HID_DEVICE_UP:0001_U:000[458]$", RegexOptions.IgnoreCase));
        }

        private static bool IsProtectedHid(DeviceNode node)
        {
            return node.AllIds.Any(id => Eq(id, "HID_DEVICE_SYSTEM_KEYBOARD") ||
                Eq(id, "HID_DEVICE_SYSTEM_MOUSE") || Eq(id, "HID_DEVICE_SYSTEM_CONTROL") ||
                Regex.IsMatch(id, @"^HID_DEVICE_UP:0001_U:(000[1267]|008[0-9A-F])$", RegexOptions.IgnoreCase));
        }

        public static List<Controller> Discover(IList<DeviceNode> nodes)
        {
            var byId = nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
            var roots = new Dictionary<string, DeviceNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in nodes.Where(IsGame))
            {
                var cursor = game;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (!IsPhysicalUsb(cursor.Id) && seen.Add(cursor.Id))
                {
                    DeviceNode parent;
                    if (!byId.TryGetValue(cursor.ParentId, out parent)) break;
                    cursor = parent;
                }
                if (!IsPhysicalUsb(cursor.Id)) cursor = game;
                roots[cursor.Id] = cursor;
            }

            var result = new List<Controller>();
            foreach (var root in roots.Values)
            {
                var tree = Descendants(root.Id, nodes);
                var games = tree.Where(IsGame).ToList();
                string name = games.Select(n => n.Product).FirstOrDefault(s => !String.IsNullOrWhiteSpace(s));
                if (String.IsNullOrEmpty(name)) name = root.Product;
                if (String.IsNullOrEmpty(name)) name = root.Name;
                string reason = ValidateTree(root, tree);
                if (String.IsNullOrEmpty(reason) && (!root.Healthy || games.Any(n => !n.Healthy)))
                    reason = "Device is disabled, unavailable, or has a driver problem. Enable it in Device Manager first.";
                result.Add(new Controller {
                    TargetId = root.Id, Name = name, HardwareIds = root.HardwareIds,
                    GameIds = games.Select(n => n.Id).ToArray(), BlockedReason = reason
                });
            }
            return result.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<DeviceNode> Descendants(string root, IList<DeviceNode> nodes)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
            bool changed;
            do
            {
                changed = false;
                foreach (var node in nodes)
                    if (ids.Contains(node.ParentId) && ids.Add(node.Id)) changed = true;
            } while (changed);
            return nodes.Where(n => ids.Contains(n.Id)).ToList();
        }

        public static string ValidateTree(DeviceNode root, IList<DeviceNode> tree)
        {
            if (!IsPhysicalUsb(root.Id))
                return "Only dedicated USB HID controllers are supported. Bluetooth and virtual devices are shown for reference.";
            if (!(Eq(root.Service, "HidUsb") || Eq(root.Service, "usbccgp")) ||
                !(Eq(root.ClassName, "HIDClass") || Eq(root.ClassName, "USB") || Eq(root.ClassName, "USBDevice")))
                return "This USB device uses an unsupported driver. Reconnect it manually.";
            foreach (var node in tree)
            {
                if (IsProtectedHid(node) || Eq(node.ClassName, "Keyboard") || Eq(node.ClassName, "Mouse"))
                    return "Shares its USB device with keyboard, mouse, or system controls. Reconnect it manually.";
                // MOZA devices include their own usbser configuration port. It is part
                // of this controller, not an unrelated USB peripheral behind a hub.
                bool configPort = Eq(node.ClassName, "Ports") && Eq(node.Service, "usbser") &&
                    node.Id.StartsWith(root.Id.Split('\\')[0] + "\\" + root.Id.Split('\\')[1] + "&MI_", StringComparison.OrdinalIgnoreCase);
                // MOZA's wheel driver adds software components below its HID node.
                // Their child nodes are still individually checked by this loop.
                bool mozaComponent = root.Id.StartsWith("USB\\VID_346E&", StringComparison.OrdinalIgnoreCase) &&
                    Eq(node.ClassName, "SoftwareComponent") && node.Id.StartsWith("SWD\\DRIVERENUM\\", StringComparison.OrdinalIgnoreCase) &&
                    node.HardwareIds.Any(id => id.StartsWith("SWC\\VEN_346E&", StringComparison.OrdinalIgnoreCase));
                if (!Eq(node.Id, root.Id) && !Eq(node.ClassName, "HIDClass") && !configPort && !mozaComponent)
                    return "Shares its USB device with a non-HID function (" + node.ClassName + "). Reconnect it manually.";
            }
            return null;
        }

        public static void ValidateRecovery(Controller saved, IList<DeviceNode> nodes)
        {
            var root = nodes.FirstOrDefault(n => Eq(n.Id, saved.TargetId));
            if (root == null) throw new InvalidOperationException("Device is missing. Connect it to its original USB port: " + saved.Name);
            string reason = ValidateTree(root, Descendants(root.Id, nodes));
            if (reason != null) throw new InvalidOperationException(saved.Name + ": " + reason);
            if (saved.HardwareIds == null || !root.HardwareIds.Intersect(saved.HardwareIds, StringComparer.OrdinalIgnoreCase).Any())
                throw new InvalidOperationException("Device identity changed: " + saved.Name);
        }

        public static bool Eq(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}
