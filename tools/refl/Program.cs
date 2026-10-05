using System;
using System.IO;
using System.Reflection;
using System.Linq;

class P
{
    static string INTEROP = @"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\BepInEx\interop";
    static string CORE = @"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\BepInEx\core";
    static void Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var n = new AssemblyName(e.Name).Name + ".dll";
            foreach (var d in new[] { INTEROP, CORE }) { var p = Path.Combine(d, n); if (File.Exists(p)) return Assembly.LoadFrom(p); }
            return null;
        };
        var asm = Assembly.LoadFrom(Path.Combine(INTEROP, "UnityEngine.CoreModule.dll"));
        foreach (var nm in new[] { "UnityEngine.Gradient", "UnityEngine.GradientColorKey" })
        {
            var t = asm.GetType(nm);
            Console.WriteLine("=== " + nm + " value=" + (t != null && t.IsValueType));
            if (t == null) continue;
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                Console.WriteLine("   prop " + p.PropertyType.Name + " " + p.Name + " get=" + p.CanRead + " set=" + p.CanWrite);
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                Console.WriteLine("   fld  " + f.FieldType.Name + " " + f.Name);
        }
        var m = asm.GetType("UnityEngine.GradientMode");
        Console.WriteLine("GradientMode? " + (m != null));
        var ps = Assembly.LoadFrom(Path.Combine(INTEROP, "UnityEngine.ParticleSystemModule.dll")).GetType("UnityEngine.ParticleSystemGradientMode");
        if (ps != null) Console.WriteLine("ParticleSystemGradientMode: " + string.Join(", ", Enum.GetNames(ps)));
    }
}
