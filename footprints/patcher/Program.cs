using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Footprints 1.2.14 perffix: lifespan config value is now in QUARTER game hours (1 = 0.25 game h).
// Patches Footprint.get_IsExpired and Footprint.get_RemainingLifeFraction: after the ternary
// (IsForPlayer ? Player : Animal)Lifespan is converted to double (conv.r8), multiply by 0.25.
// Safety: each getter must contain exactly one *FootprintLifespan call pair followed by conv.r8, else abort.
internal static class Program
{
    private static int Main(string[] a)
    {
        string input = a[0], output = a[1], vsDir = a[2];
        var res = new DefaultAssemblyResolver();
        foreach (var d in new[] { vsDir, Path.Combine(vsDir, "Lib"), Path.GetDirectoryName(Path.GetFullPath(input)) }) res.AddSearchDirectory(d);
        var module = ModuleDefinition.ReadModule(input, new ReaderParameters { AssemblyResolver = res, InMemory = true });
        var t = module.GetType("components.Footprints.Footprint") ?? throw new Exception("Footprint type not found");
        int patched = 0;
        foreach (var name in new[] { "get_IsExpired", "get_RemainingLifeFraction" })
        {
            var m = t.Methods.Single(x => x.Name == name);
            var il = m.Body.Instructions;
            int calls = il.Count(i => i.OpCode.Code == Code.Callvirt && i.Operand is MethodReference r && r.Name.EndsWith("FootprintLifespan"));
            if (calls != 2) throw new Exception(name + ": expected 2 lifespan getter calls, got " + calls);
            var last = il.Last(i => i.Operand is MethodReference r && r.Name.EndsWith("FootprintLifespan"));
            var conv = last.Next; // join point of the ternary; both arms branch/fall into this conv.r8
            if (conv == null || conv.OpCode.Code != Code.Conv_R8) throw new Exception(name + ": conv.r8 not after last lifespan getter; found " + conv);
            if (conv.Next == null || !conv.Next.OpCode.Name.StartsWith("stloc")) throw new Exception(name + ": stloc not after conv.r8");
            var p = m.Body.GetILProcessor();
            var ldc = p.Create(OpCodes.Ldc_R8, 0.25);
            var mul = p.Create(OpCodes.Mul);
            p.InsertAfter(conv, ldc);
            p.InsertAfter(ldc, mul);
            patched++;
            Console.WriteLine("patched " + name);
        }
        module.Write(output);
        Console.WriteLine("methods patched: " + patched);
        return 0;
    }
}
