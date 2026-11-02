using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using AttributeRenderingLibrary;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

// Tests for the patched ARL: JIT every method, list references, and exercise ArlFastPath against a fake world.
public class MultiblockLike : Block
{
    public override T GetInterface<T>(IWorldAccessor world, BlockPos pos) => null;
}

public class Fake : DispatchProxy
{
    public Dictionary<string, object> Values = new Dictionary<string, object>();
    public List<string> Added = new List<string>();

    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name.StartsWith("add_")) { Added.Add(m.Name); return null; }
        if (m.Name.StartsWith("remove_")) return null;
        string key = m.Name.StartsWith("get_") ? m.Name.Substring(4) : m.Name;
        if (Values.TryGetValue(key, out object v)) return v;
        return m.ReturnType.IsValueType && m.ReturnType != typeof(void) ? Activator.CreateInstance(m.ReturnType) : null;
    }

    public static T Make<T>(Dictionary<string, object> values, out Fake fake) where T : class
    {
        T p = Create<T, Fake>();
        fake = (Fake)(object)p;
        fake.Values = values;
        return p;
    }
}

internal static class Program
{
    static int failures;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) failures++;
    }

    static T U<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    static void SetCollectible(ItemStack stack, CollectibleObject obj)
    {
        foreach (FieldInfo f in typeof(ItemStack).GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            if (f.FieldType == typeof(Block) && obj is Block) f.SetValue(stack, obj);
            if (f.FieldType == typeof(Item) && obj is Item) f.SetValue(stack, obj);
            if (f.FieldType == typeof(EnumItemClass)) f.SetValue(stack, obj.ItemClass);
        }
    }

    static int Main(string[] args)
    {
        string vs = (System.Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? System.IO.Path.Combine(System.Environment.GetEnvironmentVariable("APPDATA"), "Vintagestory"));
        string[] dirs = { vs, Path.Combine(vs, "Lib"), Path.Combine(vs, "Mods"), Path.GetFullPath(@"..\..\patched") };
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            foreach (string d in dirs)
            {
                string p = Path.Combine(d, name.Name + ".dll");
                if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p);
            }
            return null;
        };
        return Run(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Run(string[] args)
    {
        Assembly arl = typeof(ArlFastPath).Assembly;
        Console.WriteLine("Loaded " + arl.Location);
        Console.WriteLine("References: " + string.Join(", ", arl.GetReferencedAssemblies().Select(a => a.Name).OrderBy(x => x)));
        Check(!arl.GetReferencedAssemblies().Any(a => a.Name.Contains("ArlFastPathHelper")), "no reference to the helper assembly");

        int n = 0, bad = 0;
        foreach (Type t in arl.GetTypes())
        {
            foreach (MethodBase m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                         .Concat(t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
            {
                if (m.IsAbstract || m.ContainsGenericParameters || t.ContainsGenericParameters || m.GetMethodBody() == null) continue;
                n++;
                try { RuntimeHelpers.PrepareMethod(m.MethodHandle); }
                catch (Exception e) { bad++; Console.WriteLine("JITFAIL " + t.FullName + "." + m.Name + " " + e.GetType().Name + " " + e.Message); }
            }
        }
        Console.WriteLine($"JIT all: {n} methods, {bad} failed");
        if (bad > 0) failures++;
        if (args.Contains("--jit-only")) return Done();

        Block plain = U<Block>(); plain.BlockId = 1;
        Check(ArlFastPath.MayHaveSupplier(plain) && ArlFastPath.MayHaveSupplierAtPos(plain), "no tables yet -> slow path (true)");

        var bb = U<BlockBehaviorShapeTexturesFromAttributes>();
        Block air = U<Block>(); air.BlockId = 0;
        Block stone = U<Block>(); stone.BlockId = 1; stone.BlockBehaviors = new BlockBehavior[0]; stone.CollectibleBehaviors = new CollectibleBehavior[0];
        Block chest = U<Block>(); chest.BlockId = 2; chest.EntityClass = "GenericTypedContainer";
        Block arlBlock = U<Block>(); arlBlock.BlockId = 3; arlBlock.BlockBehaviors = new BlockBehavior[] { bb }; arlBlock.CollectibleBehaviors = new CollectibleBehavior[] { bb };
        Block posOnly = U<Block>(); posOnly.BlockId = 4; posOnly.BlockBehaviors = new BlockBehavior[] { bb };
        Block multi = U<MultiblockLike>(); multi.BlockId = 5;
        Item stick = U<Item>(); stick.ItemId = 1;
        Item arlItem = U<ItemShapeTexturesFromAttributes>(); arlItem.ItemId = 2;
        Item behItem = U<Item>(); behItem.ItemId = 3; behItem.CollectibleBehaviors = new CollectibleBehavior[] { U<CollectibleBehaviorShapeTexturesFromAttributes>() };

        var world = Fake.Make<IWorldAccessor>(new Dictionary<string, object> {
            { "Blocks", new List<Block> { air, stone, chest, arlBlock, posOnly, multi } },
            { "Items", new List<Item> { null, stick, arlItem, behItem } } }, out _);
        var events = Fake.Make<IServerEventAPI>(new Dictionary<string, object>(), out Fake evFake);
        var api = Fake.Make<ICoreServerAPI>(new Dictionary<string, object> { { "Side", EnumAppSide.Server }, { "World", world }, { "Event", events } }, out _);

        ArlFastPath.Compute(api);
        Check(evFake.Added.Contains("add_SaveGameLoaded"), "server recompute hooked on SaveGameLoaded");
        Check(!ArlFastPath.MayHaveSupplier(stone), "stone: collectible fast path skips");
        Check(!ArlFastPath.MayHaveSupplierAtPos(stone), "stone: at-pos fast path skips");
        Check(!ArlFastPath.MayHaveSupplier(air), "air: skip");
        Check(!ArlFastPath.MayHaveSupplier(chest) && ArlFastPath.MayHaveSupplierAtPos(chest), "chest (has BE): collectible skip, at-pos slow path");
        Check(ArlFastPath.MayHaveSupplier(arlBlock) && ArlFastPath.MayHaveSupplierAtPos(arlBlock), "ARL block: both slow path");
        Check(!ArlFastPath.MayHaveSupplier(posOnly) && ArlFastPath.MayHaveSupplierAtPos(posOnly), "BlockBehavior-only supplier: at-pos slow path");
        Check(ArlFastPath.MayHaveSupplierAtPos(multi), "class overriding GetInterface: at-pos slow path");
        Check(!ArlFastPath.MayHaveSupplier(stick), "stick item: skip");
        Check(ArlFastPath.MayHaveSupplier(arlItem), "ARL item class: slow path");
        Check(ArlFastPath.MayHaveSupplier(behItem), "item with ARL collectible behaviour: slow path");
        Item unknownItem = U<Item>(); unknownItem.ItemId = 999;
        Block unknownBlock = U<Block>(); unknownBlock.BlockId = 999;
        Check(ArlFastPath.MayHaveSupplier(unknownItem) && ArlFastPath.MayHaveSupplierAtPos(unknownBlock), "ids outside table: slow path");
        Check(ArlFastPath.MayHaveSupplier(null) && ArlFastPath.MayHaveSupplierAtPos(null), "null: slow path (original null handling)");
        Check(ArlFastPath.MayHaveSupplierAtPos(stick), "item passed to at-pos guard: slow path (original behaviour)");

        ItemStack stoneStack = U<ItemStack>(); SetCollectible(stoneStack, stone);
        ItemStack arlStack = U<ItemStack>(); SetCollectible(arlStack, arlBlock);
        if (stoneStack.Collectible == stone && arlStack.Collectible == arlBlock)
        {
            Check(!ArlFastPath.MayHaveSupplierStackOrPos(arlBlock, new BlockPos(0), stoneStack), "stack without supplier -> original returns early -> skip");
            Check(ArlFastPath.MayHaveSupplierStackOrPos(stone, new BlockPos(0), arlStack), "stack with supplier -> run original");
        }
        else Console.WriteLine("SKIP stack tests (could not set ItemStack.Collectible)");
        Check(!ArlFastPath.MayHaveSupplierStackOrPos(arlBlock, null, null), "no stack, no pos -> original returns -> skip");
        Check(!ArlFastPath.MayHaveSupplierStackOrPos(stone, new BlockPos(0), null), "no stack, plain block at pos -> skip");
        Check(ArlFastPath.MayHaveSupplierStackOrPos(chest, new BlockPos(0), null), "no stack, BE block at pos -> run original");

        Block stone2 = U<Block>(); stone2.BlockId = 1; stone2.BlockBehaviors = new BlockBehavior[] { bb };
        var cworld = Fake.Make<IWorldAccessor>(new Dictionary<string, object> { { "Blocks", new List<Block> { air, stone2 } }, { "Items", new List<Item>() } }, out _);
        var cevents = Fake.Make<IClientEventAPI>(new Dictionary<string, object>(), out Fake cevFake);
        var capi = Fake.Make<ICoreClientAPI>(new Dictionary<string, object> { { "Side", EnumAppSide.Client }, { "World", cworld }, { "Event", cevents } }, out _);
        ArlFastPath.Compute(capi);
        Check(cevFake.Added.Contains("add_LevelFinalize"), "client recompute hooked on LevelFinalize");
        Check(ArlFastPath.MayHaveSupplierAtPos(stone), "OR-merge: id 1 is now maybe (client side has a supplier there)");
        Check(ArlFastPath.MayHaveSupplierAtPos(chest), "OR-merge: ids only one side knows stay maybe");
        Check(!ArlFastPath.MayHaveSupplierAtPos(air), "OR-merge: id both sides agree on stays skip");
        return Done();
    }

    static int Done()
    {
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }
}
