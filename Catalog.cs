using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace Apocasetter
{
    public enum ModKind { Loaded, Disabled, Available }

    public class ModEntry
    {
        public string Guid, Name, Version, Location;
        public ModKind Kind;
        public PluginInfo Info;
        public ConfigFile Config;
        public bool OptedIn, Self;
        public List<ConfigEntryBase> Entries = new List<ConfigEntryBase>();
        public DisabledMod DisabledInfo;
        public IndexMod Index { get { return Updates.Find(Guid); } }
        public bool Other { get { return Kind == ModKind.Loaded && !OptedIn; } }

        private bool _iconTried;
        private Texture2D _icon;
        public Texture2D Icon
        {
            get
            {
                if (_iconTried) return _icon;
                _iconTried = true;
                _icon = Catalog.LoadIcon(this);
                return _icon;
            }
        }

        public string Initials
        {
            get
            {
                var words = Regex.Split(Name ?? "?", @"[^A-Za-z0-9]+").Where(w => w.Length > 0).ToArray();
                if (words.Length >= 2) return (words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpperInvariant();
                var n = words.Length > 0 ? words[0] : "?";
                if (n.StartsWith("Apoca", StringComparison.OrdinalIgnoreCase) && n.Length > 6) n = n.Substring(5);
                return n.Substring(0, Math.Min(2, n.Length)).ToUpperInvariant();
            }
        }

        /// BepInEx\plugins\<Folder> when the mod lives in its own folder, else null (single DLL in plugins)
        public string PluginFolder
        {
            get
            {
                if (string.IsNullOrEmpty(Location)) return null;
                var plugins = Path.GetFullPath(Paths.PluginPath).TrimEnd('\\', '/');
                var dir = Path.GetFullPath(Path.GetDirectoryName(Location)).TrimEnd('\\', '/');
                if (string.Equals(dir, plugins, StringComparison.OrdinalIgnoreCase)) return null;
                if (!dir.StartsWith(plugins + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
                var top = dir.Substring(plugins.Length + 1).Split('\\', '/')[0];
                return Path.Combine(plugins, top);
            }
        }

        /// What Remove / Disable move: the plugin's own folder, or its DLL (+ .pdb / .xml beside it)
        public List<string> PluginPaths()
        {
            var r = new List<string>();
            var folder = PluginFolder;
            if (folder != null) { r.Add(folder); return r; }
            if (string.IsNullOrEmpty(Location)) return r;
            r.Add(Location);
            foreach (var ext in new[] { ".pdb", ".xml", ".png" })
            {
                var p = Path.ChangeExtension(Location, ext);
                if (File.Exists(p)) r.Add(p);
            }
            return r;
        }

        /// The mod's config file(s) and a config\<Name>\ data folder when there is one
        public List<string> ConfigPaths()
        {
            var r = new List<string>();
            try
            {
                if (Config != null && File.Exists(Config.ConfigFilePath)) r.Add(Config.ConfigFilePath);
                foreach (var f in Directory.GetFiles(Paths.ConfigPath, Guid + ".*.cfg")) if (!r.Contains(f)) r.Add(f);
                var names = new[] { Name, PluginFolder != null ? Path.GetFileName(PluginFolder) : null, Location != null ? Path.GetFileNameWithoutExtension(Location) : null };
                foreach (var n in names.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var d = Path.Combine(Paths.ConfigPath, n);
                    if (Directory.Exists(d) && !r.Contains(d)) r.Add(d);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("ConfigPaths(" + Name + "): " + e.Message); }
            return r;
        }

        /// Mods that declare a dependency on this one: (name, hard?)
        public List<KeyValuePair<string, bool>> Dependents()
        {
            var r = new List<KeyValuePair<string, bool>>();
            foreach (var kv in Chainloader.PluginInfos)
            {
                if (kv.Key == Guid || kv.Value == null) continue;
                foreach (var d in kv.Value.Dependencies ?? Enumerable.Empty<BepInDependency>())
                    if (d.DependencyGUID == Guid)
                        r.Add(new KeyValuePair<string, bool>(kv.Value.Metadata.Name, (d.Flags & BepInDependency.DependencyFlags.HardDependency) != 0));
            }
            var im = Index;
            if (im != null)
                foreach (var other in Updates.Index.Values)
                    if (other.Optional.Contains(Guid) && Chainloader.PluginInfos.ContainsKey(other.Guid) && !r.Any(x => x.Key == other.Name))
                        r.Add(new KeyValuePair<string, bool>(other.Name, false));
            return r;
        }
    }

    // =====================================================================
    //  Every plugin BepInEx loaded (opted-in ones show their settings), the
    //  ones disabled through the Mods window, and index mods not installed.
    // =====================================================================
    public static class Catalog
    {
        private static readonly Regex OptInRx = new Regex(@"^\s*Apocasetter\s*=\s*true\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static List<ModEntry> _mods;

        public static void Invalidate() { _mods = null; }

        public static List<ModEntry> All()
        {
            if (_mods != null) return _mods;
            var list = new List<ModEntry>();
            foreach (var kv in Chainloader.PluginInfos)
            {
                var info = kv.Value;
                // the game destroys plugin GameObjects on scene load, so Unity's == null would be true; the managed object (and its Config) is still fine
                if (info == null) continue;
                ConfigFile cfg = null;
                if (!ReferenceEquals(info.Instance, null)) { try { cfg = info.Instance.Config; } catch { } }
                var m = new ModEntry
                {
                    Guid = kv.Key, Name = info.Metadata.Name, Version = info.Metadata.Version.ToString(), Location = info.Location,
                    Kind = ModKind.Loaded, Info = info, Config = cfg, Self = kv.Key == Plugin.GUID
                };
                m.OptedIn = m.Self || (cfg != null && OptedIn(cfg));
                if (cfg != null)
                    foreach (var e in cfg.Keys)
                    {
                        if (string.Equals(e.Key, "Apocasetter", StringComparison.OrdinalIgnoreCase)) continue;
                        m.Entries.Add(cfg[e]);
                    }
                list.Add(m);
            }
            foreach (var d in Updates.Disabled)
                if (!list.Any(x => x.Guid == d.Guid))
                    list.Add(new ModEntry { Guid = d.Guid, Name = d.Name, Version = d.Version, Kind = ModKind.Disabled, DisabledInfo = d });
            foreach (var im in Updates.Index.Values)
                if (!im.Blocked && !list.Any(x => x.Guid == im.Guid))
                    list.Add(new ModEntry { Guid = im.Guid, Name = im.Name, Version = null, Kind = ModKind.Available });

            _mods = list.OrderBy(m => m.Self ? 0 : 1).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return _mods;
        }

        public static ModEntry Find(string guid) { return All().FirstOrDefault(m => m.Guid == guid); }

        private static bool OptedIn(ConfigFile cfg)
        {
            foreach (var d in cfg.Keys)
            {
                if (!string.Equals(d.Key, "Apocasetter", StringComparison.OrdinalIgnoreCase)) continue;
                var v = cfg[d].BoxedValue;
                if (v is bool) return (bool)v;
                return string.Equals(Convert.ToString(v), "true", StringComparison.OrdinalIgnoreCase);
            }
            try
            {
                if (!string.IsNullOrEmpty(cfg.ConfigFilePath) && File.Exists(cfg.ConfigFilePath))
                    return OptInRx.IsMatch(File.ReadAllText(cfg.ConfigFilePath));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Cannot read " + cfg.ConfigFilePath + ": " + e.Message); }
            return false;
        }

        // icon.png in the mod's own folder, <Dll>.png beside a loose DLL, or Apocasetter's own theme\game\icons\<guid>.png
        public static Texture2D LoadIcon(ModEntry m)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(m.Location))
            {
                if (m.PluginFolder != null) candidates.Add(Path.Combine(Path.GetDirectoryName(m.Location), "icon.png"));
                candidates.Add(Path.ChangeExtension(m.Location, ".png"));
            }
            candidates.Add(Path.Combine(Path.Combine(GameSkin.Dir, "icons"), m.Guid + ".png"));
            foreach (var p in candidates)
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(t, File.ReadAllBytes(p), false)) continue;
                    t.hideFlags = HideFlags.HideAndDontSave; t.filterMode = FilterMode.Bilinear; t.wrapMode = TextureWrapMode.Clamp;
                    return t;
                }
                catch (Exception e) { Plugin.Log.LogWarning("Icon " + p + ": " + e.Message); }
            }
            return null;
        }
    }
}
