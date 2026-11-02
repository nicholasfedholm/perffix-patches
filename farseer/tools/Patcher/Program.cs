using Mono.Cecil;
using Mono.Cecil.Cil;

// usage: Patcher <stockFarseer.dll> <FarseerPerffix.dll> <out Farseer.dll> [--phase2]
// Phase 1: TerrainSamplerAdapter.StartGeneratingRegion body -> FarseerPerffix.SamplerScheduler.Start(this, regionIdx)
// Phase 2: prepend SamplerScheduler.Reprioritize/CancelNotIn(this.terrainSamplerAdapter, arg) to FarRegionGen.SortTasksByPriority/CancelTasksNotIn
string stock = args[0], helper = args[1], outPath = args[2];
bool phase2 = args.Contains("--phase2");

var resolver = new DefaultAssemblyResolver();
foreach (var d in new[] { Path.GetDirectoryName(Path.GetFullPath(stock)), Path.GetDirectoryName(Path.GetFullPath(helper)),
                          Path.Combine(Environment.GetEnvironmentVariable("APPDATA"), "Vintagestory"), Path.Combine(Environment.GetEnvironmentVariable("APPDATA"), "Vintagestory", "Lib") })
    resolver.AddSearchDirectory(d);
var rp = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate };
using var asm = AssemblyDefinition.ReadAssembly(stock, rp);
using var helperAsm = AssemblyDefinition.ReadAssembly(helper, rp);
var mod = asm.MainModule;
var sched = helperAsm.MainModule.GetType("FarseerPerffix.SamplerScheduler") ?? throw new Exception("SamplerScheduler not found");
MethodReference Imp(string name) => mod.ImportReference(sched.Methods.Single(m => m.Name == name));

var adapter = mod.GetType("Farseer.Server.TerrainSamplerAdapter") ?? throw new Exception("adapter type missing");
var start = adapter.Methods.Single(m => m.Name == "StartGeneratingRegion" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Int64");
if (start.Body.Instructions.Count != 77) throw new Exception($"StartGeneratingRegion IL count {start.Body.Instructions.Count} != 77 (not stock 1.4.0?)");
int before = start.Body.Instructions.Count;
start.Body.Instructions.Clear();
start.Body.Variables.Clear();
start.Body.ExceptionHandlers.Clear();
var il = start.Body.GetILProcessor();
il.Emit(OpCodes.Ldarg_0);
il.Emit(OpCodes.Ldarg_1);
il.Emit(OpCodes.Call, Imp("Start"));
il.Emit(OpCodes.Ret);
Console.WriteLine($"PATCH TerrainSamplerAdapter::StartGeneratingRegion il {before} -> {start.Body.Instructions.Count} (calls SamplerScheduler.Start)");

if (phase2)
{
    var gen = mod.GetType("Farseer.Server.FarRegionGen") ?? throw new Exception("FarRegionGen missing");
    var fAdapter = gen.Fields.Single(f => f.Name == "terrainSamplerAdapter");
    foreach (var (target, hook, il0) in new[] { ("SortTasksByPriority", "Reprioritize", 17), ("CancelTasksNotIn", "CancelNotIn", 35) })
    {
        var m = gen.Methods.Single(x => x.Name == target && x.Parameters.Count == 1);
        if (m.Body.Instructions.Count != il0) throw new Exception($"{target} IL count {m.Body.Instructions.Count} != {il0}");
        var p = m.Body.GetILProcessor();
        var first = m.Body.Instructions[0];
        p.InsertBefore(first, p.Create(OpCodes.Ldarg_0));
        p.InsertBefore(first, p.Create(OpCodes.Ldfld, fAdapter));
        p.InsertBefore(first, p.Create(OpCodes.Ldarg_1));
        p.InsertBefore(first, p.Create(OpCodes.Call, Imp(hook)));
        Console.WriteLine($"PATCH FarRegionGen::{target} il {il0} -> {m.Body.Instructions.Count} (prepends SamplerScheduler.{hook})");
    }
}
asm.Write(outPath);

// verify by reloading
using var check = AssemblyDefinition.ReadAssembly(outPath, new ReaderParameters { AssemblyResolver = resolver });
var refs = string.Join(",", check.MainModule.AssemblyReferences.Select(r => r.Name).Where(n => n.StartsWith("Farseer")));
var s2 = check.MainModule.GetType("Farseer.Server.TerrainSamplerAdapter").Methods.Single(m => m.Name == "StartGeneratingRegion");
Console.WriteLine($"VERIFY asm={check.Name.Name} v{check.Name.Version} refs+={refs} start il={s2.Body.Instructions.Count} -> {string.Join(" | ", s2.Body.Instructions.Select(i => i.ToString()))}");
