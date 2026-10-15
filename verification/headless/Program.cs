using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Apocasetter;
using BepInEx.Configuration;

partial class Program
{
    static int count;
    static void Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            string name = new AssemblyName(args.Name).Name + ".dll";
            foreach (string dir in new[] { @"E:\SteamLibrary\steamapps\common\Apocalypter\BepInEx\core", @"E:\SteamLibrary\steamapps\common\Apocalypter\Apocalypter_Data\Managed" })
            { string path = Path.Combine(dir, name); if (File.Exists(path)) return Assembly.LoadFrom(path); }
            return null;
        };
        Run();
        RunPatcherChecks();
        Console.WriteLine("COMPLETE: " + count + " checks passed");
    }
    static void Check(bool ok, string text)
    { if (!ok) throw new Exception(text); count++; Console.WriteLine("PASS " + text); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run()
    {
        var config = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixture.cfg"), false);
        config.SaveOnConfigSet = false;
        Plugin.UpdateFrequencyEntry = config.Bind("Updates", "UpdateFrequency", 6, new ConfigDescription("fixture", new AcceptableValueRange<int>(0, 48)));
        Check(Plugin.UpdateFrequencyEntry.Value == 6, "Default frequency preserves the six-hour cache interval");
        Updates.HaveIndex = true; Updates.IndexTimeUtc = DateTime.UtcNow;
        Plugin.UpdateFrequencyEntry.Value = 0;
        Check(!Updates.CacheFresh, "Each time (0) never reuses cached results, including just-fetched results");
        Plugin.UpdateFrequencyEntry.Value = 1; Updates.IndexTimeUtc = DateTime.UtcNow.AddMinutes(-30);
        Check(Updates.CacheFresh, "One-hour frequency accepts a thirty-minute cache");
        Updates.IndexTimeUtc = DateTime.UtcNow.AddHours(-2);
        Check(!Updates.CacheFresh, "One-hour frequency expires a two-hour cache");
        Plugin.UpdateFrequencyEntry.Value = 48; Updates.IndexTimeUtc = DateTime.UtcNow.AddHours(-47);
        Check(Updates.CacheFresh, "48-hour frequency reuses a 47-hour cache");
        Updates.IndexTimeUtc = DateTime.UtcNow.AddHours(-49);
        Check(!Updates.CacheFresh, "48-hour frequency expires a 49-hour cache");
        Plugin.UpdateFrequencyEntry.Value = 100;
        Check(Plugin.UpdateFrequencyEntry.Value == 48, "Config values above the maximum clamp to 48");
        Plugin.UpdateFrequencyEntry.Value = -1;
        Check(Plugin.UpdateFrequencyEntry.Value == 0, "Config values below the minimum clamp to Each time");
        Plugin.UpdateFrequencyEntry.Value = 48; Updates.IndexTimeUtc = DateTime.UtcNow; Updates.HaveIndex = false;
        Check(!Updates.CacheFresh, "No index always requires a check");

        var trustOf = typeof(SettingsUI).GetMethod("TrustOf", BindingFlags.Static | BindingFlags.NonPublic);
        var mod = new ModEntry { Guid = "verification.trust" };
        foreach (string category in new[] { "official", "community", "unverified" })
        {
            Updates.Index[mod.Guid] = new IndexMod { Guid = mod.Guid, Trust = category.ToUpperInvariant() };
            Check((string)trustOf.Invoke(null, new object[] { mod }) == category, "Trust classification handles " + category + " without case sensitivity");
        }
        Updates.Index.Remove(mod.Guid);
        Check((string)trustOf.Invoke(null, new object[] { mod }) == "unverified", "Plugins absent from the index are unverified");
    }
}
