using System.Runtime.CompilerServices;
using dc;
using dc.en;
using dc.en.inter;
using dc.tool;

namespace LocalCoop
{
    /// <summary>
    /// Starting pipes ("Armory", meta unlock ArmoryUnlock): four tubes, each holding a random loadout,
    /// each with a switch. In single player, pulling one switch drops that tube's items and retracts all
    /// the other tubes (ArmoryButton.onActivate f@23374).
    ///
    /// Co-op: each player pulls their own switch. The first switch drops that tube for that player only
    /// and leaves the other tubes in place. When the partner pulls a different switch, that tube drops for
    /// them; if they pull the same one, they get their own copy of that loadout. Only after both have
    /// chosen do the remaining switches turn off. Items are reserved for the player whose switch released
    /// them and are never duplicated by world-loot sharing.
    /// </summary>
    public partial class LocalCoopMod
    {
        private sealed class PipeItem { public InventItem Item = null!; public int Cx, Cy; public double Xr, Yr; }
        private sealed class ArmoryState
        {
            public dc.pr.Level? Level;
            public ArmoryButton? P1Choice, P2Choice;
            public readonly PinnedTable<ArmoryButton, List<PipeItem>> Released = new();
        }
        private readonly ArmoryState _armory = new();

        private void InitArmoryHooks()
        {
            Hook_ArmoryButton.onActivate += Hook_ArmoryButton_onActivate;
            Hook_ArmoryButton.canBeActivated += Hook_ArmoryButton_canBeActivated;
        }

        private ArmoryState ArmoryFor(dc.pr.Level? lvl)
        {
            if (!ReferenceEquals(_armory.Level, lvl))
            {
                _armory.Level = lvl;
                _armory.P1Choice = null;
                _armory.P2Choice = null;
            }
            return _armory;
        }

        private ArmoryButton? ChoiceOf(ArmoryState st, Hero h) => IsP2(h) ? st.P2Choice : st.P1Choice;

        private bool Hook_ArmoryButton_canBeActivated(Hook_ArmoryButton.orig_canBeActivated orig, ArmoryButton self, Hero by)
        {
            if (!CoopActive || by == null) return orig(self, by!);
            var st = ArmoryFor(self._level);
            if (ChoiceOf(st, by) != null) return false;                 // you already picked your loadout
            var partner = Partner(by);
            if (partner != null && ReferenceEquals(ChoiceOf(st, partner), self))
            {
                // Same tube as the partner: allowed (the switch is already "off", bypass that check).
                bool was = self.isActivated;
                self.isActivated = false;
                try { return orig(self, by); } finally { self.isActivated = was; }
            }
            return orig(self, by);
        }

        private void Hook_ArmoryButton_onActivate(Hook_ArmoryButton.orig_onActivate orig, ArmoryButton self, Hero by, bool lp)
        {
            if (!CoopActive || by == null) { orig(self, by!, lp); return; }
            try
            {
                var st = ArmoryFor(self._level);
                if (ChoiceOf(st, by) != null) return;
                var partner = Partner(by);
                var partnerChoice = partner != null ? ChoiceOf(st, partner) : null;
                string who = IsP2(by) ? "P2" : "P1";

                int n;
                if (partnerChoice != null && ReferenceEquals(partnerChoice, self)) n = CopyPipeFor(st, self, by);
                else n = ReleasePipe(st, self, by);
                if (!self.isActivated) self.deactivate();               // switch flips to "off"
                if (IsP2(by)) st.P2Choice = self; else st.P1Choice = self;
                Logger.Information("[LocalCoop] Armory: {Who} chose a tube ({N} items)", who, n);

                if (partnerChoice != null || partner == null)
                {
                    // Both chose: turn every other switch off. Unchosen tubes keep their items locked inside.
                    foreach (var o in self._level.entities)
                        if (o is ArmoryButton b && !b.destroyed && !b.isActivated) b.deactivate();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[LocalCoop] Co-op armory failed; falling back to the game's single-player armory");
                orig(self, by, lp);
            }
        }

        private int ReleasePipe(ArmoryState st, ArmoryButton btn, Hero forHero)
        {
            var lvl = btn._level;
            var map = lvl?.map;
            if (lvl == null || map == null) return 0;
            var room = map.getRoomAt(btn.cx, btn.cy);
            double bx = btn.cx + btn.xr;
            var list = new List<PipeItem>();
            foreach (var o in lvl.entities)
            {
                if (o is not ItemDrop d || d.destroyed || !d.inArmory) continue;
                if (!ReferenceEquals(map.getRoomAt(d.cx, d.cy), room)) continue;
                if (System.Math.Abs(d.cx + d.xr - bx) >= 3.0) continue;
                d.inArmory = false;
                d.hasGravity = true;
                d.deinterlaceBumpStrength = 0.5;
                Reserve(d, forHero);
                _noParity.AddOrUpdate(d, this);
                try { list.Add(new PipeItem { Item = d.item.clone(false, null), Cx = d.cx, Cy = d.cy, Xr = d.xr, Yr = d.yr }); }
                catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Armory: snapshot of a tube item failed"); }
            }
            st.Released.AddOrUpdate(btn, list);
            return list.Count;
        }

        private int CopyPipeFor(ArmoryState st, ArmoryButton btn, Hero forHero)
        {
            if (!st.Released.TryGetValue(btn, out var list)) return 0;
            int n = 0;
            _spawningClone++;
            try
            {
                foreach (var p in list)
                {
                    bool inArmory = false;
                    var copy = p.Item.clone(false, null);
                    var d = new ItemDrop(btn._level, p.Cx, p.Cy, copy, true, new HaxeProxy.Runtime.Ref<bool>(ref inArmory));
                    d.init();
                    d.setPosCase(p.Cx, p.Cy, p.Xr, p.Yr);
                    Reserve(d, forHero);
                    _noParity.AddOrUpdate(d, this);
                    n++;
                }
            }
            catch (Exception ex) { Logger.Error(ex, "[LocalCoop] Armory: copying the tube for the second player failed"); }
            finally { _spawningClone--; }
            return n;
        }
    }
}
