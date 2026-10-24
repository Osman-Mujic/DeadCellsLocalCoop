using System.Runtime.CompilerServices;
using dc;
using dc.en;
using dc.en.inter;
using dc.en.inter.door;
using dc.en.inter.npc;
using dc.en.ltrap;
using dc.pr;
using dc.tool;
using dc.ui;

namespace LocalCoop
{
    /// <summary>
    /// Fixes from playtest 7: fountains, the forge, doors that close between the players, the flytrap bounce,
    /// scroll tiers on P2's HUD, controller prompts stuck on keyboard, and a "regroup" button for P2.
    /// </summary>
    public partial class LocalCoopMod
    {
        private void InitCoopFixes7()
        {
            // Fountains between biomes: one refill per player (the game marks the fountain used after one).
            Hook_Fountain.canBeActivated += Hook_Fountain_canBeActivated;
            Hook_Fountain.onActivate += Hook_Fountain_onActivate;

            // Forges: the menu lists and upgrades game.hero's items, so run it as whoever talked to the smith.
            Hook_SmallBlacksmith.onActivate += (orig, self, by, lp) => RunAsPicker(by, () => orig(self, by, lp));
            Hook_Blacksmith.onActivate += (orig, self, by, lp) => RunAsPicker(by, () => orig(self, by, lp));
            Hook__ForgeUnderground.__constructor__ += (orig, self) =>
                OpenUi(self, null, () => orig(self), ui => ui.controller = HandOver(ui.controller));
            Hook_ForgeUnderground.update += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ForgeUnderground.postUpdate += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ForgeUnderground.onValidate += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ForgeUnderground.onDispose += (orig, self) => AsOwner(self, () => orig(self));
            Hook__ForgeLegendary.__constructor__ += (orig, self) =>
                OpenUi(self, null, () => orig(self), ui => ui.controller = HandOver(ui.controller));
            Hook_ForgeLegendary.update += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ForgeLegendary.postUpdate += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ForgeLegendary.onValidate += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ForgeLegendary.onDispose += (orig, self) => AsOwner(self, () => orig(self));

            // Doors closed by a pressure plate / trigger behind one player.
            Hook_TriggeredDoor.onDistantTrigger += Hook_TriggeredDoor_onDistantTrigger;
            // The mutation room's exit door: open once BOTH players have taken their mutations.
            Hook_PerkMaster.fixedUpdate += (orig, self) => { _perkMasterDepth++; try { orig(self); } finally { _perkMasterDepth--; } };
            Hook_PerkMaster.getAvailablePerks += Hook_PerkMaster_getAvailablePerks;

            // Flytrap: only game.hero may touch it (onTouch returns early for any other non-mob).
            Hook_CarnivorousPlant.onTouch += (orig, self, e) =>
            {
                if (e is Hero h && IsP2(h) && !ReferenceEquals(Game.Class.ME?.hero, h)) WithHero(h, () => orig(self, e));
                else orig(self, e);
            };
        }

        // ------------------------------------------------------------------ fountains

        private sealed class FountainUse { public bool P1, P2; }
        private readonly ConditionalWeakTable<Fountain, FountainUse> _fountainUse = new();

        private bool UsedFountain(Fountain f, Hero by)
        {
            if (!_fountainUse.TryGetValue(f, out var u)) return false;
            return IsP2(by) ? u.P2 : u.P1;
        }

        private bool Hook_Fountain_canBeActivated(Hook_Fountain.orig_canBeActivated orig, Fountain self, Hero by)
        {
            if (!CoopActive || by == null || !self.used || self.broken || UsedFountain(self, by)) return orig(self, by);
            self.used = false;
            try { return orig(self, by); }
            finally { self.used = true; }
        }

        private void Hook_Fountain_onActivate(Hook_Fountain.orig_onActivate orig, Fountain self, Hero by, bool lp)
        {
            if (!CoopActive || by == null) { orig(self, by, lp); return; }
            bool wasUsed = self.used;
            bool mine = UsedFountain(self, by);
            if (wasUsed && !mine) self.used = false;   // the partner's refill doesn't use up this player's
            try { RunAsPicker(by, () => orig(self, by, lp)); }
            finally
            {
                if (self.used && (!wasUsed || !mine))
                {
                    var u = _fountainUse.GetOrCreateValue(self);
                    if (IsP2(by)) u.P2 = true; else u.P1 = true;
                    Logger.Information("[LocalCoop] {Who} used the fountain ({Left})", IsP2(by) ? "P2" : "P1",
                        u.P1 && u.P2 ? "both done" : "partner can still use it");
                }
                else if (wasUsed && !self.used) self.used = true;   // refused ("I don't need it"): put it back
            }
        }

        // ------------------------------------------------------------------ doors

        private void Hook_TriggeredDoor_onDistantTrigger(Hook_TriggeredDoor.orig_onDistantTrigger orig, TriggeredDoor self, Interactive trigger, Entity by)
        {
            bool wasOpen = !self._closed;
            orig(self, trigger, by);
            if (!CoopActive || !wasOpen || !self._closed) return;
            try { BringPartnerThrough(self, by as Hero); }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Door regroup failed"); }
        }

        /// <summary>A door just closed: if it now separates the players, bring the other one to the side of whoever closed it.</summary>
        private void BringPartnerThrough(Door door, Hero? closer)
        {
            var p1 = P1; var p2 = P2;
            if (p1 == null || p2 == null || !ReferenceEquals(p1._level, door._level) || !ReferenceEquals(p2._level, door._level)) return;
            double dx = door.cx + door.xr;
            int s1 = System.Math.Sign(PosX(p1) - dx), s2 = System.Math.Sign(PosX(p2) - dx);
            if (s1 == s2 || s1 == 0 || s2 == 0) return;
            Hero anchor = closer != null && (IsP2(closer) || ReferenceEquals(closer, p1)) ? closer : p1;
            Hero other = ReferenceEquals(anchor, p1) ? p2 : p1;
            if ((ReferenceEquals(other, p1) && _p1Down.Active) || (ReferenceEquals(other, p2) && _p2Down.Active)) return;
            MoveTo(other, anchor);
            Logger.Information("[LocalCoop] A door closed between the players: {Who} brought through", IsP2(other) ? "P2" : "P1");
        }

        private void MoveTo(Hero who, Hero to)
        {
            who.setPosCase(to.cx, to.cy, to.xr, to.yr);
            who.cancelVelocities();
            Remember(P1!, _last1);
            Remember(P2!, _last2);
        }

        private int _perkMasterDepth;

        private int Hook_PerkMaster_getAvailablePerks(Hook_PerkMaster.orig_getAvailablePerks orig, PerkMaster self)
        {
            int n = orig(self);
            if (_perkMasterDepth == 0 || !CoopActive || P2 == null || _p2Down.Active || !ReferenceEquals(P2._level, self._level)) return n;
            int n2 = 0;
            try { WithHero(P2, () => n2 = orig(self)); } catch { }
            return System.Math.Max(n, n2);
        }

        // ------------------------------------------------------------------ regroup button
        // P2 holds Back/Select (the map button) for 1.5 s: P1 is brought to P2. Escape hatch for anything that
        // splits the players (doors, one-way drops, elevators) the mod doesn't know about.

        private double _regroupSince;
        private bool _regroupDone;

        private void UpdateRegroup(Hero p1, Hero p2)
        {
            var c2 = _input.P2Controller;
            bool down = false;
            try
            {
                if (c2 != null && _input.P2Pad != null)
                {
                    var b = c2.get_bindings();
                    int k = Convert.ToInt32(b.padA.getDyn(9));
                    down = k >= 0 && c2.padIsDown(k);
                }
            }
            catch { down = false; }
            if (!down) { _regroupSince = 0; _regroupDone = false; return; }
            if (_regroupDone) return;
            if (_regroupSince <= 0) { _regroupSince = Clock.Now; return; }
            if (Clock.Now - _regroupSince < 1.5) return;
            _regroupDone = true;
            if (!ReferenceEquals(p1._level, p2._level) || _p2Down.Active || _p1Down.Active || p2.life <= 0) return;
            MoveTo(p1, p2);
            Logger.Information("[LocalCoop] Regroup: P1 brought to P2");
        }

        // ------------------------------------------------------------------ HUD upkeep (every frame)

        private void UpdateHudUpkeep(Hero p1, Hero p2)
        {
            var hud1 = Game.Class.ME?.hud;
            if (Hud2Ok)
            {
                ApplyHudLayout(_hud2!);
                SyncTiers(p2, _hud2!);
            }
            if (hud1 != null && !hud1.destroyed)
            {
                ApplyHudLayout(hud1);
                if (!ReferenceEquals(hud1, _hud2)) SyncTiers(p1, hud1);
            }
        }

        /// <summary>
        /// Scroll tiers are pushed to the HUD from several places (cinematics, the choice menu, Hero.computeTiers),
        /// some of them after P2's turn has ended, so P2's numbers sometimes went to P1's HUD (playtest 7, bug 2).
        /// The displayed numbers are simply corrected from the hero's real tiers.
        /// </summary>
        private void SyncTiers(Hero h, HUD hud)
        {
            var bar = hud.tierBar;
            if (bar == null) return;
            bool anim = true;
            try
            {
                if (bar.survivalTier != null && bar.survivalTier.value != h.survivalTier)
                    AsHudOwner(hud, () => hud.setSurvivalTier(h.survivalTier, new HaxeProxy.Runtime.Ref<bool>(ref anim)));
                if (bar.brutalityTier != null && bar.brutalityTier.value != h.brutalityTier)
                    AsHudOwner(hud, () => hud.setBrutalityTier(h.brutalityTier, new HaxeProxy.Runtime.Ref<bool>(ref anim)));
                if (bar.tacticTier != null && bar.tacticTier.value != h.tacticTier)
                    AsHudOwner(hud, () => hud.setTacticTier(h.tacticTier, new HaxeProxy.Runtime.Ref<bool>(ref anim)));
            }
            catch (Exception ex)
            {
                if (_hud2Errors.Add("tiers:" + ex.Message)) Logger.Warning(ex, "[LocalCoop] Tier display sync failed");
            }
        }

        // ------------------------------------------------------------------ controller prompts
        // Pressing a key (e.g. Esc) switches P1's button prompts to keyboard; the game's switch back to pad
        // prompts didn't fire with P1 pinned to one pad (playtest 7, bug 3). Any activity on P1's pad switches back.

        private double _p1PadActivity = -1;

        private void UpdatePromptMode()
        {
            var c1 = _input.P1Controller;
            var pad = _input.P1Pad;
            if (c1 == null || pad == null || pad.device == null) { _p1PadActivity = -1; return; }
            double act = pad.lastActivity;
            if (_p1PadActivity < 0) { _p1PadActivity = act; return; }
            if (act == _p1PadActivity) return;
            _p1PadActivity = act;
            if (c1.mainMode is not MainMode.MM_Keyboard) return;
            c1.mainMode = new MainMode.MM_Gamepad();
            try { c1.onChange?.Invoke(); } catch { }
            if (Config.Verbose) Logger.Information("[LocalCoop] P1 prompts back to gamepad");
        }
    }
}
