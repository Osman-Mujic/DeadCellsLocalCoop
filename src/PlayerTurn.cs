using System.Runtime.CompilerServices;
using dc;
using dc.en;
using dc.pr;
using dc.tool;
using dc.ui;

namespace LocalCoop
{
    /// <summary>
    /// "Player 2's turn": much of Dead Cells assumes there is one hero (game.hero) and one HUD
    /// (game.hud / HUD.ME). When player 2 does something that runs such code (picking an item that
    /// opens the replace-item screen, choosing a scroll stat, buying mutations, updating their HUD)
    /// we point those at P2 and P2's HUD for the duration of the call, then point them back.
    /// </summary>
    public partial class LocalCoopMod
    {
        private readonly ConditionalWeakTable<dc.ui.Process, object> _p2Uis = new();
        private int _p2TurnDepth;
        private int _p2UiDepth;

        private void InitPlayerTurns()
        {
            Hook__ReplaceItem.__constructor__ += (orig, self, source, item, cb) =>
                OpenUi(self, null, () => orig(self, source, item, cb), ui => ui.controller = HandOver(ui.controller));
            Hook__ReplaceSingleItem.__constructor__ += Hook_ReplaceSingleItem_ctor;
            Hook__ReplaceItemWithDual.__constructor__ += (orig, self, source, a, b, cb) =>
                OpenUi(self, null, () => orig(self, source, a, b, cb), ui => ui.controller = HandOver(ui.controller));
            Hook__TierSelect.__constructor__ += (orig, self, hero, src, item) =>
                OpenUi(self, hero, () => orig(self, hero, src, item), ui => ui.controller = HandOver(ui.controller));
            Hook__PerkSelect.__constructor__ += (orig, self, hero, src) =>
                OpenUi(self, hero, () => orig(self, hero, src), ui => ui.controller = HandOver(ui.controller));

            Hook_ReplaceItem.onValidate += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ReplaceItem.onSwap += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ReplaceItem.update += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ReplaceItem.onDispose += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ReplaceSingleItem.onValidate += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ReplaceSingleItem.update += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ReplaceSingleItem.onDispose += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ReplaceItemWithDual.onValidate += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ReplaceItemWithDual.onSwap += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ReplaceItemWithDual.update += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ReplaceItemWithDual.onDispose += (orig, self) => AsOwner(self, () => orig(self));
            Hook_TierSelect.validate += (orig, self) => AsOwner(self, () => orig(self));
            Hook_TierSelect.update += (orig, self) => AsOwner(self, () => orig(self));
            Hook_TierSelect.onDispose += (orig, self) => AsOwner(self, () => orig(self));
            Hook_PerkSelect.onValidate += (orig, self) => AsOwner(self, () => orig(self));
            Hook_PerkSelect.onChoose += (orig, self) => AsOwner(self, () => orig(self));
            Hook_PerkSelect.update += (orig, self) => AsOwner(self, () => orig(self));
            Hook_PerkSelect.onDispose += (orig, self) => AsOwner(self, () => orig(self));
        }

        /// <summary>
        /// Swap a menu's input from player 1 to player 2.
        /// Menus open an *exclusive* access on the main controller, which freezes player 1's hero
        /// until that access is disposed. We dispose it right away (P1 is free again) and give the menu an
        /// exclusive access on P2's controller instead, so P2's hero holds still while P2 chooses.
        /// (v0.1 replaced the field without disposing: P1 stayed locked forever.)
        /// </summary>
        private ControllerAccess HandOver(ControllerAccess? original)
        {
            try { original?.dispose(default); }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Releasing P1's menu access failed"); }
            return _input.CreateP2Access(exclusive: true);
        }

        private void Hook_ReplaceSingleItem_ctor(Hook__ReplaceSingleItem.orig___constructor__ orig, ReplaceSingleItem self,
            Entity source, InventItem newItem, InventItem oldItem, HaxeProxy.Runtime.HlAction<bool> cb, HaxeProxy.Runtime.Ref<bool> isBackpack)
        {
            bool forP2 = CoopActive && _p2TurnDepth > 0;
            if (!forP2) { orig(self, source, newItem, oldItem, cb, isBackpack); return; }
            var scope = EnterP2Scope();
            try { orig(self, source, newItem, oldItem, cb, isBackpack); }
            finally { ExitP2Scope(scope); }
            try { self.controller = HandOver(self.controller); _p2Uis.AddOrUpdate(self, this); }
            catch (Exception ex) { Logger.Error(ex, "[LocalCoop] Giving P2 the ReplaceSingleItem controls failed"); }
        }

        internal readonly struct P2Scope
        {
            public readonly Hero? Hero; public readonly HUD? GameHud; public readonly HUD? HudMe; public readonly bool Active;
            public readonly bool Turn;
            public P2Scope(Hero? h, HUD? g, HUD? m, bool a, bool turn) { Hero = h; GameHud = g; HudMe = m; Active = a; Turn = turn; }
        }

        /// <summary>Point game.hero / game.hud / HUD.ME at player 2. Nestable.</summary>
        internal P2Scope EnterP2Scope(bool swapHero = true)
        {
            var game = Game.Class.ME;
            var p2 = P2;
            if (game == null || p2 == null) return default;
            var s = new P2Scope(game.hero, game.hud, HUD.Class.ME, true, swapHero);
            if (swapHero) game.hero = p2;
            var hud2 = _hud2;
            if (hud2 != null && !hud2.destroyed) { game.hud = hud2; HUD.Class.ME = hud2; }
            if (swapHero) _p2TurnDepth++;   // HUD-only scopes (P2's per-frame update) are not a "turn"
            return s;
        }

        internal void ExitP2Scope(P2Scope s)
        {
            if (!s.Active) return;
            if (s.Turn) _p2TurnDepth--;
            var game = Game.Class.ME;
            if (game == null) return;
            // Only restore what we changed (a Richter swap or HUD rebuild in between wins).
            if (ReferenceEquals(game.hero, P2)) game.hero = s.Hero;
            if (_hud2 != null && ReferenceEquals(game.hud, _hud2)) game.hud = s.GameHud;
            if (_hud2 != null && ReferenceEquals(HUD.Class.ME, _hud2)) HUD.Class.ME = s.HudMe;
        }

        /// <summary>Run <paramref name="act"/> as player 2 (hero + HUD).</summary>
        internal void WithHero(Hero h, Action act)
        {
            if (!IsP2(h)) { act(); return; }
            var s = EnterP2Scope();
            try { act(); }
            finally { ExitP2Scope(s); }
        }

        private void RunAsPicker(Hero picker, Action act)
        {
            if (IsP2(picker)) WithHero(picker, act);
            else act();
        }

        private void OpenUi<T>(T ui, Hero? heroArg, Action ctor, Action<T> giveP2Controls) where T : dc.ui.Process
        {
            bool forP2 = CoopActive && (IsP2(heroArg) || (heroArg == null && _p2TurnDepth > 0));
            if (!forP2) { ctor(); return; }
            WithHero(P2!, ctor);
            try
            {
                giveP2Controls(ui);
                _p2Uis.AddOrUpdate(ui, this);
                if (Config.Verbose) Logger.Information("[LocalCoop] {Ui} opened for P2", typeof(T).Name);
            }
            catch (Exception ex) { Logger.Error(ex, "[LocalCoop] Giving P2 the {Ui} controls failed", typeof(T).Name); }
        }

        private void AsOwner(dc.ui.Process ui, Action act)
        {
            if (P2 != null && !P2.destroyed && _p2Uis.TryGetValue(ui, out _))
            {
                _p2UiDepth++;
                try { WithHero(P2, act); } finally { _p2UiDepth--; }
            }
            else act();
        }
    }
}
