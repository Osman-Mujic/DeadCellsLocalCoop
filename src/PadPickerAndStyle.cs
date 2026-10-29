using dc;
using dc.en;
using dc.en.inter;
using dc.en.inter.npc;
using dc.libs.heaps;
using dc.pr;
using dc.tool;
using dc.ui.sel;

namespace LocalCoop
{
    /// <summary>
    /// 1. Choosing which controller is player 1: hold Back + Start on a controller for one second and that
    ///    controller becomes player 1 (F10 on the keyboard swaps too). Saved in the config, so it sticks.
    /// 2. Outfits and heads per player. The game keeps ONE outfit and ONE head in the profile (User.heroSkin /
    ///    User.heroHeadSkin) and every hero is drawn from it (Hero.getSkinInfo -> User.getHeroSkinInfos), and the
    ///    outfit/head menus change game.hero with P1's controller. Now P2 using the wardrobe or the head room gets the
    ///    menu on P2's pad, the choice goes to P2 only (saved as P2Skin/P2Head), and P1's profile choice is put back.
    /// </summary>
    public partial class LocalCoopMod
    {
        // ------------------------------------------------------------------ controller picker

        private const int PadBack = 4;
        private double _pickHeld1 = -1, _pickHeld2 = -1;
        private bool _swapKeyWasDown;

        private void UpdatePadPicker()
        {
            bool key = dc.hxd.Key.Class.isDown(121);   // F10
            if (key && !_swapKeyWasDown) DoSwapPads("F10");
            _swapKeyWasDown = key;

            CheckPickCombo(_input.P1Pad, ref _pickHeld1, isP1: true);
            CheckPickCombo(_input.P2Pad, ref _pickHeld2, isP1: false);
        }

        private void CheckPickCombo(GamePad? pad, ref double heldSince, bool isP1)
        {
            bool combo = pad != null && CoopInput.PadDown(pad, PadBack) && CoopInput.PadDown(pad, PadStart);
            if (!combo) { heldSince = -1; return; }
            if (heldSince < 0) { heldSince = Clock.Now; return; }
            if (heldSince == double.MaxValue || Clock.Now - heldSince < 1.0) return;
            heldSince = double.MaxValue;   // once per hold
            // Back+Start on either controller opens the "Choose your side" screen (v0.9.0; before: instant swap).
            if (_input.AllConnectedPads().Count >= 2) OpenSidesScreen(thenControls: false);
            else { Announce(); CoopInput.Rumble(_input.P1Pad, 0.6, 0.3); }
        }

        private void DoSwapPads(string how)
        {
            if (!_input.SwapPads()) { Logger.Information("[LocalCoop] Swap controllers ({How}): needs two controllers", how); return; }
            try { _config?.Save(); } catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Saving the controller choice failed"); }
            _l3Was1 = _l3Was2 = true;          // the buttons still held must not trigger anything on the new owner
            _p2StartWas = true;
            _pickHeld1 = _pickHeld2 = double.MaxValue;
            CoopInput.Rumble(_input.P1Pad, 0.6, 0.3);
            Announce();
            Logger.Information("[LocalCoop] Controllers swapped ({How}); saved SwapPads={Swap}", how, Config.SwapPads);
        }

        private void Announce()
        {
            try { P1?.popText("PLAYER 1".AsHaxeString(), 0xFFD24A); } catch { }
            try { if (CoopActive) P2!.popText("PLAYER 2".AsHaxeString(), 0x4AC8FF); } catch { }
        }

        // ------------------------------------------------------------------ outfits & heads

        private void InitStyleHooks()
        {
            Hook_Skinner.onActivate += (orig, self, by, lp) => { _styleMenuForP2 = CoopActive && IsP2(by); RunAsPicker(by, () => orig(self, by, lp)); };
            Hook_TailorDaughter.onActivate += (orig, self, by, lp) => { _styleMenuForP2 = CoopActive && IsP2(by); RunAsPicker(by, () => orig(self, by, lp)); };
            Hook__SkinSelector.__constructor__ += (orig, self, group, skinner) => OpenStyleMenu(self, () => orig(self, group, skinner));
            Hook__HeadSelector.__constructor__ += (orig, self) => OpenStyleMenu(self, () => orig(self));
            Hook_GridSelector.update += (orig, self) => AsOwner(self, () => orig(self));
            Hook_GridSelector.postUpdate += (orig, self) => AsOwner(self, () => orig(self));
            Hook_GridSelector.onDispose += (orig, self) => AsOwner(self, () => orig(self));
            Hook_SkinSelector.close += (orig, self) => CloseStyleMenu(self, () => orig(self));
            Hook_HeadSelector.close += (orig, self) => CloseStyleMenu(self, () => orig(self));

            // Drawing P2 uses P2's outfit / head.
            Hook_Hero.getSkinInfo += (orig, self) => IsP2(self) ? WithP2Style(() => orig(self)) : orig(self);
            Hook_Hero.getGlowColorsData += (orig, self) => IsP2(self) ? WithP2Style(() => orig(self)) : orig(self);
            Hook_HeroHead.init += Hook_HeroHead_init;
        }

        private bool _styleMenuForP2;
        private string? _p1SkinSaved, _p1HeadSaved;

        private void OpenStyleMenu(GridSelector ui, Action ctor)
        {
            bool forP2 = _styleMenuForP2 && CoopActive && P2 != null;
            _styleMenuForP2 = false;
            if (!forP2) { ctor(); return; }
            var user = Game.Class.ME?.user;
            if (user != null)
            {
                _p1SkinSaved = user.heroSkin?.ToString();
                _p1HeadSaved = user.heroHeadSkin?.ToString();
                // The menu starts on P2's current look.
                if (!string.IsNullOrEmpty(Config.P2Skin)) user.heroSkin = Config.P2Skin.AsHaxeString();
                if (!string.IsNullOrEmpty(Config.P2Head)) user.heroHeadSkin = Config.P2Head.AsHaxeString();
            }
            WithHero(P2!, ctor);
            try
            {
                ui.controller = HandOver(ui.controller);
                _p2Uis.AddOrUpdate(ui, this);
                Logger.Information("[LocalCoop] {Ui} opened for P2", ui.GetType().Name);
            }
            catch (Exception ex) { Logger.Error(ex, "[LocalCoop] Giving P2 the style menu failed"); }
        }

        private void CloseStyleMenu(GridSelector ui, Action close)
        {
            if (!_p2Uis.TryGetValue(ui, out _)) { close(); return; }
            AsOwner(ui, close);   // the game applies the choice to game.hero (= P2 here) and writes it to the profile
            var user = Game.Class.ME?.user;
            if (user != null)
            {
                Config.P2Skin = user.heroSkin?.ToString() ?? "";
                Config.P2Head = user.heroHeadSkin?.ToString() ?? "";
                if (_p1SkinSaved != null) user.heroSkin = _p1SkinSaved.AsHaxeString();
                if (_p1HeadSaved != null) user.heroHeadSkin = _p1HeadSaved.AsHaxeString();
                try { _config?.Save(); } catch { }
                Logger.Information("[LocalCoop] P2 style saved: outfit={Skin} head={Head} (P1 keeps {P1Skin}/{P1Head})",
                    Config.P2Skin, Config.P2Head, _p1SkinSaved, _p1HeadSaved);
            }
            _p1SkinSaved = _p1HeadSaved = null;
            KeepCameraOnPlayers();
        }

        // A fresh Haxe string each time on purpose: a C#-held Haxe string isn't known to be kept alive by Haxe's GC.
        private static dc.String HxCached(string value, ref (string Src, dc.String Hx)? cache) => value.AsHaxeString();
        private (string Src, dc.String Hx)? _p2SkinHx, _p2HeadHx;

        /// <summary>Run with the profile outfit/head temporarily set to P2's.</summary>
        private T WithP2Style<T>(Func<T> f)
        {
            var user = Game.Class.ME?.user;
            if (user == null || (string.IsNullOrEmpty(Config.P2Skin) && string.IsNullOrEmpty(Config.P2Head)) || _p1SkinSaved != null) return f();
            var skin = user.heroSkin; var head = user.heroHeadSkin;
            if (!string.IsNullOrEmpty(Config.P2Skin)) user.heroSkin = HxCached(Config.P2Skin, ref _p2SkinHx);
            if (!string.IsNullOrEmpty(Config.P2Head)) user.heroHeadSkin = HxCached(Config.P2Head, ref _p2HeadHx);
            try { return f(); }
            finally { user.heroSkin = skin; user.heroHeadSkin = head; }
        }

        private void Hook_HeroHead_init(Hook_HeroHead.orig_init orig, HeroHead self, Level level, dc.h2d.Object parent, HaxeProxy.Runtime.Ref<bool> fromUI)
        {
            bool p2Head = false;
            try { p2Head = P2 != null && CoopInput.Ptr(P2.heroHead) == CoopInput.Ptr(self); } catch { }
            if (!p2Head) { orig(self, level, parent, fromUI); return; }
            var user = Game.Class.ME?.user;
            if (user == null || string.IsNullOrEmpty(Config.P2Head) || _p1SkinSaved != null) { orig(self, level, parent, fromUI); return; }
            var head = user.heroHeadSkin;
            user.heroHeadSkin = HxCached(Config.P2Head, ref _p2HeadHx);
            try { orig(self, level, parent, fromUI); }
            finally { user.heroHeadSkin = head; }
        }

        /// <summary>Menus that focus the camera on "the hero" can leave it following P2 alone; put it back on P1 (shared camera).</summary>
        private void KeepCameraOnPlayers()
        {
            try
            {
                var p1 = P1;
                var vp = p1?._level?.viewport;
                if (vp != null && P2 != null && CoopInput.Ptr(vp.tracked) == CoopInput.Ptr(P2)) vp.track(p1, null);
            }
            catch { }
        }
    }
}
