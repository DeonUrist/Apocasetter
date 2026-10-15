using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;

namespace Apocasetter
{
    internal static class PatcherCatalog
    {
        private static readonly HashSet<string> Warned = new HashSet<string>();

        internal static void AddTo(List<ModEntry> mods)
        {
            if (!Directory.Exists(Paths.PatcherPluginPath)) return;
            var installer = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Apocasetter.Installer");
            var installerType = installer != null ? installer.GetType("Apocasetter.Installer.Patcher") : null;
            var completed = installerType != null ? installerType.GetField("CompletedPatchers", BindingFlags.Static | BindingFlags.Public) : null;
            var initializedTypes = completed != null ? completed.GetValue(null) as HashSet<string> : null;
            foreach (var path in Directory.GetFiles(Paths.PatcherPluginPath, "*.dll", SearchOption.AllDirectories))
            {
                try
                {
                    var metadata = PatcherMetadata.Read(path);
                    if (metadata == null) continue; // Native DLLs and managed helper libraries are not patchers.
                    var indexed = string.IsNullOrEmpty(metadata.Guid) ? null : Updates.Find(metadata.Guid);
                    if (indexed == null)
                    {
                        var matches = Updates.Index.Values.Where(im => im.Zip != null && im.Zip.ExtractTo == "BepInEx" && !string.IsNullOrEmpty(im.Zip.GuidDll)
                            && im.Zip.GuidDll.Replace('\\', '/').StartsWith("patchers/", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(Path.GetFileName(im.Zip.GuidDll.Replace('/', Path.DirectorySeparatorChar)), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)).ToList();
                        if (matches.Count == 1) indexed = matches[0];
                    }
                    string guid = indexed != null ? indexed.Guid : metadata.Guid;
                    if (string.IsNullOrEmpty(guid)) guid = "patcher." + Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                    bool self = string.Equals(Path.GetFullPath(path), Path.GetFullPath(Updates.InstallerPath), StringComparison.OrdinalIgnoreCase);
                    var existing = mods.FirstOrDefault(m => string.Equals(m.Guid, guid, StringComparison.OrdinalIgnoreCase));
                    if (existing != null) existing.CompanionPaths.Add(path);
                    var mod = existing ?? new ModEntry { Guid = guid, Name = indexed != null ? indexed.Name : metadata.Name,
                        Version = metadata.Version, Location = path, Kind = ModKind.Loaded, IsPatcher = true, Self = self };
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (assembly.IsDynamic || !string.Equals(assembly.Location, path, StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (var name in metadata.Types)
                        {
                            var type = assembly.GetType(name, false);
                            // Static field reads can run initializers. Only inspect already executed preloader types.
                            if (type == null || initializedTypes == null || !initializedTypes.Contains(assembly.FullName + "|" + type.FullName)) continue;
                            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                            {
                                if (!typeof(ConfigFile).IsAssignableFrom(field.FieldType)) continue;
                                ConfigFile config;
                                try { config = field.GetValue(null) as ConfigFile; } catch { continue; }
                                if (config == null) continue;
                                if (mod.Config == null) mod.Config = config;
                                foreach (var key in config.Keys)
                                    if (!string.Equals(key.Key, "Apocasetter", StringComparison.OrdinalIgnoreCase) && !mod.Entries.Contains(config[key])) mod.Entries.Add(config[key]);
                            }
                        }
                    }
                    if (existing == null) mods.Add(mod);
                }
                catch (BadImageFormatException) { }
                catch (Exception e)
                {
                    if (Warned.Add(path + "|" + e.Message)) Plugin.Log.LogWarning("Could not inspect patcher " + Path.GetFileName(path) + ": " + e.Message);
                }
            }
        }
    }
}
