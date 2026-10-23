using System.Runtime.CompilerServices;
using dc;
using dc.en;
using dc.en.inter;
using dc.tool;

namespace LocalCoop
{
    /// <summary>
    /// "Everything one player can take, the other can take too."
    ///
    /// When a hero picks up a per-player item (weapon, skill, amulet, mutation, scroll, food...)
    /// from the world, a copy is dropped on the same spot that only the partner can pick up.
    /// Items bought in a shop are not duplicated (each player pays for their own), and copies
    /// are never copied again.
    /// </summary>
    public partial class LocalCoopMod
    {
        private sealed class Reservation { public Hero? For; }
        private readonly ConditionalWeakTable<ItemDrop, Reservation> _reserved = new();
        private readonly ConditionalWeakTable<Entity, object> _noParity = new();
        private int _shopDepth;
        private int _spawningClone;
        private int _parityCopies;

        private void InitLootParity()
        {
            Hook_Hero.pickItem += Hook_Hero_pickItem;
            Hook_ItemDrop.canBeActivated += Hook_ItemDrop_canBeActivated;
            Hook__ItemDrop.__constructor__ += Hook_ItemDrop_ctor;
            Hook_ShopBooth.onActivate += Hook_ShopBooth_onActivate;
            InitPlayerTurns();
        }

        private void UpdateLootParity(double dt) { }

        internal static bool IsParityKind(InventItem? item)
        {
            if (item?.kind == null) return false;
            // Keys, runes, boss cells, blueprints, skins and outfits stay single.
            return item.kind is InventItemKind.Weapon or InventItemKind.Active or InventItemKind.Talisman
                or InventItemKind.Perk or InventItemKind.Consumable or InventItemKind.BagItem;
        }

        private void Hook_ShopBooth_onActivate(Hook_ShopBooth.orig_onActivate orig, ShopBooth self, Hero by, bool lp)
        {
            _shopDepth++;
            try { orig(self, by, lp); }
            finally { _shopDepth--; }
        }

        private void Hook_ItemDrop_ctor(Hook__ItemDrop.orig___constructor__ orig, ItemDrop self, dc.pr.Level lvl, int x, int y,
            InventItem i, bool isNew, HaxeProxy.Runtime.Ref<bool> inArmory)
        {
            orig(self, lvl, x, y, i, isNew, inArmory);
            if (_shopDepth > 0) _noParity.AddOrUpdate(self, this);   // paid for: no free copy
        }

        private bool Hook_ItemDrop_canBeActivated(Hook_ItemDrop.orig_canBeActivated orig, ItemDrop self, Hero by)
        {
            if (_reserved.TryGetValue(self, out var r) && r.For != null && !r.For.destroyed && by != null && !ReferenceEquals(r.For, by))
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
            try
            {
                if (Config.LootParity && CoopActive && _spawningClone == 0 && from != null && IsParityKind(item))
                {
                    partner = Partner(self);
                    bool fromClone = from is ItemDrop d && _reserved.TryGetValue(d, out _);
                    bool paid = _noParity.TryGetValue(from, out _);
                    if (partner != null && !partner.destroyed && !fromClone && !paid)
                    {
                        copy = item.clone(false, null);
                        lvl = from._level ?? self._level;
                        cx = from.cx; cy = from.cy; xr = from.xr; yr = from.yr;
                    }
                }
            }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Loot parity snapshot failed"); copy = null; }

            RunAsPicker(self, () => orig(self, from, item, onComplete));

            if (copy == null || lvl == null || partner == null) return;
            _spawningClone++;
            try
            {
                bool inArmory = false;
                var drop = new ItemDrop(lvl, cx, cy, copy, true, new HaxeProxy.Runtime.Ref<bool>(ref inArmory));
                drop.init();
                drop.setPosCase(cx, cy, xr, yr);
                _reserved.AddOrUpdate(drop, new Reservation { For = partner });
                _parityCopies++;
                Logger.Information("[LocalCoop] Loot parity: {Who} took {Item}; copy left for {Partner} (#{N})",
                    IsP2(self) ? "P2" : "P1", item.toString()?.ToString(), IsP2(partner) ? "P2" : "P1", _parityCopies);
            }
            catch (Exception ex) { Logger.Error(ex, "[LocalCoop] Loot parity copy failed"); }
            finally { _spawningClone--; }
        }
    }
}
