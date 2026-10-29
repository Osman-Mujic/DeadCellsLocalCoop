# Dead Cells Local Co-op

Two players, one PC, two controllers: a second Beheaded joins your run in **Dead Cells**, with its own
health, flasks, weapons, skills, mutations, scrolls and HUD. Built as a code mod for
[Dead Cells Core Modding (DCCM)](https://github.com/dead-cells-core-modding/core).

> **Status: beta.** Played from the start of a run through the Bank (Prisoners' Quarters, Promenade,
> Prison Depths, Ossuary, Black Bridge) by two players on version 0.6.0. Versions 0.7.x and 0.8.0 fix the bugs
> found in that run but have not been played yet. Expect rough edges and please report them.

## What you get

- **Second player** on a second controller (or keyboard + one controller: player 1 on keyboard).
- **Shared camera** that follows both players and keeps them on one screen.
- **Player 2 HUD** in the bottom-right corner; player 1's minimap moves to the top-left.
- **Loot for two**: when one player takes a weapon, skill, amulet, mutation or food, a copy appears that only the
  other player can take. Scrolls of power are offered to both. Each player chooses their own starting weapons,
  their own item in reward rooms, and can use each fountain once.
- **Down, not out**: a player who "dies" goes down and is revived by standing next to them. The run ends only if
  both are down.
- **Menus for player 2**: item swap, scroll choice, mutations, the forge, the pause/inventory screen and the
  outfit room all work with player 2's controller and change player 2's things.
- **Team keys**: a key or keycard carried by either player opens doors for both.
- **Regroup**: doors, boss arenas and teleporters bring the other player along; if you still get split up,
  click the left stick (L3) to pull your partner to you.

## Requirements

- Dead Cells on Steam, **game version 35** ("The End is Near" update). Other versions are untested.
- **DCCM 35.14.1** installed (it needs the .NET 10 runtime). Follow the install guide in the DCCM repository.
- Two controllers, or one controller and the keyboard.

## Install

1. Install DCCM and start the game once with it, so the `coremod` folder exists.
2. Download `DeadCellsLocalCoop-<version>.zip` from [Releases](../../releases).
3. Unzip it into `<Dead Cells folder>\coremod\mods\`, so you get
   `coremod\mods\DeadCellsLocalCoop\DeadCellsLocalCoop.dll` and `modinfo.json`.
4. Plug in both controllers and start a run. Player 2 appears next to player 1.

To uninstall, delete the `DeadCellsLocalCoop` folder.

## Controls

| Action | How |
|---|---|
| Choose which controller is player 1 | The **Choose your side** screen opens on your first co-op run. Each player pushes their stick left (player 1) or right (player 2) and presses **A**. Open it again any time by holding **Back + Start** for one second, or from Options. **F10** swaps from the keyboard. |
| Regroup | **L3** (click left stick): your partner is brought to you. |
| Player 2 pause / inventory | **Start** on player 2's controller. |
| Map | Player 1's map button (the minimap is in the top-left corner). |
| Add / remove player 2 | **F9** |

## In-game pages

`Options > Core Modding > Local Co-op` has **Choose controllers**, **Co-op controls** (the extra buttons, with your
controller's button icons) and the main settings as toggles. The controls page also opens by itself after you
choose sides the first time.

## Settings

`<Dead Cells folder>\coremod\config\DeadCellsLocalCoop.json` (created on first start). Useful keys:
`SharedCamera`, `KeepBothOnScreen`, `LootParity`, `P2Hud`, `HideMinimap`, `ReviveSeconds`, `ReviveLifeFraction`,
`SwapPads`, `SidesChosen` (set to false to see the side screen again), `P2Skin`/`P2Head` (player 2's outfit and head, set from the outfit room), `Verbose` (extra log lines in
`coremod\logs`).

## Known issues

- Player 2 has no visible head.
- Now and then player 2 floats after a double jump until they slam down. No trigger found yet.
- Some skills or items that read "the hero" may act on player 1 when player 2 uses them. Diverse Deck and the
  Adele scythe are fixed; report others.
- The kill streak counter and "no hit" challenges are shared: either player getting hit resets them.
- Not on Steam Workshop: DCCM code mods are installed by hand.

Report bugs in [Issues](../../issues) with the log file `coremod\logs\log_latest.log` and, if possible, a screenshot.

## Build from source

Needs the .NET 10 SDK and an installed DCCM (its `core/mdk` folder).

```bash
DCCM_DIR="<Dead Cells folder>/coremod" ./build.sh
# output: src/bin/Release/net10.0/output/DeadCellsLocalCoop/
```

On Windows, set the same variables yourself and run `dotnet build -c Release` in `src`:
`DCCM_MDK_ROOT=<Dead Cells folder>\coremod\core\mdk`, `DEAD_CELLS_GAME_PATH=<Dead Cells folder>\coremod`.
(Don't name your own variable `DCCM_ROOT`: the MDK reads that name itself.)

## How it works (for modders)

The game assumes one hero (`Game.hero`), one HUD and one controller. The mod spawns a second `en.hero.Beheaded`,
gives it its own `tool.Controller` bound to the second pad, and runs "player 2's turn" (temporarily pointing
`Game.hero` at player 2) around game code that only knows about "the hero": menus, pickups, NPCs, HUD updates.
Every place it touches is commented with the game function it hooks (`f@<index>` = HashLink function index in v35).

| File | What |
|---|---|
| `LocalCoopMod.cs` | Spawning, level changes, shared camera, screen bounds, settings |
| `CoopInput.cs` | Splitting controllers between players, prompt icons |
| `PlayerTurn.cs` | Menus opened by player 2 (handing over controls) |
| `HudP2.cs`, `HudRouting.cs` | Player 2's HUD and routing HUD updates to the right one |
| `LootParity.cs`, `Armory.cs` | Loot copies, starting weapons |
| `Downed.cs` | Down / revive |
| `KeyRing.cs` | Shared keys |
| `CoopFixes*.cs`, `PadPickerAndStyle.cs` | Fixes from each playtest, controller picker, outfits |

Research notes, playtest reports and the reasons behind each fix:
[Co-op Anything: games/dead-cells](https://github.com/cranebeaker/coop-anything/tree/main/games/dead-cells).

## Credits and license

- Mod: Osman-Mujic, built with Claude (Anthropic) following the [Co-op Anything](https://github.com/cranebeaker/coop-anything) playbook.
- [Dead Cells Core Modding](https://github.com/dead-cells-core-modding/core) (MIT) makes code mods possible.
- Dead Cells is © Motion Twin / Evil Empire. This repo contains no game files.

MIT License, see [LICENSE](LICENSE).
