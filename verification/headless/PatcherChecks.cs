extern alias installer;
using System;
using InstallerPatcher = installer::Apocasetter.Installer.Patcher;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using Apocasetter;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Preloader.Patching;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Drawing;

partial class Program
{
    const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static string sandbox, fixtureDll, marker;
    static void RunPatcherChecks()
    {
        sandbox = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox-" + DateTime.UtcNow.Ticks);
        Directory.CreateDirectory(sandbox);
        typeof(Paths).GetMethod("SetExecutablePath", All).Invoke(null, new object[] { Path.Combine(sandbox, "Game.exe"), null, null, null });
        Directory.CreateDirectory(Paths.PatcherPluginPath); Directory.CreateDirectory(Paths.ConfigPath);
        typeof(ConfigFile).GetProperty("CoreConfig", All).GetValue(null, null);
        Plugin.Log = BepInEx.Logging.Logger.CreateLogSource("PatcherChecks");
        Updates.InstallerPath = Path.Combine(Paths.PatcherPluginPath, "Apocasetter.Installer.dll");
        Updates.DataDir = Path.Combine(Paths.CachePath, "Apocasetter"); Directory.CreateDirectory(Updates.DataDir);
        fixtureDll = Path.Combine(Paths.PatcherPluginPath, "FixturePatcher.dll"); marker = Path.Combine(sandbox, "initializer-ran.txt");
        MakeFixture(fixtureDll, "1.2.3", true);
        string helper = Path.Combine(Paths.PatcherPluginPath, "Helper.dll"); MakeFixture(helper, "1.0.0", false);
        var metadataType = typeof(ModEntry).Assembly.GetType("Apocasetter.PatcherMetadata");
        var metadata = metadataType.GetMethod("Read", All).Invoke(null, new object[] { fixtureDll });
        Check(metadata != null && (string)metadataType.GetField("Guid", All).GetValue(metadata) == "verification.patcher", "Patcher metadata discovers a DLL without a runtime plugin");
        Check((string)metadataType.GetField("Version", All).GetValue(metadata) == "1.2.3", "Patcher version comes from declared metadata");
        Check(metadataType.GetMethod("Read", All).Invoke(null, new object[] { helper }) == null, "Helper DLLs are not listed as patchers");
        Check(!File.Exists(marker), "Metadata discovery does not run a patcher's static constructor");
        var loaded = Assembly.LoadFile(fixtureDll);
        var list = ScanPatchers();
        Check(list.Count == 1 && list[0].IsPatcher && !list[0].Other, "Patcher catalog lists one patcher and ignores helpers");
        Check(!File.Exists(marker) && list[0].Config == null, "Catalog does not initialize a merely loaded patcher to read its config");
        var fixtureType = loaded.GetType("Fixture.Patcher");
        var target = (Func<IEnumerable<string>>)Delegate.CreateDelegate(typeof(Func<IEnumerable<string>>), fixtureType.GetMethod("get_TargetDLLs"));
        target();
        InstallerPatcher.CompletedPatchers.Add(loaded.FullName + "|" + fixtureType.FullName);
        list = ScanPatchers();
        Check(list[0].Config != null && list[0].Entries.Count == 1 && list[0].Entries[0].Definition.Section == "Patches", "Already initialized patcher ConfigFile settings are exposed in the UI");
        Check(list[0].PluginPaths().Single() == fixtureDll, "Patcher file operations target its patchers DLL");
        Updates.Index["verification.patcher"] = new IndexMod { Guid = "verification.patcher", Name = "Fixture", Trust = "community" };
        Check(ScanPatchers()[0].Index.Trust == "community", "Patcher index/trust metadata uses its declared GUID");
        var parent = new ModEntry { Guid = "verification.patcher", Kind = ModKind.Loaded, Name = "Companion plugin" };
        var combined = new List<ModEntry> { parent };
        typeof(ModEntry).Assembly.GetType("Apocasetter.PatcherCatalog").GetMethod("AddTo", All).Invoke(null, new object[] { combined });
        Check(combined.Count == 1 && parent.CompanionPaths.Single() == fixtureDll, "A plugin and companion patcher share one management entry");
        var registered = new PatcherPlugin { TypeName = fixtureType.FullName, TargetDLLs = target, Initializer = () => {}, Finalizer = () => {}, Patcher = (ref AssemblyDefinition a) => {} };
        Register(registered);
        var disable = Op("disable"); disable["paths"] = new List<object> { fixtureDll };
        var result = Apply(disable);
        Check(!File.Exists(fixtureDll) && MiniJson.Bool(result, "patcherRestartRequired", false), "Disabling a patcher moves it out and requires a clean restart");
        Check(registered.Initializer == null && registered.Finalizer == null && registered.Patcher == null && !registered.TargetDLLs().Any(), "Transition startup suppresses old preloader callbacks");
        typeof(Updates).GetMethod("LoadResults", All).Invoke(null, null);
        Check(Updates.PatcherRestartRequired, "Current transition process receives the restart completion screen");
        result["patcherRestartProcess"] = -1;
        File.WriteAllText(Path.Combine(Updates.DataDir, "last-install.json"), MiniJson.Write(result));
        typeof(Updates).GetMethod("LoadResults", All).Invoke(null, null);
        Check(!Updates.PatcherRestartRequired, "A stale result cannot force another process into a restart loop");
        var enable = Op("enable"); result = Apply(enable);
        Check(File.Exists(fixtureDll) && MiniJson.Bool(result, "patcherRestartRequired", false), "Enabling a patcher restores it for the next clean preload scan");
        string fresh = Path.Combine(sandbox, "Fresh.dll"); MakeFixture(fresh, "2.0.0", true);
        string zip = Path.Combine(sandbox, "update.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntryFromFile(fresh, "patchers/FixturePatcher.dll");
        var update = Op("update"); update["zip"] = zip; update["extractTo"] = "BepInEx"; update["guidDll"] = "patchers/FixturePatcher.dll"; update["oldDll"] = fixtureDll;
        result = Apply(update);
        metadata = metadataType.GetMethod("Read", All).Invoke(null, new object[] { fixtureDll });
        Check((string)metadataType.GetField("Version", All).GetValue(metadata) == "2.0.0" && MiniJson.Bool(result, "patcherRestartRequired", false), "Updating an already loaded patcher atomically replaces its disk copy");
        string plugin = Path.Combine(Paths.PluginPath, "Ordinary.dll"); Directory.CreateDirectory(Paths.PluginPath); File.WriteAllText(plugin, "fixture");
        var ordinary = Op("remove"); ordinary["guid"] = "verification.ordinary"; ordinary["paths"] = new List<object> { plugin };
        result = Apply(ordinary);
        Check(!MiniJson.Bool(result, "patcherRestartRequired", true), "Ordinary plugin operations do not trigger the extra preloader restart");
        var remove = Op("remove"); remove["paths"] = new List<object> { fixtureDll }; result = Apply(remove);
        Check(!File.Exists(fixtureDll) && MiniJson.Bool(result, "patcherRestartRequired", false), "Removing a patcher preserves the clean-restart requirement");
        var colors = (UnityEngine.Color[])typeof(GameSkin).Assembly.GetType("Apocasetter.SteelCable").GetMethod("Pixels", All).Invoke(null, new object[] { 256, 24 });
        Check(colors.Length == 256 * 24 && colors.Any(c => c.a == 0) && colors.Any(c => c.a > 0.9), "Cable texture has transparent cut edges and opaque steel strands");
        Check(colors.All(c => !float.IsNaN(c.r) && c.a >= 0 && c.a <= 1), "Cable texture values are finite and valid");
        using (var bitmap = new Bitmap(256, 24))
        using (var preview = new Bitmap(1024, 96))
        {
            for (int y = 0; y < 24; y++) for (int x = 0; x < 256; x++)
            { var c = colors[y * 256 + x]; bitmap.SetPixel(x, 23 - y, Color.FromArgb((int)(c.a * 255), (int)(c.r * 255), (int)(c.g * 255), (int)(c.b * 255))); }
            using (var graphics = Graphics.FromImage(preview))
            { graphics.Clear(Color.FromArgb(20, 15, 13)); graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor; graphics.DrawImage(bitmap, 0, 0, 1024, 96); }
            preview.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cable-preview.png"));
        }
    }
    static List<ModEntry> ScanPatchers()
    {
        var mods = new List<ModEntry>();
        typeof(ModEntry).Assembly.GetType("Apocasetter.PatcherCatalog").GetMethod("AddTo", All).Invoke(null, new object[] { mods }); return mods;
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void Register(PatcherPlugin patcher) { AssemblyPatcher.PatcherPlugins.Add(patcher); }
    static Dictionary<string, object> Op(string kind) { return new Dictionary<string, object> { { "op", kind }, { "guid", "verification.patcher" }, { "name", "Fixture" }, { "version", "1.2.3" } }; }
    static Dictionary<string, object> Apply(Dictionary<string, object> op)
    {
        File.WriteAllText(Path.Combine(Updates.DataDir, "pending.json"), MiniJson.Write(new Dictionary<string, object> { { "ops", new List<object> { op } } }));
        typeof(InstallerPatcher).GetField("_log", All).SetValue(null, Plugin.Log);
        typeof(InstallerPatcher).GetMethod("Run", All).Invoke(null, null);
        var result = MiniJson.Obj(MiniJson.Parse(File.ReadAllText(Path.Combine(Updates.DataDir, "last-install.json"))));
        Check(MiniJson.Bool(MiniJson.Obj(MiniJson.Arr(result, "results")[0]), "ok", false), "Installer applies staged " + (string)op["op"] + " operation"); return result;
    }
    static void MakeFixture(string path, string version, bool patcher)
    {
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(patcher ? "FixturePatcher" : "Helper", new Version(version)), "Fixture", ModuleKind.Dll))
        {
            var module = assembly.MainModule;
            var type = new TypeDefinition("Fixture", "Patcher", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract | Mono.Cecil.TypeAttributes.Sealed, module.TypeSystem.Object); module.Types.Add(type);
            if (patcher)
            {
                foreach (var kv in new Dictionary<string, string> { { "GUID", "verification.patcher" }, { "NAME", "Fixture patcher" }, { "VERSION", version } })
                    type.Fields.Add(new FieldDefinition(kv.Key, Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static | Mono.Cecil.FieldAttributes.Literal | Mono.Cecil.FieldAttributes.HasDefault, module.TypeSystem.String) { Constant = kv.Value });
                var config = new FieldDefinition("Config", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, module.ImportReference(typeof(ConfigFile))); type.Fields.Add(config);
                var ctor = new MethodDefinition(".cctor", Mono.Cecil.MethodAttributes.Private | Mono.Cecil.MethodAttributes.Static | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName, module.TypeSystem.Void); type.Methods.Add(ctor);
                var il = ctor.Body.GetILProcessor(); il.Emit(OpCodes.Ldstr, marker); il.Emit(OpCodes.Ldstr, "initialized"); il.Emit(OpCodes.Call, module.ImportReference(typeof(File).GetMethod("WriteAllText", new[] { typeof(string), typeof(string) })));
                il.Emit(OpCodes.Ldstr, Path.Combine(Paths.ConfigPath, "verification.patcher.cfg")); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Newobj, module.ImportReference(typeof(ConfigFile).GetConstructor(new[] { typeof(string), typeof(bool), typeof(BepInPlugin) }))); il.Emit(OpCodes.Stsfld, config);
                var bind = typeof(ConfigFile).GetMethods().First(m => m.Name == "Bind" && m.IsGenericMethodDefinition && m.GetParameters().Length == 4 && m.GetParameters()[3].ParameterType == typeof(ConfigDescription)).MakeGenericMethod(typeof(bool));
                il.Emit(OpCodes.Ldsfld, config); il.Emit(OpCodes.Ldstr, "Patches"); il.Emit(OpCodes.Ldstr, "Enabled"); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Callvirt, module.ImportReference(bind)); il.Emit(OpCodes.Pop); il.Emit(OpCodes.Ret);
                var target = new MethodDefinition("get_TargetDLLs", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.ImportReference(typeof(IEnumerable<string>))); type.Methods.Add(target);
                type.Properties.Add(new PropertyDefinition("TargetDLLs", Mono.Cecil.PropertyAttributes.None, target.ReturnType) { GetMethod = target });
                il = target.Body.GetILProcessor(); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Newarr, module.TypeSystem.String); il.Emit(OpCodes.Ret);
                var patch = new MethodDefinition("Patch", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void); patch.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(AssemblyDefinition)))); type.Methods.Add(patch); patch.Body.GetILProcessor().Emit(OpCodes.Ret);
            }
            assembly.Write(path);
        }
    }
}
