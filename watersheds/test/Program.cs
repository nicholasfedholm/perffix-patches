using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Watersheds.Patches;

// Behaviour test for the patched Watersheds PartiallyLitChunkFixPatch: disk-loaded columns must not Unpack(),
// freshly generated columns must still be checked (and broken ones queued for relight).
public class Fake : DispatchProxy
{
    public Dictionary<string, object> Values = new Dictionary<string, object>();
    public List<string> Calls = new List<string>();
    public List<object[]> Args = new List<object[]>();

    protected override object Invoke(MethodInfo m, object[] args)
    {
        Calls.Add(m.Name);
        Args.Add(args);
        string key = m.Name.StartsWith("get_") ? m.Name.Substring(4) : m.Name;
        if (Values.TryGetValue(key, out object v)) return v is System.Func<object[], object> f ? f(args) : v;
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

    static int Main()
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
        return Run();
    }

    static IWorldChunk TopChunk(bool brokenLight, out Fake chunkFake)
    {
        var light = Fake.Make<IChunkLight>(new Dictionary<string, object> { { "IsNull", false }, { "GetSunlight", (System.Func<object[], object>)(a => brokenLight ? 0 : 24) } }, out _);
        return Fake.Make<IWorldChunk>(new Dictionary<string, object> { { "Lighting", light } }, out chunkFake);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Run()
    {
        Type patchT = typeof(PartiallyLitChunkFixPatch);
        MethodInfo onLoaded = patchT.GetMethod("OnChunkColumnLoaded", BindingFlags.NonPublic | BindingFlags.Static);
        var pending = (System.Collections.IDictionary)patchT.GetField("pendingChunks", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

        var wm = Fake.Make<IWorldManagerAPI>(new Dictionary<string, object> { { "ChunkSize", 32 }, { "MapSizeY", 256 } }, out _);
        var ev = Fake.Make<IServerEventAPI>(new Dictionary<string, object>(), out Fake evFake);
        var api = Fake.Make<ICoreServerAPI>(new Dictionary<string, object> { { "Event", ev }, { "WorldManager", wm } }, out _);

        PartiallyLitChunkFixPatch.Initialize(api);
        int genIdx = evFake.Calls.IndexOf("ChunkColumnGeneration");
        Check(genIdx >= 0, "Initialize registers a ChunkColumnGeneration handler");
        Check(evFake.Calls.Contains("add_ChunkColumnLoaded"), "Initialize still subscribes ChunkColumnLoaded");
        object[] genArgs = evFake.Args[genIdx];
        Check((EnumWorldGenPass)genArgs[1] == EnumWorldGenPass.PreDone && (string)genArgs[2] == "standard", "handler is for PreDone / standard");
        var handler = (ChunkColumnGenerationDelegate)genArgs[0];

        // 1. Column loaded from disk (never generated this session): no Unpack, nothing queued even if lighting looks broken
        IWorldChunk diskTop = TopChunk(true, out Fake diskFake);
        onLoaded.Invoke(null, new object[] { new Vec2i(10, 20), new IWorldChunk[] { diskTop } });
        Check(!diskFake.Calls.Contains("Unpack"), "disk-loaded column: topChunk.Unpack() skipped");
        Check(pending.Count == 0, "disk-loaded column: not queued");

        // 2. Freshly generated column with broken lighting: checked and queued (original behaviour)
        var req = Fake.Make<IChunkColumnGenerateRequest>(new Dictionary<string, object> { { "ChunkX", 11 }, { "ChunkZ", 21 } }, out _);
        handler(req);
        IWorldChunk freshTop = TopChunk(true, out Fake freshFake);
        onLoaded.Invoke(null, new object[] { new Vec2i(11, 21), new IWorldChunk[] { freshTop } });
        Check(freshFake.Calls.Contains("Unpack"), "fresh column: lighting check runs (Unpack called)");
        Check(pending.Count == 1, "fresh column with broken light: queued for relight");

        // 3. Same column loaded again later (e.g. after unload): it now counts as from disk
        IWorldChunk againTop = TopChunk(true, out Fake againFake);
        onLoaded.Invoke(null, new object[] { new Vec2i(11, 21), new IWorldChunk[] { againTop } });
        Check(!againFake.Calls.Contains("Unpack"), "second load of the same column: skipped (fresh flag consumed)");

        // 4. Fresh column with good lighting: checked, not queued
        var req2 = Fake.Make<IChunkColumnGenerateRequest>(new Dictionary<string, object> { { "ChunkX", -5 }, { "ChunkZ", 7 } }, out _);
        handler(req2);
        IWorldChunk okTop = TopChunk(false, out Fake okFake);
        onLoaded.Invoke(null, new object[] { new Vec2i(-5, 7), new IWorldChunk[] { okTop } });
        Check(okFake.Calls.Contains("Unpack") && pending.Count == 1, "fresh column with good light: checked, not queued");

        // 5. Negative coords don't collide with positive ones in the key
        handler(Fake.Make<IChunkColumnGenerateRequest>(new Dictionary<string, object> { { "ChunkX", 1 }, { "ChunkZ", -1 } }, out _));
        IWorldChunk collideTop = TopChunk(true, out Fake collideFake);
        onLoaded.Invoke(null, new object[] { new Vec2i(-1, 1), new IWorldChunk[] { collideTop } });
        Check(!collideFake.Calls.Contains("Unpack"), "key (1,-1) does not match (-1,1)");

        // 6. Dispose clears the fresh set
        handler(Fake.Make<IChunkColumnGenerateRequest>(new Dictionary<string, object> { { "ChunkX", 3 }, { "ChunkZ", 3 } }, out _));
        PartiallyLitChunkFixPatch.Dispose();
        Check(!PartiallyLitFreshColumns.ConsumeIsFresh(new Vec2i(3, 3)), "Dispose clears tracked columns");

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }
}
