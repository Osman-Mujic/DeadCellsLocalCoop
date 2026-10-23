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
            c2.mode = Controller.Class.ENABLE_PAD;   // P2 never reads the keyboard
            c2.mainMode = new MainMode.MM_Gamepad();
            P2Controller = c2;
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
                P1Pad = pads[0];
                P2Pad = pads[1];
                P1Controller.gc = P1Pad;     // P1: keyboard + pad 1
            }
            else if (pads.Count >= 1)
            {
                P1Pad = null;
                P2Pad = pads[0];
                P1Controller.gc = null;
                // P1 must not also read P2's pad: keyboard only.
                P1Controller.mode = Controller.Class.ENABLE_KEY;
                P1Controller.mainMode = new MainMode.MM_Keyboard();
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
            _log.Information("[LocalCoop] Pads: {Count} connected. P1={P1} P2={P2}",
                pads.Count,
                P1Pad == null ? "keyboard" : $"pad#{P1Pad.device.index}",
                P2Pad == null ? "none" : $"pad#{P2Pad.device.index}");
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

        public ControllerAccess CreateP2Access(bool exclusive = false)
        {
            EnsureCreated();
            var ca = P2Controller!.createAccess((exclusive ? "p2menu" : "hero").AsHaxeString(), exclusive);
            ca.rightDeadZone = 0;
            return ca;
        }
    }
}
