using dc;
using dc.en;
using dc.pr;
using dc.tool;

namespace LocalCoop
{
    /// <summary>
    /// Playtest 14 (v0.11.3, full run): stuck when the Queen fight loaded. Owner: "player 1 floated off and player 2
    /// started falling infinitely from the top of the screen to the castle floor and then got stuck on the floor".
    ///
    /// Observed (log 2026-11-14 15:26): in T_Queen P2 pressed L3 nine times in two seconds ("P1 brought to P2") while
    /// the players dropped into the exit; in QueenArena "P2 teleported; partner follows" one second after arriving, then
    /// "Leash: pulled P2 to P1" with P1 18-19 cells above P2, twice, and L3 presses from both. No boss-entry log.
    /// Observed in the bytecode (v35): T_Queen is left by falling (en.inter.ExitToQueenTrigger fires for any member of
    /// the hero team in its rectangle -> cine.ExitToQueen -> LevelTransition.goto). A hero that falls below a cliff
    /// level gets a fatal fall (level.disp.Cliff.postUpdate, game.hero only; Entity.onFatalFallStart: cooldowns
    /// 264241152 for 99999 s and 266338304 for the delay; Hero.onFatalFallStart: controls locked 99999 s, camera stops
    /// tracking), then onFatalFallDamage (no damage in T_Queen only) and respawnOnLastStable. Nothing clears that state
    /// on a level change (no level-arrival function touches 264241152).
    /// Derived (not observed): P1, pulled by L3 to a P2 already falling past the trigger, started a fatal fall in T_Queen;
    /// the exit fired for P2 and P1 arrived in the arena still mid fatal fall (controls locked, respawn pending at a
    /// T_Queen spot). The leash then kept pulling P2 up to P1, and P2 fell back down: the loop the owner saw.
    /// Unknown: what exactly moved P1 up, and why P2 stayed stuck on the floor.
    ///
    /// Changes: a fatal fall does not survive a level change (cooldowns cleared, controls unlocked, for both players);
    /// the leash, L3 and the teleport follow wait while a cinematic plays or either player is in a fatal fall, and the
    /// teleport follow waits 2 s after arriving in a level; T_Queen and QueenArena get a 0.5 s position trace for their
    /// first 20 s; every hero fatal fall, fall damage and respawn is logged. Test warp: T_Queen and QueenArena added.
    /// </summary>
    public partial class LocalCoopMod
    {
        private const int CdFatalFall = 264241152;        // Entity.onFatalFallStart, 99999 s
        private const int CdFatalFallDelay = 266338304;   // Entity.onFatalFallStart -> onCooldownEnd -> onFatalFallDamage
        private const int CdFallRespawn = unchecked((int)2541748224u);   // Hero.onFatalFallDamage, 0.5 s before the respawn

        private double _arrivedAt = -99;
        private string _arrivedLevel = "";
        private double _traceUntil = -99, _traceNextAt;
        private double _regroupBlockedLogAt = -99;

        private void InitPlaytest14()
        {
            Hook_Hero.onFatalFallStart += Hook_Hero_onFatalFallStart;
            Hook_Hero.onFatalFallDamage += (o, s) =>
            {
                if (CoopActive) NoteFall(s, "fall damage / respawn timer");
                o(s);
            };
            Hook_Hero.respawnOnLastStable += (o, s, climb, immunity) =>
            {
                o(s, climb, immunity);
                if (CoopActive) NoteFall(s, "respawned on its last stable spot");
            };
        }

        private void Hook_Hero_onFatalFallStart(Hook_Hero.orig_onFatalFallStart orig, Hero self, HaxeProxy.Runtime.Ref<double> delay)
        {
            orig(self, delay);
            if (CoopActive) NoteFall(self, "fatal fall started");
        }

        private void NoteFall(Hero h, string what)
        {
            try
            {
                string who = IsP2(h) ? "P2" : ReferenceEquals(h, P1) ? "P1" : "another hero";
                Logger.Information("[LocalCoop] {Who}: {What} in {Level} at {Cx},{Cy}", who, what, h._level?.map?.id?.ToString() ?? "-", h.cx, h.cy);
            }
            catch { }
        }

        private static bool InFatalFall(Hero h)
        {
            try { return h.cd?.fastCheck?.exists(CdFatalFall) ?? false; } catch { return false; }
        }

        private static bool CinematicPlaying()
        {
            try { return Game.Class.ME?.hasCinematic() ?? false; } catch { return false; }
        }

        /// <summary>Removes a cooldown the way the game does (Cooldown.unset is inlined): list, frames, fast check.</summary>
        private static bool CdUnset(Cooldown? cd, int key)
        {
            var fc = cd?.fastCheck;
            if (fc == null || !fc.exists(key)) return false;
            if (fc.get(key) is dc.tool._Cooldown.CdInst ci)
            {
                try { cd!.cdList.remove(ci); } catch { }
                ci.frames = 0;
            }
            fc.remove(key);
            return true;
        }

        /// <summary>Called from Game.initHero after P1 (and P2) are placed in the new level.</summary>
        private void OnArrivedInLevel(Level lvl, Hero p1, Hero? p2)
        {
            _arrivedAt = Clock.Now;
            try { _arrivedLevel = lvl.map?.id?.ToString() ?? ""; } catch { _arrivedLevel = ""; }
            ClearStaleFall(p1, "P1");
            if (p2 != null && !p2.destroyed) ClearStaleFall(p2, "P2");
            try { Remember(p1, _last1); if (p2 != null) Remember(p2, _last2); } catch { }
            _traceUntil = _arrivedLevel is "T_Queen" or "QueenArena" ? Clock.Now + 20 : -99;
            _traceNextAt = 0;
        }

        private void ClearStaleFall(Hero h, string who)
        {
            try
            {
                var cd = h.cd;
                bool a = CdUnset(cd, CdFatalFall), b = CdUnset(cd, CdFatalFallDelay), c = CdUnset(cd, CdFallRespawn);
                if (!a && !b && !c) return;
                try { h.unlockControls(); } catch { }
                try { h.cancelVelocities(); } catch { }
                Logger.Information("[LocalCoop] {Who} arrived in {Level} still in a fatal fall from the last level: cleared (controls unlocked)", who, _arrivedLevel);
            }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Clearing {Who}'s fall state failed", who); }
        }

        /// <summary>The game is placing or moving the heroes itself: no leash, regroup or teleport follow.</summary>
        private bool HandsOff(Hero p1, Hero p2) => CinematicPlaying() || InFatalFall(p1) || InFatalFall(p2);

        private bool RegroupBlocked(Hero p1, Hero p2)
        {
            if (!HandsOff(p1, p2)) return false;
            if (Clock.Now - _regroupBlockedLogAt > 2)
            {
                _regroupBlockedLogAt = Clock.Now;
                Logger.Information("[LocalCoop] Regroup ignored: {Why}", CinematicPlaying() ? "a cinematic is playing" : "a player is falling out of the level");
            }
            return true;
        }

        private void UpdateArrivalTrace(Hero p1, Hero p2)
        {
            if (Clock.Now > _traceUntil || Clock.Now < _traceNextAt) return;
            _traceNextAt = Clock.Now + 0.5;
            try
            {
                string S(Hero h)
                {
                    bool locked = false;
                    try { locked = h.controlsLocked(default); } catch { }
                    return $"{h.cx},{h.cy} dy {h.dy:0.00} locked {locked} fall {InFatalFall(h)} life {h.life}";
                }
                Logger.Information("[LocalCoop] Trace {Level} +{T:0.0}s: P1 {P1} | P2 {P2} | cinematic {Cine}",
                    _arrivedLevel, Clock.Now - _arrivedAt, S(p1), S(p2), CinematicPlaying());
            }
            catch (Exception ex) { if (_hud2Errors.Add("trace:" + ex.Message)) Logger.Warning(ex, "[LocalCoop] Trace failed"); }
        }
    }
}
