using System.Runtime.CompilerServices;
using dc;
using dc.en;
using dc.pr;
using dc.tool;
using dc.ui;

namespace LocalCoop
{
    /// <summary>
    /// "Player 2's turn": much of Dead Cells assumes there is one hero, reachable as game.hero.
    /// When player 2 does something that runs such code (picking an item that opens the
    /// replace-item screen, choosing a scroll stat, buying mutations), we point game.hero at P2 for
    /// the duration of the call and give the screen P2's controller, then point it back.
    /// </summary>
    public partial class LocalCoopMod
    {
        private readonly ConditionalWeakTable<dc.ui.Process, object> _p2Uis = new();
        private int _p2TurnDepth;

        private void InitPlayerTurns()
        {
            Hook__ReplaceItem.__constructor__ += (orig, self, source, item, cb) =>
                OpenUi(self, null, () => orig(self, source, item, cb), ui => ui.controller = _input.CreateP2Access());
            Hook__ReplaceSingleItem.__constructor__ += Hook_ReplaceSingleItem_ctor;
            Hook__ReplaceItemWithDual.__constructor__ += (orig, self, source, a, b, cb) =>
                OpenUi(self, null, () => orig(self, source, a, b, cb), ui => ui.controller = _input.CreateP2Access());
            Hook__TierSelect.__constructor__ += (orig, self, hero, src, item) =>
                OpenUi(self, hero, () => orig(self, hero, src, item), ui => ui.controller = _input.CreateP2Access());
            Hook__PerkSelect.__constructor__ += (orig, self, hero, src) =>
                OpenUi(self, hero, () => orig(self, hero, src), ui => ui.controller = _input.CreateP2Access());

            Hook_ReplaceItem.onValidate += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ReplaceItem.onSwap += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ReplaceItem.update += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ReplaceSingleItem.onValidate += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ReplaceSingleItem.update += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ReplaceItemWithDual.onValidate += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ReplaceItemWithDual.onSwap += (orig, self) => AsOwner(self, () => orig(self));
            Hook_ReplaceItemWithDual.update += (orig, self) => AsOwner(self, () => orig(self));
            Hook_TierSelect.validate += (orig, self) => AsOwner(self, () => orig(self));
            Hook_TierSelect.update += (orig, self) => AsOwner(self, () => orig(self));
            Hook_PerkSelect.onValidate += (orig, self) => AsOwner(self, () => orig(self));
            Hook_PerkSelect.onChoose += (orig, self) => AsOwner(self, () => orig(self));
            Hook_PerkSelect.update += (orig, self) => AsOwner(self, () => orig(self));
        }

        private void Hook_ReplaceSingleItem_ctor(Hook__ReplaceSingleItem.orig___constructor__ orig, ReplaceSingleItem self,
            Entity source, InventItem newItem, InventItem oldItem, HaxeProxy.Runtime.HlAction<bool> cb, HaxeProxy.Runtime.Ref<bool> isBackpack)
        {
            bool forP2 = CoopActive && _p2TurnDepth > 0;
            if (!forP2) { orig(self, source, newItem, oldItem, cb, isBackpack); return; }
            var game = Game.Class.ME;
            var saved = game.hero;
            game.hero = P2;
            _p2TurnDepth++;
            try { orig(self, source, newItem, oldItem, cb, isBackpack); }
            finally { _p2TurnDepth--; if (ReferenceEquals(game.hero, P2)) game.hero = saved; }
            try { self.controller = _input.CreateP2Access(); _p2Uis.AddOrUpdate(self, this); }
            catch (Exception ex) { Logger.Error(ex, "[LocalCoop] Giving P2 the ReplaceSingleItem controls failed"); }
        }

        /// <summary>Run <paramref name="act"/> with game.hero pointing at <paramref name="h"/>.</summary>
        internal void WithHero(Hero h, Action act)
        {
            var game = Game.Class.ME;
            if (game == null || ReferenceEquals(game.hero, h)) { act(); return; }
            var saved = game.hero;
            game.hero = h;
            _p2TurnDepth++;
            try { act(); }
            finally
            {
                _p2TurnDepth--;
                // Only restore if nothing else replaced the hero meanwhile (e.g. a Richter swap).
                if (ReferenceEquals(game.hero, h)) game.hero = saved;
            }
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
            if (P2 != null && !P2.destroyed && _p2Uis.TryGetValue(ui, out _)) WithHero(P2, act);
            else act();
        }
    }
}
