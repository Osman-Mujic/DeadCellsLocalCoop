using dc;
using dc.en;
using dc.en.inter;
using dc.en.ltrap;
using dc.pr;
using dc.tool;
using dc.ui;
using dc.ui.hud;

namespace LocalCoop
{
    /// <summary>
    /// Fixes from playtest 3: deaths that bypassed the down/revive rule, teleporters, scrolls of power,
    /// the carnivorous plant (Venus flytrap) and players being pulled apart by teleports.
    /// </summary>
    public partial class LocalCoopMod
    {
        private void InitCoopFixes()
        {
            Hook_Hero.onDie += Hook_Hero_onDie;
            Hook_Teleport.onActivate += Hook_Teleport_onActivate;
            Hook_MiniMap.chooseTarget += Hook_MiniMap_chooseTarget;
            Hook_MiniMap.cancelChooseTarget += (orig, self) => { orig(self); RestoreMinimapControls(self); };
            Hook_HUD.fullscreenMap += Hook_HUD_fullscreenMap;
            Hook_Hero.applyItemPickEffect += Hook_Hero_applyItemPickEffect;
            Hook__TierSelect.__constructor__ += (orig, self, hero, src, item) => { _tierSelectsOpen++; orig(self, hero, src, item); };
            Hook_TierSelect.onDispose += (orig, self) =>
            {
                orig(self);
                if (_tierSelectsOpen > 0) _tierSelectsOpen--;
                RunPendingScrolls();
            };
            Hook_CarnivorousPlant.fixedUpdate += Hook_CarnivorousPlant_fixedUpdate;
        }

        // ------------------------------------------------------------------ deaths
        // Some deaths reach Hero.onDie without Hero.kill (P1 died for real in playtest 3 while P2 was alive).

        private void Hook_Hero_onDie(Hook_Hero.orig_onDie orig, Hero self)
        {
            if (CoopActive && (IsP2(self) || ReferenceEquals(self, P1)))
            {
                var mine = IsP2(self) ? _p2Down : _p1Down;
                var other = IsP2(self) ? _p1Down : _p2Down;
                if (mine.Active) return;                                   // already down
                if (!other.Active) { GoDown(self, mine, IsP2(self) ? "P2" : "P1"); return; }
                if (IsP2(self))
                {
                    // Last one standing is P2: end the run through P1 so the game's own death flow runs.
                    GoDown(self, mine, "P2");
                    var p1 = P1;
                    _p1Down.Reset();
                    Logger.Information("[LocalCoop] Both players down: run over");
                    if (p1 != null) { RestoreBody(p1); orig(p1); }
                    return;
                }
                _p1Down.Reset();
                RestoreBody(self);
                Logger.Information("[LocalCoop] Both players down: run over");
            }
            orig(self);
        }

        // ------------------------------------------------------------------ teleporters

        private bool _p2Teleporting;
        private MiniMap? _mmSwapped;
        private ControllerAccess? _mmSavedController;

        /// <summary>The map that picks a teleporter destination reads its own controller; give it P2's pad when P2 used the teleporter.</summary>
        private void Hook_Teleport_onActivate(Hook_Teleport.orig_onActivate orig, Teleport self, Hero by, bool lp)
        {
            _p2Teleporting = CoopActive && IsP2(by);
            try { orig(self, by, lp); }
            finally { _p2Teleporting = false; }
        }

        private void Hook_MiniMap_chooseTarget(Hook_MiniMap.orig_chooseTarget orig, MiniMap self, Entity origin, dc.hl.types.ArrayObj targets,
            HaxeProxy.Runtime.HlAction<Entity> onChoose)
        {
            orig(self, origin, targets, onChoose);
            if (!_p2Teleporting) return;
            try
            {
                _mmSavedController = self.controller;
                _mmSwapped = self;
                self.controller = _input.CreateP2Access();
                if (Config.Verbose) Logger.Information("[LocalCoop] Teleport destination: P2 chooses");
            }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Giving P2 the teleport map failed"); }
        }

        private bool Hook_HUD_fullscreenMap(Hook_HUD.orig_fullscreenMap orig, HUD self, bool v, HaxeProxy.Runtime.Ref<bool> restoreHUD)
        {
            var r = orig(self, v, restoreHUD);
            if (!v && _mmSwapped != null) RestoreMinimapControls(_mmSwapped);
            return r;
        }

        private void RestoreMinimapControls(MiniMap mm)
        {
            if (_mmSwapped == null || !ReferenceEquals(mm, _mmSwapped)) return;
            try { if (_mmSavedController != null) mm.controller = _mmSavedController; } catch { }
            _mmSwapped = null;
            _mmSavedController = null;
        }

        // Teleports (statues, runes, cursed doors...) move one hero far in a single frame. Instead of the leash
        // dragging them back (playtest 3: "P2 got teleported and teleported back"), the partner comes along.
        private sealed class LastPos { public Level? Lvl; public double X, Y; public bool Valid; }
        private readonly LastPos _last1 = new(), _last2 = new();

        private void UpdateTeleportFollow(Hero p1, Hero p2)
        {
            bool sameLevel = ReferenceEquals(p1._level, p2._level);
            bool j1 = Jumped(p1, _last1), j2 = Jumped(p2, _last2);
            if (sameLevel && !_p1Down.Active && !_p2Down.Active && j1 != j2)
            {
                Hero mover = j1 ? p1 : p2, other = j1 ? p2 : p1;
                other.setPosCase(mover.cx, mover.cy, mover.xr, mover.yr);
                other.cancelVelocities();
                Logger.Information("[LocalCoop] {Who} teleported; partner follows", j1 ? "P1" : "P2");
                Remember(other, j1 ? _last2 : _last1);
            }
        }

        private static bool Jumped(Hero h, LastPos lp)
        {
            double x = h.cx + h.xr, y = h.cy + h.yr;
            bool jumped = lp.Valid && ReferenceEquals(lp.Lvl, h._level)
                && (System.Math.Abs(x - lp.X) > 8 || System.Math.Abs(y - lp.Y) > 8);
            lp.Lvl = h._level; lp.X = x; lp.Y = y; lp.Valid = true;
            return jumped;
        }

        private static void Remember(Hero h, LastPos lp)
        {
            lp.Lvl = h._level; lp.X = h.cx + h.xr; lp.Y = h.cy + h.yr; lp.Valid = true;
        }

        // ------------------------------------------------------------------ scrolls of power
        // A scroll picked up by one player is offered to the other too (after the first player's choice screen
        // closes, so two choice screens never overlap).

        internal static bool IsScrollId(string id) =>
            id.EndsWith("Up", StringComparison.Ordinal) || id.EndsWith("Tier", StringComparison.Ordinal);

        private sealed class PendingScroll { public Hero For = null!; public Entity? From; public InventItem Item = null!; }
        private readonly Queue<PendingScroll> _pendingScrolls = new();
        private int _tierSelectsOpen;
        private int _partnerScrollDepth;

        private void Hook_Hero_applyItemPickEffect(Hook_Hero.orig_applyItemPickEffect orig, Hero self, Entity from, InventItem item)
        {
            string id = ItemId(item);
            if (!CoopActive || _partnerScrollDepth > 0 || !IsScrollId(id))
            {
                RunAsPicker(self, () => orig(self, from, item));
                return;
            }
            var partner = Partner(self);
            InventItem? copy = null;
            try { copy = item.clone(false, null); } catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Scroll copy failed"); }
            RunAsPicker(self, () => orig(self, from, item));
            if (partner == null || partner.destroyed || copy == null) return;
            _pendingScrolls.Enqueue(new PendingScroll { For = partner, From = from, Item = copy });
            Logger.Information("[LocalCoop] {Who} read {Scroll}; {Partner} gets it too", IsP2(self) ? "P2" : "P1", id, IsP2(partner) ? "P2" : "P1");
            RunPendingScrolls();
        }

        private void RunPendingScrolls()
        {
            while (_tierSelectsOpen == 0 && _pendingScrolls.Count > 0)
            {
                var p = _pendingScrolls.Dequeue();
                if (p.For.destroyed || p.For._level == null) continue;
                var src = p.From != null && !p.From.destroyed ? p.From : p.For;
                _partnerScrollDepth++;
                try { RunAsPicker(p.For, () => p.For.applyItemPickEffect(src, p.Item)); }
                catch (Exception ex) { Logger.Error(ex, "[LocalCoop] Giving the scroll to the partner failed"); }
                finally { _partnerScrollDepth--; }
            }
        }

        // ------------------------------------------------------------------ carnivorous plant (Venus flytrap)
        // Its update only looks at game.hero. Run it against whichever player is closest so either can use it.

        private void Hook_CarnivorousPlant_fixedUpdate(Hook_CarnivorousPlant.orig_fixedUpdate orig, CarnivorousPlant self)
        {
            var p1 = P1;
            var p2 = P2;
            if (!CoopActive || p1 == null || p2 == null || _p2Down.Active || !ReferenceEquals(p2._level, self._level))
            {
                orig(self);
                return;
            }
            double px = self.cx + self.xr, py = self.cy + self.yr;
            double d1 = Sq(p1.cx + p1.xr - px) + Sq(p1.cy + p1.yr - py);
            double d2 = Sq(p2.cx + p2.xr - px) + Sq(p2.cy + p2.yr - py);
            if (d2 < d1 || _p1Down.Active) WithHero(p2, () => orig(self));
            else orig(self);
        }

        private static double Sq(double v) => v * v;
    }
}
