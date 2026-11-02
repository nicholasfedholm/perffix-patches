using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// IL patcher for OverhaullibLegacyCompat 1.2.15 (perf fixes only):
//  A. AnimationPatches.Patch: drop the Harmony registration of the empty AnimationManager.OnClientFrame postfix.
//  B. AnimationPatches.AnimatorPlayerMap.Get: per-thread "last animator" cache, so the ConditionalWeakTable lookup
//     runs once per animator (per CalculateMatrices pass) instead of once per bone. Add/Clear/.ctor bump a version.
//  C. DirectionCursorRenderer.OnRenderFrame: draw scale x4, to compensate for 1024 -> 256 cursor textures.
internal static class Program
{
    private static int Main(string[] args)
    {
        string input = args[0], output = args[1], vsDir = args[2];
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(vsDir);
        resolver.AddSearchDirectory(Path.Combine(vsDir, "Lib"));
        resolver.AddSearchDirectory(Path.Combine(vsDir, "Mods"));
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input)));

        var readerParams = new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = true, InMemory = true };
        ModuleDefinition module = ModuleDefinition.ReadModule(input, readerParams);

        PatchRemoveEmptyPostfix(module);
        PatchAnimatorPlayerMap(module);
        PatchCursorScale(module);

        module.Write(output, new WriterParameters { WriteSymbols = true });
        Console.WriteLine("Wrote " + output);
        return 0;
    }

    private static void Fail(string msg) => throw new InvalidOperationException("PATCH ABORTED: " + msg);

    private static AssemblyNameReference AsmRef(ModuleDefinition module, string name) =>
        module.AssemblyReferences.FirstOrDefault(a => a.Name == name) ?? throw new InvalidOperationException("missing assembly ref " + name);

    private static void EnsureNoBranchInto(MethodBody body, ICollection<Instruction> removed)
    {
        foreach (Instruction ins in body.Instructions)
        {
            if (removed.Contains(ins)) continue;
            if (ins.Operand is Instruction t && removed.Contains(t)) Fail("branch into removed range");
            if (ins.Operand is Instruction[] ts && ts.Any(removed.Contains)) Fail("switch into removed range");
        }
        foreach (ExceptionHandler h in body.ExceptionHandlers)
        {
            if (new[] { h.TryStart, h.TryEnd, h.HandlerStart, h.HandlerEnd, h.FilterStart }.Any(x => x != null && removed.Contains(x))) Fail("exception handler touches removed range");
        }
    }

    // A
    private static void PatchRemoveEmptyPostfix(ModuleDefinition module)
    {
        TypeDefinition type = module.GetType("CombatOverhaul.Integration.AnimationPatches") ?? throw new InvalidOperationException("AnimationPatches not found");

        MethodDefinition empty = type.Methods.Single(m => m.Name == "AnimationManagerOnClientFrame");
        if (empty.Body.Instructions.Count != 1 || empty.Body.Instructions[0].OpCode != OpCodes.Ret) Fail("AnimationManagerOnClientFrame is not empty, refusing to remove it");

        MethodDefinition patch = type.Methods.Single(m => m.Name == "Patch" && m.IsStatic);
        var il = patch.Body.Instructions;
        int ldstr = il.ToList().FindIndex(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "AnimationManagerOnClientFrame");
        if (ldstr < 0) Fail("registration of AnimationManagerOnClientFrame not found");

        // Statement start = instruction after the previous 'pop' (end of the preceding harmony.Patch(...) statement)
        int start = ldstr;
        while (start > 0 && il[start - 1].OpCode != OpCodes.Pop) start--;
        // Statement end = the 'pop' after the Harmony.Patch call
        int end = ldstr;
        while (end < il.Count && !(il[end].OpCode == OpCodes.Callvirt && ((MethodReference)il[end].Operand).Name == "Patch" && ((MethodReference)il[end].Operand).DeclaringType.Name == "Harmony")) end++;
        end++;
        if (end >= il.Count || il[end].OpCode != OpCodes.Pop) Fail("unexpected IL shape after Harmony.Patch");
        if (il[start].OpCode != OpCodes.Ldtoken || ((TypeReference)il[start].Operand).Name != "AnimationManager") Fail("unexpected IL shape at statement start: " + il[start]);
        if (il[end + 1].OpCode != OpCodes.Ret || end + 2 != il.Count) Fail("registration is not the last statement");

        // The Harmony instance for this call is already on the stack (left there by the previous 'dup'), so keep the stack
        // balanced: the first instruction becomes 'pop' and the rest of the statement is removed.
        var removed = new HashSet<Instruction>(il.Skip(start + 1).Take(end - start));
        EnsureNoBranchInto(patch.Body, removed);
        il[start].OpCode = OpCodes.Pop;
        il[start].Operand = null;
        foreach (Instruction ins in removed) il.Remove(ins);
        Console.WriteLine($"A: removed {removed.Count + 1} instructions (OnClientFrame postfix registration) from AnimationPatches.Patch");
    }

    // B
    private static void PatchAnimatorPlayerMap(ModuleDefinition module)
    {
        TypeDefinition outer = module.GetType("CombatOverhaul.Integration.AnimationPatches");
        TypeDefinition map = outer.NestedTypes.Single(t => t.Name == "AnimatorPlayerMap");
        MethodDefinition get = map.Methods.Single(m => m.Name == "Get");
        MethodDefinition add = map.Methods.Single(m => m.Name == "Add");
        MethodDefinition clear = map.Methods.Single(m => m.Name == "Clear");
        FieldDefinition mapping = map.Fields.Single(f => f.Name == "_mapping");

        // Verify Get is still the trivial TryGetValue forwarder we expect
        var g = get.Body.Instructions;
        if (g.Count != 6 || g[4].OpCode != OpCodes.Callvirt || ((MethodReference)g[4].Operand).Name != "TryGetValue") Fail("AnimatorPlayerMap.Get has unexpected IL");
        MethodReference tryGetValue = (MethodReference)g[4].Operand;

        TypeReference clientAnimator = get.Parameters[0].ParameterType;
        TypeReference entityPlayer = ((ByReferenceType)get.Parameters[1].ParameterType).ElementType;

        var threadStaticCtor = new MethodReference(".ctor", module.TypeSystem.Void,
            new TypeReference("System", "ThreadStaticAttribute", module, AsmRef(module, "System.Runtime"))) { HasThis = true };

        FieldDefinition NewStatic(string name, TypeReference ft, bool threadStatic)
        {
            var f = new FieldDefinition(name, FieldAttributes.Private | FieldAttributes.Static, ft);
            if (threadStatic) f.CustomAttributes.Add(new CustomAttribute(threadStaticCtor));
            map.Fields.Add(f);
            return f;
        }
        FieldDefinition version = NewStatic("s_perfVersion", module.TypeSystem.Int32, false);
        FieldDefinition lastAnimator = NewStatic("t_perfLastAnimator", clientAnimator, true);
        FieldDefinition lastPlayer = NewStatic("t_perfLastPlayer", entityPlayer, true);
        FieldDefinition lastFound = NewStatic("t_perfLastFound", module.TypeSystem.Boolean, true);
        FieldDefinition lastVersion = NewStatic("t_perfLastVersion", module.TypeSystem.Int32, true);

        // Get(animator, out player)
        get.Body.Instructions.Clear();
        get.Body.Variables.Clear();
        var vVer = new VariableDefinition(module.TypeSystem.Int32);
        var vFound = new VariableDefinition(module.TypeSystem.Boolean);
        get.Body.Variables.Add(vVer);
        get.Body.Variables.Add(vFound);
        get.Body.InitLocals = true;
        ILProcessor p = get.Body.GetILProcessor();
        Instruction miss = Instruction.Create(OpCodes.Volatile);
        // hit: animator != null && animator == t_last && t_lastVersion == s_version
        p.Emit(OpCodes.Ldarg_1);
        p.Emit(OpCodes.Brfalse, miss);
        p.Emit(OpCodes.Ldarg_1);
        p.Emit(OpCodes.Ldsfld, lastAnimator);
        p.Emit(OpCodes.Bne_Un, miss);
        p.Emit(OpCodes.Ldsfld, lastVersion);
        p.Emit(OpCodes.Volatile);
        p.Emit(OpCodes.Ldsfld, version);
        p.Emit(OpCodes.Bne_Un, miss);
        p.Emit(OpCodes.Ldarg_2);
        p.Emit(OpCodes.Ldsfld, lastPlayer);
        p.Emit(OpCodes.Stind_Ref);
        p.Emit(OpCodes.Ldsfld, lastFound);
        p.Emit(OpCodes.Ret);
        // miss: read version first, then do the original lookup, then cache
        p.Append(miss);
        p.Emit(OpCodes.Ldsfld, version);
        p.Emit(OpCodes.Stloc, vVer);
        p.Emit(OpCodes.Ldarg_0);
        p.Emit(OpCodes.Ldfld, mapping);
        p.Emit(OpCodes.Ldarg_1);
        p.Emit(OpCodes.Ldarg_2);
        p.Emit(OpCodes.Callvirt, tryGetValue);
        p.Emit(OpCodes.Stloc, vFound);
        p.Emit(OpCodes.Ldarg_1);
        p.Emit(OpCodes.Stsfld, lastAnimator);
        p.Emit(OpCodes.Ldarg_2);
        p.Emit(OpCodes.Ldind_Ref);
        p.Emit(OpCodes.Stsfld, lastPlayer);
        p.Emit(OpCodes.Ldloc, vFound);
        p.Emit(OpCodes.Stsfld, lastFound);
        p.Emit(OpCodes.Ldloc, vVer);
        p.Emit(OpCodes.Stsfld, lastVersion);
        p.Emit(OpCodes.Ldloc, vFound);
        p.Emit(OpCodes.Ret);
        get.Body.MaxStackSize = 4;

        // Interlocked.Increment(ref s_perfVersion) after every mutation of _mapping
        var interlocked = new TypeReference("System.Threading", "Interlocked", module, AsmRef(module, "System.Threading"));
        var increment = new MethodReference("Increment", module.TypeSystem.Int32, interlocked);
        increment.Parameters.Add(new ParameterDefinition(new ByReferenceType(module.TypeSystem.Int32)));

        void BumpAfter(MethodDefinition m, string calledName)
        {
            var ins = m.Body.Instructions;
            Instruction call = ins.Single(i => i.OpCode == OpCodes.Callvirt && ((MethodReference)i.Operand).Name == calledName);
            if (call.Next == null || call.Next.OpCode != OpCodes.Ret) Fail(m.Name + ": expected ret after " + calledName);
            ILProcessor mp = m.Body.GetILProcessor();
            Instruction a = Instruction.Create(OpCodes.Ldsflda, version);
            mp.InsertAfter(call, a);
            Instruction b = Instruction.Create(OpCodes.Call, increment);
            mp.InsertAfter(a, b);
            mp.InsertAfter(b, Instruction.Create(OpCodes.Pop));
            m.Body.MaxStackSize = Math.Max(m.Body.MaxStackSize, 3);
        }
        BumpAfter(add, "AddOrUpdate");
        BumpAfter(clear, "Clear");
        // new map instance (re-Patch after Unpatch) must not see another instance's cached entries
        MethodDefinition ctor = map.Methods.Single(m => m.IsConstructor && !m.IsStatic);
        Instruction ctorRet = ctor.Body.Instructions.Last();
        if (ctorRet.OpCode != OpCodes.Ret || ctor.Body.Instructions.Count(i => i.OpCode == OpCodes.Ret) != 1) Fail("unexpected ctor IL");
        ILProcessor cp = ctor.Body.GetILProcessor();
        cp.InsertBefore(ctorRet, Instruction.Create(OpCodes.Ldsflda, version));
        cp.InsertBefore(ctorRet, Instruction.Create(OpCodes.Call, increment));
        cp.InsertBefore(ctorRet, Instruction.Create(OpCodes.Pop));
        Console.WriteLine("B: AnimatorPlayerMap.Get now caches the last animator per thread; Add/Clear/.ctor invalidate");
    }

    // C
    private static void PatchCursorScale(ModuleDefinition module)
    {
        TypeDefinition type = module.GetType("CombatOverhaul.Inputs.DirectionCursorRenderer") ?? throw new InvalidOperationException("DirectionCursorRenderer not found");
        MethodDefinition render = type.Methods.Single(m => m.Name == "OnRenderFrame");
        var il = render.Body.Instructions;
        var stores = il.Where(i => i.OpCode == OpCodes.Stloc_1).ToList();
        if (stores.Count != 1) Fail("expected exactly one stloc.1 in OnRenderFrame, found " + stores.Count);
        Instruction st = stores[0];
        if (st.Previous.OpCode != OpCodes.Mul || st.Previous.Previous.OpCode != OpCodes.Ldc_R4 || (float)st.Previous.Previous.Operand != 0.5f) Fail("unexpected IL before scale store");
        if (render.Body.Variables[1].VariableType.FullName != "System.Single") Fail("local 1 is not float");
        // ... * 0.5f  ->  ... * 0.5f * 4f   (textures are now 256 instead of 1024 px)
        ILProcessor p = render.Body.GetILProcessor();
        Instruction four = Instruction.Create(OpCodes.Ldc_R4, 4f);
        p.InsertBefore(st, four);
        p.InsertBefore(st, Instruction.Create(OpCodes.Mul));
        Console.WriteLine("C: DirectionCursorRenderer draw scale x4");
    }
}
