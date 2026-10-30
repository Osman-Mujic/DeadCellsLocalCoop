using dc;
using dc.en;
using dc.en.mob;
using dc.pr;

namespace LocalCoop
{
    /// <summary>
    /// Curses and Malaise at higher difficulties (checked in the v35 bytecode before playtest 11).
    ///
    /// Curses are per hero (Hero.curseCounter). The Curser's skulls and the Jinx curse the hero they hit, and a dying
    /// mob lowers the curse of every cursed, targetable hero (Mob.onDie -> Hero.reduceCurse): all correct for P2.
    /// The Sore Loser (SoreLooser.tryToPreventDeath f@21122) cursed game.hero, i.e. P1, whoever killed it.
    ///
    /// Malaise (boss cell 5, "infection" in the code) is ONE meter for the run: game.data.heroInfection and
    /// game.data.infectionTier, whose tier switches on game-wide enemy rules. Hits on either player add to it and
    /// each kill lowers it once (Mob.reduceHeroInfection f@1067, through game.hero). But every hero runs its own timer
    /// (Hero.updateInfection f@2155, called from Hero.fixedUpdate) and both add to the one meter, so in co-op it
    /// filled from time twice as fast as solo. Owner's choice: solo speed. Only P1's timer counts; P2's update only
    /// refreshes P2's HUD.
    /// </summary>
    public partial class LocalCoopMod
    {
        private double _p2InfectionShown = double.NaN;

        private void InitCurseAndMalaise()
        {
            Hook_SoreLooser.tryToPreventDeath += Hook_SoreLooser_tryToPreventDeath;
            Hook_Hero.updateInfection += Hook_Hero_updateInfection;
            // Kills only lower Malaise while game.hero (P1) is alive; with P1 down, P2's kills counted for nothing.
            Hook_Mob.reduceHeroInfection += (o, s) =>
            {
                var p1 = P1;
                if (CoopActive && p1 != null && (_p1Down.Active || p1.life <= 0) && !_p2Down.Active && P2!.life > 0)
                    WithHero(P2!, () => o(s));
                else o(s);
            };
        }

        /// <summary>The hero behind an attack: the hero itself, or the owner of a pet, turret or the head.</summary>
        private Hero? HeroBehind(Entity? src, Entity? victim)
        {
            try
            {
                for (int i = 0; i < 4 && src != null; i++)
                {
                    if (src is Hero h) return h;
                    src = src.parent;
                }
                return victim?.lastHitHero;
            }
            catch { return null; }
        }

        private bool Hook_SoreLooser_tryToPreventDeath(Hook_SoreLooser.orig_tryToPreventDeath orig, SoreLooser self,
            dc.tool.atk.AttackData a, double prevLife)
        {
            if (!CoopActive) return orig(self, a, prevLife);
            Hero? killer = null;
            try { killer = HeroBehind(a?.source, self); } catch { }
            if (killer == null || !IsP2(killer)) return orig(self, a, prevLife);
            bool r = false;
            WithHero(killer, () => r = orig(self, a, prevLife));
            if (Config.Verbose) Logger.Information("[LocalCoop] Sore Loser killed by P2: P2 is cursed");
            return r;
        }

        private void Hook_Hero_updateInfection(Hook_Hero.orig_updateInfection orig, Hero self)
        {
            if (!CoopActive || !IsP2(self)) { orig(self); return; }
            // P2's timer doesn't add (solo speed). Keep P2's HUD showing the shared meter.
            try
            {
                var data = Game.Class.ME?.data;
                if (data == null) return;
                double v = data.heroInfection;
                if (v == _p2InfectionShown) return;
                _p2InfectionShown = v;
                self.refreshInfectionHud();
            }
            catch (Exception ex) { if (_hud2Errors.Add("malaise:" + ex.Message)) Logger.Warning(ex, "[LocalCoop] P2 Malaise display failed"); }
        }
    }
}
