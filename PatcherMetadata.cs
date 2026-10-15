using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace Apocasetter
{
    // Metadata only: discovery must not load a DLL or run its static constructors.
    internal sealed class PatcherMetadata
    {
        internal string Guid, Name, Version;
        internal readonly List<string> Types = new List<string>();

        internal static PatcherMetadata Read(string path)
        {
            using (var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true }))
            {
                var types = assembly.MainModule.Types.Where(IsPatcher).ToList();
                if (types.Count == 0) return null;
                var info = new PatcherMetadata { Name = assembly.Name.Name, Version = assembly.Name.Version.ToString() };
                var version = assembly.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "System.Reflection.AssemblyInformationalVersionAttribute")
                    ?? assembly.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "System.Reflection.AssemblyFileVersionAttribute");
                if (version != null && version.ConstructorArguments.Count > 0) info.Version = Convert.ToString(version.ConstructorArguments[0].Value);
                foreach (var type in types)
                {
                    info.Types.Add(type.FullName);
                    var plugin = type.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "BepInEx.BepInPlugin" && a.ConstructorArguments.Count == 3);
                    if (plugin != null)
                    {
                        info.Guid = Convert.ToString(plugin.ConstructorArguments[0].Value);
                        info.Name = Convert.ToString(plugin.ConstructorArguments[1].Value);
                        info.Version = Convert.ToString(plugin.ConstructorArguments[2].Value);
                        break;
                    }
                    foreach (var field in type.Fields.Where(f => f.IsStatic && f.HasConstant && f.Constant is string))
                    {
                        if (field.Name == "GUID") info.Guid = (string)field.Constant;
                        else if (field.Name == "NAME") info.Name = (string)field.Constant;
                        else if (field.Name == "VERSION") info.Version = (string)field.Constant;
                    }
                }
                // A multi-type DLL is still one file-management entry.
                info.Types.Clear(); info.Types.AddRange(types.Select(t => t.FullName));
                return info;
            }
        }

        private static bool IsPatcher(TypeDefinition type)
        {
            if (type.IsInterface || (type.IsAbstract && !type.IsSealed)) return false;
            return type.Methods.Any(m => string.Equals(m.Name, "get_TargetDLLs", StringComparison.OrdinalIgnoreCase) && m.IsPublic && m.IsStatic
                && m.ReturnType.FullName == "System.Collections.Generic.IEnumerable`1<System.String>")
                && type.Methods.Any(m => m.Name == "Patch" && m.IsPublic && m.IsStatic && m.ReturnType.FullName == "System.Void" && m.Parameters.Count == 1
                    && (m.Parameters[0].ParameterType.FullName == "Mono.Cecil.AssemblyDefinition" || m.Parameters[0].ParameterType.FullName == "Mono.Cecil.AssemblyDefinition&"));
        }
    }
}
