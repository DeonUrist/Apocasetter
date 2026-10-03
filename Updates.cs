using System;
using System.Globalization;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BepInEx;
using UnityEngine;
using UnityEngine.Networking;

namespace Apocasetter
{
    // =====================================================================
    //  The Apocasetter index (github.com/DeonUrist/Apocasetter-Index): one
    //  index.json listing every known mod with its latest release and zip.
    //  Downloaded once per game start (or on "Check for updates"), cached.
    // =====================================================================
    public class IndexMod
    {
        public string Guid, Name, Author, Repo, Summary, Trust, PluginFolder, BlockedReason, Error, DeprecatedReason;
        public bool Blocked, Deprecated;
        public List<string> ReplacedBy = new List<string>(), Replaces = new List<string>();
        public List<string> Tags = new List<string>(), Requires = new List<string>(), Optional = new List<string>();
        public string Version, Tag, Published, Page, Notes;
        public IndexZip Zip;
    }

    public class IndexZip
    {
        public string Name, Url, Sha256, ExtractTo, StripPrefix, TopFolder, GuidDll;
        public long Size;
        public List<string> Skip = new List<string>();
    }

    public class DisabledMod { public string Guid, Name, Version, Dir; }

    public static class Updates
    {
        public const string IndexUrl = "https://raw.githubusercontent.com/DeonUrist/Apocasetter-Index/main/index.json";
        public const string IndexApiUrl = "https://api.github.com/repos/DeonUrist/Apocasetter-Index/contents/index.json";
        public const string IndexPage = "https://github.com/DeonUrist/Apocasetter-Index";
        public const double CacheHours = 6;

        public static string DataDir;
        public static readonly Dictionary<string, IndexMod> Index = new Dictionary<string, IndexMod>(StringComparer.OrdinalIgnoreCase);
        public static bool HaveIndex, Checking;
        public static string CheckError;
        public static DateTime IndexTimeUtc = DateTime.MinValue;   // when the index we hold was fetched
        public static readonly Dictionary<string, float> Downloading = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        public static readonly Dictionary<string, string> Errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static readonly List<Dictionary<string, object>> Ops = new List<Dictionary<string, object>>();
        public static readonly List<DisabledMod> Disabled = new List<DisabledMod>();
        public static List<Dictionary<string, object>> LastResults = new List<Dictionary<string, object>>();
        public static bool InstallerPresent;
        public static string InstallerPath;
        public static event Action Changed;

        private static string PendingPath { get { return Path.Combine(DataDir, "pending.json"); } }
        private static string CachePath { get { return Path.Combine(DataDir, "index.json"); } }

        public static void Init()
        {
            DataDir = Path.Combine(Paths.CachePath, "Apocasetter");
            try { Directory.CreateDirectory(DataDir); } catch (Exception e) { Plugin.Log.LogWarning("Cannot create " + DataDir + ": " + e.Message); }
            InstallerPath = Path.Combine(Paths.PatcherPluginPath, "Apocasetter.Installer.dll");
            SwapInstaller();
            InstallerPresent = File.Exists(InstallerPath);
            if (!InstallerPresent) Plugin.Log.LogWarning("Apocasetter.Installer.dll is missing from BepInEx\\patchers: updates and removals can't be applied");
            LoadPending();
            LoadDisabled();
            LoadResults();
            LoadCache();
        }

        // ---------------------------------------------------------------- index
        private static void LoadCache()
        {
            try
            {
                if (!File.Exists(CachePath)) return;
                Parse(File.ReadAllText(CachePath));
                IndexTimeUtc = File.GetLastWriteTimeUtc(CachePath);
                HaveIndex = true;
                Plugin.Log.LogInfo("Index cache: " + Index.Count + " mods, from " + IndexTimeUtc.ToLocalTime());
            }
            catch (Exception e) { Plugin.Log.LogWarning("Index cache unreadable: " + e.Message); }
        }

        public static bool CacheFresh { get { return HaveIndex && (DateTime.UtcNow - IndexTimeUtc).TotalHours < CacheHours; } }

        public static IEnumerator Check(bool force)
        {
            if (Checking) yield break;
            if (!force && CacheFresh) yield break;
            Checking = true; CheckError = null;
            // Sources, freshest first. The GitHub API serves main's index.json at once (60 requests/hour per IP without a login);
            // raw.githubusercontent.com goes through a CDN (up to 5 min old). A unique query string and no-cache headers keep any cache on
            // the player's side (Windows/ISP/antivirus proxies served a 2-hour-old index) from answering instead.
            string stamp = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
            var sources = new List<KeyValuePair<string, bool>>();
            if (force) sources.Add(new KeyValuePair<string, bool>(IndexApiUrl + "?t=" + stamp, true));
            sources.Add(new KeyValuePair<string, bool>(IndexUrl + "?t=" + stamp, false));
            bool done = false;
            foreach (var src in sources)
            {
                using (var req = UnityWebRequest.Get(src.Key))
                {
                    req.timeout = 20;
                    req.SetRequestHeader("Cache-Control", "no-cache");
                    req.SetRequestHeader("Pragma", "no-cache");
                    req.SetRequestHeader("User-Agent", "Apocasetter/" + Plugin.VERSION);
                    if (src.Value) req.SetRequestHeader("Accept", "application/vnd.github.raw");
                    yield return req.SendWebRequest();
                    string via = src.Value ? "GitHub API" : "raw.githubusercontent.com";
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        CheckError = req.error;
                        Plugin.Log.LogWarning("Update check via " + via + " failed: " + req.error + " (HTTP " + req.responseCode + ")");
                        continue;
                    }
                    try
                    {
                        var text = req.downloadHandler.text;
                        Parse(text);
                        File.WriteAllText(CachePath, text);
                        IndexTimeUtc = DateTime.UtcNow;
                        HaveIndex = true;
                        CheckError = null;
                        string generated = "";
                        try { generated = MiniJson.Str(MiniJson.Obj(MiniJson.Parse(text)), "generated"); } catch { }
                        Plugin.Log.LogInfo("Update check: index has " + Index.Count + " mods (built " + generated + ", via " + via + ")");
                        done = true;
                    }
                    catch (Exception e) { CheckError = "index unreadable: " + e.Message; Plugin.Log.LogWarning("Update check via " + via + ": " + e); }
                }
                if (done) break;
            }
            Checking = false;
            Fire();
        }

        private static void Parse(string json)
        {
            var root = MiniJson.Obj(MiniJson.Parse(json));
            var mods = MiniJson.Arr(root, "mods");
            if (mods == null) throw new FormatException("no 'mods' list");
            Index.Clear();
            foreach (var o in mods)
            {
                var d = MiniJson.Obj(o);
                if (d == null) continue;
                var m = new IndexMod
                {
                    Guid = MiniJson.Str(d, "guid"), Name = MiniJson.Str(d, "name"), Author = MiniJson.Str(d, "author"),
                    Repo = MiniJson.Str(d, "repo"), Summary = MiniJson.Str(d, "summary"), Trust = MiniJson.Str(d, "trust", "community"),
                    PluginFolder = MiniJson.Str(d, "pluginFolder", null), Blocked = MiniJson.Bool(d, "blocked", false),
                    BlockedReason = MiniJson.Str(d, "blockedReason"), Error = MiniJson.Str(d, "error", null),
                    Tags = MiniJson.StrList(d, "tags"), Requires = MiniJson.StrList(d, "requires"), Optional = MiniJson.StrList(d, "optional"),
                    Deprecated = MiniJson.Bool(d, "deprecated", false), DeprecatedReason = MiniJson.Str(d, "deprecatedReason"),
                    ReplacedBy = MiniJson.StrList(d, "replacedBy"), Replaces = MiniJson.StrList(d, "replaces"),
                };
                var latest = MiniJson.Obj(d.ContainsKey("latest") ? d["latest"] : null);
                if (latest != null)
                {
                    m.Version = MiniJson.Str(latest, "version"); m.Tag = MiniJson.Str(latest, "tag"); m.Published = MiniJson.Str(latest, "published");
                    m.Page = MiniJson.Str(latest, "page"); m.Notes = MiniJson.Str(latest, "notes");
                    var z = MiniJson.Obj(latest.ContainsKey("zip") ? latest["zip"] : null);
                    if (z != null)
                        m.Zip = new IndexZip
                        {
                            Name = MiniJson.Str(z, "name"), Url = MiniJson.Str(z, "url"), Sha256 = MiniJson.Str(z, "sha256"),
                            ExtractTo = MiniJson.Str(z, "extractTo", "plugins"), StripPrefix = MiniJson.Str(z, "stripPrefix"),
                            TopFolder = MiniJson.Str(z, "topFolder", null), GuidDll = MiniJson.Str(z, "guidDll"),
                            Size = (long)MiniJson.Num(z, "size", 0), Skip = MiniJson.StrList(z, "skip")
                        };
                }
                if (!string.IsNullOrEmpty(m.Guid)) Index[m.Guid] = m;
            }
        }

        public static IndexMod Find(string guid)
        {
            IndexMod m;
            return guid != null && Index.TryGetValue(guid, out m) ? m : null;
        }

        /// &gt;0 when a is newer than b
        public static int CompareVersions(string a, string b)
        {
            Version va, vb;
            if (TryVersion(a, out va) && TryVersion(b, out vb)) return va.CompareTo(vb);
            return string.Compare(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryVersion(string s, out Version v)
        {
            v = null;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.TrimStart('v', 'V');
            int cut = 0;
            while (cut < s.Length && (char.IsDigit(s[cut]) || s[cut] == '.')) cut++;
            s = s.Substring(0, cut).Trim('.');
            if (s.Length == 0) return false;
            if (s.IndexOf('.') < 0) s += ".0";
            try { v = new Version(s); return true; } catch { return false; }
        }

        // ---------------------------------------------------------------- download + stage
        private static string Host(string url) { try { return new Uri(url).Host; } catch { return url ?? ""; } }

        public static IEnumerator Download(Dictionary<string, object> op, IndexMod im)
        {
            var guid = MiniJson.Str(op, "guid");
            if (im == null || im.Zip == null || Downloading.ContainsKey(guid)) yield break;
            Errors.Remove(guid);
            Downloading[guid] = 0f;
            Fire();
            var staged = Path.Combine(DataDir, "staged");
            Directory.CreateDirectory(staged);
            var file = Path.Combine(staged, Safe(im.Name) + "-" + Safe(im.Version) + ".zip");
            string error = null;
            // GitHub's asset host answers 502/503/504 now and then: try three times, 2 s and 5 s apart
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var req = new UnityWebRequest(im.Zip.Url, UnityWebRequest.kHttpVerbGET);
                req.downloadHandler = new DownloadHandlerFile(file) { removeFileOnAbort = true };
                req.timeout = 600;
                req.SetRequestHeader("User-Agent", "Apocasetter/" + Plugin.VERSION);
                var send = req.SendWebRequest();
                while (!send.isDone) { Downloading[guid] = req.downloadProgress; yield return null; }
                error = req.result == UnityWebRequest.Result.Success ? null : req.error;
                long code = req.responseCode;
                bool retry = error != null && (req.result == UnityWebRequest.Result.ConnectionError || code == 429 || code >= 500);
                if (error != null)
                    Plugin.Log.LogWarning("Download of " + im.Name + " (attempt " + attempt + "/3): " + error + " · HTTP " + code + " · server '" + (req.GetResponseHeader("Server") ?? "")
                        + "' · from " + Host(req.url) + (retry && attempt < 3 ? " · retrying" : ""));
                req.Dispose();
                if (!retry || attempt == 3) break;
                float until = Time.realtimeSinceStartup + (attempt == 1 ? 2f : 5f);
                while (Time.realtimeSinceStartup < until) yield return null;
            }
            if (error == null)
            {
                try
                {
                    var sha = Sha256(file);
                    if (!string.IsNullOrEmpty(im.Zip.Sha256) && !string.Equals(sha, im.Zip.Sha256, StringComparison.OrdinalIgnoreCase))
                        error = "the download's checksum doesn't match the index";
                    else
                    {
                        op["zip"] = file; op["sha256"] = sha; op["version"] = im.Version;
                        op["extractTo"] = im.Zip.ExtractTo; op["stripPrefix"] = im.Zip.StripPrefix;
                        op["skip"] = im.Zip.Skip.Cast<object>().ToList(); op["guidDll"] = im.Zip.GuidDll;
                        Stage(op);
                    }
                }
                catch (Exception e) { error = e.Message; }
            }
            if (error != null)
            {
                Errors[guid] = error;
                Plugin.Log.LogWarning("Download of " + im.Name + " failed: " + error);
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }
            Downloading.Remove(guid);
            Fire();
        }

        public static void Stage(Dictionary<string, object> op)
        {
            var guid = MiniJson.Str(op, "guid");
            Unstage(guid, false);
            op["id"] = guid + "-" + DateTime.UtcNow.Ticks;
            Ops.Add(op);
            SavePending();
            Plugin.Log.LogInfo("Staged " + MiniJson.Str(op, "op") + " of " + MiniJson.Str(op, "name"));
            Fire();
        }

        public static void Unstage(string guid, bool deleteZip = true)
        {
            var old = Ops.Where(o => string.Equals(MiniJson.Str(o, "guid"), guid, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var o in old)
            {
                Ops.Remove(o);
                var z = MiniJson.Str(o, "zip");
                if (deleteZip && z.Length > 0) try { File.Delete(z); } catch { }
            }
            if (old.Count > 0) { SavePending(); Fire(); }
        }

        public static void UnstageAll()
        {
            foreach (var g in Ops.Select(o => MiniJson.Str(o, "guid")).Distinct().ToList()) Unstage(g);
        }

        public static Dictionary<string, object> OpFor(string guid)
        {
            return Ops.LastOrDefault(o => string.Equals(MiniJson.Str(o, "guid"), guid, StringComparison.OrdinalIgnoreCase));
        }

        private static void LoadPending()
        {
            Ops.Clear();
            try
            {
                if (!File.Exists(PendingPath)) return;
                var ops = MiniJson.Arr(MiniJson.Obj(MiniJson.Parse(File.ReadAllText(PendingPath))), "ops");
                if (ops != null) foreach (var o in ops) { var d = MiniJson.Obj(o); if (d != null) Ops.Add(d); }
                Plugin.Log.LogInfo(Ops.Count + " staged operation(s) carried over (the installer applies them at the next start)");
            }
            catch (Exception e) { Plugin.Log.LogWarning("pending.json unreadable: " + e.Message); }
        }

        private static void SavePending()
        {
            try
            {
                if (Ops.Count == 0) { if (File.Exists(PendingPath)) File.Delete(PendingPath); return; }
                File.WriteAllText(PendingPath, MiniJson.Write(new Dictionary<string, object> { { "ops", Ops.Cast<object>().ToList() } }));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Cannot write pending.json: " + e.Message); }
        }

        private static void LoadDisabled()
        {
            Disabled.Clear();
            var dir = Path.Combine(DataDir, "disabled");
            if (!Directory.Exists(dir)) return;
            foreach (var d in Directory.GetDirectories(dir))
            {
                try
                {
                    var mf = Path.Combine(d, "manifest.json");
                    if (!File.Exists(mf)) continue;
                    var m = MiniJson.Obj(MiniJson.Parse(File.ReadAllText(mf)));
                    Disabled.Add(new DisabledMod { Guid = MiniJson.Str(m, "guid"), Name = MiniJson.Str(m, "name"), Version = MiniJson.Str(m, "version"), Dir = d });
                }
                catch (Exception e) { Plugin.Log.LogWarning("Disabled mod manifest unreadable in " + d + ": " + e.Message); }
            }
        }

        private static void LoadResults()
        {
            var p = Path.Combine(DataDir, "last-install.json");
            try
            {
                if (!File.Exists(p)) return;
                var r = MiniJson.Arr(MiniJson.Obj(MiniJson.Parse(File.ReadAllText(p))), "results");
                if (r != null) foreach (var o in r) { var d = MiniJson.Obj(o); if (d != null) LastResults.Add(d); }
                var shown = Path.Combine(DataDir, "last-install.shown.json");
                if (File.Exists(shown)) File.Delete(shown);
                File.Move(p, shown);   // report once
            }
            catch (Exception e) { Plugin.Log.LogWarning("last-install.json unreadable: " + e.Message); }
        }

        // a new installer arrives as Apocasetter.Installer.dll.new (it can't overwrite itself while it runs)
        private static void SwapInstaller()
        {
            var fresh = InstallerPath + ".new";
            if (!File.Exists(fresh)) return;
            try
            {
                var old = InstallerPath + ".old";
                if (File.Exists(old)) File.Delete(old);
                if (File.Exists(InstallerPath)) File.Move(InstallerPath, old);
                File.Move(fresh, InstallerPath);
                Plugin.Log.LogInfo("Installer updated");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Could not swap in the new installer (" + e.Message + "); copy " + fresh + " over the old one by hand"); }
        }

        private static void Fire() { var h = Changed; if (h != null) try { h(); } catch { } }

        public static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        public static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }
    }
}
