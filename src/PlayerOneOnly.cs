using dc;
using dc.en;
using dc.en.inter;
using dc.en.inter.door;
using dc.en.inter.elevator;
using dc.en.ltrap;
using dc.pr;
using dc.tool.skill;

namespace LocalCoop
{
    /// <summary>
    /// World objects that only react to player 1 (audit after playtest 10, v0.10.1).
    ///
    /// The bytecode was scanned for every function of a world object, trap, item or skill that reads Game.ME.hero
    /// (189 world-object functions, 27 gear functions). Most are cosmetic (NPCs greeting or facing "the hero",
    /// lighting, prompts). The ones below change what happens in play. Most run a per-frame check against
    /// game.hero; for those the object's update now runs as whichever living player is nearer to it, so every check
    /// inside works for player 2 without re-implementing the game's logic.
    /// Function indexes are for v35.
    /// </summary>
    public partial class LocalCoopMod
    {
        private void InitPlayerOneOnlyFixes()
        {
            // Crumbling floors and platforms (UnstableGround f@27114, UnstablePF f@27136): only gave way under P1.
            Hook_UnstableGround.fixedUpdate += (o, s) => AsNearestHero(s, () => o(s));
            Hook_UnstablePF.fixedUpdate += (o, s) => AsNearestHero(s, () => o(s));
            // Breakable tiles (BreakableTile.fixedUpdate f@21683): only broke when P1 rammed them.
            Hook_BreakableTile.fixedUpdate += (o, s) => AsNearestHero(s, () => o(s));
            // Retractable spikes (checkHeroPositionInCase f@26986): only knew whether P1 stood on them.
            Hook_RetractableSpikes.checkHeroPositionInCase += (o, s) => o(s) || AsP2Check(s, () => o(s));
            // Elevator doors and multi-floor elevators follow "the hero" (ElevatorDoor.fixedUpdate,
            // MultiFloorElevator.fixedUpdate: isInElevator(game.hero)): P2 riding alone was ignored.
            Hook_ElevatorDoor.fixedUpdate += (o, s) => AsNearestHero(s, () => o(s));
            Hook_MultiFloorElevator.fixedUpdate += (o, s) => AsNearestHero(s, () => o(s));
            // Secret walls, hidden blocks and secret levers: the reveal checks P1's position and amulet only.
            Hook_HiddenBlock.postUpdate += (o, s) => AsNearestHero(s, () => o(s));
            Hook_HiddenGroundBlock.postUpdate += (o, s) => AsNearestHero(s, () => o(s));
            Hook_SecretLever.postUpdate += (o, s) => AsNearestHero(s, () => o(s));
            // Fire walls are put out when they touch game.hero (FireWall.checkTouchOpponents f@8114): touching P2
            // kept burning.
            Hook_FireWall.checkTouchOpponents += (o, s) => AsNearestHero(s, () => o(s));
            // Swinging ball traps hit game.hero whoever the ball touched (TriggerTrap.fixedUpdate f@25122): P2
            // touching the ball stunned and hurt P1.
            Hook_TriggerTrap.fixedUpdate += Hook_TriggerTrap_fixedUpdate;
            // Items that are picked up by touching them (enableAutoPick): ItemDrop.fixedUpdate f@10063 only checks
            // game.hero.
            Hook_ItemDrop.fixedUpdate += Hook_ItemDrop_fixedUpdate;
            // Treasure chests that turn out to be mimics target game.hero; cursed chests curse game.hero when their
            // timer ends (CursedChest.onCooldownEnd f@9553 -> Hero.curse): P2 opening one cursed P1.
            Hook_TreasureChest.onActivate += (o, s, by, lp) => { NoteOpener(s, by); o(s, by, lp); };
            Hook_TreasureChest.onCooldownEnd += (o, s, k, v) => AsOpener(s, () => o(s, k, v));
            Hook_CursedChest.onActivate += (o, s, by, lp) => { NoteOpener(s, by); o(s, by, lp); };
            Hook_CursedChest.onCooldownEnd += (o, s, k, v) => AsOpener(s, () => o(s, k, v));
            // Flip platforms listen to P1's landing signal only (FlipPF.init f@26872): P2 landing didn't flip them.
            Hook_FlipPF.init += (o, s) => { o(s); _flipPlatforms.Add(s); };
            Hook_Hero.onLand += Hook_Hero_onLand_FlipPF;
            // Skill effects aimed at "the user" go to game.hero (SkillCommonEffect.getEntitiesFromTarget f@4918,
            // target "Hero"): P2's self-buffs landed on P1.
            Hook_SkillCommonEffect.getEntitiesFromTarget += (o, s, target) =>
            {
                Hero? owner = null;
                try { owner = s.skill?.owner as Hero; } catch { }
                if (owner == null || !IsP2(owner) || ReferenceEquals(Game.Class.ME?.hero, owner)) return o(s, target);
                dc.hl.types.ArrayObj r = null!;
                WithHero(owner, () => r = o(s, target));
                return r;
            };
        }

        // ------------------------------------------------------------------ helpers

        private static double Dist2(Hero h, Entity e)
        {
            double dx = (h.cx + h.xr) - (e.cx + e.xr), dy = (h.cy + h.yr) - (e.cy + e.yr);
            return dx * dx + dy * dy;
        }

        private bool P2CanAct(Entity e)
        {
            var p2 = P2;
            return p2 != null && !p2.destroyed && !_p2Down.Active && p2.life > 0 && p2._level != null
                && e._level != null && ReferenceEquals(p2._level, e._level);
        }

        /// <summary>Run <paramref name="act"/> with game.hero = whichever living player is nearer to <paramref name="e"/>.</summary>
        private void AsNearestHero(Entity e, Action act)
        {
            if (!CoopActive || !P2CanAct(e)) { act(); return; }
            var p1 = P1;
            bool p1Counts = p1 != null && !p1.destroyed && !_p1Down.Active && p1.life > 0 && ReferenceEquals(p1._level, e._level);
            if (!p1Counts || Dist2(P2!, e) < Dist2(p1!, e)) WithHero(P2!, act);
            else act();
        }

        /// <summary>A pure "is the hero here?" check, asked again with game.hero = P2.</summary>
        private bool AsP2Check(Entity e, Func<bool> check)
        {
            if (!CoopActive || !P2CanAct(e)) return false;
            bool r = false;
            try { WithHero(P2!, () => r = check()); } catch { }
            return r;
        }

        // ------------------------------------------------------------------ swinging ball traps

        private void Hook_TriggerTrap_fixedUpdate(Hook_TriggerTrap.orig_fixedUpdate orig, TriggerTrap self)
        {
            if (!CoopActive || !P2CanAct(self)) { orig(self); return; }
            bool p2Hit = false, p1Hit = false;
            try
            {
                // Same test as the game: ball centre vs entity centre, within bRadius + radius (pixels).
                double bx = (self.cx + self.xr) * 24 + System.Math.Cos(self.ang) * self.radius;
                double by = (self.cy + self.yr) * 24 - self.hei * 0.5 + System.Math.Sin(self.ang) * self.radius;
                bool Touches(Hero h)
                {
                    double hx = (h.cx + h.xr) * 24, hy = (h.cy + h.yr) * 24 - h.hei * 0.5;
                    double d = System.Math.Sqrt((hx - bx) * (hx - bx) + (hy - by) * (hy - by));
                    return d <= self.bRadius + h.radius;
                }
                p2Hit = Touches(P2!);
                var p1 = P1;
                p1Hit = p1 != null && !_p1Down.Active && ReferenceEquals(p1._level, self._level) && Touches(p1);
            }
            catch { }
            if (p2Hit && !p1Hit) WithHero(P2!, () => orig(self));
            else orig(self);
        }

        // ------------------------------------------------------------------ touch-pickup items

        private void Hook_ItemDrop_fixedUpdate(Hook_ItemDrop.orig_fixedUpdate orig, ItemDrop self)
        {
            bool touchPick = false;
            try { touchPick = CoopActive && self.pickOnTouch && !self.pickLocked && P2CanAct(self); } catch { }
            if (!touchPick) { orig(self); return; }
            // A copy waits for its owner; anything else goes to whoever is nearer.
            if (_reserved.TryGetValue(self, out var r) && r.For != null)
            {
                if (IsP2(r.For)) WithHero(P2!, () => orig(self));
                else orig(self);
                return;
            }
            AsNearestHero(self, () => orig(self));
        }

        // ------------------------------------------------------------------ chests

        private readonly PinnedTable<Entity, Hero> _opener = new();

        private void NoteOpener(Entity chest, Hero? by)
        {
            if (CoopActive && by != null && (IsP2(by) || ReferenceEquals(by, P1))) _opener.AddOrUpdate(chest, by);
        }

        private void AsOpener(Entity chest, Action act)
        {
            if (CoopActive && _opener.TryGetValue(chest, out var h) && IsP2(h) && !h.destroyed && h.life > 0
                && ReferenceEquals(h._level, chest._level))
                WithHero(h, act);
            else act();
        }

        // ------------------------------------------------------------------ flip platforms

        private readonly List<FlipPF> _flipPlatforms = new();

        private void Hook_Hero_onLand_FlipPF(Hook_Hero.orig_onLand orig, Hero self, double fallHeight)
        {
            orig(self, fallHeight);
            if (!IsP2(self) || _flipPlatforms.Count == 0) return;
            _flipPlatforms.RemoveAll(f => { try { return f.destroyed; } catch { return true; } });
            foreach (var f in _flipPlatforms.ToArray())
            {
                try
                {
                    if (!ReferenceEquals(f._level, self._level)) continue;
                    WithHero(self, () => f.onHeroLand(fallHeight));
                }
                catch (Exception ex) { if (_hud2Errors.Add("flippf:" + ex.Message)) Logger.Warning(ex, "[LocalCoop] Flip platform for P2 failed"); }
            }
        }
    }
}
