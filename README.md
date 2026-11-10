# Treacherous Chests

A BepInEx mod for Demeo: some treasure chests are Mimics. Open the wrong one and the chest is gone, and an awake Mimic is in its place. Defeat it to claim its hoard.

Only the host needs the mod. Guests, including players on standalone Quest, join without installing anything.

> **Testing build.** This is an early version made for testing. It has had little play so far, so expect rough edges. Problems and ideas are welcome as GitHub issues. Once it works well, it is planned to be folded into [Umbra Revolutions](https://github.com/Umbra13-ai/umbra-revolutions).

## Features

- **Mimic chests:** each treasure chest has a chance to be a Mimic, decided when a hero opens it.
- **No free loot:** a Mimic chest hands out nothing when opened. The reward comes from beating the Mimic.
- **The hoard:** defeating a Mimic gives cards from a reward pool, to the hero who landed the killing blow (or whose pet or summon did) or to every hero. Only cards that fit in the hand are given.
- **Fair play:** at most one Mimic per floor by default, and the first chest opened on each floor is always safe. Mimics appear only on dungeon floors, never in the entrance room or shops, and on the boss floor only if you allow it.
- **Difficulty:** Easy, Normal (the game's own Mimic) or Nasty.

## Requirements

- Demeo on PC with BepInEx 5. Tested with Demeo 1.42 (PC VR) and BepInEx 5.4.22.
- Works on its own, or alongside other mods such as HouseRules rulesets.

## Installation

1. Install BepInEx 5 in your Demeo folder, as for other Demeo mods.
2. With Demeo closed, put `TreacherousChests.dll` in `BepInEx\plugins`.

## Settings

Everything is in `BepInEx\config\umbra.treacherouschests.cfg`, created on first launch:

- **General:** the master switch.
- **Mimics:**
  - `ChancePercent`: default 15
  - `MinPerFloor`: default 0. Once the chests still closed on a floor could not reach this number by chance, the next chests opened are Mimics for sure. 0 = chance only.
  - `MaxPerFloor`: default 1
  - `FirstChestSafe`: default true
  - `BossFloor`: default false
  - `Difficulty`: default Normal
- **Rewards:**
  - `Enabled`
  - `Mode`: Killer or EveryHero
  - `CardsPerHero`: default 2
  - `Pool`: the cards the hoard is drawn from

## How it works

When a hero opens a chest, the host rolls for a Mimic. If it is one, the opening is cancelled before any loot is handed out, the chest is removed, and an awake Mimic is spawned on its tile with a full board sync. Guests see all of this through the game's own events. The Mimic is the game's own monster, with its own model, attacks and AI.

## Logs

`BepInEx\LogOutput.log` in the Demeo folder records each chest roll, each Mimic and each reward.

## Building

```powershell
dotnet build .\TreacherousChests\TreacherousChests.csproj -c Release -p:GameDir="<your Demeo folder>"
```

## Credits

- Made by Umbra (Umbra13-ai).
- Built on BepInEx and Harmony.
- Demeo is made by Resolution Games. This is an unofficial fan mod, not made or endorsed by Resolution Games.

## License

MIT License. See [LICENSE](LICENSE).
