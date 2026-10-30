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
        public double FarSince;         // when the partner got far from the body (0 = not far)
        public void Reset() { Active = false; ReviveProgress = 0; FarSince = 0; }
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

        private void Revive(Hero h, DownState st, string reason, Hero? helper = null)
        {
            st.Reset();
            RestoreBody(h);
            // Stand up where the partner stands (on ground they are alive on), not inside whatever downed you:
            // in the Lighthouse a revived player stood up in the green fire, couldn't move and went down again
            // (playtest 10, bug 10).
            if (helper != null && !helper.destroyed && helper.life > 0 && ReferenceEquals(helper._level, h._level))
            {
                try { MoveTo(h, helper); } catch { }
            }
            int life = System.Math.Max(1, (int)(h.maxLife * Config.ReviveLifeFraction));
            try { h.setLifeAndRally(life, 0); } catch { h.life = life; }
            try
            {
                h.awake = true;
                h.unlockControls();
                h.cancelSkillControlLock();
                h.setIntengible(true, 2.0);   // a moment to get clear
            }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Revive cleanup"); }
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
            BodyFollows(downed, helper, st);
            int r = Config.ReviveRadiusCells;
            bool close = System.Math.Abs(helper.cx - downed.cx) <= r && System.Math.Abs(helper.cy - downed.cy) <= r && helper.life > 0;
            st.ReviveProgress = close ? st.ReviveProgress + step : System.Math.Max(0, st.ReviveProgress - step * 2);
            if (st.ReviveProgress >= Config.ReviveSeconds)
                Revive(downed, st, "partner", helper);
        }

        /// <summary>
        /// A downed body is carried along: when the living partner has been about half a screen away for a second,
        /// the body is moved next to them. The level keeps following "the hero" (P1), so a downed P1 left behind
        /// held the Lighthouse climb (camera, rising fire) at the body and the partner could not go on
        /// (playtest 10, bug 10). The partner still has to stand by the body to revive it.
        /// </summary>
        private void BodyFollows(Hero downed, Hero helper, DownState st)
        {
            if (helper.life <= 0) { st.FarSince = 0; return; }
            double cellsX = 30, cellsY = 17;
            try
            {
                var vp = helper._level?.viewport;
                if (vp != null && vp.wid > 0) cellsX = vp.wid / 24.0;
                if (vp != null && vp.hei > 0) cellsY = vp.hei / 24.0;
            }
            catch { }
            bool far = System.Math.Abs(helper.cx - downed.cx) > cellsX * 0.45 || System.Math.Abs(helper.cy - downed.cy) > cellsY * 0.45;
            if (!far) { st.FarSince = 0; return; }
            double now = Clock.Now;
            if (st.FarSince <= 0) { st.FarSince = now; return; }
            if (now - st.FarSince < 1.0) return;
            st.FarSince = 0;
            try
            {
                MoveTo(downed, helper);
                downed.cancelVelocities();
                st.Cx = downed.cx; st.Cy = downed.cy;
                Logger.Information("[LocalCoop] {Who}'s body carried to the partner", IsP2(downed) ? "P2" : "P1");
            }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Moving the downed body failed"); }
        }
    }
}
