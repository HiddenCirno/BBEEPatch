// 把第三方插件 DLL 里的 【老程序集引用名】 改成 BepInEx 6 新名字。
//
// 背景: BepInEx 6 把链加载器程序集从 `BepInEx.IL2CPP` 改名为 `BepInEx.Unity.IL2CPP`。
// 老插件(如 UnityExplorer 4.9.0)继承 `BepInEx.IL2CPP.BasePlugin`,
// 而 `BaseChainloader.ToPluginInfo` 里是
//     try { if (!type.IsSubtypeOf(typeof(TPlugin))) return null; }
//     catch (AssemblyResolutionException) { return null; }     ← 静默丢弃, 一行日志都不打
// ⇒ 插件被无声无息地跳过(缓存里记成 0 个插件类型)。
//
// 用法:
//   bie_refix dump <插件.dll>                 只看引用清单
//   bie_refix fix  <插件.dll> <输出.dll>      改写并另存(原文件不动)
using Mono.Cecil;

const string OLD = "BepInEx.IL2CPP";          // 老名
const string NEW = "BepInEx.Unity.IL2CPP";    // be.788 的实名

if (args.Length < 2) { Console.WriteLine("用法: dump <dll> | fix <in> <out>"); return 1; }
string mode = args[0], src = args[1];
Console.OutputEncoding = System.Text.Encoding.UTF8;

var rp = new ReaderParameters { ReadWrite = mode == "fix" };
using var asm = AssemblyDefinition.ReadAssembly(src, rp);

Console.WriteLine($"程序集: {asm.Name.Name} {asm.Name.Version}");

// 1) 所有引用里名字像 BepInEx 的
Console.WriteLine("--- AssemblyReferences ---");
foreach (var r in asm.MainModule.AssemblyReferences)
    if (r.Name.Contains("BepInEx", StringComparison.OrdinalIgnoreCase) ||
        r.Name.Contains("IL2CPP", StringComparison.OrdinalIgnoreCase))
        Console.WriteLine($"   {(r.Name == OLD ? "★" : " ")} {r.Name} {r.Version}");

// 2) 指向老程序集的所有类型引用（改名后要跟着改 namespace）
Console.WriteLine($"--- 指向 {OLD} 的 TypeReferences ---");
int hits = 0;
foreach (var t in asm.MainModule.GetTypeReferences())
{
    var scope = t.Scope?.Name;
    if (scope == OLD) { Console.WriteLine($"   {t.FullName}"); hits++; }
}
Console.WriteLine($"   共 {hits} 个");

// 3) 插件类(带 BepInPlugin 的)的基类 —— 这就是解析失败的那一环
Console.WriteLine("--- 带 BepInPlugin 的类型及其基类 ---");
foreach (var t in asm.MainModule.Types)
{
    bool hasAttr = t.CustomAttributes.Any(a => a.AttributeType.Name == "BepInPlugin");
    if (hasAttr)
    {
        Console.WriteLine($"   {t.FullName} : {t.BaseType?.FullName} [{t.BaseType?.Scope?.Name}]");
        // 属性也要看: [BepInProcess] 过滤器一挂, 名字对不上就会被跳过(那条有日志, 但先排除掉)
        foreach (var a in t.CustomAttributes)
            Console.WriteLine($"        [attr] {a.AttributeType.Name}({string.Join(", ", a.ConstructorArguments.Select(x => x.Value))})");
    }
}

if (mode == "verify")
{
    // 改完之后必须验: 插件类 overrode 的那些方法, 在新基类里还在不在。
    // (类型改名是机械的, 但万一新库删了某个虚方法, 运行时就是 TypeLoadException)
    string core = args[2];
    using var newAsm = AssemblyDefinition.ReadAssembly(core);
    var baseTd = newAsm.MainModule.GetType(NEW + ".BasePlugin")
                 ?? newAsm.MainModule.GetType("BepInEx.Unity.IL2CPP.BasePlugin");
    if (baseTd == null) { Console.WriteLine("!! 新库找不到 BepInEx.Unity.IL2CPP.BasePlugin"); return 2; }
    var avail = new HashSet<string>(baseTd.Methods.Select(m => m.Name));
    foreach (var f in baseTd.Fields) avail.Add(f.Name);
    foreach (var p in baseTd.Properties) avail.Add(p.Name);
    Console.WriteLine($"新基类成员 {avail.Count} 个");

    bool bad = false;
    foreach (var t in asm.MainModule.Types)
        if (t.CustomAttributes.Any(a => a.AttributeType.Name == "BepInPlugin"))
        {
            var baseName = t.BaseType?.FullName;
            Console.WriteLine($"插件类 {t.FullName} 基类={baseName}");
            if (baseName != NEW + ".BasePlugin") { Console.WriteLine("   !! 基类名没改对"); bad = true; }
            // 只认【真覆盖】(IsVirtual 且复用基类槽位)。插件类自己新声明的虚方法(IsNewSlot)
            // 不是覆盖, 拿它去比对基类是假阳性。
            foreach (var m in t.Methods.Where(m => !m.IsConstructor))
            {
                bool realOverride = m.IsVirtual && !m.IsNewSlot;
                if (realOverride && !avail.Contains(m.Name))
                { Console.WriteLine($"   !! 新基类没有成员 [{m.Name}] (真覆盖) —— 运行时会炸"); bad = true; }
                else if (!realOverride && m.IsVirtual)
                    Console.WriteLine($"   ·  自有虚方法 [{m.Name}] (不是覆盖, 忽略)");
            }
        }
    Console.WriteLine(bad ? "结论: 有问题" : "结论: 全部对得上 ✔");
    return bad ? 3 : 0;
}

if (mode != "fix") return 0;

// ---------------- 改写 ----------------
int renamedRefs = 0, renamedTypes = 0;
foreach (var r in asm.MainModule.AssemblyReferences)
    if (r.Name == OLD) { r.Name = NEW; renamedRefs++; }

foreach (var t in asm.MainModule.GetTypeReferences())
{
    if (t.Scope?.Name != NEW) continue;
    // 类型本身的名字空间也要跟着搬(老库里叫 BepInEx.IL2CPP.X, 新库里叫 BepInEx.Unity.IL2CPP.X)
    if (t.Namespace == OLD || t.Namespace.StartsWith(OLD + ".", StringComparison.Ordinal))
    {
        t.Namespace = NEW + t.Namespace.Substring(OLD.Length);
        renamedTypes++;
    }
}

// ★ 版本也要对齐。.NET 的默认绑定规则是【请求版本 ≤ 实际版本】才放行;
//   UnityExplorer 请求 BepInEx.Unity.IL2CPP 6.0.0.538、BepInEx.Core 6.0.0.423,
//   而 be.788 里这两个程序集的实际版本都是 6.0.0.0 ⇒ 请求高于实际 ⇒ 加载失败。
//   做法: 凡是能在 core 目录里找到同名 dll 的引用, 一律按实际版本改写。
if (args.Length > 3 && Directory.Exists(args[3]))
{
    int fixVer = 0;
    foreach (var r in asm.MainModule.AssemblyReferences)
    {
        string cand = Path.Combine(args[3], r.Name + ".dll");
        if (!File.Exists(cand)) continue;
        var real = System.Reflection.AssemblyName.GetAssemblyName(cand).Version;
        if (real != null && r.Version != real)
        { Console.WriteLine($"   版本对齐 {r.Name}: {r.Version} -> {real}"); r.Version = real; fixVer++; }
    }
    Console.WriteLine($"版本改写 {fixVer} 条 (core={args[3]})");
}

Console.WriteLine($"改名: 程序集引用 {renamedRefs} 条, 类型引用 {renamedTypes} 条");
asm.Write(args[2]);
Console.WriteLine($"已写出: {args[2]}");
return 0;
