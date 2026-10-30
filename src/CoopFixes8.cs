using System.Runtime.CompilerServices;
using dc;
using dc.en;
using dc.en.inter;
using dc.en.pet;
using dc.pow;
using dc.pr;
using dc.tool;
using dc.ui;
using dc.ui.pause;

namespace LocalCoop
{
    /// <summary>
    /// Fixes from playtest 8: P2's pause menu, the scythe ghosts, the Diverse Deck, doubled kills, selling loot,
    /// reward-room altars (each player chooses), and the HUD life bars overlapping.
    /// </summary>
    public partial class LocalCoopMod
    {
        private void InitCoopFixes8()
        {
            // P2's pause menu (inventory): built as P2, driven by P2's pad.
            Hook__DefaultPause.__constructor__ += (orig, self) =>
                OpenUi(self, null, () => orig(self), ui => ui.controller = HandOver(ui.controller));
            Hook_DefaultPause.update += (orig, self) => AsOwner(self, () => orig(self));
            Hook_DefaultPause.postUpdate += (orig, self) => AsOwner(self, () => orig(self));
            Hook_DefaultPause.onDispose += (orig, self) => AsOwner(self, () => orig(self));
            Hook_DefaultPause.onResize += (orig, self) => AsOwner(self, () => orig(self));
            Hook_DefaultPause.startSwapBackpack += (orig, self) => AsOwner(self, () => orig(self));
            Hook_DefaultPause.onLeavingOptionsMenu += (orig, self) => AsOwner(self, () => orig(self));

            // Scythe ghosts follow game.hero (the spawn callback reads it): give them to the weapon's owner.
            Hook__AdeleScytheGhost.__constructor__ += Hook_AdeleScytheGhost_ctor;

            // Diverse Deck reads game.hero (transforms "the hero's" deck, places effects on it).
            Hook_DiverseDeckBase.doActive += (orig, self) => AsPowerOwner(self, () => orig(self));
            Hook_DiverseDeckBase.fixedUpdate += (orig, self) => AsPowerOwner(self, () => orig(self));
            Hook_DiverseDeckCatalyst.doActive += (orig, self) => AsPowerOwner(self, () => orig(self));
            Hook_DiverseDeckElectro.doActive += (orig, self) => AsPowerOwner(self, () => orig(self));
            Hook_DiverseDeckJuggernaut.doActive += (orig, self) => AsPowerOwner(self, () => orig(self));
            Hook_DiverseDeckWatcher.doActive += (orig, self) => AsPowerOwner(self, () => orig(self));

            // Every living hero is told about each mob death (Mob.notifyHeroOfDeath f@1062): count it once.
            Hook_Hero.addKillCount += Hook_Hero_addKillCount;

            // Selling loot on the ground leaves the partner their own copy, like picking it up does.
            Hook_ItemDrop.recycle += Hook_ItemDrop_recycle;

            // Reward-room altars: each player picks their own.
            Hook_ItemAltar.disable += Hook_ItemAltar_disable;
            Hook_ItemAltar.canBeActivated += Hook_ItemAltar_canBeActivated;
            Hook_ItemAltar.onActivate += (orig, self, by, lp) =>
            {
                _lastAltarPicker = by;
                orig(self, by, lp);
            };
        }

        // ------------------------------------------------------------------ P2 pause (Start on P2's pad)

        private const int PadStart = 5;
        private bool _p2StartWas;

        private void UpdateP2Pause(Hero p2)
        {
            bool down = CoopInput.PadDown(_input.P2Pad, PadStart);
            bool press = down && !_p2StartWas;
            _p2StartWas = down;
            if (!press) return;
            var game = Game.Class.ME;
            if (game == null || game.paused || _p2Down.Active || p2.destroyed || p2._level == null) return;
            try
            {
                if (p2.controlsLocked(default)) return;
                WithHero(p2, () => { _ = new DefaultPause(); });
                Logger.Information("[LocalCoop] P2 opened the pause menu");
            }
            catch (Exception ex) { Logger.Error(ex, "[LocalCoop] Opening P2's pause menu failed"); }
        }

        // ------------------------------------------------------------------ scythe ghosts

        private void Hook_AdeleScytheGhost_ctor(Hook__AdeleScytheGhost.orig___constructor__ orig, AdeleScytheGhost self, Level lvl,
            int x, int y, int dmgTier, int lifeTier, Entity parent, InventItem item, dc.tool.weap.SprData spr)
        {
            var p2 = P2;
            if (CoopActive && p2 != null && item != null && !IsP2(parent as Hero) && Owns(p2, item))
            {
                parent = p2;
                try { int t = p2.getRelevantTierFor(item); dmgTier = t; lifeTier = t; } catch { }
            }
            orig(self, lvl, x, y, dmgTier, lifeTier, parent, item, spr);
        }

        private static bool Owns(Hero h, InventItem item)
        {
            try
            {
                var p = CoopInput.Ptr(item);
                var items = h.inventory?.items;
                if (items == null) return false;
                foreach (var o in items) if (CoopInput.Ptr(o) == p) return true;
            }
            catch { }
            return false;
        }

        // ------------------------------------------------------------------ powers

        private void AsPowerOwner(Power p, Action act)
        {
            Hero? owner = null;
            try { owner = p.owner as Hero; } catch { }
            if (owner != null && IsP2(owner) && !ReferenceEquals(Game.Class.ME?.hero, owner)) WithHero(owner, act);
            else act();
        }

        // ------------------------------------------------------------------ kills

        private sealed class KillMark { public IntPtr Hero; }
        private readonly PinnedTable<Mob, KillMark> _killCounted = new();

        private void Hook_Hero_addKillCount(Hook_Hero.orig_addKillCount orig, Hero self, Mob m)
        {
            if (CoopActive && m != null)
            {
                var me = CoopInput.Ptr(self);
                if (_killCounted.TryGetValue(m, out var k) && k.Hero != me) return;   // the partner already counted it
                if (k == null) _killCounted.AddOrUpdate(m, new KillMark { Hero = me });
            }
            orig(self, m);
        }

        // ------------------------------------------------------------------ selling ground loot

        private void Hook_ItemDrop_recycle(Hook_ItemDrop.orig_recycle orig, ItemDrop self, Hero by)
        {
            InventItem? copy = null;
            Hero? partner = null;
            Level? lvl = null;
            int cx = self.cx, cy = self.cy;
            double xr = self.xr, yr = self.yr;
            try
            {
                var item = self.item;
                if (Config.LootParity && CoopActive && by != null && item != null && IsParityItem(item)
                    && !_reserved.TryGetValue(self, out _) && !IsCopyDrop(self) && !_noParity.TryGetValue(self, out _)
                    && !_ownedItems.TryGetValue(item, out _) && !IsOwnedId(SafeId(item)))
                {
                    partner = Partner(by);
                    if (partner != null && !partner.destroyed) { copy = item.clone(false, null); lvl = self._level; }
                }
            }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Sell snapshot failed"); copy = null; }

            if (by != null) RunAsPicker(by, () => orig(self, by)); else orig(self, by!);

            if (copy == null || lvl == null || partner == null || !self.destroyed) return;
            SpawnPartnerCopy(lvl, cx, cy, xr, yr, copy, partner, "sold");
        }

        private ItemDrop? SpawnPartnerCopy(Level lvl, int cx, int cy, double xr, double yr, InventItem copy, Hero partner, string why)
        {
            _spawningClone++;
            try
            {
                bool inArmory = false;
                var drop = new ItemDrop(lvl, cx, cy, copy, true, new HaxeProxy.Runtime.Ref<bool>(ref inArmory));
                drop.init();
                drop.setPosCase(cx, cy, xr, yr);
                Reserve(drop, partner);
                _noParity.AddOrUpdate(drop, this);
                MarkCopyDrop(drop);
                _parityCopies++;
                Logger.Information("[LocalCoop] Loot parity ({Why}): {Item} left for {Partner} (#{N})", why, ItemId(copy), IsP2(partner) ? "P2" : "P1", _parityCopies);
                return drop;
            }
            catch (Exception ex) { Logger.Error(ex, "[LocalCoop] Loot parity copy failed"); return null; }
            finally { _spawningClone--; }
        }

        // ------------------------------------------------------------------ reward-room altars
        // Altars in one row are linked: picking one disables the rest (ItemAltar.disable f@16519 walks the room).
        // In co-op the first pick leaves the others standing for the partner only; the partner then chooses one of
        // them, or the copy of the first player's pick (loot parity). The partner's pick clears the rest.

        private sealed class AltarGroup
        {
            public Hero? For;
            public readonly List<ItemAltar> Remaining = new();
            public ItemAltar? FirstPicked;
            public bool Done;
        }

        private readonly PinnedTable<ItemAltar, AltarGroup> _altarGroups = new();
        private readonly List<AltarGroup> _openAltarGroups = new();
        private readonly PinnedTable<ItemAltar, ItemDrop> _altarCopies = new();
        private AltarGroup? _collectingGroup;
        private Hero? _lastAltarPicker;

        private void Hook_ItemAltar_disable(Hook_ItemAltar.orig_disable orig, ItemAltar self, bool isPicked)
        {
            if (!CoopActive) { orig(self, isPicked); return; }

            // Inside a first pick: the linked altars are kept for the partner instead of being switched off.
            if (_collectingGroup != null && !isPicked)
            {
                if (!_altarGroups.TryGetValue(self, out _)) _collectingGroup.Remaining.Add(self);
                return;
            }

            _altarGroups.TryGetValue(self, out var existing);
            if (isPicked && existing == null)
            {
                var picker = _lastAltarPicker;
                var g = new AltarGroup { For = picker != null ? Partner(picker) : null, FirstPicked = self };
                _collectingGroup = g;
                try { orig(self, isPicked); }
                finally { _collectingGroup = null; }
                if (g.For == null || g.Remaining.Count == 0)
                {
                    foreach (var a in g.Remaining) try { a.disable(false); } catch { }
                    return;
                }
                foreach (var a in g.Remaining) { _altarGroups.AddOrUpdate(a, g); _noParity.AddOrUpdate(a, this); }
                _openAltarGroups.Add(g);
                Logger.Information("[LocalCoop] Altar pick by {Who}: {N} other altar(s) left for {Partner}",
                    IsP2(picker) ? "P2" : "P1", g.Remaining.Count, IsP2(g.For) ? "P2" : "P1");
                return;
            }

            // The partner picked one of the kept altars: the group is finished.
            if (isPicked && existing != null && !existing.Done)
            {
                orig(self, isPicked);   // disables the other kept altars of the row (normal behaviour)
                FinishAltarGroup(existing, "partner chose an altar");
                return;
            }
            orig(self, isPicked);
        }

        private bool Hook_ItemAltar_canBeActivated(Hook_ItemAltar.orig_canBeActivated orig, ItemAltar self, Hero by)
        {
            if (CoopActive && _altarGroups.TryGetValue(self, out var g) && !g.Done && g.For != null && by != null
                && !ReferenceEquals(g.For, by))
                return false;
            return orig(self, by);
        }

        /// <summary>The partner took the copy of the first pick instead of an altar.</summary>
        private void OnParityCopyPicked(Entity from)
        {
            foreach (var g in _openAltarGroups.ToArray())
            {
                if (g.Done || g.FirstPicked == null || !_altarCopies.TryGetValue(g.FirstPicked, out var drop)) continue;
                if (CoopInput.Ptr(drop) != CoopInput.Ptr(from)) continue;
                foreach (var a in g.Remaining) try { if (!a.destroyed && a.isEnabled) a.disable(false); } catch { }
                FinishAltarGroup(g, "partner took the same item");
            }
        }

        private void FinishAltarGroup(AltarGroup g, string why)
        {
            g.Done = true;
            _openAltarGroups.Remove(g);
            // The copy of the first pick is no longer on offer.
            if (g.FirstPicked != null && _altarCopies.TryGetValue(g.FirstPicked, out var drop))
            {
                try { if (!drop.destroyed) drop.destroy(); } catch { }
                _altarCopies.Remove(g.FirstPicked);
            }
            Logger.Information("[LocalCoop] Altar group closed ({Why})", why);
        }

        // ------------------------------------------------------------------ life bars
        // The life bar widens with max life (HUD.updateLifeSize f@5325, 217..350 px at 1x, from 14 call sites, and it
        // reads game.hero's items even for P2's HUD); two wide bars overlapped in the middle of the screen, and P2's
        // right-aligned HUD slid left as its bar grew (playtests 8 and 10). In co-op both bars keep one fixed width
        // (the game's own smallest, ratio 1.0): the HUDs never move. The numbers still show the real life.

        private readonly Dictionary<IntPtr, int> _fixedLifeWid = new();

        private void UpdateLifeSizeCoop(Hook_HUD.orig_updateLifeSize o, HUD h, double? m)
        {
            if (!Hud2Ok) { o(h, m); return; }
            o(h, 1.0);
            try
            {
                var st = h.heroLifeBar?.curState;
                if (st != null) _fixedLifeWid[CoopInput.Ptr(h)] = st.outerWid;
            }
            catch { }
        }

        /// <summary>Per frame: if anything resized a life bar behind our back, put it back to the fixed width.</summary>
        private void KeepLifeBarFixed(HUD? h)
        {
            if (h == null || h.destroyed || !Hud2Ok) return;
            try
            {
                var st = h.heroLifeBar?.curState;
                if (st == null) return;
                if (_fixedLifeWid.TryGetValue(CoopInput.Ptr(h), out int want) && st.outerWid == want) return;
                AsHudOwner(h, () => h.updateLifeSize(1.0));
            }
            catch (Exception ex) { if (_hud2Errors.Add("lifewid:" + ex.Message)) Logger.Warning(ex, "[LocalCoop] Life bar width fix failed"); }
        }
    }
}
