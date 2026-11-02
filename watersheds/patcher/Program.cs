using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// IL patcher for Watersheds 7.0.4 (perf fix only): PartiallyLitChunkFixPatch.OnChunkColumnLoaded skips its lighting check,
// and so topChunk.Unpack(), for chunk columns loaded from disk. Only columns generated in this session are checked.
//  1. Clone Watersheds.Patches.PartiallyLitFreshColumns (helper\FreshColumns.cs) into Watersheds.dll.
//  2. PartiallyLitChunkFixPatch.Initialize: call PartiallyLitFreshColumns.Register(api) before returning.
//  3. PartiallyLitChunkFixPatch.OnChunkColumnLoaded: first line "if (!PartiallyLitFreshColumns.ConsumeIsFresh(chunkCoordinate)) return;".
//  4. PartiallyLitChunkFixPatch.Dispose: call PartiallyLitFreshColumns.Clear() first.
internal static class Program
{
    private static int Main(string[] args)
    {
        string input = args[0], helper = args[1], output = args[2], vsDir = args[3];
        var resolver = new DefaultAssemblyResolver();
        foreach (string d in new[] { vsDir, Path.Combine(vsDir, "Lib"), Path.Combine(vsDir, "Mods"), Path.GetDirectoryName(Path.GetFullPath(input)) }) resolver.AddSearchDirectory(d);

        ModuleDefinition module = ModuleDefinition.ReadModule(input, new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
        ModuleDefinition helperModule = ModuleDefinition.ReadModule(helper, new ReaderParameters { AssemblyResolver = resolver, InMemory = true });

        AlignAssemblyRefs(helperModule, module);
        TypeDefinition fresh = CloneType(helperModule.GetType("Watersheds.Patches.PartiallyLitFreshColumns"), module);
        TypeDefinition target = module.GetType("Watersheds.Patches.PartiallyLitChunkFixPatch") ?? throw new InvalidOperationException("PartiallyLitChunkFixPatch not found");
        MethodReference register = fresh.Methods.Single(m => m.Name == "Register");
        MethodReference consume = fresh.Methods.Single(m => m.Name == "ConsumeIsFresh");
        MethodReference clear = fresh.Methods.Single(m => m.Name == "Clear");

        // 2. Initialize(ICoreServerAPI api): ... Register(api); ret
        MethodDefinition init = target.Methods.Single(m => m.Name == "Initialize");
        if (init.Parameters.Count != 1 || init.Parameters[0].ParameterType.Name != "ICoreServerAPI") Fail("unexpected Initialize signature");
        Instruction initRet = init.Body.Instructions.Last();
        if (initRet.OpCode != OpCodes.Ret || init.Body.Instructions.Count(i => i.OpCode == OpCodes.Ret) != 1) Fail("Initialize must have a single ret");
        if (!init.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "add_ChunkColumnLoaded")) Fail("Initialize does not subscribe ChunkColumnLoaded");
        ILProcessor ip = init.Body.GetILProcessor();
        // keep branch targets that point at the ret valid: turn the old ret into the first new instruction
        initRet.OpCode = OpCodes.Ldarg_0;
        initRet.Operand = null;
        ip.Append(ip.Create(OpCodes.Call, register));
        ip.Append(ip.Create(OpCodes.Ret));

        // 3. OnChunkColumnLoaded(Vec2i chunkCoordinate, IWorldChunk[] chunks)
        MethodDefinition loaded = target.Methods.Single(m => m.Name == "OnChunkColumnLoaded");
        if (loaded.Parameters.Count != 2 || loaded.Parameters[0].ParameterType.Name != "Vec2i") Fail("unexpected OnChunkColumnLoaded signature");
        MethodDefinition hasBroken = target.Methods.Single(m => m.Name == "HasBrokenLighting");
        if (!hasBroken.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "Unpack")) Fail("HasBrokenLighting no longer calls Unpack - patch not needed?");
        ILProcessor lp = loaded.Body.GetILProcessor();
        Instruction first = loaded.Body.Instructions[0];
        lp.InsertBefore(first, lp.Create(OpCodes.Ldarg_0));
        lp.InsertBefore(first, lp.Create(OpCodes.Call, consume));
        lp.InsertBefore(first, lp.Create(OpCodes.Brtrue, first));
        lp.InsertBefore(first, lp.Create(OpCodes.Ret));
        loaded.Body.MaxStackSize = Math.Max(loaded.Body.MaxStackSize, 2);

        // 4. Dispose(): Clear() first
        MethodDefinition dispose = target.Methods.Single(m => m.Name == "Dispose" && m.IsStatic);
        ILProcessor dp = dispose.Body.GetILProcessor();
        dp.InsertBefore(dispose.Body.Instructions[0], dp.Create(OpCodes.Call, clear));

        AssertNoDuplicateAssemblyRefs(module);
        module.Write(output);
        Console.WriteLine($"Cloned PartiallyLitFreshColumns ({fresh.Methods.Count} methods), patched Initialize/OnChunkColumnLoaded/Dispose. Wrote {output}");
        return 0;
    }

    private static void Fail(string msg) => throw new InvalidOperationException("PATCH ABORTED: " + msg);

    // ---- 1. clone helper type -------------------------------------------------------------------------------
    private static TypeDefinition CloneType(TypeDefinition src, ModuleDefinition dst)
    {
        if (src == null) Fail("helper type not found");
        if (dst.GetType(src.FullName) != null) Fail(src.FullName + " already exists in target");
        if (src.HasNestedTypes) Fail("helper must not have nested types (lambdas/closures)");

        var type = new TypeDefinition(src.Namespace, src.Name, src.Attributes, dst.ImportReference(src.BaseType));
        dst.Types.Add(type);

        var fieldMap = new Dictionary<FieldDefinition, FieldDefinition>();
        var methodMap = new Dictionary<MethodDefinition, MethodDefinition>();
        foreach (FieldDefinition f in src.Fields)
        {
            var nf = new FieldDefinition(f.Name, f.Attributes, dst.ImportReference(f.FieldType));
            type.Fields.Add(nf);
            fieldMap[f] = nf;
        }
        foreach (MethodDefinition m in src.Methods)
        {
            var nm = new MethodDefinition(m.Name, m.Attributes, dst.ImportReference(m.ReturnType)) { ImplAttributes = m.ImplAttributes };
            foreach (ParameterDefinition p in m.Parameters) nm.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, dst.ImportReference(p.ParameterType)));
            if (m.HasGenericParameters) Fail("generic helper methods not supported");
            type.Methods.Add(nm);
            methodMap[m] = nm;
        }

        object MapOperand(object operand, Dictionary<Instruction, Instruction> insMap, MethodDefinition nm)
        {
            switch (operand)
            {
                case null: return null;
                case Instruction i: return insMap[i];
                case Instruction[] arr: return arr.Select(x => insMap[x]).ToArray();
                case VariableDefinition v: return nm.Body.Variables[v.Index];
                case ParameterDefinition p: return nm.Parameters[p.Index];
                case FieldReference fr:
                    {
                        FieldDefinition fd = fr.Resolve();
                        if (fd != null && fd.DeclaringType == src) return fieldMap[fd];
                        return dst.ImportReference(fr);
                    }
                case MethodReference mr:
                    {
                        if (mr.DeclaringType.Resolve() == src) return methodMap[mr.Resolve()];
                        return dst.ImportReference(mr);
                    }
                case TypeReference tr:
                    if (tr.Resolve() == src) return type;
                    return dst.ImportReference(tr);
                case CallSite: Fail("calli not supported"); return null;
                default: return operand; // string, numbers
            }
        }

        foreach (MethodDefinition m in src.Methods)
        {
            if (!m.HasBody) continue;
            MethodDefinition nm = methodMap[m];
            nm.Body.InitLocals = m.Body.InitLocals;
            nm.Body.MaxStackSize = m.Body.MaxStackSize;
            foreach (VariableDefinition v in m.Body.Variables) nm.Body.Variables.Add(new VariableDefinition(dst.ImportReference(v.VariableType)));
            var insMap = new Dictionary<Instruction, Instruction>();
            foreach (Instruction i in m.Body.Instructions)
            {
                // placeholder operand; fixed up below once every instruction exists
                Instruction ni = Instruction.Create(OpCodes.Nop);
                ni.OpCode = i.OpCode;
                insMap[i] = ni;
                nm.Body.Instructions.Add(ni);
            }
            foreach (Instruction i in m.Body.Instructions) insMap[i].Operand = MapOperand(i.Operand, insMap, nm);
            foreach (ExceptionHandler h in m.Body.ExceptionHandlers)
            {
                nm.Body.ExceptionHandlers.Add(new ExceptionHandler(h.HandlerType)
                {
                    TryStart = h.TryStart == null ? null : insMap[h.TryStart],
                    TryEnd = h.TryEnd == null ? null : insMap[h.TryEnd],
                    HandlerStart = h.HandlerStart == null ? null : insMap[h.HandlerStart],
                    HandlerEnd = h.HandlerEnd == null ? null : insMap[h.HandlerEnd],
                    FilterStart = h.FilterStart == null ? null : insMap[h.FilterStart],
                    CatchType = h.CatchType == null ? null : dst.ImportReference(h.CatchType),
                });
            }
        }
        return type;
    }

    // The helper was compiled against VintagestoryAPI 1.22.7, the target against 1.22.6. Align the helper's assembly references
    // with the target's before cloning, so imported type refs reuse the target's existing references instead of adding duplicates.
    private static void AlignAssemblyRefs(ModuleDefinition helper, ModuleDefinition target)
    {
        foreach (AssemblyNameReference r in helper.AssemblyReferences)
        {
            AssemblyNameReference existing = target.AssemblyReferences.FirstOrDefault(a => a.Name == r.Name);
            if (existing != null && existing.Version != r.Version)
            {
                Console.WriteLine($"  helper ref {r.Name} {r.Version} -> {existing.Version} (the target's)");
                r.Version = existing.Version;
            }
        }
    }

    private static void AssertNoDuplicateAssemblyRefs(ModuleDefinition module)
    {
        var dups = module.AssemblyReferences.GroupBy(a => a.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dups.Count > 0) Fail("duplicate assembly references: " + string.Join(", ", dups));
    }

}
