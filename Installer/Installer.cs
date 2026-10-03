using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace Apocasetter.Installer
{
    // =====================================================================
    //  Apocasetter.Installer: a BepInEx preloader patcher.
    //
    //  The game holds every loaded plugin DLL, so the Mods window can only
    //  *stage* updates, installs and removals (BepInEx\cache\Apocasetter\pending.json).
    //  BepInEx runs patchers before it loads any plugin; at that moment no mod DLL
    //  is in use, and this class applies the staged operations:
    //    update / install  verify the zip's SHA-256, back up every file it replaces,
    //                      unzip on top (never deletes files the zip doesn't contain)
    //    remove            move the mod's files to cache\Apocasetter\removed\...
    //    disable / enable  move them to / back from cache\Apocasetter\disabled\<guid>\
    //  Any failure rolls that operation back. Results go to last-install.json, which
    //  the Mods window reports once.
    //  It patches no assembly (TargetDLLs is empty).
    // =====================================================================
    public static class Patcher
    {
        public const string VERSION = "2.0.0";
        public static IEnumerable<string> TargetDLLs { get { return new string[0]; } }
        public static void Patch(AssemblyDefinition assembly) { }

        private static ManualLogSource _log;
        private static string _root, _data;

        public static void Initialize()
        {
            _log = Logger.CreateLogSource("Apocasetter.Installer");
            try { Run(); }
            catch (Exception e) { _log.LogError("Installer failed: " + e); }
        }

        private static void Run()
        {
            _root = Full(Paths.BepInExRootPath);
            _data = Path.Combine(Path.Combine(_root, "cache"), "Apocasetter");
            var pendingPath = Path.Combine(_data, "pending.json");
            if (!File.Exists(pendingPath)) { _log.LogInfo("Apocasetter.Installer " + VERSION + ": nothing staged"); return; }

            var doc = MiniJson.Obj(MiniJson.Parse(File.ReadAllText(pendingPath)));
            var ops = MiniJson.Arr(doc, "ops") ?? new List<object>();
            _log.LogInfo("Apocasetter.Installer " + VERSION + ": " + ops.Count + " staged operation(s)");
            var results = new List<object>();
            foreach (var o in ops)
            {
                var op = MiniJson.Obj(o);
                if (op == null) continue;
                string kind = MiniJson.Str(op, "op"), name = MiniJson.Str(op, "name");
                string msg;
                bool ok;
                try
                {
                    switch (kind)
                    {
                        case "update": case "install": msg = Extract(op); break;
                        case "remove": msg = MoveAway(op, false); break;
                        case "disable": msg = MoveAway(op, true); break;
                        case "enable": msg = Enable(op); break;
                        default: throw new Exception("unknown operation '" + kind + "'");
                    }
                    ok = true;
                    _log.LogInfo(kind + " " + name + ": " + msg);
                }
                catch (Exception e)
                {
                    ok = false;
                    msg = e.Message;
                    _log.LogError(kind + " " + name + " failed: " + e);
                }
                results.Add(new Dictionary<string, object>
                {
                    { "id", MiniJson.Str(op, "id") }, { "op", kind }, { "guid", MiniJson.Str(op, "guid") }, { "name", name },
                    { "version", MiniJson.Str(op, "version") }, { "ok", ok }, { "message", msg }
                });
            }
            File.WriteAllText(Path.Combine(_data, "last-install.json"), MiniJson.Write(new Dictionary<string, object>
            {
                { "time", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") }, { "results", results }
            }));
            File.Delete(pendingPath);
        }

        // ---------------------------------------------------------------- update / install
        private static string Extract(Dictionary<string, object> op)
        {
            var zipPath = MiniJson.Str(op, "zip");
            if (!File.Exists(zipPath)) throw new Exception("the downloaded zip is gone: " + zipPath);
            var want = MiniJson.Str(op, "sha256").ToLowerInvariant();
            var have = Sha256(zipPath);
            if (want.Length > 0 && want != have) throw new Exception("the zip's checksum doesn't match (expected " + want + ", got " + have + ")");

            var extractTo = MiniJson.Str(op, "extractTo", "plugins");
            var destRoot = extractTo == "BepInEx" ? _root : Full(Paths.PluginPath);
            var strip = MiniJson.Str(op, "stripPrefix").Replace('\\', '/');
            var skip = new HashSet<string>(MiniJson.StrList(op, "skip").Select(s => s.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backup = Path.Combine(Path.Combine(_data, "backup"), Safe(MiniJson.Str(op, "name")) + "-" + Safe(MiniJson.Str(op, "fromVersion", "new")) + "-" + stamp);

            var written = new List<string>();                 // files created that didn't exist before
            var backedUp = new List<KeyValuePair<string, string>>(); // original path -> backup path
            var moved = new List<KeyValuePair<string, string>>();    // old DLL moved away
            int files = 0;
            try
            {
                using (var fs = File.OpenRead(zipPath))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    var entries = new List<KeyValuePair<ZipArchiveEntry, string>>();
                    foreach (var e in zip.Entries)
                    {
                        var n = e.FullName.Replace('\\', '/');
                        if (n.EndsWith("/") || e.Length == 0 && e.Name.Length == 0) continue;
                        if (skip.Contains(n)) continue;
                        if (strip.Length > 0)
                        {
                            if (!n.StartsWith(strip, StringComparison.OrdinalIgnoreCase)) { _log.LogWarning("skipped (outside " + strip + "): " + n); continue; }
                            n = n.Substring(strip.Length);
                        }
                        var dest = Full(Path.Combine(destRoot, n.Replace('/', Path.DirectorySeparatorChar)));
                        if (!Inside(dest, destRoot)) throw new Exception("the zip wants to write outside BepInEx\\" + extractTo + ": " + e.FullName);
                        if (destRoot == _root)
                        {
                            var top = n.Split('/')[0].ToLowerInvariant();
                            if (top != "plugins" && top != "patchers" && top != "config") throw new Exception("the zip wants to write into BepInEx\\" + top + ": " + e.FullName);
                        }
                        entries.Add(new KeyValuePair<ZipArchiveEntry, string>(e, dest));
                    }
                    if (entries.Count == 0) throw new Exception("the zip contains nothing to install");

                    // the DLL being replaced may live somewhere else than where the zip puts it: move it away, so there aren't two copies
                    var oldDll = MiniJson.Str(op, "oldDll");
                    var guidDll = MiniJson.Str(op, "guidDll").Replace('\\', '/');
                    if (oldDll.Length > 0 && File.Exists(oldDll) && guidDll.Length > 0)
                    {
                        var newDll = Full(Path.Combine(destRoot, guidDll.Replace('/', Path.DirectorySeparatorChar)));
                        if (!string.Equals(Full(oldDll), newDll, StringComparison.OrdinalIgnoreCase) && Inside(oldDll, _root))
                        {
                            var b = Path.Combine(backup, Rel(oldDll));
                            Directory.CreateDirectory(Path.GetDirectoryName(b));
                            File.Move(oldDll, b);
                            moved.Add(new KeyValuePair<string, string>(oldDll, b));
                        }
                    }

                    foreach (var kv in entries)
                    {
                        var dest = kv.Value;
                        if (IsSelf(dest)) dest += ".new";   // can't overwrite this running patcher; the plugin swaps it later
                        if (File.Exists(dest))
                        {
                            var b = Path.Combine(backup, Rel(dest));
                            Directory.CreateDirectory(Path.GetDirectoryName(b));
                            File.Copy(dest, b, true);
                            backedUp.Add(new KeyValuePair<string, string>(dest, b));
                        }
                        else written.Add(dest);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        using (var src = kv.Key.Open())
                        using (var dst = File.Create(dest)) src.CopyTo(dst);
                        files++;
                    }
                }
            }
            catch
            {
                // roll back: restore replaced files, delete new ones, put the old DLL back
                foreach (var kv in backedUp) try { File.Copy(kv.Value, kv.Key, true); } catch (Exception x) { _log.LogError("rollback: " + x.Message); }
                foreach (var p in written) try { if (File.Exists(p)) File.Delete(p); } catch (Exception x) { _log.LogError("rollback: " + x.Message); }
                foreach (var kv in moved) try { if (!File.Exists(kv.Key)) File.Move(kv.Value, kv.Key); } catch (Exception x) { _log.LogError("rollback: " + x.Message); }
                throw;
            }
            try { File.Delete(zipPath); } catch { }
            return files + " file(s) installed" + (backedUp.Count + moved.Count > 0 ? ", previous files backed up to " + Rel(backup) : "");
        }

        // ---------------------------------------------------------------- remove / disable / enable
        private static string MoveAway(Dictionary<string, object> op, bool disable)
        {
            var guid = MiniJson.Str(op, "guid");
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var target = disable
                ? Path.Combine(Path.Combine(_data, "disabled"), Safe(guid))
                : Path.Combine(Path.Combine(_data, "removed"), Safe(MiniJson.Str(op, "name")) + "-" + Safe(MiniJson.Str(op, "version")) + "-" + stamp);
            var paths = MiniJson.StrList(op, "paths");
            if (MiniJson.Bool(op, "removeConfig", false)) paths.AddRange(MiniJson.StrList(op, "configPaths"));
            var moved = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (var p0 in paths)
                {
                    var p = Full(p0);
                    if (!Inside(p, _root)) throw new Exception("refusing to move a path outside BepInEx: " + p0);
                    if (IsSelf(p) || p.IndexOf(Path.DirectorySeparatorChar + "patchers" + Path.DirectorySeparatorChar + "Apocasetter", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new Exception("refusing to move Apocasetter's own installer");
                    if (!File.Exists(p) && !Directory.Exists(p)) continue;
                    var dest = Path.Combine(target, Rel(p));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (Directory.Exists(dest) || File.Exists(dest)) dest += "-" + stamp;
                    if (Directory.Exists(p)) Directory.Move(p, dest); else File.Move(p, dest);
                    moved.Add(new KeyValuePair<string, string>(p, dest));
                }
            }
            catch
            {
                foreach (var kv in moved) try { if (Directory.Exists(kv.Value)) Directory.Move(kv.Value, kv.Key); else File.Move(kv.Value, kv.Key); } catch (Exception x) { _log.LogError("rollback: " + x.Message); }
                throw;
            }
            if (disable)
            {
                File.WriteAllText(Path.Combine(target, "manifest.json"), MiniJson.Write(new Dictionary<string, object>
                {
                    { "guid", guid }, { "name", MiniJson.Str(op, "name") }, { "version", MiniJson.Str(op, "version") },
                    { "files", moved.Select(kv => (object)Rel(kv.Key)).ToList() }
                }));
            }
            return moved.Count + " item(s) moved to " + Rel(target);
        }

        private static string Enable(Dictionary<string, object> op)
        {
            var dir = Path.Combine(Path.Combine(_data, "disabled"), Safe(MiniJson.Str(op, "guid")));
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) throw new Exception("no disabled copy found in " + Rel(dir));
            var files = MiniJson.StrList(MiniJson.Obj(MiniJson.Parse(File.ReadAllText(manifestPath))), "files");
            int n = 0;
            foreach (var rel in files)
            {
                var src = Path.Combine(dir, rel);
                var dest = Full(Path.Combine(_root, rel));
                if (!Inside(dest, _root)) throw new Exception("bad path in manifest: " + rel);
                if (File.Exists(dest) || Directory.Exists(dest)) throw new Exception(rel + " already exists; remove it first");
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                if (Directory.Exists(src)) Directory.Move(src, dest); else if (File.Exists(src)) File.Move(src, dest); else continue;
                n++;
            }
            File.Delete(manifestPath);
            try { Directory.Delete(dir, true); } catch { }
            return n + " item(s) moved back";
        }

        // ---------------------------------------------------------------- helpers
        private static string Full(string p) { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        private static bool Inside(string p, string root)
        {
            var f = Full(p);
            return f.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        private static string Rel(string p) { var f = Full(p); return Inside(f, _root) ? f.Substring(_root.Length + 1) : f; }
        private static bool IsSelf(string p)
        {
            string self;
            try { self = Full(typeof(Patcher).Assembly.Location); } catch { return false; }
            return string.Equals(Full(p), self, StringComparison.OrdinalIgnoreCase);
        }
        private static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }
    }
}
