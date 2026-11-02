using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Farseer;
using Farseer.Server;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace FarseerPerffix;

/// <summary>
/// Replacement for TerrainSamplerAdapter.StartGeneratingRegion (wired in by IL patch).
/// One global pool of sampler threads (the budget = max concurrent GetBlockColumnHeight calls) pulls single grid ROWS
/// from the highest-priority region, so one region is sampled by many threads at once. Each grid point is computed with
/// the stock math and written to its own slot, so output is independent of thread count and order.
/// Phase 2: Reprioritize / CancelNotIn are forwarded from FarRegionGen.SortTasksByPriority / CancelTasksNotIn.
/// </summary>
public static class SamplerScheduler
{
    const BindingFlags NP = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly FieldInfo fModSystem = typeof(TerrainSamplerAdapter).GetField("modSystem", NP);
    static readonly FieldInfo fSapi = typeof(TerrainSamplerAdapter).GetField("sapi", NP);
    static readonly FieldInfo fSample = typeof(TerrainSamplerAdapter).GetField("sampleHeightDelegate", NP);
    static readonly FieldInfo fChunksInRegionColumn = typeof(TerrainSamplerAdapter).GetField("chunksInRegionColumn", NP);
    static readonly FieldInfo fInProgress = typeof(TerrainSamplerAdapter).GetField("asyncSamplingInProgress", NP);
    static readonly FieldInfo fCts = typeof(TerrainSamplerAdapter).GetField("cancelSamplingTokenSource", NP);
    static readonly FieldInfo fDisposed = typeof(TerrainSamplerAdapter).GetField("disposed", NP);
    static readonly FieldInfo fRegionGenerated = typeof(TerrainSamplerAdapter).GetField("RegionGenerated", NP);

    sealed class AdapterState
    {
        public TerrainSamplerAdapter Adapter;
        public ICoreServerAPI Sapi;
        public FarseerModSystem ModSystem;
        public Func<int, int, int> Sample;
        public ConcurrentDictionary<long, byte> InProgress;
        public CancellationToken Token;
        public int ChunksInRegionColumn;
        public Dictionary<long, int> LastPriorities;
        public bool IsDisposed => (bool)fDisposed.GetValue(Adapter);
    }

    sealed class RegionJob
    {
        public AdapterState S;
        public long RegionIdx;
        public int Grid, BaseX, BaseZ, SeaLevel;
        public float Step;
        public FarRegionHeightmap Heightmap;
        public long Seq;
        public int Priority = int.MaxValue;
        public int NextRow, RowsDone, Active;
        public bool Cancelled, Finalized;
        public Exception Error;
    }

    static readonly object gate = new();
    static readonly List<RegionJob> jobs = new();
    static readonly ConditionalWeakTable<TerrainSamplerAdapter, AdapterState> states = new();
    static long seq;
    static int threadCount;
    static bool started;

    public static void Start(TerrainSamplerAdapter adapter, long regionIdx)
    {
        var s = GetState(adapter);
        if (s.Token.IsCancellationRequested || s.IsDisposed) return;
        if (!s.InProgress.TryAdd(regionIdx, 0)) return;

        // Same inputs as stock StartGeneratingRegion
        int gridSize = s.ModSystem.Server.Config.HeightmapGridSize;
        Vec3i pos = s.Sapi.WorldManager.MapRegionPosFromIndex2D(regionIdx);
        int chunkSize = s.Sapi.WorldManager.ChunkSize;
        var job = new RegionJob
        {
            S = s,
            RegionIdx = regionIdx,
            Grid = gridSize,
            BaseX = pos.X * s.ChunksInRegionColumn * chunkSize,
            BaseZ = pos.Z * s.ChunksInRegionColumn * chunkSize,
            SeaLevel = s.Sapi.World.SeaLevel,
            Step = (float)s.Sapi.WorldManager.RegionSize / (float)gridSize,
            Heightmap = new FarRegionHeightmap { GridSize = gridSize, Points = new int[gridSize * gridSize] },
        };
        lock (gate)
        {
            EnsureWorkers(s);
            job.Seq = seq++;
            if (s.LastPriorities != null && s.LastPriorities.TryGetValue(regionIdx, out var p)) job.Priority = p;
            jobs.Add(job);
            SortJobs();
            Monitor.PulseAll(gate);
        }
    }

    /// <summary>Phase 2: called at the start of FarRegionGen.SortTasksByPriority (main thread). Lower value = sooner.</summary>
    public static void Reprioritize(TerrainSamplerAdapter adapter, Dictionary<long, int> regionPriorities)
    {
        if (adapter == null || regionPriorities == null || !PerffixConfig.Current.PriorityQueue) return;
        var s = GetState(adapter);
        var copy = new Dictionary<long, int>(regionPriorities);
        lock (gate)
        {
            s.LastPriorities = copy;
            foreach (var j in jobs)
                if (j.S == s) j.Priority = copy.TryGetValue(j.RegionIdx, out var p) ? p : int.MaxValue;
            SortJobs();
        }
    }

    /// <summary>Phase 2: called at the start of FarRegionGen.CancelTasksNotIn (main thread). Drops queued work for unwanted
    /// regions; regions being sampled stop at their next row.</summary>
    public static void CancelNotIn(TerrainSamplerAdapter adapter, HashSet<long> regionsToKeep)
    {
        if (adapter == null || regionsToKeep == null || !PerffixConfig.Current.CancelStale) return;
        var s = GetState(adapter);
        List<RegionJob> done = null;
        int cancelled = 0;
        lock (gate)
        {
            foreach (var j in jobs)
            {
                if (j.S != s || j.Cancelled || regionsToKeep.Contains(j.RegionIdx)) continue;
                j.Cancelled = true;
                cancelled++;
                if (j.Active == 0) (done ??= new()).Add(j);
            }
            if (done != null) foreach (var j in done) Finalize_NoLock(j);
        }
        if (done != null) foreach (var j in done) Complete(j);
        if (cancelled > 0) PerffixConfig.Stats.Cancelled += cancelled;
    }

    static AdapterState GetState(TerrainSamplerAdapter adapter)
    {
        lock (states)
        {
            if (states.TryGetValue(adapter, out var st)) return st;
            st = new AdapterState
            {
                Adapter = adapter,
                Sapi = (ICoreServerAPI)fSapi.GetValue(adapter),
                ModSystem = (FarseerModSystem)fModSystem.GetValue(adapter),
                InProgress = (ConcurrentDictionary<long, byte>)fInProgress.GetValue(adapter),
                ChunksInRegionColumn = (int)fChunksInRegionColumn.GetValue(adapter),
            };
            PerffixConfig.Load(st.Sapi);
            var original = (Func<int, int, int>)fSample.GetValue(adapter);
            st.Sample = PerffixConfig.Current.FastDelegate ? TryFastDelegate(original) ?? original : original;
            try { st.Token = ((CancellationTokenSource)fCts.GetValue(adapter)).Token; }
            catch (ObjectDisposedException) { st.Token = new CancellationToken(true); }
            st.Sapi.Logger.Notification("[FarseerPerffix] sampler threads={0} priority={1} fastDelegate={2} priorityQueue={3} cancelStale={4}",
                PerffixConfig.Current.SamplerThreads, PerffixConfig.Current.ThreadPriority, st.Sample != original,
                PerffixConfig.Current.PriorityQueue, PerffixConfig.Current.CancelStale);
            states.Add(adapter, st);
            return st;
        }
    }

    // Stock delegate is a closure doing MethodInfo.Invoke(instance, new object[]{x,z}) per point; bind the same method directly.
    static Func<int, int, int> TryFastDelegate(Func<int, int, int> original)
    {
        try
        {
            var target = original?.Target;
            if (target == null) return null;
            var t = target.GetType();
            var mi = t.GetField("getHeightMethod")?.GetValue(target) as MethodInfo;
            var inst = t.GetField("instance")?.GetValue(target);
            if (mi == null || inst == null || mi.IsStatic || mi.ReturnType != typeof(int)) return null;
            var ps = mi.GetParameters();
            if (ps.Length != 2 || ps[0].ParameterType != typeof(int) || ps[1].ParameterType != typeof(int)) return null;
            return mi.CreateDelegate<Func<int, int, int>>(inst);
        }
        catch { return null; }
    }

    static void EnsureWorkers(AdapterState s)
    {
        if (started) return;
        started = true;
        threadCount = PerffixConfig.Current.SamplerThreads;
        for (int i = 0; i < threadCount; i++)
        {
            var th = new Thread(WorkerLoop) { IsBackground = true, Name = "FarseerPerffix sampler " + i, Priority = PerffixConfig.Current.ThreadPriority };
            th.Start();
        }
    }

    // Started regions first (finish what is half done), then priority (lower first), then request order.
    static void SortJobs() => jobs.Sort((a, b) =>
    {
        int c = (b.NextRow > 0).CompareTo(a.NextRow > 0);
        if (c != 0) return c;
        c = a.Priority.CompareTo(b.Priority);
        return c != 0 ? c : a.Seq.CompareTo(b.Seq);
    });

    static bool TryClaim(out RegionJob job, out int row)
    {
        List<RegionJob> dead = null;
        job = null; row = -1;
        foreach (var j in jobs)
        {
            if (!j.Cancelled && j.S.Token.IsCancellationRequested) j.Cancelled = true;
            if (j.Cancelled)
            {
                if (j.Active == 0 && !j.Finalized) (dead ??= new()).Add(j);
                continue;
            }
            if (j.NextRow < j.Grid) { job = j; row = j.NextRow++; j.Active++; break; }
        }
        if (dead != null)
        {
            foreach (var j in dead) Finalize_NoLock(j);
            // complete outside the lock: hand to the thread pool
            ThreadPool.UnsafeQueueUserWorkItem(_ => { foreach (var j in dead) Complete(j); }, null);
        }
        return job != null;
    }

    static void WorkerLoop()
    {
        while (true)
        {
            RegionJob job; int row;
            lock (gate)
            {
                while (!TryClaim(out job, out row)) Monitor.Wait(gate);
            }
            try
            {
                if (!job.Cancelled) SampleRow(job, row);
            }
            catch (Exception e) { job.Error ??= e; }

            bool complete = false;
            lock (gate)
            {
                job.Active--;
                job.RowsDone++;
                if (job.Error != null) job.Cancelled = true;
                if (!job.Finalized && (job.Cancelled ? job.Active == 0 : job.RowsDone == job.Grid))
                {
                    Finalize_NoLock(job);
                    complete = true;
                }
            }
            if (complete) Complete(job);
        }
    }

    static void SampleRow(RegionJob job, int i)
    {
        // Stock: blockX = chunkStartX*chunkSize + (int)((float)j*num); blockZ = chunkStartZ*chunkSize + (int)((float)i*num);
        //        Points[i*gridSize+j] = GameMath.Max(height, seaLevel)
        var sample = job.S.Sample;
        int[] pts = job.Heightmap.Points;
        int grid = job.Grid, sea = job.SeaLevel, baseX = job.BaseX;
        float step = job.Step;
        int blockZ = job.BaseZ + (int)((float)i * step);
        int off = i * grid;
        for (int j = 0; j < grid; j++)
        {
            int h = sample(baseX + (int)((float)j * step), blockZ);
            pts[off + j] = GameMath.Max(h, sea);
        }
    }

    static void Finalize_NoLock(RegionJob job)
    {
        job.Finalized = true;
        jobs.Remove(job);
    }

    static void Complete(RegionJob job)
    {
        var s = job.S;
        if (job.Cancelled)
        {
            // Stock behaviour on cancellation/failure: forget the request so a later LoadRegion can start it again.
            s.InProgress.TryRemove(job.RegionIdx, out _);
            if (job.Error != null)
                s.Sapi.Logger.Error("[FarseerPerffix] sampling region {0} failed: {1}", job.RegionIdx, job.Error);
            return;
        }
        PerffixConfig.Stats.Completed++;
        if (s.IsDisposed) return;
        var hm = job.Heightmap;
        long idx = job.RegionIdx;
        s.Sapi.Event.EnqueueMainThreadTask(() =>
        {
            s.InProgress.TryRemove(idx, out _);
            if (!s.IsDisposed) (fRegionGenerated.GetValue(s.Adapter) as FarRegionGeneratedDelegate)?.Invoke(idx, hm);
        }, "farseer-terrain-sample");
    }
}
