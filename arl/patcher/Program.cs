using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// IL patcher for AttributeRenderingLibrary 3.2.0 (perf fixes only):
//  1. Clone AttributeRenderingLibrary.ArlFastPath (compiled from helper\ArlFastPath.cs) into the ARL module.
//  2. Core gets an AssetsFinalize override that calls ArlFastPath.Compute(api).
//  3. Each hot property postfix starts with "if (!ArlFastPath.MayHave...(...)) return;".
//  4. lock(pos) in the GetBlockMaterial / GetLightHsv postfixes is neutralised (Monitor.Enter/Exit -> pop).
internal static class Program
{
    // postfix class -> guard kind
    private static readonly (string type, string guard)[] Guards =
    {
        ("AttributeRenderingLibrary.HarmonyPatches.Properties.Block_GetBlockMaterial_Patch", "StackOrPos"),
        ("AttributeRenderingLibrary.HarmonyPatches.Properties.CollectibleObject_GetLightHsv_Patch", "StackOrPos"),
        ("AttributeRenderingLibrary.HarmonyPatches.Properties.Block_GetRequiredMiningTier_Patch", "AtPos"),
        ("AttributeRenderingLibrary.HarmonyPatches.Properties.Block_GetColor_Patch", "Coll"),
        ("AttributeRenderingLibrary.HarmonyPatches.Properties.CollectibleObject_GetCombustibleProperties_Patch", "Coll"),
        ("AttributeRenderingLibrary.HarmonyPatches.Properties.CollectibleObject_GetCrushingProperties_Patch", "Coll"),
        ("AttributeRenderingLibrary.HarmonyPatches.Properties.CollectibleObject_GetDamagedBy_Patch", "Coll"),
        ("AttributeRenderingLibrary.HarmonyPatches.Properties.CollectibleObject_GetGrindingProperties_Patch", "Coll"),
        ("AttributeRenderingLibrary.HarmonyPatches.Properties.CollectibleObject_GetNutritionProperties_Patch", "Coll"),
        ("AttributeRenderingLibrary.HarmonyPatches.Properties.CollectibleObject_GetTags_Patch", "Coll"),
        ("AttributeRenderingLibrary.HarmonyPatches.Properties.CollectibleObject_GetToolTier_Patch", "Coll"),
        ("AttributeRenderingLibrary.HarmonyPatches.Properties.CollectibleObject_GetTool_Patch", "Coll"),
        ("AttributeRenderingLibrary.HarmonyPatches.Properties.CollectibleObject_GetTransitionableProperties_Patch", "Coll"),
        ("AttributeRenderingLibrary.HarmonyPatches.Properties.Item_GetRandomColor_stack_Patch", "Coll"),
    };

    private static int Main(string[] args)
    {
        string input = args[0], helper = args[1], output = args[2], vsDir = args[3];
        var resolver = new DefaultAssemblyResolver();
        foreach (string d in new[] { vsDir, Path.Combine(vsDir, "Lib"), Path.Combine(vsDir, "Mods"), Path.GetDirectoryName(Path.GetFullPath(input)) }) resolver.AddSearchDirectory(d);

        ModuleDefinition module = ModuleDefinition.ReadModule(input, new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = true, InMemory = true });
        ModuleDefinition helperModule = ModuleDefinition.ReadModule(helper, new ReaderParameters { AssemblyResolver = resolver, InMemory = true });

        AlignAssemblyRefs(helperModule, module);
        TypeDefinition fast = CloneType(helperModule.GetType("AttributeRenderingLibrary.ArlFastPath"), module);
        AddAssetsFinalize(module, fast);
        int guarded = AddGuards(module, fast);
        int unlocked = RemoveLocks(module);
        AssertNoDuplicateAssemblyRefs(module);

        module.Write(output, new WriterParameters { WriteSymbols = true });
        Console.WriteLine($"Cloned ArlFastPath ({fast.Methods.Count} methods), guarded {guarded} postfixes, removed {unlocked} lock(pos). Wrote {output}");
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

    private static MethodReference FastMethod(TypeDefinition fast, string name) => fast.Methods.Single(m => m.Name == name);

    // ---- 2. Core.AssetsFinalize -----------------------------------------------------------------------------
    private static void AddAssetsFinalize(ModuleDefinition module, TypeDefinition fast)
    {
        TypeDefinition core = module.GetType("AttributeRenderingLibrary.Core") ?? throw new InvalidOperationException("Core not found");
        if (core.Methods.Any(m => m.Name == "AssetsFinalize")) Fail("Core already has AssetsFinalize");
        MethodDefinition start = core.Methods.Single(m => m.Name == "Start" && m.Parameters.Count == 1);
        TypeReference apiType = start.Parameters[0].ParameterType; // ICoreAPI

        TypeReference modSystem = core.BaseType;
        var baseFinalize = new MethodReference("AssetsFinalize", module.TypeSystem.Void, modSystem) { HasThis = true };
        baseFinalize.Parameters.Add(new ParameterDefinition(apiType));
        if (modSystem.Resolve().Methods.SingleOrDefault(m => m.Name == "AssetsFinalize" && m.IsVirtual) == null) Fail("ModSystem.AssetsFinalize not found/virtual");

        var m = new MethodDefinition("AssetsFinalize", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual, module.TypeSystem.Void);
        m.Parameters.Add(new ParameterDefinition("api", ParameterAttributes.None, apiType));
        ILProcessor p = m.Body.GetILProcessor();
        p.Emit(OpCodes.Ldarg_0);
        p.Emit(OpCodes.Ldarg_1);
        p.Emit(OpCodes.Call, baseFinalize);
        p.Emit(OpCodes.Ldarg_1);
        p.Emit(OpCodes.Call, FastMethod(fast, "Compute"));
        p.Emit(OpCodes.Ret);
        core.Methods.Add(m);
    }

    // ---- 3. guards ------------------------------------------------------------------------------------------
    private static int AddGuards(ModuleDefinition module, TypeDefinition fast)
    {
        int n = 0;
        foreach (var (typeName, guard) in Guards)
        {
            TypeDefinition t = module.GetType(typeName) ?? throw new InvalidOperationException("missing " + typeName);
            MethodDefinition post = t.Methods.Single(m => m.Name == "Postfix");
            if (!post.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPostfix" || a.AttributeType.Name == "HarmonyPostfixAttribute")) Fail(typeName + ".Postfix is not a [HarmonyPostfix]");
            if (post.Parameters[0].Name != "__instance") Fail(typeName + ": first parameter is not __instance");
            if (post.ReturnType.FullName != "System.Void") Fail(typeName + ": postfix does not return void");

            ILProcessor p = post.Body.GetILProcessor();
            Instruction first = post.Body.Instructions[0];
            var guardIl = new List<Instruction> { p.Create(OpCodes.Ldarg_0) };
            switch (guard)
            {
                case "Coll":
                    guardIl.Add(p.Create(OpCodes.Call, FastMethod(fast, "MayHaveSupplier")));
                    break;
                case "AtPos":
                    guardIl.Add(p.Create(OpCodes.Call, FastMethod(fast, "MayHaveSupplierAtPos")));
                    break;
                case "StackOrPos":
                    ParameterDefinition pos = post.Parameters.Single(x => x.Name == "pos");
                    ParameterDefinition stack = post.Parameters.Single(x => x.Name == "stack");
                    if (pos.ParameterType.Name != "BlockPos" || stack.ParameterType.Name != "ItemStack") Fail(typeName + ": unexpected pos/stack types");
                    guardIl.Add(p.Create(OpCodes.Ldarg, pos));
                    guardIl.Add(p.Create(OpCodes.Ldarg, stack));
                    guardIl.Add(p.Create(OpCodes.Call, FastMethod(fast, "MayHaveSupplierStackOrPos")));
                    break;
            }
            guardIl.Add(p.Create(OpCodes.Brtrue, first));
            guardIl.Add(p.Create(OpCodes.Ret));
            foreach (Instruction ins in guardIl) p.InsertBefore(first, ins);
            post.Body.MaxStackSize = Math.Max(post.Body.MaxStackSize, 3);
            n++;
        }
        return n;
    }

    // The helper was compiled against VintagestoryAPI 1.22.7, ARL against 1.22.6. Align the helper's assembly references
    // with ARL's before cloning, so imported type refs reuse ARL's existing references instead of adding duplicates.
    private static void AlignAssemblyRefs(ModuleDefinition helper, ModuleDefinition target)
    {
        foreach (AssemblyNameReference r in helper.AssemblyReferences)
        {
            AssemblyNameReference existing = target.AssemblyReferences.FirstOrDefault(a => a.Name == r.Name);
            if (existing != null && existing.Version != r.Version)
            {
                Console.WriteLine($"  helper ref {r.Name} {r.Version} -> {existing.Version} (ARL's)");
                r.Version = existing.Version;
            }
        }
    }

    private static void AssertNoDuplicateAssemblyRefs(ModuleDefinition module)
    {
        var dups = module.AssemblyReferences.GroupBy(a => a.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dups.Count > 0) Fail("duplicate assembly references: " + string.Join(", ", dups));
    }

    // ---- 4. lock(pos) ---------------------------------------------------------------------------------------
    private static int RemoveLocks(ModuleDefinition module)
    {
        int n = 0;
        foreach (string typeName in new[] { "AttributeRenderingLibrary.HarmonyPatches.Properties.Block_GetBlockMaterial_Patch", "AttributeRenderingLibrary.HarmonyPatches.Properties.CollectibleObject_GetLightHsv_Patch" })
        {
            MethodDefinition post = module.GetType(typeName).Methods.Single(m => m.Name == "Postfix");
            var il = post.Body.Instructions;
            List<Instruction> enters = il.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.DeclaringType.FullName == "System.Threading.Monitor" && r.Name == "Enter").ToList();
            List<Instruction> exits = il.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.DeclaringType.FullName == "System.Threading.Monitor" && r.Name == "Exit").ToList();
            if (enters.Count != 1 || exits.Count != 1 || ((MethodReference)enters[0].Operand).Parameters.Count != 2) Fail(typeName + ": unexpected lock IL");
            // Monitor.Enter(obj, ref lockTaken) -> pop; pop   (lockTaken stays false, so the finally skips Exit)
            Instruction e = enters[0];
            e.OpCode = OpCodes.Pop;
            e.Operand = null;
            post.Body.GetILProcessor().InsertAfter(e, Instruction.Create(OpCodes.Pop));
            // Monitor.Exit(obj) -> pop   (unreachable now, neutralised anyway)
            exits[0].OpCode = OpCodes.Pop;
            exits[0].Operand = null;
            n++;
        }
        return n;
    }
}
