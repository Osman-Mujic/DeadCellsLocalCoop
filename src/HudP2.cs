using dc.en;
using dc.pr;
using dc.ui;

namespace LocalCoop
{
    /// <summary>
    /// Player 2 HUD: a second copy of the game's own HUD (health, flasks, weapons, skills,
    /// scroll tiers, mutations), built and updated while game.hero points at P2, mirrored to the
    /// bottom-right corner. P1's HUD keeps the left side; its money/cells/kills move to the top
    /// right and the minimap is hidden (the game's full-screen map button still works).
    ///
    /// game.hud / HUD.ME are never swapped: the game reaches the minimap through them, and P2's HUD has
    /// none (v0.2.0 swapped them and crashed on game.hud.minimap). Instead, while P2 is acting, calls that
    /// report hero state to P1's HUD are redirected to P2's HUD.
    /// </summary>
    public partial class LocalCoopMod
    {
        private HUD? _hud2;
        private int _p2ActDepth;   // > 0 while P2's own code runs (update, pick, menus)
        private bool _hud1TopAligned;

        private void InitHud2Hooks()
        {
            Hook_HUD.update += (orig, self) => AsHudOwner(self, () => orig(self));
            Hook_HUD.initHero += (orig, self) => AsHudOwner(self, () => orig(self));
            Hook_HUD.postUpdate += (orig, self) => { AsHudOwner(self, () => orig(self)); ApplyHudLayout(self); };
            Hook_HUD.onResize += (orig, self) => { AsHudOwner(self, () => orig(self)); ApplyHudLayout(self); };
            Hook_HUD.initItems += (orig, self, hero) =>
            {
                var t = IsP2(hero) && Hud2Ok ? _hud2! : (ReferenceEquals(self, _hud2) ? self : self);
                AsHudOwner(t, () => orig(t, hero));
            };

            // Hero -> HUD reports made while P2 acts go to P2's HUD.
            Hook_HUD.setHealings += (o, s, a, b) => Route(s, h => o(h, a, b));
            Hook_HUD.updateAmmo += (o, s, i) => Route(s, h => o(h, i));
            Hook_HUD.updateIcon += (o, s, i, t) => Route(s, h => o(h, i, t));
            Hook_HUD.highlightItem += (o, s, i) => Route(s, h => o(h, i));
            Hook_HUD.setSkillFade += (o, s, i, f) => Route(s, h => o(h, i, f));
            Hook_HUD.setWeaponFade += (o, s, i, f) => Route(s, h => o(h, i, f));
            Hook_HUD.setPowerFade += (o, s, i, f) => Route(s, h => o(h, i, f));
            Hook_HUD.hideBuffs += (o, s) => Route(s, h => o(h));
            Hook_HUD.showCanBeEquippedHighlightFor += (o, s, i) => Route(s, h => o(h, i));
            Hook_HUD.hideCanBeEquippedHighlight += (o, s) => Route(s, h => o(h));
            Hook_HUD.refreshItems += (o, s) => Route(s, h => o(h));
            Hook_HUD.updateLifeSize += (o, s, m) => Route(s, h => o(h, m));
            Hook_HUD.updateSkillBgs += (o, s) => Route(s, h => o(h));
            Hook_HUD.updateInfection += (o, s, v, m, n) => Route(s, h => o(h, v, m, n));
            Hook_HUD.onItemPick += (o, s, i, x, y) => Route(s, h => o(h, i, x, y));
            Hook_HUD.setCustomWeaponsFg += (o, s, t) => Route(s, h => o(h, t));
            Hook_HUD.showBuffFor += (o, s, hero, tier) =>
            {
                var t = IsP2(hero) && Hud2Ok ? _hud2! : s;
                AsHudOwner(t, () => o(t, hero, tier));
            };
            Hook_HUD.setSurvivalTier += Hook_HUD_setSurvivalTier;
            Hook_HUD.setBrutalityTier += Hook_HUD_setBrutalityTier;
            Hook_HUD.setTacticTier += Hook_HUD_setTacticTier;

            // The life bar is written to HUD.ME by Hero.updateLifeBar; make sure each hero writes to its own.
            // (v0.2.x: P2's damage showed on P1's bar, P1's bar glitched white, P2's stayed half orange.)
            Hook_Hero.updateLifeBar += (orig, self) =>
            {
                if (!Hud2Ok) { orig(self); return; }
                var want = IsP2(self) ? _hud2 : Game.Class.ME?.hud;
                var saved = HUD.Class.ME;
                if (want != null) HUD.Class.ME = want;
                try { orig(self); } finally { HUD.Class.ME = saved; }
            };
            Hook_Hero.preUpdate += (orig, self) => AsP2Acting(self, () => orig(self));
            Hook_Hero.fixedUpdate += (orig, self) => AsP2Acting(self, () => orig(self));
            Hook_Hero.postUpdate += (orig, self) => AsP2Acting(self, () => orig(self));
        }

        private bool Hud2Ok => _hud2 != null && !_hud2.destroyed && P2 != null && !P2.destroyed;

        /// <summary>Where a hero->HUD report should go: P2's HUD while P2 is acting, otherwise unchanged.</summary>
        private void Route(HUD self, Action<HUD> call)
        {
            if (_p2ActDepth > 0 && Hud2Ok && !ReferenceEquals(self, _hud2))
            {
                var h2 = _hud2!;
                AsHudOwner(h2, () => call(h2));
            }
            else AsHudOwner(self, () => call(self));
        }

        private void Hook_HUD_setSurvivalTier(Hook_HUD.orig_setSurvivalTier o, HUD s, int v, HaxeProxy.Runtime.Ref<bool> animated)
        {
            var t = _p2ActDepth > 0 && Hud2Ok ? _hud2! : s;
            if (!ReferenceEquals(t, _hud2)) { o(t, v, animated); return; }
            var sc = EnterP2Scope(); try { o(t, v, animated); } finally { ExitP2Scope(sc); }
        }
        private void Hook_HUD_setBrutalityTier(Hook_HUD.orig_setBrutalityTier o, HUD s, int v, HaxeProxy.Runtime.Ref<bool> animated)
        {
            var t = _p2ActDepth > 0 && Hud2Ok ? _hud2! : s;
            if (!ReferenceEquals(t, _hud2)) { o(t, v, animated); return; }
            var sc = EnterP2Scope(); try { o(t, v, animated); } finally { ExitP2Scope(sc); }
        }
        private void Hook_HUD_setTacticTier(Hook_HUD.orig_setTacticTier o, HUD s, int v, HaxeProxy.Runtime.Ref<bool> animated)
        {
            var t = _p2ActDepth > 0 && Hud2Ok ? _hud2! : s;
            if (!ReferenceEquals(t, _hud2)) { o(t, v, animated); return; }
            var sc = EnterP2Scope(); try { o(t, v, animated); } finally { ExitP2Scope(sc); }
        }

        /// <summary>Code inside P2's HUD reads game.hero: make that P2 while it runs.</summary>
        private void AsHudOwner(HUD hud, Action act)
        {
            if (_hud2 != null && ReferenceEquals(hud, _hud2) && P2 != null && !P2.destroyed)
            {
                var s = EnterP2Scope();
                try { act(); } finally { ExitP2Scope(s); }
            }
            else act();
        }

        private void AsP2Acting(Hero h, Action act)
        {
            if (!IsP2(h)) { act(); return; }
            // P2 is between levels (cleaned: no sprite, no level) or the run is ending: nothing to update.
            // v0.2.2 crashed here on "Null access .alpha" when P1's death tore the level down.
            if (h.spr == null || h._level == null || h.destroyed) return;
            var s = EnterP2Scope(swapHero: false);
            try { act(); } finally { ExitP2Scope(s); }
        }

        private void CreateHud2()
        {
            if (!Config.P2Hud) return;
            var game = Game.Class.ME;
            if (game == null || P2 == null) return;
            DestroyHud2();
            var hud1 = game.hud;
            var me1 = HUD.Class.ME;
            var s = EnterP2Scope();          // hero = P2 while the HUD reads its items
            try
            {
                _hud2 = new HUD(game);       // the constructor sets HUD.ME = itself
                game.hud = hud1;
                HUD.Class.ME = me1;
                // Shared, single-copy parts live on P1's HUD; P2's HUD points at them so any code that reaches
                // them through HUD.ME while P2 acts finds the real (visible) objects instead of null/hidden ones.
                _hud2.minimap = hud1.minimap;
                _hud2.moneyCount = hud1.moneyCount;
                _hud2.cellsCount = hud1.cellsCount;
                _hud2.killCount = hud1.killCount;
                _hud2.goldCombo = hud1.goldCombo;
                _hud2.debtCount = hud1.debtCount;
                _hud2.bossCellCount = hud1.bossCellCount;
                _hud2.initHero();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[LocalCoop] Creating player 2 HUD failed");
                try { _hud2?.destroy(); } catch { }
                _hud2 = null;
            }
            finally
            {
                ExitP2Scope(s);
                game.hud = hud1;
                HUD.Class.ME = me1;
            }
            if (_hud2 != null) Logger.Information("[LocalCoop] Player 2 HUD created");
        }

        private void RefreshHud2()
        {
            var hud2 = _hud2;
            if (hud2 == null || hud2.destroyed || P2 == null) return;
            try { AsHudOwner(hud2, () => { hud2.initHero(); hud2.show(null); }); }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Refreshing player 2 HUD failed"); }
        }

        private void DestroyHud2()
        {
            var hud2 = _hud2;
            _hud2 = null;
            RestoreHud1Layout();
            if (hud2 == null) return;
            try { if (!hud2.destroyed) hud2.destroy(); } catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Destroying P2 HUD failed"); }
        }

        private void RestoreHud1Layout()
        {
            if (!_hud1TopAligned) return;
            _hud1TopAligned = false;
            try
            {
                var hud1 = Game.Class.ME?.hud;
                if (hud1?.rightFlow == null) return;
                hud1.rightFlow.verticalAlign = new dc.h2d.FlowAlign.Bottom();
                if (hud1.minimapCont != null) hud1.minimapCont.visible = true;
                hud1.rightFlow.reflow();
            }
            catch { }
        }

        private void ApplyHudLayout(HUD hud)
        {
            try
            {
                if (_hud2 != null && ReferenceEquals(hud, _hud2))
                {
                    // Mirror the item/health block into the bottom-right corner; shared panels stay on P1's HUD.
                    int w = dc.hxd.Window.Class.getInstance.Invoke().get_width();
                    double hm = System.Math.Ceiling(w * 0.025);
                    var lf = hud.leftFlow;
                    if (lf != null) lf.x = w - lf.get_outerWidth() - hm;
                    if (hud.rightFlow != null) hud.rightFlow.visible = false;
                    if (hud.topRightFlow != null) hud.topRightFlow.visible = false;
                    if (hud.bossLifebar != null) hud.bossLifebar.visible = false;
                }
                else if (Hud2Ok && ReferenceEquals(hud, Game.Class.ME?.hud) && hud.rightFlow != null)
                {
                    // P1: money/cells/kills to the top-right (rightFlow spans the screen and aligns its
                    // content to the bottom; aligning it to the top moves the counters up), minimap off.
                    if (hud.minimapCont != null) hud.minimapCont.visible = Config.ShowMinimap;
                    if (!_hud1TopAligned)
                    {
                        hud.rightFlow.verticalAlign = new dc.h2d.FlowAlign.Top();
                        hud.rightFlow.reflow();
                        _hud1TopAligned = true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_layoutErrorLogged) { _layoutErrorLogged = true; Logger.Warning(ex, "[LocalCoop] HUD layout failed"); }
            }
        }
        private bool _layoutErrorLogged;
    }
}
