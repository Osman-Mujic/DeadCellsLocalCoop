using System.Runtime.CompilerServices;
using dc;
using dc.en;
using dc.en.inter;
using dc.tool;

namespace LocalCoop
{
    /// <summary>
    /// Loot rules for two players.
    ///
    /// 1. Starting pipes (the Random*Spawner tubes in the Prisoners' Quarters): each player gets their
    ///    OWN roll from every pipe, so the two can go for different builds. P1's items and P2's items sit
    ///    side by side and only their owner can take them.
    /// 2. World loot (weapons, skills, amulets, mutations, scrolls, food found behind doors, in chests,
    ///    dropped by elites...): when one player takes it, a copy appears on the spot that only the
    ///    partner can take.
    /// Never copied: shop purchases, items a player drops or swaps out of their inventory, copies,
    /// keys/runes/boss cells/blueprints/outfits, money pickups.
    /// </summary>
    public partial class LocalCoopMod
    {
        private sealed class Reservation { public Hero? For; }
        private readonly ConditionalWeakTable<ItemDrop, Reservation> _reserved = new();
        private readonly ConditionalWeakTable<Entity, object> _noParity = new();
        private readonly ConditionalWeakTable<InventItem, object> _ownedItems = new();
        private sealed class PipeInfo { public bool InArmory; public ItemDrop? P1Drop; public ItemDrop? P2Drop; }
        private readonly ConditionalWeakTable<RandomWeaponSpawner, PipeInfo> _pipes = new();
        private readonly List<WeakReference<RandomWeaponSpawner>> _pipeList = new();
        private int _shopDepth;
        private int _pickDepth;
        private int _spawningClone;
        private int _spawningP2Pipe;
        private int _parityCopies;

        private void InitLootParity()
        {
            Hook_Hero.pickItem += Hook_Hero_pickItem;
            Hook_ItemDrop.canBeActivated += Hook_ItemDrop_canBeActivated;
            Hook__ItemDrop.__constructor__ += Hook_ItemDrop_ctor;
            Hook_ShopBooth.onActivate += Hook_ShopBooth_onActivate;
            Hook_RandomWeaponSpawner.spawnItem += Hook_RandomWeaponSpawner_spawnItem;
            Hook_Entity.canHaveRepellingWith += Hook_Entity_canHaveRepellingWith;
            InitPlayerTurns();
            InitHud2Hooks();
        }

        private void UpdateLootParity(double dt) { }

        internal static string ItemId(InventItem? item)
        {
            try { return item?._itemData?.id?.ToString() ?? ""; } catch { return ""; }
        }

        internal static bool IsParityItem(InventItem? item)
        {
            if (item?.kind == null) return false;
            if (item.kind is InventItemKind.Weapon or InventItemKind.Active or InventItemKind.Talisman or InventItemKind.Perk)
                return true;
            if (item.kind is InventItemKind.Consumable or InventItemKind.BagItem)
            {
                // Scrolls of power, scroll fragments and food are per player; keys and money are not.
                var id = ItemId(item);
                return id.EndsWith("Tier", StringComparison.Ordinal) || id == "QuarterUp"
                    || id.Contains("Food", StringComparison.Ordinal) || id.Contains("Meat", StringComparison.Ordinal);
            }
            return false;
        }

        // ------------------------------------------------------------------ players never push each other

        private bool Hook_Entity_canHaveRepellingWith(Hook_Entity.orig_canHaveRepellingWith orig, Entity self, Entity other)
        {
            if (P2 != null && self is Hero && other is Hero
                && (ReferenceEquals(self, P2) || ReferenceEquals(other, P2)))
                return false;
            return orig(self, other);
        }

        // ------------------------------------------------------------------ starting pipes

        private ItemDrop Hook_RandomWeaponSpawner_spawnItem(Hook_RandomWeaponSpawner.orig_spawnItem orig, RandomWeaponSpawner self, bool inArmory)
        {
            var drop = orig(self, inArmory);
            if (drop == null) return drop!;
            _noParity.AddOrUpdate(drop, this);
            var info = _pipes.GetValue(self, _ => { _pipeList.Add(new WeakReference<RandomWeaponSpawner>(self)); return new PipeInfo(); });
            info.InArmory = inArmory;
            if (_spawningP2Pipe > 0) info.P2Drop = drop;
            else
            {
                info.P1Drop = drop;
                // Pipe refilled while co-op is running (recycling, armory): keep it P1's and give P2 theirs.
                if (CoopActive) { Reserve(drop, P1); SpawnP2Pipe(self, info); }
            }
            return drop;
        }

        /// <summary>Give P2 their own roll from every starting pipe in P2's current level.</summary>
        private void GiveP2StartingPipes()
        {
            if (P2 == null) return;
            var lvl = P2._level;
            int n = 0;
            _pipeList.RemoveAll(w => !w.TryGetTarget(out var s) || s.destroyed);
            foreach (var w in _pipeList.ToArray())
            {
                if (!w.TryGetTarget(out var spawner) || !ReferenceEquals(spawner._level, lvl)) continue;
                if (!_pipes.TryGetValue(spawner, out var info)) continue;
                if (info.P2Drop != null && !info.P2Drop.destroyed) continue;
                if (info.P1Drop != null && !info.P1Drop.destroyed) Reserve(info.P1Drop, P1);
                if (SpawnP2Pipe(spawner, info)) n++;
            }
            if (n > 0) Logger.Information("[LocalCoop] Starting pipes: rolled {N} items for P2", n);
        }

        private bool SpawnP2Pipe(RandomWeaponSpawner spawner, PipeInfo info)
        {
            _spawningP2Pipe++;
            try
            {
                var d = spawner.spawnItem(info.InArmory);
                if (d == null) return false;
                // Sit next to P1's item in the same tube so both are visible.
                double xr = spawner.xr >= 0.5 ? spawner.xr - 0.55 : spawner.xr + 0.55;
                d.setPosCase(spawner.cx, spawner.cy, xr, spawner.yr);
                Reserve(d, P2);
                if (Config.Verbose) Logger.Information("[LocalCoop] P2 pipe item: {Item}", d.item?.toString()?.ToString());
                return true;
            }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Rolling a pipe item for P2 failed"); return false; }
            finally { _spawningP2Pipe--; }
        }

        private void Reserve(ItemDrop d, Hero? who)
        {
            if (who != null) _reserved.AddOrUpdate(d, new Reservation { For = who });
        }

        // ------------------------------------------------------------------ world loot copies

        private void Hook_ShopBooth_onActivate(Hook_ShopBooth.orig_onActivate orig, ShopBooth self, Hero by, bool lp)
        {
            _shopDepth++;
            try { RunAsPicker(by, () => orig(self, by, lp)); }
            finally { _shopDepth--; }
        }

        private void Hook_ItemDrop_ctor(Hook__ItemDrop.orig___constructor__ orig, ItemDrop self, dc.pr.Level lvl, int x, int y,
            InventItem i, bool isNew, HaxeProxy.Runtime.Ref<bool> inArmory)
        {
            orig(self, lvl, x, y, i, isNew, inArmory);
            // Paid for in a shop, or a player's old item dropped while picking/swapping: never copied.
            if (_shopDepth > 0 || _pickDepth > 0 || _p2UiDepth > 0) _noParity.AddOrUpdate(self, this);
        }

        private bool Hook_ItemDrop_canBeActivated(Hook_ItemDrop.orig_canBeActivated orig, ItemDrop self, Hero by)
        {
            if (CoopActive && _reserved.TryGetValue(self, out var r) && r.For != null && !r.For.destroyed && by != null && !ReferenceEquals(r.For, by))
                return false;
            return orig(self, by);
        }

        private void Hook_Hero_pickItem(Hook_Hero.orig_pickItem orig, Hero self, Entity from, InventItem item,
            HaxeProxy.Runtime.HlAction<bool> onComplete)
        {
            // Snapshot before orig: picking may consume or destroy the source and mutate the item.
            InventItem? copy = null;
            int cx = 0, cy = 0;
            double xr = 0.5, yr = 1;
            dc.pr.Level? lvl = null;
            Hero? partner = null;
            string why = "";
            try
            {
                if (Config.LootParity && CoopActive && _spawningClone == 0 && from != null && item != null)
                {
                    partner = Partner(self);
                    if (partner == null || partner.destroyed) why = "no partner";
                    else if (!IsParityItem(item)) why = "not per-player";
                    else if (from is ItemDrop d && _reserved.TryGetValue(d, out _)) why = "reserved/copy";
                    else if (_noParity.TryGetValue(from, out _)) why = "shop/pipe/dropped";
                    else if (_ownedItems.TryGetValue(item, out _)) why = "already owned";
                    else
                    {
                        copy = item.clone(false, null);
                        lvl = from._level ?? self._level;
                        cx = from.cx; cy = from.cy; xr = from.xr; yr = from.yr;
                    }
                }
            }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Loot parity snapshot failed"); copy = null; }

            if (item != null) _ownedItems.AddOrUpdate(item, this);
            _pickDepth++;
            try { RunAsPicker(self, () => orig(self, from, item, onComplete)); }
            finally { _pickDepth--; }

            if (copy == null || lvl == null || partner == null)
            {
                if (Config.Verbose && CoopActive && item != null)
                    Logger.Information("[LocalCoop] {Who} took {Item} (no copy: {Why})", IsP2(self) ? "P2" : "P1", ItemId(item), why);
                return;
            }
            _spawningClone++;
            try
            {
                bool inArmory = false;
                var drop = new ItemDrop(lvl, cx, cy, copy, true, new HaxeProxy.Runtime.Ref<bool>(ref inArmory));
                drop.init();
                drop.setPosCase(cx, cy, xr, yr);
                Reserve(drop, partner);
                _noParity.AddOrUpdate(drop, this);
                _parityCopies++;
                Logger.Information("[LocalCoop] Loot parity: {Who} took {Item}; copy left for {Partner} (#{N})",
                    IsP2(self) ? "P2" : "P1", ItemId(item), IsP2(partner) ? "P2" : "P1", _parityCopies);
            }
            catch (Exception ex) { Logger.Error(ex, "[LocalCoop] Loot parity copy failed"); }
            finally { _spawningClone--; }
        }

        /// <summary>Everything already in either inventory counts as owned: dropping it later never copies it.</summary>
        private void MarkInventoryOwned(Hero h)
        {
            try
            {
                var items = h.inventory?.items;
                if (items == null) return;
                foreach (var o in items) if (o is InventItem ii) _ownedItems.AddOrUpdate(ii, this);
            }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Marking inventory failed"); }
        }
    }
}
