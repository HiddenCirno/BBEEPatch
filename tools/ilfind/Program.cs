// 在任意托管 DLL 里按【字符串常量】定位方法, 并把该方法的 IL 打出来。
//
// 用途: 第三方库(UniverseLib / UnityExplorer)是黑盒, 但它抛出来的日志字符串就在 #US 堆里。
// 拿那句日志当锚点, 直接看它前后到底调了什么 —— 比猜、比搜文档都快。
//
// 用法: ildump <dll> <字符串片段> [上下文条数]
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 2) { Console.WriteLine("用法: ildump <dll> <字符串片段> [上下文]"); return 1; }
Console.OutputEncoding = System.Text.Encoding.UTF8;
string path = args[0], needle = args[1];
int ctx = args.Length > 2 ? int.Parse(args[2]) : 3;

using var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = false });

foreach (var type in asm.MainModule.GetTypes())
foreach (var m in type.Methods)
{
    if (!m.HasBody) continue;
    var ins = m.Body.Instructions;
    int at = -1;
    for (int i = 0; i < ins.Count; i++)
        if (ins[i].OpCode == OpCodes.Ldstr && ins[i].Operand is string s &&
            s.Contains(needle, StringComparison.Ordinal)) { at = i; break; }
    if (at < 0) continue;

    Console.WriteLine($"\n===== {type.FullName}::{m.Name}  (ldstr 在第 {at} 条) =====");
    int from = Math.Max(0, at - ctx * 6), to = Math.Min(ins.Count - 1, at + ctx * 6);
    for (int i = from; i <= to; i++)
    {
        var op = ins[i];
        string mark = i == at ? "  <<< 锚点" : "";
        Console.WriteLine($"  IL_{op.Offset:X4}  {op.OpCode,-14} {Format(op.Operand)}{mark}");
    }
}

static string Format(object o) => o switch
{
    null => "",
    string s => "\"" + s.Replace("\n", "\\n") + "\"",
    Instruction t => $"IL_{t.Offset:X4}",
    Instruction[] ts => string.Join(", ", ts.Select(t => $"IL_{t.Offset:X4}")),
    MethodReference mr => $"{mr.DeclaringType?.FullName}::{mr.Name}",
    FieldReference fr => $"{fr.DeclaringType?.FullName}::{fr.Name}",
    TypeReference tr => tr.FullName,
    _ => o.ToString()
};
return 0;
