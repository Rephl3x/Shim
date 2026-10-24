using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace FHMozaFix
{
    public interface IRecoveryStore
    {
        bool Exists { get; }
        List<Controller> Read();
        void Write(List<Controller> devices);
    }

    public sealed class RecoveryStore : IRecoveryStore
    {
        private readonly string path;
        public RecoveryStore(string path) { this.path = path; }
        public bool Exists { get { return File.Exists(path); } }
        public List<Controller> Read()
        {
            if (!Exists) return new List<Controller>();
            var result = Json.Read<List<Controller>>(path);
            if (result == null || result.Count == 0)
                throw new InvalidDataException("The recovery file is empty or damaged. Keep it and use Device Manager to check your controllers.");
            foreach (var controller in result)
                if (controller == null || !DeviceSafety.IsPhysicalUsb(controller.TargetId) ||
                    controller.HardwareIds == null || controller.HardwareIds.Length == 0 ||
                    controller.GameIds == null || controller.GameIds.Length == 0)
                    throw new InvalidDataException("The recovery file contains an invalid device. Keep it and use Device Manager to check your controllers.");
            return result;
        }
        public void Write(List<Controller> devices)
        {
            if (devices.Count == 0) { if (Exists) File.Delete(path); return; }
            Json.WriteAtomic(path, devices);
        }
    }

    public sealed class Profile
    {
        public List<string> Order { get; set; }
        public int Mode { get; set; }
        public int DelaySeconds { get; set; }
        public bool ForceReconnect { get; set; }
        public Profile() { Order = new List<string>(); DelaySeconds = 3; Mode = 1; }
    }

    public static class Json
    {
        public static T Read<T>(string path)
        {
            return new JavaScriptSerializer().Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
        }

        public static void WriteAtomic(string path, object value)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            string temp = path + ".tmp";
            byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(value));
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
    }
}
