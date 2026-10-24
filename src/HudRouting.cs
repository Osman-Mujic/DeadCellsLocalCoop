using dc;
using dc.en;
using dc.tool;
using dc.tool.hero;
using dc.tool.mainSkills;
using dc.tool.weap.dual;
using dc.tool.weap.sh;
using dc.pr;
using dc.ui;
using HaxeProxy.Runtime;

namespace LocalCoop
{
    /// <summary>
    /// Every place in hero code that writes to "the" HUD (ui.HUD.ME), found by scanning the bytecode for
    /// reads of the HUD global in tool/hero, en/Hero, mainSkills and weapon code (hudscan.py, v35).
    /// Each is run with HUD.ME pointing at the HUD of the hero that owns the call.
    ///
    /// Before this, P2's skill cooldowns, ammo, flask counter and scroll tiers went to P1's HUD: P1's
    /// icons flipped between both players' timers every frame and the "skill ready" sound kept firing
    /// (playtest 6, bugs 1-2), and scroll tiers showed on the wrong HUD (bug 3).
    ///
    /// Deliberately NOT routed (shared or minimap-related, must stay on P1's HUD): minimapTracking,
    /// hudSetMoney, hudSetCells, postUpdate, onDie, deathRespawn, tryToPreventDeath, checkContinueMode.
    /// </summary>
    public partial class LocalCoopMod
    {
        private void InitHudRouting()
        {
            // en.Hero
            Hook_Hero.overrideMaxLife += (o, s, v) => OnHudOf(s, () => o(s, v));
            Hook_Hero.hudInitItems += (o, s) => OnHudOf(s, () => o(s));
            Hook_Hero.hudOnItemPick += (o, s, i, x, y) => OnHudOf(s, () => o(s, i, x, y));
            Hook_Hero.hudUpdateKeyStock += (o, s) => OnHudOf(s, () => o(s));
            Hook_Hero.hudUpdatePerkStock += (o, s) => OnHudOf(s, () => o(s));
            Hook_Hero.highlighHudSlotFor += Hook_Hero_highlighHudSlotFor;
            Hook_Hero.hudSetSurvival += Hook_Hero_hudSetSurvival;
            Hook_Hero.hudSetBrutality += Hook_Hero_hudSetBrutality;
            Hook_Hero.hudSetTactic += Hook_Hero_hudSetTactic;

            // skills / weapons managers (cooldown icons, locks, ammo)
            Hook_HeroActiveSkillsManager.hudGetSkillPower += (o, s, idx) => OnHudOf(s.hero, () => o(s, idx));
            Hook_HeroActiveSkillsManager.applyHudLockFade += (o, s) => OnHudOf(s.hero, () => o(s));
            Hook_HeroActiveSkillsManager.addSkillLock += Hook_AS_addSkillLock;
            Hook_HeroActiveSkillsManager.removeSkillLock += Hook_AS_removeSkillLock;
            Hook_HeroWeaponsManager.applyHudLockFade += (o, s) => OnHudOf(s.hero, () => o(s));
            Hook_HeroWeaponsManager.refillAllAmmo += (o, s) => OnHudOf(s.hero, () => o(s));
            Hook_HeroWeaponsManager.addWeaponLock += Hook_WM_addWeaponLock;
            Hook_HeroWeaponsManager.removeWeaponLock += Hook_WM_removeWeaponLock;

            // flasks
            Hook_Heal.onEnd += (o, s) => OnHudOf(s.hero, () => o(s));
            Hook_Heal.onFountainRefill += (o, s) => OnHudOf(s.hero, () => o(s));
            Hook_Heal.onUnlockItem += (o, s, k) => OnHudOf(s.hero, () => o(s, k));
            Hook_Heal.addHealingFlaskUnit += (o, s, k) => OnHudOf(s.hero, () => o(s, k));
            Hook_HealFlask.onEnd += (o, s) => OnHudOf(s.hero, () => o(s));
            Hook_CollectorFlask.onEnd += (o, s) => OnHudOf(s.hero, () => o(s));
            Hook_CollectorFlask.onPickItem += (o, s, f, i) => OnHudOf(s.hero, () => o(s, f, i));
            Hook_HealCaptainChicken.triggerHeal += (o, s) => OnHudOf(s.hero, () => o(s));

            // weapons with their own HUD bits
            Hook_OffHandRanged.updateAmmoHud += (o, s) => OnHudOf(s.owner, () => o(s));
            Hook_HoldShield.addFrames += (o, s, f) => OnHudOf(s.owner, () => o(s, f));
        }

        private void OnHudOf(Hero? owner, Action act) => OnHudOf(owner, () => { act(); return 0; });

        private T OnHudOf<T>(Hero? owner, Func<T> f)
        {
            HUD? want = null;
            if (owner != null && Hud2Ok)
            {
                if (IsP2(owner)) want = _hud2;
                else if (ReferenceEquals(HUD.Class.ME, _hud2)) want = Game.Class.ME?.hud;   // P1 call nested in a P2 one
            }
            if (want == null || ReferenceEquals(want, HUD.Class.ME)) return f();
            var saved = HUD.Class.ME;
            HUD.Class.ME = want;
            try { return f(); }
            catch (Exception ex) when (IsP2(owner))
            {
                // P2's HUD is cosmetic: never let a display error take the game down.
                if (_hud2Errors.Add(ex.Message)) Logger.Warning(ex, "[LocalCoop] P2 HUD update error (skipped)");
                return default!;
            }
            finally { HUD.Class.ME = saved; }
        }

        private void Hook_Hero_highlighHudSlotFor(Hook_Hero.orig_highlighHudSlotFor o, Hero s, InventItem i, Ref<int> c, Ref<double> t)
        { var saved = SwapHudFor(s); try { o(s, i, c, t); } finally { HUD.Class.ME = saved; } }
        private void Hook_Hero_hudSetSurvival(Hook_Hero.orig_hudSetSurvival o, Hero s, int v, Ref<bool> a)
        { var saved = SwapHudFor(s); try { o(s, v, a); } finally { HUD.Class.ME = saved; } }
        private void Hook_Hero_hudSetBrutality(Hook_Hero.orig_hudSetBrutality o, Hero s, int v, Ref<bool> a)
        { var saved = SwapHudFor(s); try { o(s, v, a); } finally { HUD.Class.ME = saved; } }
        private void Hook_Hero_hudSetTactic(Hook_Hero.orig_hudSetTactic o, Hero s, int v, Ref<bool> a)
        { var saved = SwapHudFor(s); try { o(s, v, a); } finally { HUD.Class.ME = saved; } }
        private void Hook_AS_addSkillLock(Hook_HeroActiveSkillsManager.orig_addSkillLock o, HeroActiveSkillsManager s, dc.String str, Ref<int> id)
        { var saved = SwapHudFor(s.hero); try { o(s, str, id); } finally { HUD.Class.ME = saved; } }
        private void Hook_AS_removeSkillLock(Hook_HeroActiveSkillsManager.orig_removeSkillLock o, HeroActiveSkillsManager s, dc.String str, Ref<int> id)
        { var saved = SwapHudFor(s.hero); try { o(s, str, id); } finally { HUD.Class.ME = saved; } }
        private void Hook_WM_addWeaponLock(Hook_HeroWeaponsManager.orig_addWeaponLock o, HeroWeaponsManager s, dc.String str, Ref<int> id)
        { var saved = SwapHudFor(s.hero); try { o(s, str, id); } finally { HUD.Class.ME = saved; } }
        private void Hook_WM_removeWeaponLock(Hook_HeroWeaponsManager.orig_removeWeaponLock o, HeroWeaponsManager s, dc.String str, Ref<int> id)
        { var saved = SwapHudFor(s.hero); try { o(s, str, id); } finally { HUD.Class.ME = saved; } }

        /// <summary>Point HUD.ME at <paramref name="owner"/>'s HUD; returns the previous value to restore.</summary>
        private HUD? SwapHudFor(Hero? owner)
        {
            var saved = HUD.Class.ME;
            if (owner == null || !Hud2Ok) return saved;
            if (IsP2(owner)) HUD.Class.ME = _hud2;
            else if (ReferenceEquals(saved, _hud2)) HUD.Class.ME = Game.Class.ME?.hud;
            return saved;
        }
    }
}
