using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace AttributeRenderingLibrary
{
    /// <summary>
    /// Perf fast path for ARL's property postfixes (local build). This class is compiled separately and cloned into
    /// AttributeRenderingLibrary.dll by the patcher, so typeof(ArlFastPath).Assembly is ARL itself at runtime.
    ///
    /// For every block/item id it precomputes whether ARL's supplier lookup could ever find something:
    ///  - collectible path (GetCollectibleInterface): the class or one of its CollectibleBehaviors implements
    ///    ICollectiblePropertiesSupplier (base of IPropertiesSupplier / IBlockPropertiesSupplier), or the class
    ///    overrides GetCollectibleInterface.
    ///  - block-at-pos path (Block.GetInterface(world, pos)): the above, or a BlockBehavior implements it, or the class
    ///    overrides GetInterface (e.g. multiblock fillers), or the block has a block entity (BE / BE behaviours are only
    ///    known at runtime, so every block with an EntityClass always takes the original slow path).
    /// Anything unknown (tables not built yet, id out of range, other CollectibleObject subtypes) answers "maybe" and
    /// runs the original code, so the fast path can only skip work that would have found nothing.
    /// </summary>
    public static class ArlFastPath
    {
        private static bool[] s_blockColl;
        private static bool[] s_blockPos;
        private static bool[] s_itemColl;

        private static ICoreAPI s_serverApi;
        private static ICoreAPI s_clientApi;
        private static readonly object s_computeLock = new object();

        /// <summary>Called from Core.AssetsFinalize (both sides). Recomputed once more when the world is ready.</summary>
        public static void Compute(ICoreAPI api)
        {
            if (api == null) return;
            if (api.Side == EnumAppSide.Server)
            {
                if (s_serverApi != api)
                {
                    s_serverApi = api;
                    ((ICoreServerAPI)api).Event.SaveGameLoaded += new Action(RecomputeServer); // explicit delegate: no compiler cache class, so the type stays clonable
                }
            }
            else if (s_clientApi != api)
            {
                s_clientApi = api;
                ((ICoreClientAPI)api).Event.LevelFinalize += new Action(RecomputeClient);
            }
            Build(api);
        }

        private static void RecomputeServer() => Build(s_serverApi);

        private static void RecomputeClient() => Build(s_clientApi);

        private static void Build(ICoreAPI api)
        {
            IWorldAccessor world = api?.World;
            if (world == null) return;
            Type iface = typeof(ArlFastPath).Assembly.GetType("AttributeRenderingLibrary.ICollectiblePropertiesSupplier");
            if (iface == null) return; // never true for the ARL build; keeps the original slow path if it were

            var collOverrides = new Dictionary<Type, bool>();
            var posOverrides = new Dictionary<Type, bool>();
            IList<Block> blocks = world.Blocks;
            IList<Item> items = world.Items;

            int maxBlock = -1;
            for (int i = 0; i < blocks.Count; i++) if (blocks[i] != null && blocks[i].BlockId > maxBlock) maxBlock = blocks[i].BlockId;
            int maxItem = -1;
            for (int i = 0; i < items.Count; i++) if (items[i] != null && items[i].ItemId > maxItem) maxItem = items[i].ItemId;

            var blockColl = new bool[maxBlock + 1];
            var blockPos = new bool[maxBlock + 1];
            var itemColl = new bool[maxItem + 1];

            for (int i = 0; i < blocks.Count; i++)
            {
                Block block = blocks[i];
                if (block == null) continue;
                bool coll = CollectibleMay(block, iface, collOverrides);
                bool pos = coll
                    || AnyImplements(block.BlockBehaviors, iface)
                    || Overrides(block.GetType(), "GetInterface", typeof(Block), posOverrides)
                    || block.EntityClass != null;
                blockColl[block.BlockId] |= coll;
                blockPos[block.BlockId] |= pos;
            }
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (item == null) continue;
                itemColl[item.ItemId] |= CollectibleMay(item, iface, collOverrides);
            }

            // In singleplayer the client and server share these statics: merge (OR) so neither side loses a "maybe".
            lock (s_computeLock)
            {
                s_blockColl = Merge(s_blockColl, blockColl);
                s_blockPos = Merge(s_blockPos, blockPos);
                s_itemColl = Merge(s_itemColl, itemColl);
            }
        }

        private static bool[] Merge(bool[] old, bool[] add)
        {
            if (old == null) return add;
            var merged = new bool[Math.Max(old.Length, add.Length)];
            for (int i = 0; i < merged.Length; i++)
            {
                // ids that only one side knows stay "maybe" (true) until both sides agree
                bool a = i < old.Length ? old[i] : true;
                bool b = i < add.Length ? add[i] : true;
                merged[i] = a || b;
            }
            return merged;
        }

        private static bool CollectibleMay(CollectibleObject obj, Type iface, Dictionary<Type, bool> collOverrides)
        {
            return iface.IsAssignableFrom(obj.GetType())
                || AnyImplements(obj.CollectibleBehaviors, iface)
                || Overrides(obj.GetType(), "GetCollectibleInterface", typeof(CollectibleObject), collOverrides);
        }

        private static bool AnyImplements(object[] behaviors, Type iface)
        {
            if (behaviors == null) return false;
            for (int i = 0; i < behaviors.Length; i++)
            {
                if (behaviors[i] != null && iface.IsAssignableFrom(behaviors[i].GetType())) return true;
            }
            return false;
        }

        private static bool Overrides(Type type, string name, Type baseType, Dictionary<Type, bool> cache)
        {
            bool result;
            if (cache.TryGetValue(type, out result)) return result;
            MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name == name && m.DeclaringType != baseType && m.IsVirtual && m.GetBaseDefinition().DeclaringType == baseType)
                {
                    result = true;
                    break;
                }
            }
            cache[type] = result;
            return result;
        }

        /// <summary>Fast path for postfixes that call GetCollectibleInterface on <paramref name="obj"/>.</summary>
        public static bool MayHaveSupplier(CollectibleObject obj)
        {
            bool[] table;
            int id;
            Block block = obj as Block;
            if (block != null)
            {
                table = s_blockColl;
                id = block.BlockId;
            }
            else
            {
                Item item = obj as Item;
                if (item == null) return true;
                table = s_itemColl;
                id = item.ItemId;
            }
            if (table == null || (uint)id >= (uint)table.Length) return true;
            return table[id];
        }

        /// <summary>Fast path for postfixes that call Block.GetInterface(world, pos) on <paramref name="obj"/>.</summary>
        public static bool MayHaveSupplierAtPos(CollectibleObject obj)
        {
            Block block = obj as Block;
            if (block == null) return true;
            bool[] table = s_blockPos;
            int id = block.BlockId;
            if (table == null || (uint)id >= (uint)table.Length) return true;
            return table[id];
        }

        /// <summary>
        /// Fast path for GetBlockMaterial / GetLightHsv. Mirrors the original control flow: with a stack, the postfix
        /// returns early unless the stack's collectible has a supplier; without a stack it only does the pos lookup.
        /// </summary>
        public static bool MayHaveSupplierStackOrPos(CollectibleObject obj, BlockPos pos, ItemStack stack)
        {
            if (stack != null) return MayHaveSupplier(stack.Collectible);
            if (pos == null) return false;
            return MayHaveSupplierAtPos(obj);
        }
    }
}
