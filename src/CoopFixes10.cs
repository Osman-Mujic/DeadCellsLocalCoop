using dc;
using dc.cine;
using dc.en;
using dc.en.inter;
using dc.en.ltrap;
using dc.en.mob.boss.gardener;
using dc.pr;
using dc.tool;

namespace LocalCoop
{
    /// <summary>
    /// Fixes from playtest 10 (v0.9.0, a run from the Prisoners' Quarters to the Lighthouse):
    /// boss arenas that start with an entry cinematic, selling from a legendary pedestal or a timed-door item,
    /// Mausoleum mushrooms, Lighthouse ropes (pulleys), a controller switching off, and copies of copies.
    /// (Foresight: CoopFixes9.cs. Life bars: CoopFixes8.cs. Outfits: PadPickerAndStyle.cs. Downed: Downed.cs.
    /// Lost per-object marks: Pinned.cs.)
    /// </summary>
    public partial class LocalCoopMod
    {
        private void InitCoopFixes10()
        {
            // ---- Boss arenas (bug 5). Most bosses start with an entry cinematic that gets the hero who walked in;
            // only some of them close a BossDoor (handled in CoopFixes9). The Death arena (EnterRoomDeathBoss) left
            // P2 outside. Every boss entry now brings the partner to the player who triggered it.
            Hook__EnterRoomBoss.__constructor__ += (o, s, h) => { o(s, h); RegroupForBoss(h, "EnterRoomBoss"); };
            Hook__EnterRoomDeathBoss.__constructor__ += (o, s, h) => { o(s, h); RegroupForBoss(h, "EnterRoomDeathBoss"); };
            Hook__EnterDeathArena.__constructor__ += (o, s, h) => { o(s, h); RegroupForBoss(h, "EnterDeathArena"); };
            Hook__EnterRoomGardenerBoss.__constructor__ += (o, s, h) => { o(s, h); RegroupForBoss(h, "EnterRoomGardenerBoss"); };
            Hook__EnterRoomMedusa.__constructor__ += (o, s, h, m) => { o(s, h, m); RegroupForBoss(h, "EnterRoomMedusa"); };
            Hook__EnterDookuBossRoom.__constructor__ += (o, s, h) => { o(s, h); RegroupForBoss(h, "EnterDookuBossRoom"); };
            Hook__EnterDualBehemoth.__constructor__ += (o, s, h) => { o(s, h); RegroupForBoss(h, "EnterDualBehemoth"); };
            Hook__EnterGiantRoom.__constructor__ += (o, s, h) => { o(s, h); RegroupForBoss(h, "EnterGiantRoom"); };
            Hook__EnterModifiedGiantRoom.__constructor__ += (o, s, h) => { o(s, h); RegroupForBoss(h, "EnterModifiedGiantRoom"); };
            Hook__EnterThroneRoom.__constructor__ += (o, s, h) => { o(s, h); RegroupForBoss(h, "EnterThroneRoom"); };
            Hook__EnterThroneRoomAsKing.__constructor__ += (o, s, h) => { o(s, h); RegroupForBoss(h, "EnterThroneRoomAsKing"); };
            Hook__EnterThroneBossRush.__constructor__ += (o, s, h) => { o(s, h); RegroupForBoss(h, "EnterThroneBossRush"); };
            Hook__EnterTimeKeeperRoomFirst.__constructor__ += (o, s, h) => { o(s, h); RegroupForBoss(h, "EnterTimeKeeperRoomFirst"); };
            Hook__StartAmazonBrutalBossFight.__constructor__ += (o, s, h) => { o(s, h); RegroupForBoss(h, "StartAmazonBrutalBossFight"); };
            Hook__StartAmazonSurvivalBossFight.__constructor__ += (o, s, h) => { o(s, h); RegroupForBoss(h, "StartAmazonSurvivalBossFight"); };
            Hook__StartAmazonTacticBossFight.__constructor__ += (o, s, h) => { o(s, h); RegroupForBoss(h, "StartAmazonTacticBossFight"); };
            Hook__StartCollectorFight.__constructor__ += (o, s, h) => { o(s, h); RegroupForBoss(h, "StartCollectorFight"); };
            Hook__StartCollectorFightAlt.__constructor__ += (o, s, h) => { o(s, h); RegroupForBoss(h, "StartCollectorFightAlt"); };
            dc.cine.queen.Hook__EnterRoomQueenBoss.__constructor__ += (o, s) => { o(s); RegroupForBoss(null, "EnterRoomQueenBoss"); };

            // ---- Selling from a legendary pedestal or a timed-door item (bug 6): these sell from their own
            // onActivate (LegendaryAltar f@19020, TimedItem f@24882), not ItemDrop.recycle, so no copy was left.
            Hook_LegendaryAltar.onActivate += (o, s, by, lp) => SellFromStand(s, () => s.item, by, lp, () => o(s, by, lp));
            Hook_TimedItem.onActivate += (o, s, by, lp) => SellFromStand(s, () => s.item, by, lp, () => o(s, by, lp));

            // ---- Mausoleum mushrooms (bug 7): Mushroom.fixedUpdate (f@9495) only checks whether game.hero lands on
            // it, and bounce() launches game.hero. P2 gets the same check.
            Hook_Mushroom.fixedUpdate += Hook_Mushroom_fixedUpdate;

            // ---- Lighthouse ropes (bug 9): hitting a pulley's attach point runs Pulley.trigger_execute (f@7973),
            // which puts game.hero on the rope (setPosPixel + lockControls) - P2's hit pulled P1 onto it - and
            // Pulley.fixedUpdate keeps carrying game.hero. The pulley now belongs to whoever triggered it.
            Hook_PulleyAttachPoint.trigger += (o, s, by) => { NotePulleyRider(s, by); AsEntityHero(by, () => o(s, by)); };
            Hook_PulleyAttachPoint.execute += (o, s, by) => { NotePulleyRider(s, by); AsEntityHero(by, () => o(s, by)); };
            Hook_Pulley.trigger_execute += Hook_Pulley_trigger_execute;
            Hook_Pulley.fixedUpdate += (o, s) => AsPulleyRider(s, () => o(s));
            Hook_Pulley.postUpdate += (o, s) => AsPulleyRider(s, () => o(s));
        }

        // ------------------------------------------------------------------ copies of copies (bug 3)
        // Belt and braces next to the pinned tables: the game object address of every partner copy. HashLink's GC
        // doesn't move objects; the set is cleared at each level change.

        private readonly HashSet<IntPtr> _copyDrops = new();

        private void MarkCopyDrop(ItemDrop d) { var p = CoopInput.Ptr(d); if (p != IntPtr.Zero) _copyDrops.Add(p); }
        private bool IsCopyDrop(ItemDrop d) { var p = CoopInput.Ptr(d); return p != IntPtr.Zero && _copyDrops.Contains(p); }
        private void ClearCopyDrops() => _copyDrops.Clear();

        // ------------------------------------------------------------------ boss arenas (bug 5)

        private Hero? _bossAnchor;
        private double _bossRecheckAt;

        private void RegroupForBoss(Hero? trigger, string what)
        {
            if (!CoopActive) return;
            try
            {
                var anchor = trigger != null && (IsP2(trigger) || ReferenceEquals(trigger, P1)) ? trigger
                    : (_lastTriggerHero != null && !_lastTriggerHero.destroyed ? _lastTriggerHero : P1);
                if (anchor == null) return;
                BringPartnerTo(anchor, 3, $"boss entry ({what})");
                // The entry cinematic may still walk the hero into the arena: check again shortly.
                _bossAnchor = anchor;
                _bossRecheckAt = Clock.Now + 1.5;
            }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Boss entry regroup failed ({What})", what); }
        }

        private void UpdateBossRegroup()
        {
            if (_bossAnchor == null || Clock.Now < _bossRecheckAt) return;
            var a = _bossAnchor;
            _bossAnchor = null;
            try { if (!a.destroyed) BringPartnerTo(a, 6, "boss entry (after the cinematic)"); }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Boss entry recheck failed"); }
        }

        /// <summary>Move the partner of <paramref name="anchor"/> next to them if more than <paramref name="cells"/> apart.</summary>
        private bool BringPartnerTo(Hero anchor, int cells, string why)
        {
            var other = Partner(anchor);
            if (other == null || other.destroyed || other._level == null || !ReferenceEquals(other._level, anchor._level)) return false;
            if ((ReferenceEquals(other, P1) && _p1Down.Active) || (IsP2(other) && _p2Down.Active)) return false;
            if (System.Math.Abs(other.cx - anchor.cx) <= cells && System.Math.Abs(other.cy - anchor.cy) <= cells) return false;
            MoveTo(other, anchor);
            Logger.Information("[LocalCoop] {Why}: {Who} brought to {Anchor}", why, IsP2(other) ? "P2" : "P1", IsP2(anchor) ? "P2" : "P1");
            return true;
        }

        // ------------------------------------------------------------------ selling from a stand (bug 6)

        private void SellFromStand(Entity stand, Func<InventItem?> getItem, Hero by, bool sell, Action orig)
        {
            if (!sell || !CoopActive || by == null) { orig(); return; }   // taking it goes through Hero.pickItem
            InventItem? item = null, copy = null;
            Hero? partner = null;
            Level? lvl = null;
            int cx = 0, cy = 0;
            double xr = 0.5, yr = 1;
            try
            {
                item = getItem();
                cx = stand.cx; cy = stand.cy; xr = stand.xr; yr = stand.yr;
                if (Config.LootParity && item != null && IsParityItem(item) && !_noParity.TryGetValue(stand, out _)
                    && !_ownedItems.TryGetValue(item, out _) && !IsOwnedId(SafeId(item)))
                {
                    partner = Partner(by);
                    if (partner != null && !partner.destroyed) { copy = item.clone(false, null); lvl = stand._level; }
                }
            }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Stand sell snapshot failed"); copy = null; }

            RunAsPicker(by, orig);

            if (copy == null || lvl == null || partner == null || item == null) return;
            bool sold;
            try { sold = stand.destroyed || CoopInput.Ptr(getItem()) != CoopInput.Ptr(item); } catch { sold = true; }
            if (sold) SpawnPartnerCopy(lvl, cx, cy, xr, yr, copy, partner, "sold from a stand");
        }

        // ------------------------------------------------------------------ mushrooms (bug 7)

        private void Hook_Mushroom_fixedUpdate(Hook_Mushroom.orig_fixedUpdate orig, Mushroom self)
        {
            orig(self);
            var p2 = P2;
            if (p2 == null || p2.destroyed || _p2Down.Active || self.destroyed || p2._level == null
                || !ReferenceEquals(p2._level, self._level)) return;
            try
            {
                // Same test the game makes for game.hero (ground mushrooms only, sideOfTheRoom 0): falling onto it.
                if (self.sideOfTheRoom != 0 || p2.dy <= 0) return;
                int dx = p2.cx - self.cx;
                if (dx < -1 || dx > 1) return;
                bool onTop = (p2.cy == self.cy - 1 && p2.yr > 0.5) || p2.cy == self.cy;
                if (!onTop) return;
                WithHero(p2, () => self.bounce());
            }
            catch (Exception ex) { if (_hud2Errors.Add("mushroom:" + ex.Message)) Logger.Warning(ex, "[LocalCoop] Mushroom bounce for P2 failed"); }
        }

        // ------------------------------------------------------------------ pulleys (bug 9)

        private readonly PinnedTable<Pulley, Hero> _pulleyRider = new();

        private void NotePulleyRider(PulleyAttachPoint pt, Entity? by)
        {
            try
            {
                var trap = pt.linkedTrap;
                if (trap != null && by is Hero h && (IsP2(h) || ReferenceEquals(h, P1))) _pulleyRider.AddOrUpdate(trap, h);
            }
            catch { }
        }

        private void AsEntityHero(Entity? by, Action act)
        {
            if (by is Hero h && IsP2(h)) WithHero(h, act);
            else act();
        }

        private bool PulleyRiderIsP2(Pulley p) =>
            CoopActive && _pulleyRider.TryGetValue(p, out var h) && IsP2(h) && !h.destroyed && !_p2Down.Active
            && ReferenceEquals(h._level, p._level);

        private void AsPulleyRider(Pulley p, Action act)
        {
            if (PulleyRiderIsP2(p)) WithHero(P2!, act);
            else act();
        }

        // Its Ref<bool> argument can't be captured by a lambda, so this one is written out.
        private void Hook_Pulley_trigger_execute(Hook_Pulley.orig_trigger_execute o, Pulley s, HaxeProxy.Runtime.Ref<bool> fromHero, int cx, int cy)
        {
            if (!PulleyRiderIsP2(s)) { o(s, fromHero, cx, cy); return; }
            var sc = EnterP2Scope();
            try { o(s, fromHero, cx, cy); }
            finally { ExitP2Scope(sc); }
        }

        // ------------------------------------------------------------------ a controller switching off (bug 8)
        // With one pad left, the mod gives it to P2 and puts P1 on the keyboard, so after a controller ran out of
        // battery and came back the players had swapped controllers. Now, when two controllers are connected again,
        // the "Choose your side" screen opens.

        private int _lastPadCount = -1;
        private bool _padLost;

        private void UpdatePadLoss()
        {
            int n = _input.ConnectedCount;
            if (n == _lastPadCount) return;
            int before = _lastPadCount;
            _lastPadCount = n;
            if (before < 0) return;
            if (before >= 2 && n < 2 && CoopActive)
            {
                _padLost = true;
                Logger.Information("[LocalCoop] A controller disconnected ({N} left); sides will be chosen again when it is back", n);
                try { P1?.popText("CONTROLLER DISCONNECTED".AsHaxeString(), 0xFF6060); } catch { }
            }
            else if (_padLost && n >= 2)
            {
                _padLost = false;
                _openSidesNextFrame = true;
                Logger.Information("[LocalCoop] Two controllers again: opening Choose your side");
            }
        }
    }
}
