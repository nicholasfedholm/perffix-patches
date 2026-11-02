using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Farseer;
using Farseer.Server;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace FarseerBench;

// Bench-only harness: drives Farseer's TerrainSamplerAdapter directly on a throwaway world and dumps heightmaps + timings.
// Env: FARSEER_BENCH_OUT (dir, required), FARSEER_BENCH_PASSES (default 2), FARSEER_BENCH_MODE (plain|prio),
//      FARSEER_BENCH_SERIALREF (1 = independent serial reference compare after the passes)
public class FarseerBenchSystem : ModSystem
{
    const BindingFlags NP = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    ICoreServerAPI sapi;
    string outDir, mode;
    int passes, pass;
    bool serialRef;
    TerrainSamplerAdapter adapter;
    FarRegionGen generator;
    List<long> regions;
    HashSet<long> expected;
    readonly Dictionary<long, long> requestedAtTicks = new();
    readonly Dictionary<long, (double ms, string sha, int order)> results = new();
    readonly Dictionary<long, int[]> pass1 = new();
    readonly Stopwatch sw = new();
    readonly StringBuilder report = new();
    TimeSpan cpuAtStart;
    long listenerId;
    double lastResultAt;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        outDir = Environment.GetEnvironmentVariable("FARSEER_BENCH_OUT");
        if (string.IsNullOrEmpty(outDir)) { api.Logger.Notification("[FarseerBench] FARSEER_BENCH_OUT not set, idle"); return; }
        Directory.CreateDirectory(outDir);
        passes = int.TryParse(Environment.GetEnvironmentVariable("FARSEER_BENCH_PASSES"), out var p) ? p : 2;
        mode = Environment.GetEnvironmentVariable("FARSEER_BENCH_MODE") ?? "plain";
        serialRef = Environment.GetEnvironmentVariable("FARSEER_BENCH_SERIALREF") == "1";
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => api.Event.RegisterCallback(_ => Begin(), 3000));
    }

    void Log(string s) { report.AppendLine(s); sapi.Logger.Notification("[FarseerBench] " + s); }

    void Begin()
    {
        try
        {
            var fsys = sapi.ModLoader.GetModSystem<FarseerModSystem>();
            var provider = typeof(FarseerServer).GetField("regionProvider", NP).GetValue(fsys.Server);
            generator = (FarRegionGen)typeof(FarRegionProvider).GetField("generator", NP).GetValue(provider);
            adapter = (TerrainSamplerAdapter)typeof(FarRegionGen).GetField("terrainSamplerAdapter", NP).GetValue(generator);
            Log($"farseer mvid={typeof(TerrainSamplerAdapter).Module.ModuleVersionId} perffix={AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "FarseerPerffix")}");
            Log($"adapter={(adapter != null)} grid={fsys.Server.Config.HeightmapGridSize} procs={Environment.ProcessorCount} mode={mode}");
            LogSamplerState();
            if (adapter == null) { Finish("NO ADAPTER"); return; }
            adapter.RegionGenerated += OnGenerated;

            int rs = sapi.WorldManager.RegionSize;
            int cx = sapi.WorldManager.MapSizeX / 2 / rs, cz = sapi.WorldManager.MapSizeZ / 2 / rs;
            regions = new List<long>();
            for (int dz = -2; dz <= 2; dz++)
                for (int dx = -2; dx <= 2; dx++)
                    if (dx != 0 || dz != 0) regions.Add(sapi.WorldManager.MapRegionIndex2D(cx + dx, cz + dz));
            Log($"regionSize={rs} center=({cx},{cz}) regions={regions.Count}");
            listenerId = sapi.Event.RegisterGameTickListener(Watch, 200);
            StartPass();
        }
        catch (Exception e) { Finish("BEGIN FAILED " + e); }
    }

    void LogSamplerState()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = null;
            try { t = asm.GetType("AlgernonsTerrainSampler.TerrainSamplerMod"); } catch { }
            if (t != null)
            {
                var inst = t.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                var wl = inst == null ? null : t.GetProperty("WatershedsLoaded")?.GetValue(inst);
                var wgi = inst == null ? null : t.GetField("watershedsGenTerraInstance", NP)?.GetValue(inst);
                Log($"sampler: WatershedsLoaded={wl} watershedsGenTerraInstance={(wgi != null)}");
            }
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }
            foreach (var pt in types.Where(x => x.Name == "SamplerAndWatershedsPatch"))
                Log("p5: " + string.Join(" ", pt.GetFields(BindingFlags.Static | BindingFlags.Public)
                    .Where(f => f.FieldType == typeof(long) || f.FieldType == typeof(bool)).Select(f => $"{f.Name}={f.GetValue(null)}")));
        }
    }

    void StartPass()
    {
        pass++;
        results.Clear();
        requestedAtTicks.Clear();
        expected = new HashSet<long>(regions);
        cpuAtStart = Process.GetCurrentProcess().TotalProcessorTime;
        lastResultAt = 0;
        sw.Restart();
        foreach (var r in regions) { requestedAtTicks[r] = sw.ElapsedTicks; adapter.StartGeneratingRegion(r); }
        if (mode == "prio")
        {
            // Priority = reverse of request order (last requested most urgent, lower = sooner); then drop the 8 earliest-requested from the wanted set.
            var prio = new Dictionary<long, int>();
            for (int i = 0; i < regions.Count; i++) prio[regions[i]] = regions.Count - i;
            generator.SortTasksByPriority(prio);
            var keep = new HashSet<long>(regions.Skip(8));
            generator.CancelTasksNotIn(keep);
            expected = keep;
            Log($"pass {pass}: prio applied, keep={keep.Count} dropped={regions.Count - keep.Count}");
        }
        Log($"pass {pass} requested {regions.Count} regions");
    }

    void OnGenerated(long idx, FarRegionHeightmap hm)
    {
        if (!requestedAtTicks.TryGetValue(idx, out var t0) || results.ContainsKey(idx)) return;
        double ms = (sw.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
        var bytes = new byte[hm.Points.Length * 4];
        Buffer.BlockCopy(hm.Points, 0, bytes, 0, bytes.Length);
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        results[idx] = (ms, sha, results.Count);
        lastResultAt = sw.Elapsed.TotalMilliseconds;
        File.WriteAllBytes(Path.Combine(outDir, $"p{pass}_{idx}.bin"), bytes);
        if (pass == 1) pass1[idx] = (int[])hm.Points.Clone();
    }

    void Watch(float dt)
    {
        bool timeout = sw.Elapsed.TotalMinutes > 30;
        bool allExpected = expected.All(results.ContainsKey);
        // prio mode: also wait for a quiet period so late output from dropped regions (stock behaviour) is captured
        bool quiet = results.Count > 0 && sw.Elapsed.TotalMilliseconds - lastResultAt > 8000;
        bool done = mode == "prio" ? (allExpected && quiet) : allExpected;
        if (!done && !timeout) return;
        EndPass(timeout && !done);
        if (pass < passes && !timeout) { StartPass(); return; }
        sapi.Event.UnregisterGameTickListener(listenerId);
        if (serialRef) SerialReference();
        LogSamplerState();
        Finish(timeout && !done ? "TIMEOUT" : "DONE");
    }

    void EndPass(bool timedOut)
    {
        double wall = results.Count == 0 ? sw.Elapsed.TotalMilliseconds : results.Values.Max(v => v.ms);
        var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpuAtStart).TotalMilliseconds;
        var lat = results.Values.Select(v => v.ms).OrderBy(x => x).ToList();
        double P(double q) => lat.Count == 0 ? 0 : lat[Math.Min(lat.Count - 1, (int)Math.Ceiling(q * lat.Count) - 1)];
        Log($"PASS {pass} produced={results.Count}/{regions.Count} wall_ms={wall:F0} p50_ms={P(0.5):F0} p95_ms={P(0.95):F0} cpu_ms={cpu:F0}{(timedOut ? " TIMEOUT" : "")}");
        using var w = new StreamWriter(Path.Combine(outDir, $"pass{pass}.tsv"));
        w.WriteLine("reqOrder\tregionIdx\tdoneOrder\tlatency_ms\tsha256");
        for (int i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            if (results.TryGetValue(r, out var v)) w.WriteLine($"{i}\t{r}\t{v.order}\t{v.ms:F1}\t{v.sha}");
            else w.WriteLine($"{i}\t{r}\t-\t-\tNOT_PRODUCED");
        }
    }

    // Independent serial reference: stock grid math, sampler called through plain reflection on the main thread, one point at a time.
    void SerialReference()
    {
        Type t = AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType("AlgernonsTerrainSampler.TerrainSamplerMod"); } catch { return null; } }).First(x => x != null);
        var inst = t.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public).GetValue(null);
        var mi = t.GetMethod("GetBlockColumnHeight", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(int), typeof(int) }, null);
        int grid = sapi.ModLoader.GetModSystem<FarseerModSystem>().Server.Config.HeightmapGridSize;
        int rs = sapi.WorldManager.RegionSize, cs = sapi.WorldManager.ChunkSize, sea = sapi.World.SeaLevel, cir = rs / cs;
        float step = (float)rs / (float)grid;
        int match = 0, mismatch = 0, missing = 0;
        var sws = Stopwatch.StartNew();
        foreach (var r in regions)
        {
            if (!pass1.TryGetValue(r, out var got)) { missing++; continue; }
            var pos = sapi.WorldManager.MapRegionPosFromIndex2D(r);
            int sx = pos.X * cir, sz = pos.Z * cir;
            var pts = new int[grid * grid];
            for (int i = 0; i < grid; i++)
                for (int j = 0; j < grid; j++)
                {
                    int h = (int)mi.Invoke(inst, new object[] { sx * cs + (int)((float)j * step), sz * cs + (int)((float)i * step) });
                    pts[i * grid + j] = Math.Max(h, sea);
                }
            if (got.Length == pts.Length && pts.AsSpan().SequenceEqual(got)) match++;
            else
            {
                mismatch++;
                int d = 0;
                for (int k = 0; k < Math.Min(pts.Length, got.Length); k++) if (pts[k] != got[k]) d++;
                Log($"SERIALREF MISMATCH region {r}: {d} points differ (len {got.Length} vs {pts.Length})");
            }
        }
        Log($"SERIALREF match={match} mismatch={mismatch} missing={missing} serial_ms={sws.ElapsedMilliseconds}");
    }

    void Finish(string status)
    {
        Log("STATUS " + status);
        File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
        sapi.Event.RegisterCallback(_ => sapi.Server.ShutDown(), 1000);
    }
}
