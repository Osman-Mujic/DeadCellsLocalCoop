using dc;
using dc.en;
using dc.en.mob;
using dc.pow;
using dc.pr;

namespace LocalCoop
{
    /// <summary>
    /// Fixes from playtest 11 (v0.10.3, owner run from the Prisoners' Quarters to the Lighthouse, then quit and
    /// continue). Function indexes are for v35.
    /// Head ownership (bugs 1 and 2) is in PadPickerAndStyle.cs (HeroHead.init); shop copies (bug 3) in LootParity.cs.
    /// </summary>
    public partial class LocalCoopMod
    {
        private void InitPlaytest11()
        {
            // ---- Bug 1: head throw (Homunculus). The thrown head (en.Homunculus) reads game.hero in nine functions
            // (detach from the hero, take/return controls, come back, die) and takes its controls from Boot.ME.controller
            // (P1's pad, en.Homunculus.initGfx). It now runs as its owner (sourceSkill.hero), on the owner's pad.
            Hook__Homunculus.__constructor__ += (o, s, lvl, x, y, cine, attached, skill) =>
            {
                Hero? owner = null;
                try { owner = skill?.hero; } catch { }
                if (owner != null && IsP2(owner)) { _homunculusOwner = owner; try { WithHero(owner, () => o(s, lvl, x, y, cine, attached, skill)); } finally { _homunculusOwner = null; } }
                else o(s, lvl, x, y, cine, attached, skill);
            };
            Hook_Homunculus.initGfx += (o, s) =>
            {
                var owner = HomunculusOwner(s);
                if (owner == null || !IsP2(owner)) { o(s); return; }
                WithHero(owner, () => o(s));
                try
                {
                    // initGfx made an access on P1's controller: give the head P2's pad instead.
                    var old = s.controller;
                    s.controller = _input.CreateP2Access();
                    try { old?.dispose(default); } catch { }
                }
                catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Giving P2's head P2's controller failed"); }
            };
            Hook_Homunculus.onDie += (o, s) => AsHomunculusOwner(s, () => o(s));
            Hook_Homunculus.comeBack += (o, s) => AsHomunculusOwner(s, () => o(s));
            Hook_Homunculus.controlsToHero += (o, s) => AsHomunculusOwner(s, () => o(s));
            Hook_Homunculus.controlsToMe += (o, s) => AsHomunculusOwner(s, () => o(s));
            Hook_Homunculus.releaseItem += (o, s, b) => AsHomunculusOwner(s, () => o(s, b));
            Hook_Homunculus.preUpdate += (o, s) => AsHomunculusOwner(s, () => o(s));
            Hook_Homunculus.detachFromHero += (o, s) => { dc.en.UsableBody r = null!; AsHomunculusOwner(s, () => r = o(s)); return r; };
            Hook_Homunculus.postUpdate += (o, s) => AsHomunculusOwner(s, () => o(s));
            Hook_Homunculus.fixedUpdate += (o, s) => AsHomunculusOwner(s, () => o(s));

            // ---- Bug 4: the Pollo power is an entity spawned by the user's skill manager
            // (BeheadedActiveSkillsManager.spawnPowerSkill f@ -> new pow.PolloPower) whose init keeps pHero = game.hero
            // (PolloPower.init f@24509): P2's chicken form turned P1 into the chicken. Spawning now runs as the user.
            dc.tool.hero.activeSkills.Hook_BeheadedActiveSkillsManager.spawnPowerSkill += Hook_spawnPowerSkill;

            // ---- Bug 5: Lighthouse sisters (Amazons). A sister counts as "in the room" only when she is in a marked
            // area of game.hero's room (AmazonBase.insideRoom f@7011). That check gates her activation and her damage
            // (AmazonBase.applyAttackResult): with P1 in another room, Calliope froze and took no damage while P2
            // fought her. Now either living player's room counts; each sister fights the nearer player; the manager
            // that runs the climb follows the player who is ahead.
            Hook_AmazonBase.insideRoom += (o, s) => o(s) || AsP2Check(s, () => o(s));
            Hook_AmazonBase.fixedUpdate += (o, s) => AsNearestHero(s, () => o(s));
            Hook_AmazonBrutal.fixedUpdate += (o, s) => AsNearestHero(s, () => o(s));
            Hook_AmazonSurvival.fixedUpdate += (o, s) => AsNearestHero(s, () => o(s));
            Hook_AmazonTactic.fixedUpdate += (o, s) => AsNearestHero(s, () => o(s));
            Hook_AmazonBase.tryToPreventDeath += (o, s, a, p) => AsAttacker(s, a, () => o(s, a, p));
            Hook_AmazonBrutal.tryToPreventDeath += (o, s, a, p) => AsAttacker(s, a, () => o(s, a, p));
            Hook_AmazonSurvival.tryToPreventDeath += (o, s, a, p) => AsAttacker(s, a, () => o(s, a, p));
            Hook_AmazonTactic.tryToPreventDeath += (o, s, a, p) => AsAttacker(s, a, () => o(s, a, p));
            Hook_AmazonManager.fixedUpdate += Hook_AmazonManager_fixedUpdate;
        }

        // ------------------------------------------------------------------ skill spawns

        private bool Hook_spawnPowerSkill(dc.tool.hero.activeSkills.Hook_BeheadedActiveSkillsManager.orig_spawnPowerSkill orig,
            dc.tool.hero.activeSkills.BeheadedActiveSkillsManager self, int id, dc.tool.InventItem i,
            Hashlink.Virtuals.virtual_ambiantDesc_castCD_cellCost_commonProps_dlc_droppable_gameplayDesc_group_icon_id_legendAffixes_moneyCost_name_props_synergy_tags_tier1_tier2_ infos)
        {
            Hero? user = null;
            try { user = self.hero; } catch { }
            if (user == null || !IsP2(user) || ReferenceEquals(Game.Class.ME?.hero, user)) return orig(self, id, i, infos);
            bool r = false;
            WithHero(user, () => r = orig(self, id, i, infos));
            return r;
        }

        // ------------------------------------------------------------------ head throw

        private Hero? _homunculusOwner;   // while P2's head is being constructed (sourceSkill not set yet)

        private Hero? HomunculusOwner(Homunculus h)
        {
            try { return h.sourceSkill?.hero ?? _homunculusOwner; } catch { return _homunculusOwner; }
        }

        private void AsHomunculusOwner(Homunculus h, Action act)
        {
            var owner = HomunculusOwner(h);
            if (owner != null && IsP2(owner) && !owner.destroyed) WithHero(owner, act);
            else act();
        }

        // ------------------------------------------------------------------ sisters

        private bool AsAttacker(Entity victim, dc.tool.atk.AttackData? a, Func<bool> f)
        {
            Hero? h = null;
            try { h = HeroBehind(a?.source, victim); } catch { }
            if (h == null || !IsP2(h)) return f();
            bool r = false;
            WithHero(h, () => r = f());
            return r;
        }

        /// <summary>The player the Lighthouse climb follows: the living one who is higher up (the climb goes up).</summary>
        private Hero? ClimbLeader(Entity e)
        {
            var p1 = P1; var p2 = P2;
            bool p1Ok = p1 != null && !p1.destroyed && !_p1Down.Active && p1.life > 0 && ReferenceEquals(p1._level, e._level);
            bool p2Ok = P2CanAct(e);
            if (p1Ok && !p2Ok) return p1;
            if (!p1Ok && p2Ok) return p2;
            if (!p1Ok) return null;
            return (p2!.cy + p2.yr) < (p1!.cy + p1.yr) - 2 ? p2 : p1;   // P2 clearly higher: P2 leads
        }

        private void Hook_AmazonManager_fixedUpdate(Hook_AmazonManager.orig_fixedUpdate orig, AmazonManager self)
        {
            if (!CoopActive) { orig(self); return; }
            var lead = ClimbLeader(self);
            try { if (lead != null && !ReferenceEquals(self.hero, lead)) self.hero = lead; } catch { }
            if (lead != null && IsP2(lead)) WithHero(lead, () => orig(self));
            else orig(self);
        }

        // ------------------------------------------------------------------ saved player 2 (bug 6)
        // Heroes are saved as ordinary level entities (Level.registerEntity -> savedEntities; Entity.shouldSave is
        // true), and Game keeps its own reference to game.hero. So quitting to the title saved P2 inside the level;
        // Continue restored it as a second, unmanaged hero bound to P1's controller (Hero.init), and the mod spawned a
        // fresh P2 on top - a copy of P1's saved data. Every quit/continue added another. Using a skill then fired the
        // copies' skills too; a copy's Foresight had no HUD and the game crashed. Now the saved P2 is taken back as
        // player 2 (with its own items) and any other extra hero is removed.

        private List<Hero> ExtraHeroes(Level lvl)
        {
            var list = new List<Hero>();
            var main = Game.Class.ME?.hero;
            // During a Richter section game.hero is Richter and the Beheaded waits in the level: leave everything alone.
            if (main is not dc.en.hero.Beheaded) return list;
            void Scan(dc.hl.types.ArrayObj? arr)
            {
                if (arr == null) return;
                foreach (var o in arr)
                    if (o is dc.en.hero.Beheaded h && !h.destroyed && !ReferenceEquals(h, main) && !IsP2(h)
                        && !list.Exists(x => CoopInput.Ptr(x) == CoopInput.Ptr(h)))
                        list.Add(h);
            }
            try { Scan(lvl.entities); Scan(lvl.qTreeEntities); } catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Scanning for saved heroes failed"); }
            return list;
        }

        /// <summary>True if a hero saved with the level was taken back as player 2.</summary>
        private bool TryAdoptSavedP2(Hero p1)
        {
            var lvl = p1._level;
            if (lvl == null) return false;
            var extra = ExtraHeroes(lvl);
            if (extra.Count == 0) return false;
            var h = extra[0];
            for (int i = 1; i < extra.Count; i++) RemoveExtraHero(extra[i]);
            try
            {
                P2 = h;
                var old = h.controller;
                h.controller = _input.CreateP2Access();
                try { old?.dispose(default); } catch { }
                _p2Down.Reset();
                if (h.life <= 0) { try { h.setLifeAndRally(System.Math.Max(1, h.maxLife / 2), 0); } catch { } }
                h.visible = true;
                h.set_sprAlpha(1.0);
                try { h.set_targetable(true); } catch { }
                // The head was set up while game.hero was P1: give it back to P2, then redraw with P2's outfit and head.
                try { if (h.heroHead != null) h.heroHead.hero = h; } catch { }
                bool disposed = false;
                try { WithHero(h, () => h.disposeGfx()); disposed = true; }
                catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Restored P2: old graphics not disposed"); }
                if (disposed)
                {
                    try { WithHero(h, () => h.initGfx()); }
                    catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Restored P2: redraw failed"); }
                }
                MarkInventoryOwned(p1);
                MarkInventoryOwned(h);
                CreateHud2();
                Logger.Information("[LocalCoop] Player 2 restored from the save ({Items}, life {Life}/{Max}); {N} extra hero(es) removed",
                    DescribeInventory(h), h.life, h.maxLife, extra.Count - 1);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[LocalCoop] Restoring the saved P2 failed; removing it");
                P2 = null;
                RemoveExtraHero(h);
                return false;
            }
        }

        private void RemoveExtraHero(Hero h)
        {
            try { if (h._level != null) h.clean(); } catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Extra hero clean failed"); }
            try { if (!h.destroyed) h.destroy(); } catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Extra hero destroy failed"); }
        }
    }
}
