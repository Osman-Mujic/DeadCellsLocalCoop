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
    /// </summary>
    public partial class LocalCoopMod
    {
        private HUD? _hud2;

        private void InitHud2Hooks()
        {
            Hook_HUD.update += (orig, self) => AsHudOwner(self, () => orig(self));
            Hook_HUD.initHero += (orig, self) => AsHudOwner(self, () => orig(self));
            Hook_HUD.initItems += (orig, self, hero) => AsHudOwner(self, () => orig(self, hero));
            Hook_HUD.postUpdate += (orig, self) => { AsHudOwner(self, () => orig(self)); ApplyHudLayout(self); };
            Hook_HUD.onResize += (orig, self) => { AsHudOwner(self, () => orig(self)); ApplyHudLayout(self); };

            // P2's own per-frame code (flask use, ammo, cooldown icons...) reports to "the" HUD:
            // make that P2's HUD while P2 updates.
            Hook_Hero.preUpdate += (orig, self) => AsP2Hud(self, () => orig(self));
            Hook_Hero.fixedUpdate += (orig, self) => AsP2Hud(self, () => orig(self));
            Hook_Hero.postUpdate += (orig, self) => AsP2Hud(self, () => orig(self));
        }

        private void AsHudOwner(HUD hud, Action act)
        {
            if (_hud2 != null && ReferenceEquals(hud, _hud2) && P2 != null && !P2.destroyed)
            {
                var s = EnterP2Scope();
                try { act(); } finally { ExitP2Scope(s); }
            }
            else act();
        }

        private void AsP2Hud(Hero h, Action act)
        {
            if (_hud2 == null || !IsP2(h)) { act(); return; }
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
            if (hud2 == null) return;
            try { if (!hud2.destroyed) hud2.destroy(); } catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Destroying P2 HUD failed"); }
        }

        private void ApplyHudLayout(HUD hud)
        {
            try
            {
                int w = dc.hxd.Window.Class.getInstance.Invoke().get_width();
                int h = dc.hxd.Window.Class.getInstance.Invoke().get_height();
                double hm = System.Math.Ceiling(w * 0.025), vm = System.Math.Ceiling(h * 0.025);
                if (_hud2 != null && ReferenceEquals(hud, _hud2))
                {
                    // Mirror the item/health block into the bottom-right corner; shared panels stay on P1's HUD.
                    var lf = hud.leftFlow;
                    if (lf != null) lf.x = w - lf.get_outerWidth() - hm;
                    if (hud.rightFlow != null) hud.rightFlow.visible = false;
                    if (hud.topRightFlow != null) hud.topRightFlow.visible = false;
                    if (hud.bossLifebar != null) hud.bossLifebar.visible = false;
                }
                else if (CoopActive && _hud2 != null && ReferenceEquals(hud, Game.Class.ME?.hud))
                {
                    // P1: free the bottom-right corner for P2.
                    if (hud.minimapCont != null) hud.minimapCont.visible = Config.ShowMinimap;
                    if (hud.rightFlow != null) hud.rightFlow.y = vm;
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
