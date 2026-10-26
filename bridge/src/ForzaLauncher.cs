using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Microsoft.Win32;

namespace RigBridge
{
    public sealed class ShimOptions
    {
        public bool StartBridgeWithForza { get; set; }
        public string ForzaTargetId { get; set; }
    }

    public sealed class ForzaGameTarget
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Source { get; set; }
        public string Value { get; set; }
        public string DisplayName { get { return Name + " (" + Source + ")"; } }
        public override string ToString() { return DisplayName; }
    }

    public static class ForzaLauncher
    {
        private static readonly int[] SteamAppIds = { 2483190, 1551360, 1293830, 2440510 };

        public static bool ShouldStartBridge(bool enabled, bool alreadyRunning)
        { return enabled && !alreadyRunning; }

        public static bool IsForzaGameName(string value)
        {
            return !String.IsNullOrWhiteSpace(value) && Regex.IsMatch(value.Trim(),
                @"^Forza\s+(?:Horizon(?:\s+\d+)?|Motorsport)(?:\b|\s*\(|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        public static List<ForzaGameTarget> Discover()
        {
            var results = new List<ForzaGameTarget>();
            DiscoverAppsFolder(results);
            DiscoverStartMenuShortcuts(results);
            DiscoverSteamGames(results);
            return results.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Source).ToList();
        }

        public static Process Launch(ForzaGameTarget target)
        {
            return Process.Start(CreateStartInfo(target));
        }

        public static ProcessStartInfo CreateStartInfo(ForzaGameTarget target)
        {
            if (target == null) throw new ArgumentNullException("target");
            if (target.Id.StartsWith("app:", StringComparison.Ordinal))
            {
                if (!Regex.IsMatch(target.Value ?? "", @"^[A-Za-z0-9_.-]+![A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant))
                    throw new InvalidOperationException("The selected Xbox app identity is invalid. Refresh installed games and try again.");
                return new ProcessStartInfo { FileName = "explorer.exe", Arguments = DriverSetup.Quote("shell:AppsFolder\\" + target.Value), UseShellExecute = true };
            }
            if (target.Id.StartsWith("shortcut:", StringComparison.Ordinal))
            {
                if (String.IsNullOrWhiteSpace(target.Value) || !File.Exists(target.Value))
                    throw new FileNotFoundException("The selected Forza Start Menu shortcut is no longer available. Refresh installed games.", target.Value);
                return new ProcessStartInfo { FileName = target.Value, UseShellExecute = true };
            }
            if (target.Id.StartsWith("steam:", StringComparison.Ordinal))
            {
                int appId;
                if (!Int32.TryParse(target.Value, out appId) || !SteamAppIds.Contains(appId))
                    throw new InvalidOperationException("The selected Steam game identity is invalid. Refresh installed games and try again.");
                return new ProcessStartInfo { FileName = "steam://rungameid/" + appId, UseShellExecute = true };
            }
            throw new InvalidOperationException("The selected Forza launch target is not supported. Refresh installed games and try again.");
        }

        private static void DiscoverAppsFolder(List<ForzaGameTarget> results)
        {
            object shell = null, folder = null, items = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return;
                shell = Activator.CreateInstance(shellType);
                folder = Invoke(shell, "NameSpace", BindingFlags.InvokeMethod, "shell:AppsFolder");
                if (folder == null) return;
                items = Invoke(folder, "Items", BindingFlags.GetProperty | BindingFlags.InvokeMethod);
                int count = Convert.ToInt32(Invoke(items, "Count", BindingFlags.GetProperty));
                for (int i = 0; i < count; i++)
                {
                    object item = null;
                    try
                    {
                        item = Invoke(items, "Item", BindingFlags.InvokeMethod, i);
                        string name = Convert.ToString(Invoke(item, "Name", BindingFlags.GetProperty));
                        string aumid = Convert.ToString(Invoke(item, "Path", BindingFlags.GetProperty));
                        if (IsForzaGameName(name) && !String.IsNullOrWhiteSpace(aumid) && aumid.Contains("!"))
                            results.Add(new ForzaGameTarget { Id = "app:" + aumid, Value = aumid, Name = CleanName(name), Source = "Xbox app" });
                    }
                    catch { }
                    finally { Release(item); }
                }
            }
            catch { }
            finally { Release(items); Release(folder); Release(shell); }
        }

        private static void DiscoverStartMenuShortcuts(List<ForzaGameTarget> results)
        {
            string[] roots = { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) };
            foreach (string root in roots.Where(Directory.Exists))
            {
                try
                {
                    foreach (string shortcut in Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories))
                    {
                        string name = Path.GetFileNameWithoutExtension(shortcut);
                        if (!IsForzaGameName(name)) continue;
                        results.Add(new ForzaGameTarget { Id = "shortcut:" + shortcut, Value = shortcut, Name = CleanName(name), Source = "Start Menu" });
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void DiscoverSteamGames(List<ForzaGameTarget> results)
        {
            var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in SteamRoots())
            {
                libraries.Add(root);
                string file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(file)) continue;
                try
                {
                    string contents = File.ReadAllText(file);
                    foreach (Match match in Regex.Matches(contents, "\"path\"\\s+\"((?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase))
                        libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            var games = new Dictionary<int, string> { { 2483190, "Forza Horizon 6" }, { 1551360, "Forza Horizon 5" }, { 1293830, "Forza Horizon 4" }, { 2440510, "Forza Motorsport" } };
            foreach (string library in libraries)
                foreach (var game in games)
                    if (File.Exists(Path.Combine(library, "steamapps", "appmanifest_" + game.Key + ".acf")))
                        results.Add(new ForzaGameTarget { Id = "steam:" + game.Key, Value = game.Key.ToString(), Name = game.Value, Source = "Steam" });
        }

        private static IEnumerable<string> SteamRoots()
        {
            string[] keys = { @"Software\Valve\Steam", @"SOFTWARE\WOW6432Node\Valve\Steam" };
            foreach (string key in keys)
            {
                string value = RegistryValue(Registry.CurrentUser, key, "SteamPath");
                if (String.IsNullOrWhiteSpace(value)) value = RegistryValue(Registry.LocalMachine, key, "InstallPath");
                if (!String.IsNullOrWhiteSpace(value) && Directory.Exists(value)) yield return value;
            }
        }

        private static string RegistryValue(RegistryKey hive, string path, string name)
        {
            try { using (RegistryKey key = hive.OpenSubKey(path)) return key == null ? null : Convert.ToString(key.GetValue(name)); }
            catch (System.Security.SecurityException) { return null; }
        }

        private static string CleanName(string value)
        { return Regex.Replace(value ?? "Forza", @"\s*\([^)]*\)\s*$", "").Trim(); }

        private static object Invoke(object instance, string name, BindingFlags operation, params object[] args)
        {
            return instance.GetType().InvokeMember(name, operation | BindingFlags.Public | BindingFlags.Instance, null, instance, args);
        }

        private static void Release(object instance)
        { if (instance != null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance); }
    }
}
