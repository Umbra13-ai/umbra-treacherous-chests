# Changelog

## 0.2.2
- Fix: a hero pushed onto the tile where a Mimic chest stood could not walk off it (the chest left its tile marked as taken). The chest is now removed with the game's own pickup event, which clears the tile on every player's game.

## 0.2.1
- New `MinPerFloor` setting (default 0): once the chests still closed on a floor could not reach it by chance, the next chests opened are Mimics for sure.

## 0.2.0
- The Mimic now appears on the chest's own tile.
- The killer is read when the Mimic dies; the hoard goes to the hero who landed the blow, or to the owner of the pet or summon that did.
- Only real openings count (an already opened chest or a downed hero no longer rolls).
- Unknown `Difficulty` values fall back to Normal; unknown reward cards are skipped; only cards that fit in the hand are given.

## 0.1.1
- Only treasure chests can be Mimics.

## 0.1.0
- First release: Mimic chests with a chance per chest, a per-floor cap, a safe first chest, three difficulties and a reward hoard.
