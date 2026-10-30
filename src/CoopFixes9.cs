using dc;
using dc.en;
using dc.en.inter.door;
using dc.pow;
using dc.pr;

namespace LocalCoop
{
    /// <summary>
    /// Fixes from playtest 9: boss arena doors, keycard doors, and the Diverse Deck "Watcher" charge.
    /// (Shop/decline duplication is fixed in LootParity.cs.)
    /// </summary>
    public partial class LocalCoopMod
    {
        private void InitCoopFixes9()
        {
            // Boss arenas lock behind the player who walked in (HiddenTrigger -> cine.EnterRoomBoss & co. -> BossDoor.closeAndLock f@8875).
            Hook_BossDoor.closeAndLock += (orig, self, cb) =>
            {
                orig(self, cb);
                if (!CoopActive) return;
                // Anchor: whoever stepped on the boss trigger (HiddenTrigger passes them to the entry cinematic).
                var anchor = _lastTriggerHero != null && !_lastTriggerHero.destroyed ? _lastTriggerHero : P1;
                try { BringPartnerThrough(self, anchor); }
                catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Boss door regroup failed"); }
            };

            // Keycard / key doors check game.hero's inventory (LockedDoorButton.hasKey f@25290): keys are team keys,
            // so the reader is used as whichever player carries the key.
            Hook_LockedDoorButton.canBeActivated += (orig, self, by) => AsKeyHolder(self, by, () => orig(self, by));
            Hook_LockedDoorButton.onActivate += (orig, self, by, lp) => AsKeyHolder(self, by, () => { orig(self, by, lp); return 0; });
            Hook_LockedDoorButton.onFocus += (orig, self) => AsKeyHolder(self, null, () => { orig(self); return 0; });

            // Diverse Deck "Watcher" (foresight) keeps its charge in game.data.gameFlags under one fixed name, so
            // both players shared one counter. P2's deck uses its own name.
            Hook_DiverseDeckWatcher.init += (orig, self) => WithWatcherKey(self, () => orig(self));
            Hook_DiverseDeckWatcher.gainCharge += (orig, self) => WithWatcherKey(self, () => orig(self));
            Hook_DiverseDeckWatcher.getCharge += (orig, self) => { int r = 0; WithWatcherKey(self, () => r = orig(self)); return r; };
            Hook_DiverseDeckWatcher.onBeforeOwnerApplyAttackResult += (orig, self, a) => { bool r = false; WithWatcherKey(self, () => r = orig(self, a)); return r; };
            Hook_DiverseDeckWatcher.doActive += (orig, self) => WithWatcherKey(self, () => orig(self));
        }

        // ------------------------------------------------------------------ keys

        private T AsKeyHolder<T>(LockedDoorButton btn, Hero? by, Func<T> f)
        {
            var p1 = P1; var p2 = P2;
            if (!CoopActive || p1 == null || p2 == null) return f();
            try
            {
                var kind = btn.door?.keyKind;
                if (kind == null) return f();
                bool p1Has = p1.inventory != null && p1.inventory.countItemKind(kind) > 0;
                bool p2Has = p2.inventory != null && p2.inventory.countItemKind(kind) > 0;
                if (!p1Has && p2Has)
                {
                    T r = default!;
                    WithHero(p2, () => r = f());
                    return r;
                }
            }
            catch (Exception ex) { if (_hud2Errors.Add("key:" + ex.Message)) Logger.Warning(ex, "[LocalCoop] Key door check failed"); }
            return f();
        }

        // ------------------------------------------------------------------ Watcher charge

        private string? _watcherKeyBase;

        /// <summary>
        /// Run a Watcher method with the charge key of the deck's owner. Nested calls happen (the hit handler calls
        /// getCharge), so the key is put back to whatever it was before this call. v0.8.0-0.9.0 put back P1's key
        /// unconditionally: after P2's hit handler called getCharge, the rest of it ran with P1's key, so P2's hit
        /// emptied P1's card and P2's own charge never dropped (P2 never took damage; playtest 10, bug 2).
        /// </summary>
        private void WithWatcherKey(DiverseDeckWatcher self, Action act)
        {
            Hero? owner = null;
            try { owner = self.owner as Hero; } catch { }
            var cls = DiverseDeckWatcher.Class;
            var prev = cls.gameDataChargekeyName;
            _watcherKeyBase ??= prev?.ToString();
            if (_watcherKeyBase == null) { act(); return; }
            string want = IsP2(owner) ? _watcherKeyBase + "_coopP2" : _watcherKeyBase;
            if (prev?.ToString() == want) { act(); return; }
            cls.gameDataChargekeyName = want.AsHaxeString();
            try { act(); }
            finally { cls.gameDataChargekeyName = prev; }
        }
    }
}
