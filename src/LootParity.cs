using System.Runtime.CompilerServices;
using dc;
using dc.en;
using dc.en.inter;
using dc.tool;
using dc.ui;

namespace LocalCoop
{
    /// <summary>
    /// Loot rules for two players.
    ///
    /// 1. Starting pipes (armory tubes): each player pulls their own switch, see Armory.cs.
    /// 2. World loot (weapons, skills, amulets, mutations, scrolls, food found behind doors, in chests,
    ///    dropped by elites...): when one player takes it, a copy appears on the spot that only the
    ///    partner can take.
    /// Shop purchases leave the partner a copy too (since v0.11.0), once paid for.
    /// Never copied: items a player drops or swaps out of their inventory, copies,
    /// keys/runes/boss cells/blueprints/outfits, money pickups.
    /// </summary>
    public partial class LocalCoopMod
    {
        private sealed class Reservation { public Hero? For; }
        private readonly PinnedTable<ItemDrop, Reservation> _reserved = new();
        private readonly PinnedTable<Entity, object> _noParity = new();
        private readonly PinnedTable<InventItem, object> _ownedItems = new();
        private readonly HashSet<int> _ownedIds = new();
        private int _dropDepth;
        private int _shopDepth;
        private int _pickDepth;
        private int _spawningClone;
        private int _parityCopies;

        private void InitLootParity()
        {
            Hook_Hero.pickItem += Hook_Hero_pickItem;
            Hook_ItemDrop.canBeActivated += Hook_ItemDrop_canBeActivated;
            Hook__ItemDrop.__constructor__ += Hook_ItemDrop_ctor;
            Hook_ShopBooth.onActivate += Hook_ShopBooth_onActivate;
            Hook_ShopBooth.buy += (orig, self, by, r) => { _shopDepth++; try { orig(self, by, r); } finally { _shopDepth--; } };
            Hook__ReplaceItem.__constructor__ += (o, s, a, b, c) => { _pickUisOpen++; o(s, a, b, c); };
            Hook_ReplaceItem.onDispose += (o, s) => { o(s); if (_pickUisOpen > 0) _pickUisOpen--; };
            Hook__ReplaceSingleItem.__constructor__ += (o, s, a, b, c, d, e) => { _pickUisOpen++; o(s, a, b, c, d, e); };
            Hook_ReplaceSingleItem.onDispose += (o, s) => { o(s); if (_pickUisOpen > 0) _pickUisOpen--; };
            Hook__ReplaceItemWithDual.__constructor__ += (o, s, a, b, c, d) => { _pickUisOpen++; o(s, a, b, c, d); };
            Hook_ReplaceItemWithDual.onDispose += (o, s) => { o(s); if (_pickUisOpen > 0) _pickUisOpen--; };
            Hook_RandomWeaponSpawner.spawnItem += Hook_RandomWeaponSpawner_spawnItem;
            Hook_Entity.canHaveRepellingWith += Hook_Entity_canHaveRepellingWith;
            Hook_Hero.dropAndUpdateItem += (orig, self, i) =>
            {
                _dropDepth++;
                try { return orig(self, i); } finally { _dropDepth--; }
            };
            InitCoopFixes();
            InitPlayerTurns();
            InitHud2Hooks();
            InitArmoryHooks();
        }

        private void UpdateLootParity(double dt) => UpdatePendingPicks();

        private bool IsOwnedId(int id) => id > 0 && _ownedIds.Contains(id);
        private void AddOwnedId(int id) { if (id > 0) _ownedIds.Add(id); }

        internal static int SafeId(InventItem? item)
        {
            try { return item?.permanentId ?? 0; } catch { return 0; }
        }

        internal static string ItemId(InventItem? item)
        {
            try { return item?._itemData?.id?.ToString() ?? ""; } catch { return ""; }
        }

        internal static bool IsParityItem(InventItem? item)
        {
            if (item?.kind == null) return false;
            if (item.kind is InventItemKind.Weapon or InventItemKind.Active or InventItemKind.Talisman or InventItemKind.Perk)
                return true;
            if (IsScrollId(ItemId(item))) return false;   // scrolls: both players get the upgrade, see CoopFixes.cs
            if (item.kind is InventItemKind.Consumable or InventItemKind.BagItem)
            {
                // Scrolls of power, scroll fragments and food are per player; keys and money are not.
                var id = ItemId(item);
                // FlaskRefill: the single vial left by the fountain on higher difficulties - one each.
                return id.EndsWith("Tier", StringComparison.Ordinal) || id == "QuarterUp" || id == "FlaskRefill"
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

        /// <summary>Tube items are handled by the co-op armory (Armory.cs), never by loot sharing.</summary>
        private ItemDrop Hook_RandomWeaponSpawner_spawnItem(Hook_RandomWeaponSpawner.orig_spawnItem orig, RandomWeaponSpawner self, bool inArmory)
        {
            var drop = orig(self, inArmory);
            if (drop != null) _noParity.AddOrUpdate(drop, this);
            return drop!;
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
            if (_shopDepth > 0 || _pickDepth > 0 || _p2UiDepth > 0 || _replaceUiDepth > 0 || _dropDepth > 0)
                _noParity.AddOrUpdate(self, this);
        }

        private bool Hook_ItemDrop_canBeActivated(Hook_ItemDrop.orig_canBeActivated orig, ItemDrop self, Hero by)
        {
            if (CoopActive && self.inArmory) return false;   // still locked in a tube: pull its switch
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
                    else if (from is ItemDrop d && (_reserved.TryGetValue(d, out _) || IsCopyDrop(d))) why = "reserved/copy";
                    // A purchase at a shop booth leaves the partner a copy once it is paid for (owner request,
                    // playtest 11); other items moving during a purchase (the buyer's swapped-out item) never do.
                    else if (_shopDepth > 0 && from is not ShopBooth) why = "dropped during a purchase";
                    else if (_noParity.TryGetValue(from, out _)) why = "shop/pipe/dropped";
                    else if (_ownedItems.TryGetValue(item, out _) || IsOwnedId(SafeId(item))) why = "already owned";
                    else
                    {
                        copy = item.clone(false, null);
                        lvl = from._level ?? self._level;
                        cx = from.cx; cy = from.cy; xr = from.xr; yr = from.yr;
                    }
                }
            }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Loot parity snapshot failed"); copy = null; }

            bool wasOwned = item != null && (_ownedItems.TryGetValue(item, out _) || IsOwnedId(SafeId(item)));
            if (item != null) { _ownedItems.AddOrUpdate(item, this); AddOwnedId(SafeId(item)); }
            if (from is ItemAltar) _lastAltarPicker = self;
            if (from != null) _pendingCopies.RemoveAll(q => CoopInput.Ptr(q.From) == CoopInput.Ptr(from));
            _pickDepth++;
            try { RunAsPicker(self, () => orig(self, from, item, onComplete)); }
            finally { _pickDepth--; }

            if (from == null || item == null) return;
            // The pick may still be undecided (replace-item screen, shop confirmation). The partner's copy, and
            // anything else that assumes the item was taken, waits until it really was (playtest 9: declining a
            // shop preview or a swap left a copy every time -> endless copies).
            var pending = new PendingPick
            {
                From = from, Item = item, Picker = self, Partner = partner, Copy = copy, Lvl = lvl,
                Cx = cx, Cy = cy, Xr = xr, Yr = yr, Since = Clock.Now, WasOwned = wasOwned, Why = why,
            };
            if (PickTaken(pending)) FinishPick(pending);
            else _pendingCopies.Add(pending);
        }

        private sealed class PendingPick
        {
            public Entity From = null!; public InventItem Item = null!; public Hero Picker = null!; public Hero? Partner;
            public InventItem? Copy; public dc.pr.Level? Lvl; public int Cx, Cy; public double Xr, Yr, Since;
            public bool WasOwned; public string Why = "";
        }
        private readonly List<PendingPick> _pendingCopies = new();
        private int _pickUisOpen;

        private bool PickTaken(PendingPick p)
        {
            try
            {
                if (p.From.destroyed) return true;
                if (p.From is ItemDrop d && CoopInput.Ptr(d.item) != CoopInput.Ptr(p.Item)) return true;
                if (p.From is ItemAltar a && !a.isEnabled) return true;
                if (p.From is ShopBooth b && CoopInput.Ptr(b.item) != CoopInput.Ptr(p.Item)) return true;
                return Owns(p.Picker, p.Item);
            }
            catch { return false; }
        }

        private void FinishPick(PendingPick p)
        {
            if (CoopActive && p.From is ItemDrop fromDrop && _reserved.TryGetValue(fromDrop, out _)) OnParityCopyPicked(p.From);
            if (p.Copy == null || p.Lvl == null || p.Partner == null || p.Partner.destroyed)
            {
                if (Config.Verbose && CoopActive)
                    Logger.Information("[LocalCoop] {Who} took {Item} (no copy: {Why})", IsP2(p.Picker) ? "P2" : "P1", ItemId(p.Item), p.Why);
                return;
            }
            var drop = SpawnPartnerCopy(p.Lvl, p.Cx, p.Cy, p.Xr, p.Yr, p.Copy, p.Partner,
                (IsP2(p.Picker) ? "P2" : "P1") + (p.From is ShopBooth ? " bought it" : " took it"));
            if (drop != null && p.From is ItemAltar altar) _altarCopies.AddOrUpdate(altar, drop);
        }

        private void UpdatePendingPicks()
        {
            for (int i = _pendingCopies.Count - 1; i >= 0; i--)
            {
                var p = _pendingCopies[i];
                if (PickTaken(p)) { _pendingCopies.RemoveAt(i); FinishPick(p); continue; }
                // Undecided while a pick menu is open; once all are closed and the item is still where it was, it was declined.
                if (_pickUisOpen > 0 || Clock.Now - p.Since < 0.5) continue;
                _pendingCopies.RemoveAt(i);
                if (!p.WasOwned) { _ownedItems.Remove(p.Item); _ownedIds.Remove(SafeId(p.Item)); }
                if (Config.Verbose) Logger.Information("[LocalCoop] {Who} put {Item} back (declined): no copy", IsP2(p.Picker) ? "P2" : "P1", ItemId(p.Item));
            }
        }

        /// <summary>Everything already in either inventory counts as owned: dropping it later never copies it.</summary>
        private void MarkInventoryOwned(Hero h)
        {
            try
            {
                var items = h.inventory?.items;
                if (items == null) return;
                foreach (var o in items) if (o is InventItem ii) { _ownedItems.AddOrUpdate(ii, this); AddOwnedId(SafeId(ii)); }
            }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Marking inventory failed"); }
        }
    }
}
