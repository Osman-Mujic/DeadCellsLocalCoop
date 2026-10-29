using dc;
using dc.h2d;
using dc.libs.heaps;
using dc.pr;
using dc.tool;
using dc.ui;
using HaxeProxy.Runtime;
using ModCore.Menu;

namespace LocalCoop
{
    /// <summary>
    /// The mod's own screens, drawn with the game's UI pieces (Assets.makeText, h2d.Graphics, ui.ControlIcon) on the
    /// game's menu layer (Main.root, Const.ROOT_DP_MENU):
    ///   - "Choose your side": both controllers start in the middle, each player pushes left (player 1) or right
    ///     (player 2) and confirms with A. Opens on the first co-op run, with Back+Start held, or from Options.
    ///   - "Co-op controls": what the extra buttons do, with the game's own button icons (L3 etc.).
    ///   - A "Local Co-op" page in Options > Core Modding (DCCM IModMenu) that opens both and holds a few settings.
    /// While a screen is open the game is paused (if it wasn't), both controllers are locked for the game, and the
    /// screen reads the pads directly.
    /// </summary>
    public partial class LocalCoopMod : IModMenu
    {
        private enum ScreenKind { None, Sides, Controls }

        private ScreenKind _screen = ScreenKind.None;
        private Layers? _screenLayer;
        private dc.h2d.Object? _screenRoot;
        private ControllerAccess? _screenLock1, _screenLock2;
        private bool _screenPausedGame;
        private bool _controlsAfterSides;
        private bool _screenDirty;
        private string _screenError = "";

        // Choose-your-side state, per controller (index 0 = first controller in device order, 1 = second).
        private readonly int[] _side = new int[2];          // -1 = player 1 (left), 0 = undecided, +1 = player 2 (right)
        private readonly bool[] _confirmed = new bool[2];
        private GamePad?[] _sidePads = new GamePad?[2];
        private readonly PadEdges[] _edges = { new(), new() };
        private bool _keyEnterWas = true, _keyEscWas = true;

        private sealed class PadEdges
        {
            public bool Left = true, Right = true, A = true, B = true;   // "was down": start true so a held button can't fire at once
        }

        internal bool ScreenOpen => _screen != ScreenKind.None;

        // ------------------------------------------------------------------ opening / closing

        internal void OpenSidesScreen(bool thenControls)
        {
            var pads = _input.AllConnectedPads();
            if (pads.Count < 2)
            {
                Logger.Information("[LocalCoop] Choose sides: needs two controllers (found {N})", pads.Count);
                if (thenControls) OpenControlsScreen();
                return;
            }
            _sidePads = new[] { pads[0], pads[1] };
            for (int i = 0; i < 2; i++) { _side[i] = 0; _confirmed[i] = false; _edges[i] = new PadEdges(); }
            _controlsAfterSides = thenControls;
            _screenError = "";
            OpenScreen(ScreenKind.Sides);
            for (int i = 0; i < 2; i++) CoopInput.Rumble(_sidePads[i], 0.4, 0.2);
        }

        internal void OpenControlsScreen()
        {
            for (int i = 0; i < 2; i++) _edges[i] = new PadEdges();
            _sidePads = new[] { _input.P1Pad, _input.P2Pad };
            OpenScreen(ScreenKind.Controls);
        }

        private void OpenScreen(ScreenKind kind)
        {
            try
            {
                if (_screen == ScreenKind.None)
                {
                    // Lock both controllers for the game (exclusive access, like the game's own menus).
                    try { _screenLock1 = _input.P1Controller?.createAccess("coopscreen".AsHaxeString(), true); } catch { }
                    try { _screenLock2 = _input.P2Controller?.createAccess("coopscreen2".AsHaxeString(), true); } catch { }
                    var game = Game.Class.ME;
                    _screenPausedGame = false;
                    if (game != null && !game.destroyed && !game.paused) { game.pause(); _screenPausedGame = true; }
                }
                _keyEnterWas = _keyEscWas = true;
                _screen = kind;
                _screenDirty = true;
                Logger.Information("[LocalCoop] Screen opened: {Screen}", kind);
            }
            catch (Exception ex) { Logger.Error(ex, "[LocalCoop] Opening a co-op screen failed"); CloseScreen(); }
        }

        private void CloseScreen()
        {
            var was = _screen;
            _screen = ScreenKind.None;
            try { _screenRoot?.remove(); } catch { }
            _screenRoot = null;
            try { _screenLock1?.dispose(default); } catch { }
            try { _screenLock2?.dispose(default); } catch { }
            _screenLock1 = _screenLock2 = null;
            if (_screenPausedGame)
            {
                try { var g = Game.Class.ME; if (g != null && !g.destroyed) g.resume(); } catch { }
                _screenPausedGame = false;
            }
            // Buttons still held when a screen closes must not act in the game.
            _l3Was1 = _l3Was2 = true;
            _p2StartWas = true;
            _pickHeld1 = _pickHeld2 = double.MaxValue;
            if (was != ScreenKind.None) Logger.Information("[LocalCoop] Screen closed: {Screen}", was);
        }

        // ------------------------------------------------------------------ per frame

        private void UpdateScreens()
        {
            if (_screen == ScreenKind.None) return;
            try
            {
                if (_screen == ScreenKind.Sides) UpdateSides();
                else if (_screen == ScreenKind.Controls) UpdateControls();
                if (_screen != ScreenKind.None && (_screenDirty || _screenRoot == null)) { _screenDirty = false; BuildScreen(); }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[LocalCoop] Co-op screen failed; closing it");
                CloseScreen();
            }
        }

        private void UpdateSides()
        {
            if (_sidePads[0]?.device == null || _sidePads[1]?.device == null || !_sidePads[0]!.device.connected || !_sidePads[1]!.device.connected)
            {
                Logger.Information("[LocalCoop] Choose sides: a controller was disconnected");
                CloseScreen();
                return;
            }
            for (int i = 0; i < 2; i++)
            {
                var pad = _sidePads[i];
                var e = _edges[i];
                bool left = CoopInput.PadDown(pad, 14) || StickX(pad) < -0.5;
                bool right = CoopInput.PadDown(pad, 15) || StickX(pad) > 0.5;
                bool a = CoopInput.PadDown(pad, 0), b = CoopInput.PadDown(pad, 1);
                if (!_confirmed[i])
                {
                    if (left && !e.Left && _side[i] > -1) { _side[i]--; _screenDirty = true; _screenError = ""; CoopInput.Rumble(pad, 0.2, 0.08); }
                    if (right && !e.Right && _side[i] < 1) { _side[i]++; _screenDirty = true; _screenError = ""; CoopInput.Rumble(pad, 0.2, 0.08); }
                }
                if (a && !e.A)
                {
                    if (_side[i] == 0) { _screenError = "Push left for PLAYER 1 or right for PLAYER 2 first"; _screenDirty = true; }
                    else if (_confirmed[1 - i] && _side[1 - i] == _side[i]) { _screenError = "That side is taken: pick the other one"; _screenDirty = true; }
                    else { _confirmed[i] = true; _screenDirty = true; _screenError = ""; CoopInput.Rumble(pad, 0.5, 0.15); }
                }
                if (b && !e.B)
                {
                    if (_confirmed[i]) { _confirmed[i] = false; _screenDirty = true; }
                    else if (!_confirmed[0] && !_confirmed[1]) { CloseScreen(); if (_controlsAfterSides) OpenControlsScreen(); return; }
                }
                e.Left = left; e.Right = right; e.A = a; e.B = b;
            }
            if (_confirmed[0] && _confirmed[1] && _side[0] != _side[1])
            {
                ApplySides();
                bool then = _controlsAfterSides;
                CloseScreen();
                if (then) OpenControlsScreen();
            }
        }

        private void ApplySides()
        {
            var wantP1 = _side[0] < 0 ? _sidePads[0] : _sidePads[1];
            if (CoopInput.Ptr(wantP1) != CoopInput.Ptr(_input.P1Pad))
            {
                if (!_input.SwapPads()) Logger.Warning("[LocalCoop] Choose sides: swapping controllers failed");
            }
            Config.SidesChosen = true;
            try { _config?.Save(); } catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Saving the controller choice failed"); }
            CoopInput.Rumble(_input.P1Pad, 0.6, 0.3);
            Announce();
            Logger.Information("[LocalCoop] Sides chosen: P1={P1} (SwapPads={Swap})", _input.P1Pad?.device?.index, Config.SwapPads);
        }

        private void UpdateControls()
        {
            for (int i = 0; i < 2; i++)
            {
                var pad = _sidePads[i];
                var e = _edges[i];
                bool a = CoopInput.PadDown(pad, 0), b = CoopInput.PadDown(pad, 1);
                if ((a && !e.A) || (b && !e.B)) { CloseScreen(); return; }
                e.A = a; e.B = b;
            }
            bool enter = KeyDown(13), esc = KeyDown(27);
            if ((enter && !_keyEnterWas) || (esc && !_keyEscWas)) { CloseScreen(); return; }
            _keyEnterWas = enter; _keyEscWas = esc;
        }

        private static bool KeyDown(int code)
        {
            try { return dc.hxd.Key.Class.isDown(code); } catch { return false; }
        }

        private static double StickX(GamePad? pad)
        {
            try { return pad?.device?.xAxis ?? 0; } catch { return 0; }
        }

        // ------------------------------------------------------------------ drawing

        private void BuildScreen()
        {
            var main = Main.Class.ME;
            if (main?.root == null) return;
            if (_screenRoot != null) { try { _screenRoot.remove(); } catch { } }
            var root = new dc.h2d.Object(null);
            main.root.addChildAt(root, dc.Const.Class.ROOT_DP_MENU);
            _screenRoot = root;

            var win = dc.hxd.Window.Class.getInstance.Invoke();
            double w = win.get_width(), h = win.get_height();
            var bg = new Graphics(root);
            int black = 0x000000; double alpha = 0.88;
            bg.beginFill(ref black, ref alpha);
            bg.drawRect(0, 0, w, h);
            bg.endFill();

            if (_screen == ScreenKind.Sides) DrawSides(root, w, h);
            else DrawControls(root, w, h);
        }

        private const int White = 0xFFFFFF, Grey = 0x9A9A9A, Gold = 0xFFD24A, Blue = 0x4AA8FF, Red = 0xFF6A5A;

        private dc.ui.Text Label(dc.h2d.Object parent, string s, int color, bool big, double cx, double y, bool center = true)
        {
            var t = Assets.Class.makeText.Invoke(s.AsHaxeString(), color, big, parent);
            double tw = t.get_textWidth() * t.scaleX;
            t.x = center ? cx - tw / 2 : cx;
            t.y = y;
            t.posChanged = true;
            return t;
        }

        private void DrawSides(dc.h2d.Object root, double w, double h)
        {
            double xL = w * 0.22, xM = w * 0.5, xR = w * 0.78;
            Label(root, "CHOOSE YOUR SIDE", White, true, xM, h * 0.10);
            Label(root, "Push your stick left or right, then press A", Grey, false, xM, h * 0.18);

            Label(root, "PLAYER 1", Blue, true, xL, h * 0.28);
            Label(root, "PLAYER 2", Red, true, xR, h * 0.28);

            for (int i = 0; i < 2; i++)
            {
                double y = h * (0.42 + i * 0.14);
                double x = _side[i] < 0 ? xL : _side[i] > 0 ? xR : xM;
                int color = _confirmed[i] ? Gold : White;
                string name = PadName(_sidePads[i]);
                string arrows = !_confirmed[i] && _side[i] > -1 ? "<   " : "";
                string arrowsR = !_confirmed[i] && _side[i] < 1 ? "   >" : "";
                Label(root, $"{arrows}CONTROLLER {i + 1}{arrowsR}", color, true, x, y);
                Label(root, _confirmed[i] ? "Ready  (B: change)" : name, _confirmed[i] ? Gold : Grey, false, x, y + h * 0.055);
            }

            if (_screenError.Length > 0) Label(root, _screenError, Red, false, xM, h * 0.74);

            // Footer: [A] Confirm   [B] Back
            double fy = h * 0.86;
            double gap = w * 0.02;
            double x0 = xM - w * 0.12;
            IconWithText(root, 0, "Confirm", x0, fy, gap);
            IconWithText(root, 1, "Back", xM + w * 0.03, fy, gap);
        }

        private void DrawControls(dc.h2d.Object root, double w, double h)
        {
            double xM = w * 0.5;
            Label(root, "CO-OP CONTROLS", White, true, xM, h * 0.08);

            double xIcon = w * 0.24, xText = w * 0.33, y = h * 0.20, step = h * 0.085;
            void Row(int[] keys, string text)
            {
                double ix = xIcon;
                foreach (var k in keys)
                {
                    var ic = PadIcon(root, k, ix, y + h * 0.02);
                    ix += (ic?.width ?? 0) + w * 0.006;
                }
                Label(root, text, White, false, xText, y, center: false);
                y += step;
            }
            Row(new[] { 10 }, "L3 (click left stick): bring your partner to you");
            Row(new[] { 4, 5 }, "Hold Back + Start: choose which controller is player 1");
            Row(new[] { 5 }, "Start on player 2's controller: player 2's pause menu and inventory");
            Row(new[] { 4 }, "Back on player 1's controller: map");
            Row(Array.Empty<int>(), "Downed? Your partner revives you by standing next to you");
            Row(Array.Empty<int>(), "Loot: what one player takes leaves a copy only the other can take");
            Row(Array.Empty<int>(), "Keys and keycards work for both players");
            Row(Array.Empty<int>(), "Keyboard: F9 add/remove player 2, F10 swap controllers");

            Label(root, "This page: Options > Core Modding > Local Co-op", Grey, false, xM, h * 0.80);
            IconWithText(root, 0, "Close", xM - w * 0.03, h * 0.88, w * 0.02);
        }

        private void IconWithText(dc.h2d.Object root, int key, string text, double x, double y, double gap)
        {
            var ic = PadIcon(root, key, x, y + 12);
            Label(root, text, White, false, x + (ic?.width ?? 24) + gap * 0.5, y, center: false);
        }

        private ControlIcon? PadIcon(dc.h2d.Object parent, int padKey, double x, double y)
        {
            try
            {
                double xr = 0, yr = 0.5;
                var ic = new ControlIcon(new Ref<double>(ref xr), new Ref<double>(ref yr), parent);
                ic.setKey(new MainKey.MK_Gamepad(padKey));
                ic.x = x; ic.y = y; ic.posChanged = true;
                return ic;
            }
            catch (Exception ex)
            {
                if (_hud2Errors.Add("icon:" + ex.Message)) Logger.Warning(ex, "[LocalCoop] Button icon failed");
                return null;
            }
        }

        private static string PadName(GamePad? p)
        {
            try { var n = p?.device?.get_name()?.ToString(); return string.IsNullOrWhiteSpace(n) ? "" : n!; } catch { return ""; }
        }

        // ------------------------------------------------------------------ Options page (DCCM: Options > Core Modding)

        string IModMenu.GetName() => "Local Co-op";
        string? IModMenu.GetSubText() => "Controllers, controls and settings";

        void IModMenu.BuildMenu(dc.ui.Options options)
        {
            options.title.set_text("LOCAL CO-OP".AsHaxeString());
            options.createScroller(1);
            var flow = options.scrollerFlow;

            options.addSimpleWidget("Choose controllers".AsHaxeString(), "Pick which controller is player 1".AsHaxeString(),
                () => OpenSidesScreen(thenControls: false), Ref<int>.In(5), flow);
            options.addSimpleWidget("Co-op controls".AsHaxeString(), "What the extra buttons do".AsHaxeString(),
                () => OpenControlsScreen(), Ref<int>.In(5), flow);

            options.addSeparator("Settings".AsHaxeString(), flow);
            Toggle(options, flow, "Shared camera", "Camera follows the middle point between both players",
                () => Config.SharedCamera, v => Config.SharedCamera = v);
            Toggle(options, flow, "Keep both on screen", "A player can't walk off the edge of the shared screen",
                () => Config.KeepBothOnScreen, v => Config.KeepBothOnScreen = v);
            Toggle(options, flow, "Loot for both", "Items one player takes leave a copy for the other",
                () => Config.LootParity, v => Config.LootParity = v);
            Toggle(options, flow, "Hide minimap", "The full map button still works",
                () => Config.HideMinimap, v => Config.HideMinimap = v);
        }

        private void Toggle(dc.ui.Options options, Flow flow, string name, string desc, Func<bool> get, Action<bool> set)
        {
            options.addToggleWidget(name.AsHaxeString(), desc.AsHaxeString(), () =>
            {
                set(!get());
                try { _config?.Save(); } catch { }
                return get();
            }, Ref<bool>.In(get()), flow);
        }
    }
}
