using dc.libs.heaps;
using dc.tool;
using Serilog;

namespace LocalCoop
{
    /// <summary>
    /// Splits the game's single input stream into two players.
    ///
    /// Dead Cells reads input through tool.Controller. A Controller either merges every
    /// active gamepad, or, when its <c>gc</c> field is set, reads exactly one GamePad.
    /// Physical pads are handed to GamePad objects in creation order (GamePad.onDevice),
    /// so the boot Controller's GamePads already own every pad. We therefore:
    ///   - pin the boot controller (player 1) to the first pad (keyboard stays with P1),
    ///   - create a second Controller (player 2) that reads only the second pad.
    /// With a single pad connected, P1 plays on keyboard and P2 on that pad.
    /// </summary>
    internal sealed class CoopInput
    {
        private readonly ILogger _log;
        public Controller? P1Controller { get; private set; }
        public Controller? P2Controller { get; private set; }
        public GamePad? P1Pad { get; private set; }
        public GamePad? P2Pad { get; private set; }
        private int _lastPadSignature = -1;

        public CoopInput(ILogger log) { _log = log; }

        public bool EnsureCreated()
        {
            if (P2Controller != null) return true;
            var boot = dc.Boot.Class.ME;
            if (boot?.controller == null) return false;
            P1Controller = boot.controller;
            var c2 = new Controller(boot.s2d);
            c2.normalBindings = P1Controller.normalBindings;
            c2.customBindings = P1Controller.customBindings;
            c2.useCustomBindings = P1Controller.useCustomBindings;
            P2Controller = c2;
            CaptureModes();
            c2.mode = Controller.Class.ENABLE_PAD;   // P2 never reads the keyboard
            if (PadMode is not null) c2.mainMode = PadMode;
            _log.Information("[LocalCoop] Created player 2 controller");
            Refresh(force: true);
            return true;
        }

        /// <summary>Re-pin pads when pads are plugged or unplugged. Cheap; call every frame.</summary>
        public void Refresh(bool force = false)
        {
            if (P1Controller == null || P2Controller == null) return;
            var pads = new List<GamePad>();
            foreach (var o in P1Controller.allGamepads)
            {
                if (o is GamePad gp && gp.device != null && gp.device.connected)
                    pads.Add(gp);
            }
            pads.Sort((a, b) => a.device.index.CompareTo(b.device.index));
            int sig = pads.Count == 0 ? 0 : pads.Aggregate(17, (h, p) => h * 31 + p.device.index + 1);
            if (!force && sig == _lastPadSignature) return;
            _lastPadSignature = sig;

            if (pads.Count >= 2 && !LocalCoopMod.Config.P2UsesFirstPad)
            {
                // Physical order (device index) is fixed per controller; SwapPads (chosen in game with Back+Start,
                // saved in the config) flips which one is player 1.
                bool swap = LocalCoopMod.Config.SwapPads;
                P1Pad = swap ? pads[1] : pads[0];
                P2Pad = swap ? pads[0] : pads[1];
                P1Controller.gc = P1Pad;     // P1: keyboard + pad 1
            }
            else if (pads.Count >= 1)
            {
                P1Pad = null;
                P2Pad = pads[0];
                P1Controller.gc = null;
                // P1 must not also read P2's pad: keyboard only.
                P1Controller.mode = Controller.Class.ENABLE_KEY;
                if (KbMode is not null) P1Controller.mainMode = KbMode;
            }
            else
            {
                P1Pad = null;
                P2Pad = null;
                P1Controller.gc = null;
            }
            if (pads.Count >= 2 && !LocalCoopMod.Config.P2UsesFirstPad)
                P1Controller.mode = Controller.Class.ENABLE_PAD | Controller.Class.ENABLE_KEY;

            P2Controller.gc = P2Pad;
            if (PadMode is null) CaptureModes();
            _log.Information("[LocalCoop] Pads: {Count} connected. P1={P1} P2={P2}",
                pads.Count, Describe(P1Pad, "keyboard"), Describe(P2Pad, "none"));
        }

        /// <summary>Hand all pads back to player 1 (co-op switched off).</summary>
        public void Release()
        {
            if (P1Controller == null) return;
            P1Controller.gc = null;
            P1Controller.mode = Controller.Class.ENABLE_PAD | Controller.Class.ENABLE_KEY;
            if (P2Controller != null) P2Controller.gc = null;
            _lastPadSignature = -1;
        }

        public bool HasP2Input => P2Pad != null;

        /// <summary>Connected pads in device order (the same order Refresh uses before SwapPads).</summary>
        public List<GamePad> AllConnectedPads()
        {
            var pads = new List<GamePad>();
            if (P1Controller == null) return pads;
            foreach (var o in P1Controller.allGamepads)
                if (o is GamePad gp && gp.device != null && gp.device.connected) pads.Add(gp);
            pads.Sort((a, b) => a.device.index.CompareTo(b.device.index));
            return pads;
        }

        private static string Describe(GamePad? p, string none)
        {
            if (p?.device == null) return none;
            string name = "";
            try { name = p.device.get_name()?.ToString() ?? ""; } catch { }
            return $"pad#{p.device.index} ({name})";
        }

        /// <summary>Swap which controller is player 1 (saved). Returns false when fewer than two pads are connected.</summary>
        public bool SwapPads()
        {
            if (P1Pad == null || P2Pad == null) return false;
            LocalCoopMod.Config.SwapPads = !LocalCoopMod.Config.SwapPads;
            Refresh(force: true);
            return true;
        }

        public static void Rumble(GamePad? p, double strength, double seconds)
        {
            try { p?.device?.rumble(strength, seconds); } catch { }
        }

        // ---------------------------------------------------------------- prompt mode (keyboard / gamepad icons)
        // Haxe enum values without arguments are singletons and the game compares them by pointer
        // (Controller.updateMainMode f@2447, ControlIcon.setAction f@4401). `new MainMode.MM_Gamepad()` from C#
        // makes a NEW value that equals neither, which froze the prompts on keyboard icons (playtests 7-8).
        // The real singletons are taken from the game: updateMainMode with keyboard only stores MM_Keyboard,
        // onAnyPadEnabled on an empty pad list stores MM_Gamepad. Done on P2's controller (nothing listens to it).

        public MainMode? KbMode { get; private set; }
        public MainMode? PadMode { get; private set; }

        private void CaptureModes()
        {
            var c2 = P2Controller;
            if (c2 == null || (KbMode is not null && PadMode is not null)) return;
            int savedMode = c2.mode;
            var savedGc = c2.gc;
            try
            {
                if (KbMode is null)
                {
                    c2.mode = Controller.Class.ENABLE_KEY;
                    c2.updateMainMode();
                    KbMode = c2.mainMode;
                }
                if (PadMode is null)
                {
                    GamePad? any = null;
                    foreach (var o in (P1Controller ?? c2).allGamepads) { if (o is GamePad g) { any = g; break; } }
                    if (any != null)
                    {
                        var act = c2.activeGamepads;
                        var saved = new List<object>();
                        foreach (var o in act) saved.Add(o);
                        act.splice(0, saved.Count);
                        c2.onAnyPadEnabled(any);          // list goes 0 -> 1: mainMode = MM_Gamepad singleton
                        PadMode = c2.mainMode;
                        act.splice(0, act.length);
                        foreach (var o in saved) act.push(o);
                    }
                }
                _log.Information("[LocalCoop] Prompt modes captured (keyboard={Kb}, pad={Pad})", KbMode is not null, PadMode is not null);
            }
            catch (Exception ex) { _log.Warning(ex, "[LocalCoop] Capturing prompt modes failed"); }
            finally { c2.mode = savedMode; c2.gc = savedGc; }
        }

        internal static IntPtr Ptr(object? o) => o is HaxeProxy.Runtime.HaxeProxyBase b ? b.HashlinkPointer : IntPtr.Zero;

        /// <summary>Switch P1's prompts to pad icons if they aren't already.</summary>
        public bool SetP1PadPrompts()
        {
            var c1 = P1Controller;
            if (c1 == null || PadMode is null) return false;
            if (Ptr(c1.mainMode) == Ptr(PadMode)) return false;
            c1.mainMode = PadMode;
            try { c1.onChange?.Invoke(); } catch { }
            return true;
        }

        /// <summary>True while button <paramref name="k"/> (GamePad index: A0 B1 X2 Y3 Back4 Start5 LT6 RT7 LB8 RB9 L3=10 R3=11) is held.</summary>
        public static bool PadDown(GamePad? pad, int k)
        {
            try { return pad?.device != null && k >= 0 && Convert.ToInt32(pad.toggles.getDyn(k)) > 0; }
            catch { return false; }
        }

        public static bool AnyButtonDown(GamePad? pad)
        {
            for (int k = 0; k < 16; k++) if (PadDown(pad, k)) return true;
            return false;
        }

        public ControllerAccess CreateP2Access(bool exclusive = false)
        {
            EnsureCreated();
            var ca = P2Controller!.createAccess((exclusive ? "p2menu" : "hero").AsHaxeString(), exclusive);
            ca.rightDeadZone = 0;
            return ca;
        }
    }
}
