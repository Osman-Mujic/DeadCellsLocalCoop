using dc;
using dc.en;
using dc.pr;
using HaxeProxy.Runtime;
using ModCore.Events.Interfaces.Game;
using ModCore.Mods;
using ModCore.Storage;

namespace LocalCoop
{
    public sealed class CoopConfig
    {
        /// <summary>Spawn player 2 automatically when a second input device is available.</summary>
        public bool AutoSpawnP2 { get; set; } = true;
        /// <summary>Force P1 = keyboard, P2 = first pad, even with two pads plugged in.</summary>
        public bool P2UsesFirstPad { get; set; } = false;
        /// <summary>Camera follows the midpoint of both players.</summary>
        public bool SharedCamera { get; set; } = true;
        /// <summary>Players can't walk off the shared screen: the camera only moves when both go that way.</summary>
        public bool KeepBothOnScreen { get; set; } = true;
        /// <summary>How close (cells) to the screen edge a player may get when KeepBothOnScreen is on.</summary>
        public int ScreenEdgeMarginCells { get; set; } = 3;
        /// <summary>Max distance (cells) before the trailing player is pulled to the leader (KeepBothOnScreen off).</summary>
        public int LeashCellsX { get; set; } = 34;
        public int LeashCellsY { get; set; } = 20;
        public double LeashGraceSeconds { get; set; } = 1.5;
        /// <summary>Downed players come back with this fraction of max life.</summary>
        public double ReviveLifeFraction { get; set; } = 0.4;
        /// <summary>Seconds a living partner must stand next to a downed one to revive them.</summary>
        public double ReviveSeconds { get; set; } = 2.5;
        public int ReviveRadiusCells { get; set; } = 3;
        /// <summary>Duplicate per-player loot (weapons, skills, mutations, scrolls) for the other player.</summary>
        public bool LootParity { get; set; } = true;
        /// <summary>Show player 2's health, flasks and items in the bottom-right corner.</summary>
        public bool P2Hud { get; set; } = true;
        /// <summary>Old setting (v0.3-v0.5), no longer used: the minimap is shown top-left in co-op.</summary>
        public bool ShowMinimap { get; set; } = false;
        /// <summary>Hide the corner minimap in co-op (it keeps recording explored areas; the map button still works).</summary>
        public bool HideMinimap { get; set; } = false;
        /// <summary>Which of the two controllers is player 1 (toggled in game: hold Back+Start on a controller, or F10).</summary>
        public bool SwapPads { get; set; } = false;
        /// <summary>Set once the players picked their sides on the "Choose your side" screen (it then stops opening by itself).</summary>
        public bool SidesChosen { get; set; } = false;
        /// <summary>Player 2's own outfit and head (chosen in the outfit room / head room by P2). Empty = same as P1.</summary>
        public string P2Skin { get; set; } = "";
        public string P2Head { get; set; } = "";
        /// <summary>Write extra diagnostics to the DCCM log.</summary>
        public bool Verbose { get; set; } = true;
    }

    public partial class LocalCoopMod(ModInfo info) : ModBase(info), IOnFrameUpdate
    {
        internal static CoopConfig Config { get; private set; } = new();
        internal static LocalCoopMod? Instance { get; private set; }

        private Config<CoopConfig>? _config;
        private CoopInput _input = null!;

        /// <summary>The second player's hero. Null when co-op is inactive.</summary>
        internal Hero? P2 { get; private set; }
        /// <summary>
        /// Player 1's hero. Not simply game.hero: during "P2's turn" game.hero points at P2, and v0.3.2 then
        /// treated P2 as P1 (a scroll chosen by P2 was handed to P2 a second time).
        /// </summary>
        internal Hero? P1
        {
            get
            {
                var g = Game.Class.ME?.hero;
                if (g != null && !ReferenceEquals(g, P2)) _p1Ref = g;
                if (_p1Ref != null && _p1Ref.destroyed) _p1Ref = null;
                return _p1Ref ?? (ReferenceEquals(g, P2) ? null : g);
            }
        }
        private Hero? _p1Ref;

        private readonly DownState _p1Down = new();
        private readonly DownState _p2Down = new();
        private bool _spawningP2;
        private bool _cleaningP2;
        private double _leashTimer;
        private double _mirrorP1VisibilityUntil;
        private bool _toggleKeyWasDown;
        private bool _openSidesNextFrame;

        public override void Initialize()
        {
            Instance = this;
            _config = new Config<CoopConfig>("DeadCellsLocalCoop");
            Config = _config.Value;
            _input = new CoopInput(Logger);

            Hook_Hero.init += Hook_Hero_init;
            Hook_Hero.clean += Hook_Hero_clean;
            Hook_Hero.kill += Hook_Hero_kill;
            Hook_Game.initHero += Hook_Game_initHero;
            Hook_Game.onDispose += Hook_Game_onDispose;
            Hook_Viewport.update += Hook_Viewport_update;
            InitLootParity();

            Logger.Information("[LocalCoop] v{Version} loaded. AutoSpawnP2={Auto} SharedCamera={Cam} KeepBothOnScreen={Kb} LootParity={Loot} P2Hud={Hud}",
                Info.Version, Config.AutoSpawnP2, Config.SharedCamera, Config.KeepBothOnScreen, Config.LootParity, Config.P2Hud);
        }

        internal bool IsP2(Hero? h) => h != null && P2 != null && ReferenceEquals(h, P2);
        internal bool CoopActive => P2 != null && !P2.destroyed;
        internal Hero? Partner(Hero h) => IsP2(h) ? P1 : (ReferenceEquals(h, P1) ? P2 : null);

        // ---------------------------------------------------------------- spawning

        private void SpawnP2()
        {
            var game = Game.Class.ME;
            var p1 = game?.hero;
            var lvl = p1?._level;
            if (game == null || p1 == null || lvl == null || p1.destroyed || !p1.awake) return;
            if (!_input.EnsureCreated() || !_input.HasP2Input) return;

            _spawningP2 = true;
            try
            {
                var p2 = new dc.en.hero.Beheaded(game);
                P2 = p2;                       // must be set before wakeup: init hook keys off it
                p2.wakeup(lvl, p1.cx, p1.cy);  // -> Hero.init -> our hook swaps the controller
                p2.refillAllAmmo();
                _p2Down.Reset();
                MarkInventoryOwned(p1);
                MarkInventoryOwned(p2);
                CreateHud2();
                // First co-op run: let the players pick their sides, then show the co-op controls.
                if (!Config.SidesChosen && _input.AllConnectedPads().Count >= 2) _openSidesNextFrame = true;
                Logger.Information("[LocalCoop] Player 2 spawned in {Level} at {Cx},{Cy} life={Life}/{Max} items={Items}",
                    lvl.map?.id?.ToString(), p1.cx, p1.cy, p2.life, p2.maxLife, DescribeInventory(p2));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[LocalCoop] Failed to spawn player 2");
                TryDestroyP2();
            }
            finally { _spawningP2 = false; }
        }

        private void TryDestroyP2()
        {
            var p2 = P2;
            P2 = null;
            _p2Down.Reset();
            _p1Down.Reset();
            DestroyHud2();
            if (p2 == null) return;
            try { if (p2._level != null) p2.clean(); } catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] P2 clean failed"); }
            try { if (!p2.destroyed) p2.destroy(); } catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] P2 destroy failed"); }
            Logger.Information("[LocalCoop] Player 2 removed");
        }

        // ---------------------------------------------------------------- hooks

        private void Hook_Hero_init(Hook_Hero.orig_init orig, Hero self)
        {
            orig(self);
            if (!IsP2(self)) return;
            try
            {
                // Hero.init always binds to the boot controller; give P2 its own pad.
                self.controller = _input.CreateP2Access();
                if (Config.Verbose) Logger.Information("[LocalCoop] P2 controller bound");
            }
            catch (Exception ex) { Logger.Error(ex, "[LocalCoop] Binding P2 controller failed"); }
        }

        /// <summary>The game calls hero.clean() before leaving a level; take P2 out of it too.</summary>
        private void Hook_Hero_clean(Hook_Hero.orig_clean orig, Hero self)
        {
            orig(self);
            if (_cleaningP2 || P2 == null || !ReferenceEquals(self, P1)) return;
            _cleaningP2 = true;
            try
            {
                var lvl = P2._level;
                if (lvl != null && !P2.destroyed)
                {
                    int lx = lvl.lastHeroCX, ly = lvl.lastHeroCY;   // Hero.clean stores its own position there
                    P2.clean();
                    lvl.lastHeroCX = lx; lvl.lastHeroCY = ly;
                }
            }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] P2 clean on level exit failed"); }
            finally { _cleaningP2 = false; }
        }

        /// <summary>After P1 is placed in a (new) level, bring P2 along next to them.</summary>
        private Hero Hook_Game_initHero(Hook_Game.orig_initHero orig, Game self, Level lvl, int cx, int cy,
            dc.en.UsableBody from, bool fromDeadBody, Level oldLevel)
        {
            var hero = orig(self, lvl, cx, cy, from, fromDeadBody, oldLevel);
            var p2 = P2;
            if (p2 == null || p2.destroyed || ReferenceEquals(p2, hero)) return hero;
            try
            {
                if (p2._level != null && !ReferenceEquals(p2._level, lvl)) p2.clean();
                if (!p2.awake)
                {
                    p2.wakeup(lvl, cx, cy);
                }
                else if (p2._level == null)
                {
                    p2.set_level(lvl);
                    p2.set_team(lvl.teamHero);
                    p2.initGfx();
                    p2.setPosCase(cx, cy, null, null);
                    p2.cancelVelocities();
                    try { p2.cancelMainSkills(null); } catch { }
                    p2.visible = true;
                }
                p2.set_sprAlpha(1.0);
                // Door/level transitions fade "the hero" in by hand; P2 copies P1's visibility for a moment.
                _mirrorP1VisibilityUntil = Clock.Now + 4.0;
                p2.refillAllAmmo();
                try { p2.onLevelChanged(oldLevel); } catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] P2 onLevelChanged"); }
                // New level: downed players get back up.
                if (_p2Down.Active) Revive(p2, _p2Down, "level change");
                if (_p1Down.Active) Revive(hero, _p1Down, "level change");
                MarkInventoryOwned(hero);
                MarkInventoryOwned(p2);
                RefreshHud2();
                if (Config.Verbose) Logger.Information("[LocalCoop] P2 followed into {Level} at {Cx},{Cy}", lvl.map?.id?.ToString(), cx, cy);
            }
            catch (Exception ex) { Logger.Error(ex, "[LocalCoop] Moving P2 to new level failed"); }
            return hero;
        }

        private void Hook_Game_onDispose(Hook_Game.orig_onDispose orig, Game self)
        {
            TryDestroyP2();
            orig(self);
        }

        /// <summary>
        /// Shared camera: Viewport.update aims at tracked.spr.x/y. While it runs we park P1's sprite
        /// at the point the camera should look at (midpoint, or the living player), then put it back.
        /// Sprite positions are rebuilt from cx/xr every frame, so nothing else ever sees the swap.
        /// </summary>
        private void Hook_Viewport_update(Hook_Viewport.orig_update orig, Viewport self)
        {
            var p1 = P1;
            var p2 = P2;
            if (!Config.SharedCamera || p1 == null || p2 == null || p2.destroyed || p1.spr == null || p2.spr == null
                || !ReferenceEquals(self.tracked, p1) || !ReferenceEquals(p1._level, p2._level))
            {
                orig(self);
                return;
            }
            var s1 = p1.spr;
            double ox = s1.x, oy = s1.y;
            double tx, ty;
            if (_p1Down.Active && !_p2Down.Active) { tx = p2.spr.x; ty = p2.spr.y; }
            else if (_p2Down.Active) { orig(self); return; }
            else { tx = (s1.x + p2.spr.x) * 0.5; ty = (s1.y + p2.spr.y) * 0.5; }
            s1.x = tx; s1.y = ty;
            try { orig(self); }
            finally { s1.x = ox; s1.y = oy; }
        }

        // ---------------------------------------------------------------- per-frame

        void IOnFrameUpdate.OnFrameUpdate(double dt)
        {
            try
            {
                HandleToggleKey();
                if (_input.EnsureCreated()) _input.Refresh();
                UpdateScreens();
                if (!ScreenOpen) UpdatePadPicker();

                var p1 = P1;
                if (p1 == null || p1.destroyed || p1._level == null)
                    return;

                if (P2 != null && P2.destroyed) { Logger.Warning("[LocalCoop] P2 was destroyed by the game"); P2 = null; }
                if (P2 == null && Config.AutoSpawnP2 && _input.HasP2Input && !_spawningP2 && IsRunLevel(p1._level))
                    SpawnP2();
                if (P2 == null) return;
                if (_openSidesNextFrame && !ScreenOpen) { _openSidesNextFrame = false; OpenSidesScreen(thenControls: true); }

                if (Clock.Now < _mirrorP1VisibilityUntil && !_p2Down.Active && ReferenceEquals(p1._level, P2._level))
                {
                    P2.visible = p1.visible;
                    P2.set_sprAlpha(p1.sprAlpha);
                }
                if (_p1SkinSaved == null) KeepCameraOnPlayers();
                UpdateDowned(p1, P2, dt);
                UpdateTeleportFollow(p1, P2);
                UpdateLeash(p1, P2, dt);
                if (!ScreenOpen)
                {
                    UpdateRegroup(p1, P2);
                    UpdateP2Pause(P2);
                }
                UpdateHudUpkeep(p1, P2);
                UpdatePromptMode();
                UpdateLootParity(dt);
                if (CoopTestBot.Enabled) CoopTestBot.Tick(this, dt);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[LocalCoop] Frame update failed");
            }
        }

        private static bool IsRunLevel(Level lvl)
        {
            var game = lvl.game;
            if (game == null) return false;
            try { if (game.isTraining()) return false; } catch { }
            return true;
        }

        private void HandleToggleKey()
        {
            // F9 toggles player 2 in and out.
            bool down = dc.hxd.Key.Class.isDown(120);
            if (down && !_toggleKeyWasDown)
            {
                if (P2 != null) { TryDestroyP2(); Config.AutoSpawnP2 = false; _input.Release(); }
                else { Config.AutoSpawnP2 = true; _input.Refresh(force: true); SpawnP2(); }
            }
            _toggleKeyWasDown = down;
        }

        private void UpdateLeash(Hero p1, Hero p2, double dt)
        {
            if (_p1Down.Active || _p2Down.Active || !ReferenceEquals(p1._level, p2._level)) { _leashTimer = 0; return; }

            // Screen-bound camera: both players must stay inside one screen. The camera sits at the
            // midpoint, so we cap how far apart they can be horizontally; whoever walks into the
            // edge is held there until the partner follows. Sizes come from the live viewport
            // (level pixels, 24 per cell), so this adapts to resolution and zoom.
            var vp = p1._level.viewport;
            double screenCellsX = vp != null && vp.wid > 0 ? vp.wid / 24.0 : Config.LeashCellsX;
            double screenCellsY = vp != null && vp.hei > 0 ? vp.hei / 24.0 : Config.LeashCellsY;
            if (Config.KeepBothOnScreen)
                ClampHorizontalSpan(p1, p2, screenCellsX - 2 * Config.ScreenEdgeMarginCells);

            // Vertical gaps can't be held (gravity, drops), so a player who ends up a screen
            // above/below for a moment is brought back to the other.
            double limitX = Config.KeepBothOnScreen ? screenCellsX + 4 : Config.LeashCellsX;
            double limitY = Config.KeepBothOnScreen ? screenCellsY - Config.ScreenEdgeMarginCells : Config.LeashCellsY;
            int dx = System.Math.Abs(p1.cx - p2.cx), dy = System.Math.Abs(p1.cy - p2.cy);
            if (dx > limitX || dy > limitY)
            {
                if (_leashTimer <= 0) _leashTimer = Clock.Now;
                if (Clock.Now - _leashTimer >= Config.LeashGraceSeconds)
                {
                    p2.setPosCase(p1.cx, p1.cy, null, null);
                    p2.cancelVelocities();
                    Remember(p2, _last2);   // our own pull is not a teleport
                    _leashTimer = 0;
                    if (Config.Verbose) Logger.Information("[LocalCoop] Leash: pulled P2 to P1 ({Dx},{Dy} cells apart)", dx, dy);
                }
            }
            else _leashTimer = 0;
        }

        private static double PosX(Hero h) => h.cx + h.xr;

        /// <summary>
        /// If the players are further apart than <paramref name="maxSpan"/> cells, put the one who is
        /// moving away back at the limit and stop their horizontal motion. Position is moved back
        /// toward the partner, i.e. through space the player just crossed, so it can't push them
        /// into a wall.
        /// </summary>
        /// <summary>
        /// Keep both players on one screen: when they are <paramref name="maxSpan"/> cells apart, a player
        /// walking further away is stopped at the edge. Walking back toward the partner is never blocked.
        /// (v0.1.1-0.2.1 also snapped whoever moved; once the gap exceeded the limit, e.g. after the camera
        /// zoom changed, every step either player took got undone and both were stuck.)
        /// </summary>
        private void ClampHorizontalSpan(Hero p1, Hero p2, double maxSpan)
        {
            if (maxSpan < 6) return;
            double x1 = PosX(p1), x2 = PosX(p2);
            if (System.Math.Abs(x1 - x2) < maxSpan) return;
            Hero left = x1 < x2 ? p1 : p2, right = x1 < x2 ? p2 : p1;
            double lx = PosX(left), rx = PosX(right);
            HoldAtEdge(left, -1, rx - maxSpan);
            HoldAtEdge(right, +1, lx + maxSpan);
        }

        /// <summary>
        /// If <paramref name="h"/> is moving outward (-1 = left, +1 = right), cancel that velocity and ease
        /// them back to <paramref name="limitX"/> (at most half a cell per frame, so a camera zoom change never
        /// teleports anyone). A player moving inward, or standing still, is left alone.
        /// </summary>
        private static void HoldAtEdge(Hero h, int outward, double limitX)
        {
            bool movingOut = h.dx * outward > 0 || h.bdx * outward > 0;
            if (!movingOut) return;
            if (h.dx * outward > 0) h.dx = 0;
            if (h.bdx * outward > 0) h.bdx = 0;
            double x = PosX(h);
            if ((x - limitX) * outward <= 0) return;
            double target = outward > 0 ? System.Math.Max(limitX, x - 0.5) : System.Math.Min(limitX, x + 0.5);
            int cx = (int)System.Math.Floor(target);
            h.cx = cx;
            h.xr = target - cx;
        }

        internal static string DescribeInventory(Hero h)
        {
            try { return $"{h.inventory?.items?.length ?? -1} items"; } catch { return "?"; }
        }
    }
}
