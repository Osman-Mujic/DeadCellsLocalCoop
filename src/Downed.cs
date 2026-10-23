using dc.en;

namespace LocalCoop
{
    internal static class Clock
    {
        private static readonly System.Diagnostics.Stopwatch Sw = System.Diagnostics.Stopwatch.StartNew();
        public static double Now => Sw.Elapsed.TotalSeconds;
    }

    internal sealed class DownState
    {
        public bool Active;
        public int Cx, Cy;
        public double ReviveProgress;   // seconds a partner has spent next to the body
        public double Since;
        public void Reset() { Active = false; ReviveProgress = 0; }
    }

    public partial class LocalCoopMod
    {
        private double _lastDownTick;

        /// <summary>
        /// Death rule: a player who dies while their partner is alive goes down instead of dying.
        /// The partner revives them by standing next to the body, or both get up at the next level.
        /// Only when the last player standing dies does the game's own death (and game over) run.
        /// </summary>
        private void Hook_Hero_kill(Hook_Hero.orig_kill orig, Hero self)
        {
            if (P2 == null || P2.destroyed)
            {
                orig(self);
                return;
            }
            bool isP2 = IsP2(self);
            bool isP1 = ReferenceEquals(self, P1);
            if (!isP1 && !isP2) { orig(self); return; }

            var mine = isP2 ? _p2Down : _p1Down;
            var other = isP2 ? _p1Down : _p2Down;
            if (mine.Active) return; // already down; ignore repeated kills

            if (!other.Active)
            {
                GoDown(self, mine, isP2 ? "P2" : "P1");
                return;
            }

            // Last player standing just died.
            if (isP2)
            {
                // P2 is the survivor: P1 is down. End the run through P1 so the game's flow is intact.
                GoDown(self, mine, "P2");
                Logger.Information("[LocalCoop] Both players down: run over");
                var p1 = P1;
                _p1Down.Reset();
                if (p1 != null) { RestoreBody(p1); p1.life = 0; orig(p1); }
            }
            else
            {
                Logger.Information("[LocalCoop] Both players down: run over");
                _p1Down.Reset();
                RestoreBody(self);
                orig(self);
            }
        }

        private void GoDown(Hero h, DownState st, string who)
        {
            st.Active = true;
            st.Cx = h.cx;
            st.Cy = h.cy;
            st.ReviveProgress = 0;
            st.Since = Clock.Now;
            try
            {
                h.life = 0;
                h.set_targetable(false);
                h.cancelVelocities();
                if (h.controller != null) h.controller.manualLock = true;
                h.set_sprAlpha(0.35);
            }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] GoDown {Who}", who); }
            Logger.Information("[LocalCoop] {Who} is down at {Cx},{Cy}. Partner can revive by standing close.", who, st.Cx, st.Cy);
        }

        private static void RestoreBody(Hero h)
        {
            try
            {
                h.set_targetable(true);
                if (h.controller != null) h.controller.manualLock = false;
                h.set_sprAlpha(1.0);
            }
            catch { }
        }

        private void Revive(Hero h, DownState st, string reason)
        {
            st.Reset();
            RestoreBody(h);
            int life = System.Math.Max(1, (int)(h.maxLife * Config.ReviveLifeFraction));
            try { h.setLifeAndRally(life, 0); } catch { h.life = life; }
            Logger.Information("[LocalCoop] {Who} revived ({Reason}) with {Life}/{Max}", IsP2(h) ? "P2" : "P1", reason, life, h.maxLife);
        }

        private void UpdateDowned(Hero p1, Hero p2, double dt)
        {
            double now = Clock.Now;
            double step = _lastDownTick <= 0 ? 0 : System.Math.Min(0.25, now - _lastDownTick);
            _lastDownTick = now;
            TickDown(p2, p1, _p2Down, step);
            TickDown(p1, p2, _p1Down, step);
        }

        private void TickDown(Hero downed, Hero helper, DownState st, double step)
        {
            if (!st.Active) return;
            // Keep the body still and harmless.
            try
            {
                downed.life = 0;
                downed.cancelVelocities();
                if (downed.controller != null) downed.controller.manualLock = true;
            }
            catch { }
            if (helper.destroyed || helper._level == null || !ReferenceEquals(helper._level, downed._level)) return;
            int r = Config.ReviveRadiusCells;
            bool close = System.Math.Abs(helper.cx - downed.cx) <= r && System.Math.Abs(helper.cy - downed.cy) <= r && helper.life > 0;
            st.ReviveProgress = close ? st.ReviveProgress + step : System.Math.Max(0, st.ReviveProgress - step * 2);
            if (st.ReviveProgress >= Config.ReviveSeconds)
                Revive(downed, st, "partner");
        }
    }
}
