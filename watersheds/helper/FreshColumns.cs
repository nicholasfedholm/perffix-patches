using System.Collections.Concurrent;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Watersheds.Patches
{
    /// <summary>
    /// Perf fix (local build): remembers which chunk columns were generated in this session, so
    /// PartiallyLitChunkFixPatch.OnChunkColumnLoaded only runs its lighting check (and the topChunk.Unpack() it needs)
    /// for freshly generated columns. Columns loaded from disk were already checked (and relit if needed) when they
    /// were generated, so they skip the Unpack(), which decompressed the top chunk of every loaded column and marked it
    /// as changed. Cloned into Watersheds.dll by the patcher.
    /// </summary>
    public static class PartiallyLitFreshColumns
    {
        // Safety valve: columns that finish generation are loaded right after, so the set stays tiny; if something
        // unusual ever generates without loading, stop it from growing forever.
        private const int MaxTracked = 65536;

        private static ConcurrentDictionary<long, byte> s_fresh = new ConcurrentDictionary<long, byte>();

        private static long Key(int chunkX, int chunkZ) => ((long)chunkX << 32) | (uint)chunkZ;

        /// <summary>Called at the end of PartiallyLitChunkFixPatch.Initialize. Watersheds only generates "standard" worlds.</summary>
        public static void Register(ICoreServerAPI api)
        {
            s_fresh.Clear();
            api.Event.ChunkColumnGeneration(new ChunkColumnGenerationDelegate(OnColumnGenerated), EnumWorldGenPass.PreDone, "standard");
        }

        private static void OnColumnGenerated(IChunkColumnGenerateRequest request)
        {
            if (s_fresh.Count >= MaxTracked) s_fresh.Clear();
            s_fresh.TryAdd(Key(request.ChunkX, request.ChunkZ), 0);
        }

        /// <summary>True (once) if this column was generated in this session; false for columns loaded from disk.</summary>
        public static bool ConsumeIsFresh(Vec2i chunkCoord)
        {
            if (chunkCoord == null) return true;
            byte unused;
            return s_fresh.TryRemove(Key(chunkCoord.X, chunkCoord.Y), out unused);
        }

        public static void Clear() => s_fresh.Clear();
    }
}
