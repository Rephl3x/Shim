using System;
using System.IO;
using System.Security.Cryptography;

namespace RigBridge
{
    public static class DriverPackages
    {
        public const string VjoyInstaller = "vJoySetup_v2.2.2.0_Win10_Win11.exe";
        public const string HideInstaller = "HidHide_1.5.230_x64.exe";
        public static string PathFor(bool vjoy)
        { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "drivers", vjoy ? VjoyInstaller : HideInstaller); }
        public static void Verify(bool vjoy)
        {
            string expected = vjoy ? "ef569a3105cd301b89580f18f60c66b339e95296acf2c0dfcaf4b4bbf8ab68fe" : "f4bbbcb82e6258641b887c74bc81c4c5f66e4aa811808dfc304347687b7605f6";
            using (var stream = File.OpenRead(PathFor(vjoy))) using (var sha = SHA256.Create())
            {
                string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (actual != expected) throw new InvalidDataException("The bundled installer failed its SHA-256 check. Extract a fresh package.");
            }
        }
    }
}
