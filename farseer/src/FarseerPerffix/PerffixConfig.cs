using Vintagestory.API.Server;

namespace FarseerPerffix;

/// <summary>
/// Optional ModConfig/farseer-perffix.json (read only, never written by the mod). Missing file = defaults.
/// Env FARSEER_PERFFIX_THREADS / _PRIORITYQUEUE / _CANCELSTALE override (bench tuning).
/// </summary>
public class PerffixConfig
{
    /// <summary>Global budget of concurrent sample calls. Default: logical cores - 4 (headroom for client/server main threads), max 12.</summary>
    public int SamplerThreads = Math.Clamp(Environment.ProcessorCount - 4, 1, 12);
    public ThreadPriority ThreadPriority = ThreadPriority.BelowNormal;
    public bool FastDelegate = true;
    public bool PriorityQueue = true;
    public bool CancelStale = true;

    public static PerffixConfig Current = new();
    static bool loaded;

    public static class Stats
    {
        public static long Completed, Cancelled;
    }

    public static void Load(ICoreServerAPI sapi)
    {
        if (loaded) return;
        loaded = true;
        try
        {
            var c = sapi.LoadModConfig<PerffixConfig>("farseer-perffix.json");
            if (c != null) Current = c;
        }
        catch (Exception e) { sapi.Logger.Warning("[FarseerPerffix] bad farseer-perffix.json, using defaults: {0}", e.Message); }
        if (int.TryParse(Environment.GetEnvironmentVariable("FARSEER_PERFFIX_THREADS"), out var n)) Current.SamplerThreads = n;
        if (bool.TryParse(Environment.GetEnvironmentVariable("FARSEER_PERFFIX_PRIORITYQUEUE"), out var pq)) Current.PriorityQueue = pq;
        if (bool.TryParse(Environment.GetEnvironmentVariable("FARSEER_PERFFIX_CANCELSTALE"), out var cs)) Current.CancelStale = cs;
        Current.SamplerThreads = Math.Clamp(Current.SamplerThreads, 1, 64);
    }
}
