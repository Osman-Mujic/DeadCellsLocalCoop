using dc;
using dc.en;
using dc.en.mob;
using dc.pr;
using dc.tool;

namespace LocalCoop
{
    /// <summary>
    /// Fixes from playtest 12 (v0.11.0, Lighthouse). Function indexes are for v35.
    ///
    /// Sisters. Every frame the climb manager (AmazonManager.fixedUpdate f@7166) sets noChase when ITS hero
    /// (manager.hero) stands in a "noChaseBelow" marker (CustomEntity) of the room it tracks. noChase gives every active
    /// sister a 99999 s cooldown 1048576000 that blocks all damage (AmazonBase.applyAttackResult f@7033) and her
    /// teleport back (AmazonBase.fixedUpdate f@7021), and sends her away (AmazonBase.tpAwayFromHero f@7032: stops her
    /// animation, interrupts her skills, tpAwayFromHeroBool). Only the start of a fight undoes it
    /// (activateDeactivateAmazonsBossRoom f@7169, for the sister whose fight starts). Solo, the hero is locked in the
    /// arena once a fight starts, so this never happens mid-fight. In co-op the manager followed P1 (v0.11.0: the
    /// higher player): with that player in a no-chase spot while the other one fought, the sister froze, invulnerable,
    /// for the rest of the fight (playtests 11 and 12).
    /// Now: (1) the manager follows the player the sisters should chase: a player outside a no-chase spot beats one in
    /// it, then a player in a boss room beats one outside, then the higher player (kept until the other is clearly
    /// higher). (2) A sister mid-fight in a boss room, with a player in that room and noChase off, who still has the
    /// lock is put back into the fight the way the fight start does it. (3) Blocked hits on a sister are logged with her
    /// state (every 5 s at most), so a next report can tell which gate it was.
    ///
    /// Frostbite and Superconductor (Entity.fixedUpdate f@950). A frozen or chilled enemy (affects 23, 133) takes
    /// Frostbite damage when game.hero has P_ColdDmg; a shocked one (affect 91) arcs when game.hero has
    /// ASP_Superconductor. The game does not record who froze or shocked the enemy. So P1's Frostbite also burned
    /// enemies P2 froze (the mutation's own rule: any frozen enemy), and P2's Frostbite never did anything. Now, when only
    /// P2 has one of them, the affected enemy's update runs as P2: either player's mutation counts, once.
    /// </summary>
    public partial class LocalCoopMod
    {
        private const int CdSisterLock = 1048576000;   // set by noChase (AmazonManager.fixedUpdate), 99999 s

        private Hero? _amazonLead;
        private string _amazonLeadWhy = "";
        private double _amazonLeadLogAt = -99, _sisterBlockLogAt = -99;
        private readonly Dictionary<IntPtr, double> _sisterLockSeen = new();

        private bool _p2OnlyCold, _p2OnlyShock;
        private double _mutationCheckAt;

        private void InitPlaytest12()
        {
            Hook_AmazonBase.applyAttackResult += (o, s, a) =>
            {
                if (CoopActive) NoteSisterHit(s);
                o(s, a);
            };
            Hook_Entity.fixedUpdate += Hook_Entity_fixedUpdate_Mutations;
        }

        // ------------------------------------------------------------------ sisters: who the manager follows

        private bool SisterTarget(Hero? h, Entity e)
        {
            if (h == null || h.destroyed || h.life <= 0 || h._level == null || !ReferenceEquals(h._level, e._level)) return false;
            if (ReferenceEquals(h, P1)) return !_p1Down.Active;
            return IsP2(h) && !_p2Down.Active;
        }

        private Hero? AmazonLead(AmazonManager m, out string why)
        {
            var p1 = P1; var p2 = P2;
            bool ok1 = SisterTarget(p1, m), ok2 = SisterTarget(p2, m);
            why = "only one player up";
            if (ok1 && !ok2) return p1;
            if (!ok1 && ok2) return p2;
            if (!ok1) return null;
            // 1. The sisters stand down only when both players are in a no-chase spot.
            bool safe1 = InNoChaseSpot(p1!), safe2 = InNoChaseSpot(p2!);
            if (safe1 != safe2) { why = "the other player is in a no-chase spot"; return safe1 ? p2 : p1; }
            // 2. The player in a boss room is the one the fight is about.
            bool boss1 = InBossRoomType(p1!), boss2 = InBossRoomType(p2!);
            if (boss1 != boss2) { why = "in the boss room"; return boss1 ? p1 : p2; }
            // 3. The climb follows the higher player; the current one stays until the other is clearly higher.
            var cur = ReferenceEquals(_amazonLead, p2) ? p2! : p1!;
            var other = ReferenceEquals(cur, p1) ? p2! : p1!;
            why = "higher up";
            return (other.cy + other.yr) < (cur.cy + cur.yr) - 2 ? other : cur;
        }

        private static dc.level.Room? RoomOf(Entity e)
        {
            try { return e._level?.map?.getRoomAt(e.cx, e.cy); } catch { return null; }
        }

        /// <summary>The manager's own noChase test (AmazonManager.fixedUpdate f@7166, ops 535-588), for any player.</summary>
        private static bool InNoChaseSpot(Hero h)
        {
            try
            {
                var room = RoomOf(h);
                if (room == null) return false;
                var rects = room.getMarkerRects("CustomEntity".AsHaxeString(), "noChaseBelow".AsHaxeString());
                if (rects == null) return false;
                foreach (var o in rects)
                    if (o is CRect r && h.cx >= r.cx && h.cx <= r.cx + r.wid - 1 && h.cy >= r.cy && h.cy <= r.cy + r.hei - 1)
                        return true;
            }
            catch { }
            return false;
        }

        private static bool InBossRoomType(Entity e)
        {
            try { return RoomOf(e)?.rType?.ToString() == "Boss"; } catch { return false; }
        }

        private void Hook_AmazonManager_fixedUpdate(Hook_AmazonManager.orig_fixedUpdate orig, AmazonManager self)
        {
            if (!CoopActive) { orig(self); return; }
            var lead = AmazonLead(self, out var why);
            if (lead != null && !ReferenceEquals(lead, _amazonLead))
            {
                if (_amazonLead != null && Clock.Now - _amazonLeadLogAt > 2)
                {
                    _amazonLeadLogAt = Clock.Now;
                    Logger.Information("[LocalCoop] Sisters now follow {P} ({Why})", IsP2(lead) ? "P2" : "P1", why);
                }
                _amazonLead = lead;
                _amazonLeadWhy = why;
            }
            try { if (lead != null && !ReferenceEquals(self.hero, lead)) self.hero = lead; } catch { }
            if (lead != null && IsP2(lead)) WithHero(lead, () => orig(self));
            else orig(self);
            try { UnlockFightingSisters(self); }
            catch (Exception ex) { if (_hud2Errors.Add("sisters:" + ex.Message)) Logger.Warning(ex, "[LocalCoop] Sister check failed"); }
        }

        // ------------------------------------------------------------------ sisters: the leftover lock

        private void UnlockFightingSisters(AmazonManager m)
        {
            if (m.noChase) { _sisterLockSeen.Clear(); return; }
            var game = Game.Class.ME;
            if (game == null || game.hasCinematic()) return;
            UnlockSister(m.amazonSurvival);
            UnlockSister(m.amazonTactic);
            UnlockSister(m.amazonBrutal);
        }

        private void UnlockSister(AmazonBase? s)
        {
            if (s == null || s.destroyed || s.life <= 0 || s._level == null) return;
            var key = CoopInput.Ptr(s);
            var cd = s.cd;
            var fc = cd?.fastCheck;
            if (fc == null || !fc.exists(CdSisterLock) || !s.hasFight || !s.get_inBossFight() || !s.inBossRoom() || !PlayerInRoomWith(s))
            {
                _sisterLockSeen.Remove(key);
                return;
            }
            // Give the game's own fight start a moment to clear it first.
            double now = Clock.Now;
            if (!_sisterLockSeen.TryGetValue(key, out var since)) { _sisterLockSeen[key] = now; return; }
            if (now - since < 0.5) return;
            _sisterLockSeen.Remove(key);

            // As the fight start does it (activateDeactivateAmazonsBossRoom ops 572, 652-665, 727).
            s.tpAwayFromHeroBool = false;
            if (fc.get(CdSisterLock) is dc.tool._Cooldown.CdInst ci)
            {
                try { cd!.cdList.remove(ci); } catch { }
                ci.frames = 0;
            }
            fc.remove(CdSisterLock);
            try { double d = 0.1; s.tpInsideBossRoom(ref d); } catch { }
            Logger.Information("[LocalCoop] {Sister} was locked mid-fight by the no-chase rule (sisters followed {Lead}: {Why}); back in the fight",
                s.GetType().Name, IsP2(_amazonLead) ? "P2" : "P1", _amazonLeadWhy);
        }

        private bool PlayerInRoomWith(Entity e)
        {
            var room = RoomOf(e);
            if (room == null) return false;
            var rp = CoopInput.Ptr(room);
            foreach (var h in new[] { P1, P2 })
                if (SisterTarget(h, e) && CoopInput.Ptr(RoomOf(h!)) == rp) return true;
            return false;
        }

        private void NoteSisterHit(AmazonBase s)
        {
            try
            {
                if (Clock.Now - _sisterBlockLogAt < 5) return;
                bool locked = s.cd?.fastCheck?.exists(CdSisterLock) ?? false;
                bool outside = s.invicibleOutsideBossRoom && !s.insideRoom();
                if (!locked && !outside) return;
                _sisterBlockLogAt = Clock.Now;
                bool noChase = false;
                try { noChase = s.manager?.noChase ?? false; } catch { }
                Logger.Information("[LocalCoop] Hit on {Sister} blocked: lock {Locked}, outside her area {Outside} (fight {Fight}/{Has}, tpAway {Tp}, noChase {NoChase}, sisters follow {Lead})",
                    s.GetType().Name, locked, outside, s.get_inBossFight(), s.hasFight, s.tpAwayFromHeroBool, noChase,
                    _amazonLead == null ? "-" : IsP2(_amazonLead) ? "P2" : "P1");
            }
            catch { }
        }

        // ------------------------------------------------------------------ Frostbite / Superconductor

        /// <summary>Per frame: which of the two "enemy is frozen/shocked" mutations only P2 has (checked twice a second).</summary>
        private void UpdateMutationShare(Hero p1, Hero p2)
        {
            if (Clock.Now < _mutationCheckAt) return;
            _mutationCheckAt = Clock.Now + 0.5;
            bool cold = false, shock = false;
            try
            {
                var i1 = p1.inventory; var i2 = p2.inventory;
                if (i1 != null && i2 != null && !p2.destroyed)
                {
                    cold = i2.hasItem("P_ColdDmg".AsHaxeString()) && !i1.hasItem("P_ColdDmg".AsHaxeString());
                    shock = i2.hasItem("ASP_Superconductor".AsHaxeString()) && !i1.hasItem("ASP_Superconductor".AsHaxeString());
                }
            }
            catch { }
            if (cold != _p2OnlyCold) Logger.Information("[LocalCoop] Frostbite: {State}", cold ? "only P2 has it; frozen enemies use P2's" : "P2's no longer needed (P1 has it or P2 lost it)");
            if (shock != _p2OnlyShock) Logger.Information("[LocalCoop] Superconductor: {State}", shock ? "only P2 has it; shocked enemies use P2's" : "P2's no longer needed (P1 has it or P2 lost it)");
            _p2OnlyCold = cold;
            _p2OnlyShock = shock;
        }

        private void Hook_Entity_fixedUpdate_Mutations(Hook_Entity.orig_fixedUpdate orig, Entity self)
        {
            if ((!_p2OnlyCold && !_p2OnlyShock) || _p2TurnDepth > 0 || self is not Mob || !CoopActive) { orig(self); return; }
            bool asP2 = false;
            try
            {
                asP2 = (_p2OnlyCold && (self.hasAffect(23) || self.hasAffect(133))) || (_p2OnlyShock && self.hasAffect(91));
                asP2 = asP2 && P2CanAct(self);
            }
            catch { asP2 = false; }
            if (asP2) WithHero(P2!, () => orig(self));
            else orig(self);
        }
    }
}
