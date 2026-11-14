using dc;
using dc.en;
using dc.en.inter;
using dc.pr;
using dc.tool;
using dc.ui;

namespace LocalCoop
{
    /// <summary>
    /// Fixes from playtest 13 (v0.11.1, owner run from the Prisoners' Quarters to the Bank). Function indexes are for v35.
    ///
    /// Crash after P2 bought Diverse Deck at a shop (log 2026-11-14, "Null access ._itemData" in ShopBooth.removeLargeTip
    /// from Hero.updateInteraction -> setFocus -> clearFocus -> Entity.onBlur). Buying (ShopBooth.buy f@18957) sets the
    /// booth's item to null and destroys the booth. The buyer's own focus was cleared before (Hero.activateFocus), but
    /// the other player was still focused on the booth; when that focus moved on, the booth's onBlur passed its null item
    /// to HUD.showCanBeEquippedHighlightFor (f@5303, reads item._itemData first). Solo there is no other hero. Now a
    /// focus on a destroyed entity (its tips go with it in Entity.disposeGfx) is dropped without onBlur, and a sold
    /// booth only hides its tooltip.
    ///
    /// Flask shown empty after a refill. The flask icon is only drawn by HUD.setHealings (f@5328: frame = flasks left);
    /// the refill (Heal.onFountainRefill f@13867, queued by the fountain cinematic) calls it through HUD.ME. Which
    /// call left the icon wrong is not known yet (unknown). Now the mod remembers what each HUD was last told, and if a
    /// player's flask count differs from their HUD for 1.5 s, it redraws that HUD and logs the difference once per kind.
    ///
    /// Test warp (owner request: reaching the sisters takes a whole run). Config "TestWarpTo": "T_Lighthouse" (or
    /// "Lighthouse") sends the next level exit there instead (cine.LevelTransition.loadNewLevel f@9941 -> Game.loadMainLevel
    /// by level id). It is used once and then cleared in the config file.
    /// </summary>
    public partial class LocalCoopMod
    {
        private static readonly string[] WarpTargets = { "T_Lighthouse", "Lighthouse" };

        private readonly Dictionary<IntPtr, (int Left, int Max)> _flaskShown = new();
        private readonly double[] _flaskWrongSince = { -1, -1 };
        private readonly HashSet<string> _flaskFixLogged = new();
        private double _flaskCheckAt;

        private void InitPlaytest13()
        {
            Hook_Hero.clearFocus += Hook_Hero_clearFocus;
            Hook_ShopBooth.removeLargeTip += (o, s, instant) =>
            {
                InventItem? item = null;
                try { item = s.item; } catch { }
                if (item != null) { o(s, instant); return; }
                // Sold: hide the tooltip, skip the HUD highlight for the missing item.
                try { s.largeTip?.hide(instant); } catch { }
            };
            dc.cine.Hook_LevelTransition.loadNewLevel += Hook_LevelTransition_loadNewLevel;
        }

        // ------------------------------------------------------------------ focus on something that is gone

        private void Hook_Hero_clearFocus(Hook_Hero.orig_clearFocus orig, Hero self)
        {
            if (CoopActive)
            {
                Entity? f = null;
                try { f = self.focused; } catch { }
                bool gone = false;
                try { gone = f != null && (f.destroyed || (f is ShopBooth b && b.item == null)); } catch { }
                if (gone)
                {
                    try { self.focused = null; } catch { }
                    if (Config.Verbose) Logger.Information("[LocalCoop] {P} was looking at {What} that is gone; focus dropped",
                        IsP2(self) ? "P2" : "P1", f!.GetType().Name);
                    return;
                }
            }
            orig(self);
        }

        // ------------------------------------------------------------------ flask icon

        /// <summary>Called by the HUD.setHealings hook with the HUD that was actually drawn.</summary>
        private void NoteFlaskShown(HUD hud, int left, int max) => _flaskShown[CoopInput.Ptr(hud)] = (left, max);

        private static dc.tool.mainSkills.Heal? FlaskOf(Hero h)
        {
            var skills = h.mainSkillsManager?.skills;
            if (skills == null) return null;
            foreach (var o in skills)
                if (o is dc.tool.mainSkills.Heal heal && heal.isEnabled()) return heal;
            return null;
        }

        private void UpdateFlaskDisplay(Hero p1, Hero p2)
        {
            if (Clock.Now < _flaskCheckAt) return;
            _flaskCheckAt = Clock.Now + 0.25;
            if (ScreenOpen) return;
            CheckFlask(0, p1, Game.Class.ME?.hud);
            if (Hud2Ok) CheckFlask(1, p2, _hud2);
        }

        private void CheckFlask(int idx, Hero hero, HUD? hud)
        {
            try
            {
                if (hud == null || hud.destroyed || hero.destroyed) { _flaskWrongSince[idx] = -1; return; }
                var heal = FlaskOf(hero);
                if (heal == null) { _flaskWrongSince[idx] = -1; return; }
                int left = heal.get_healings(), max = heal.get_maxHealings();
                bool known = _flaskShown.TryGetValue(CoopInput.Ptr(hud), out var shown);
                if (known && shown.Left == left && shown.Max == max) { _flaskWrongSince[idx] = -1; return; }
                double now = Clock.Now;
                if (_flaskWrongSince[idx] < 0) { _flaskWrongSince[idx] = now; return; }
                if (now - _flaskWrongSince[idx] < 1.5) return;   // a drink in progress updates the HUD when it ends
                _flaskWrongSince[idx] = -1;
                string who = idx == 0 ? "P1" : "P2";
                string was = known ? $"{shown.Left}/{shown.Max}" : "nothing";
                if (_flaskFixLogged.Add($"{who}:{was}:{left}/{max}"))
                    Logger.Information("[LocalCoop] {Who}'s flask icon showed {Was}, the flask has {Left}/{Max}: redrawn", who, was, left, max);
                hud.setHealings(left, max);   // the hook draws it as the HUD's owner
            }
            catch (Exception ex) { if (_hud2Errors.Add("flask:" + ex.Message)) Logger.Warning(ex, "[LocalCoop] Flask icon check failed"); }
        }

        // ------------------------------------------------------------------ settings file with a typo

        // Playtest 13: a typo in DeadCellsLocalCoop.json ("After parsing a value an unexpected character", line 22)
        // made DCCM drop the whole file and write the defaults back (Config`1.Load), so TestWarpTo and the other
        // settings were silently lost. Now the file is checked first; a broken one is kept as .broken.txt and the
        // players get a message in the game.
        private string? _configProblem;
        private bool _configNoticeShown;

        private void CheckConfigFile()
        {
            try
            {
                var candidates = new List<string>();
                var dir = System.IO.Path.GetDirectoryName(typeof(LocalCoopMod).Assembly.Location);   // coremod\mods\DeadCellsLocalCoop
                if (!string.IsNullOrEmpty(dir)) candidates.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, "..", "..", "config", "DeadCellsLocalCoop.json")));
                candidates.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.CurrentDirectory, "coremod", "config", "DeadCellsLocalCoop.json")));
                var path = candidates.Find(System.IO.File.Exists);
                if (path == null) return;
                var text = System.IO.File.ReadAllText(path);
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(text, new System.Text.Json.JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    });
                }
                catch (System.Text.Json.JsonException ex)
                {
                    var backup = path + ".broken.txt";
                    System.IO.File.Copy(path, backup, true);
                    _configProblem = ex.LineNumber is long line ? $"line {line + 1}" : "unknown line";
                    Logger.Warning("[LocalCoop] DeadCellsLocalCoop.json has a typo ({Where}: {Error}). The mod starts with default settings; your file was kept as {Backup}",
                        _configProblem, ex.Message, System.IO.Path.GetFileName(backup));
                }
            }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Checking the settings file failed"); }
        }

        private void ShowConfigNotice(Hero p1)
        {
            if (_configProblem == null || _configNoticeShown || ScreenOpen) return;
            _configNoticeShown = true;
            try { p1.popText($"CO-OP SETTINGS FILE HAS A TYPO ({_configProblem.ToUpperInvariant()}): DEFAULTS USED".AsHaxeString(), 0xFF6060); } catch { }
        }

        // ------------------------------------------------------------------ test warp

        private void Hook_LevelTransition_loadNewLevel(dc.cine.Hook_LevelTransition.orig_loadNewLevel orig, dc.cine.LevelTransition self)
        {
            try
            {
                var want = Config.TestWarpTo?.Trim() ?? "";
                if (want.Length > 0)
                {
                    var target = Array.Find(WarpTargets, t => string.Equals(t, want, StringComparison.OrdinalIgnoreCase));
                    var from = self.mainId?.ToString();
                    if (target == null)
                    {
                        Logger.Warning("[LocalCoop] TestWarpTo \"{Want}\" is not supported (use T_Lighthouse or Lighthouse); ignored", want);
                    }
                    // Only a real exit from a level being played: not the start of a run, not Continue
                    // (Game.transitionToLevel -> LevelTransition.goto(data.mainLevel), no level loaded yet).
                    else if (from != null && from != target && from != "PrisonStart" && self.heroPosAfterBossRuneReload == null
                             && Game.Class.ME?.curLevel != null && P2 != null)
                    {
                        self.mainId = target.AsHaxeString();
                        Config.TestWarpTo = "";
                        try { _config?.Save(); } catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Clearing TestWarpTo failed"); }
                        Logger.Information("[LocalCoop] Test warp: this exit goes to {Target} instead of {From} (TestWarpTo cleared)", target, from);
                    }
                }
            }
            catch (Exception ex) { Logger.Warning(ex, "[LocalCoop] Test warp failed; normal exit"); }
            orig(self);
        }
    }
}
