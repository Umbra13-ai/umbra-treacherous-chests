using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Boardgame;
using Boardgame.Board;
using Boardgame.BoardEntities;
using Boardgame.Data;
using Boardgame.SerializableEvents;
using Boardgame.SerializableEvents.CustomEventHandlers;
using DataKeys;
using HarmonyLib;
using UnityEngine;

namespace TreacherousChests;

/// <summary>
/// Some treasure chests are Mimics. When a hero opens one, the chest is gone and an awake Mimic takes its place;
/// defeating the Mimic pays out cards from a reward pool.
/// </summary>
/// <remarks>
/// Host only: unmodded guests see everything through the game's own events (the chest's removal, the Mimic's spawn,
/// the reward cards) and a full sync. The chest the Mimic hid in gives no loot: the opening is marked invalid before
/// the game hands anything out, on every client. Works with or without other mods.
/// </remarks>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
public sealed class TreacherousChestsPlugin : BaseUnityPlugin
{
    internal const string PluginId = "umbra.treacherouschests";
    internal const string PluginName = "Treacherous Chests";
    internal const string PluginVersion = "0.2.2";

    private const float SwapDelaySeconds = 1f;
    private const float SpawnDelaySeconds = 0.5f;
    private const float NeverAppearedSeconds = 10f;

    private static readonly FieldInfo? GameContextField =
        typeof(GameHub).GetField("gameContext", BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly System.Random Rng = new(Guid.NewGuid().GetHashCode());

    private static TreacherousChestsPlugin? instance;

    private readonly HashSet<int> checkedChests = new();
    private readonly Dictionary<int, MimicRecord> mimics = new(); // by the Mimic's network ID
    private readonly List<PendingSwap> swaps = new();
    private List<AbilityKey> pool = new();
    private string? pooledFrom;
    private bool patchFailed;

    private ConfigEntry<bool> modEnabled = null!;
    private ConfigEntry<int> chancePercent = null!;
    private ConfigEntry<int> minPerFloor = null!;
    private ConfigEntry<int> maxPerFloor = null!;
    private ConfigEntry<bool> firstChestSafe = null!;
    private ConfigEntry<bool> bossFloor = null!;
    private ConfigEntry<string> difficulty = null!;
    private ConfigEntry<bool> rewardsEnabled = null!;
    private ConfigEntry<string> rewardMode = null!;
    private ConfigEntry<int> cardsPerHero = null!;
    private ConfigEntry<string> rewardPool = null!;
    private Harmony? harmony;
    private bool inLevel;
    private int chestsOpened;
    private int mimicsThisFloor;
    private float nextCheck;

    private void Awake()
    {
        instance = this;
        modEnabled = Config.Bind("General", "Enabled", true, "Master switch.");
        chancePercent = Config.Bind("Mimics", "ChancePercent", 15, new ConfigDescription(
            "The chance, in percent, that a treasure chest is a Mimic. Each chest is checked once, when a hero opens it.",
            new AcceptableValueRange<int>(0, 100)));
        minPerFloor = Config.Bind("Mimics", "MinPerFloor", 0, new ConfigDescription(
            "The fewest Mimics on one floor: once the chests still closed could not reach it by chance, the next chests opened are Mimics " +
            "for sure (even the first chest). 0 = chance only. Never more than MaxPerFloor, and only as many as chests get opened.",
            new AcceptableValueRange<int>(0, 10)));
        maxPerFloor = Config.Bind("Mimics", "MaxPerFloor", 1, new ConfigDescription(
            "The most Mimics on one floor.", new AcceptableValueRange<int>(0, 10)));
        firstChestSafe = Config.Bind("Mimics", "FirstChestSafe", true,
            "True: the first chest opened on each floor is never a Mimic.");
        bossFloor = Config.Bind("Mimics", "BossFloor", false, "Whether chests on the boss floor can be Mimics.");
        difficulty = Config.Bind("Mimics", "Difficulty", "Normal", new ConfigDescription(
            "Easy: 75% health. Normal: the game's own Mimic. Nasty: 150% health and 2 armor. The game's own scaling by party size applies on top.",
            new AcceptableValueList<string>("Normal", "Easy", "Nasty"))); // the default first: an unknown value falls back to it
        rewardsEnabled = Config.Bind("Rewards", "Enabled", true, "Whether defeating a Mimic pays out cards.");
        rewardMode = Config.Bind("Rewards", "Mode", "Killer", new ConfigDescription(
            "Killer: the hero who lands the killing blow gets the cards (the owner, if a pet or summon did it; otherwise the hero whose " +
            "turn it is; otherwise every hero). EveryHero: every living hero gets them.",
            new AcceptableValueList<string>("Killer", "EveryHero")));
        cardsPerHero = Config.Bind("Rewards", "CardsPerHero", 2, new ConfigDescription(
            "Cards each rewarded hero gets, drawn at random from the pool.", new AcceptableValueRange<int>(0, 5)));
        rewardPool = Config.Bind("Rewards", "Pool",
            "Strength, Speed, Invulnerability, DamageResistPotion, ExtraActionPotion, VigorPotion, LuckPotion, InvisibilityPotion, Rejuvenation",
            "Comma-separated cards (the game's ability names) the reward is drawn from.");

        ParsePool();
        harmony = new Harmony(PluginId);
        try
        {
            harmony.Patch(
                AccessTools.Method(typeof(InteractEventHandler), nameof(InteractEventHandler.BeforeResponse)),
                postfix: new HarmonyMethod(typeof(TreacherousChestsPlugin), nameof(InteractBeforeResponsePostfix)));
            harmony.Patch(
                AccessTools.Method(typeof(PieceAndTurnController), nameof(PieceAndTurnController.RemovePieceFromTurnQueue), new[] { typeof(int) }),
                postfix: new HarmonyMethod(typeof(TreacherousChestsPlugin), nameof(RemovePieceFromTurnQueuePostfix)));
        }
        catch (Exception e)
        {
            patchFailed = true;
            Logger.LogError($"The game could not be patched, so the mod stays off. Look for a newer version. {e.Message}");
        }

        Logger.LogInfo($"{PluginName} {PluginVersion} loaded: Mimic chance {chancePercent.Value}%, {minPerFloor.Value} to {maxPerFloor.Value} per floor, " +
            $"difficulty {difficulty.Value}.");
    }

    private void OnDestroy()
    {
        harmony?.UnpatchSelf();
    }

    private void Update()
    {
        try
        {
            if (patchFailed)
            {
                return;
            }

            var context = GameContextField?.GetValue(null) as GameContext;
            var playing = context != null && !GameHub.NoActiveLevel && context.pieceAndTurnController != null
                && context.gameStateMachine != null && context.gameStateMachine.GetCurrentGameState() == GameStates.PlayingState;
            if (!playing)
            {
                if (inLevel)
                {
                    ResetFloor();
                }

                return;
            }

            inLevel = true;
            if (!context!.networkController.IsMasterClient)
            {
                return;
            }

            ProcessSwaps(context);
            if (Time.unscaledTime >= nextCheck)
            {
                nextCheck = Time.unscaledTime + 0.5f;
                CheckMimics(context);
            }
        }
        catch (Exception e)
        {
            Logger.LogError($"Update failed: {e}");
        }
    }

    private void ResetFloor()
    {
        inLevel = false;
        checkedChests.Clear();
        mimics.Clear();
        swaps.Clear();
        chestsOpened = 0;
        mimicsThisFloor = 0;
    }

    /// <summary>
    /// Rolls for a Mimic when a hero opens a treasure chest; a Mimic chest's opening is made invalid, so it gives no
    /// loot on any client, and the chest is swapped for the Mimic a moment later.
    /// </summary>
    private static void InteractBeforeResponsePostfix(SerializableEvent serializableEvent)
    {
        var self = instance;
        if (self == null || !self.modEnabled.Value || serializableEvent is not SerializableEventInteract interact || !interact.isValidInteraction)
        {
            return;
        }

        try
        {
            var context = GameContextField?.GetValue(null) as GameContext;
            if (context == null || !context.networkController.IsMasterClient
                || !context.pieceAndTurnController.TryGetPiece(interact.pieceId, out var hero) || hero == null || !hero.IsPlayer())
            {
                return;
            }

            var chest = context.pieceAndTurnController.FindPieceWithPosition(interact.targetTile, PieceAndTurnController.SearchPiece.NonTurnQueue);
            var interactable = chest?.GetInteractable();
            if (chest == null || interactable == null)
            {
                return;
            }

            // Treasure chests only: other chest-like pieces (gold, water bottle, vortex dust) do not look like chests in the game.
            if (interactable.type != Interactable.Type.Chest)
            {
                return;
            }

            // The game's handler only rejects locked pieces here; the opening itself still has to pass the chest's own
            // check (not opened before, the right team, the hero not downed). An opening that will not happen is no chest opened.
            if (!interactable.CanInteract(hero))
            {
                return;
            }

            if (!self.checkedChests.Add(chest.networkID))
            {
                return;
            }

            self.chestsOpened++;
            if (!self.MayHideMimic(context, out var reason))
            {
                self.Logger.LogInfo($"Chest {chest.networkID} opened: no Mimic possible ({reason}).");
                return;
            }

            // The floor's minimum: once the chests still closed could not reach it by chance, this one is a Mimic for sure.
            // Chests already marked as Mimics but not yet removed still look closed, so they are taken off the count.
            var othersLeft = CountClosedChests(context, hero) - 1 - self.swaps.Count(swap => !swap.ChestRemoved);
            var stillNeeded = Math.Min(self.minPerFloor.Value, self.maxPerFloor.Value) - self.mimicsThisFloor;
            if (stillNeeded > 0 && othersLeft < stillNeeded)
            {
                self.Logger.LogInfo($"Chest {chest.networkID} opened: a Mimic for sure; the floor still needs {stillNeeded} and " +
                    $"only {Math.Max(0, othersLeft)} other chest(s) remain (MinPerFloor {self.minPerFloor.Value}).");
            }
            else
            {
                if (self.chestsOpened == 1 && self.firstChestSafe.Value)
                {
                    self.Logger.LogInfo($"Chest {chest.networkID} opened: the first chest on the floor is safe.");
                    return;
                }

                var chance = self.chancePercent.Value;
                var roll = Rng.Next(100);
                self.Logger.LogInfo($"Chest {chest.networkID} opened: rolled {roll} against {chance}%.");
                if (roll >= chance)
                {
                    return;
                }
            }

            interact.isValidInteraction = false;
            self.mimicsThisFloor++;
            self.swaps.Add(new PendingSwap(chest.networkID, chest.gridPos.min, Time.unscaledTime + SwapDelaySeconds));
            self.Logger.LogInfo($"Chest {chest.networkID} is a Mimic; its loot is withheld.");
        }
        catch (Exception e)
        {
            self.Logger.LogError($"Chest check failed: {e}");
        }
    }

    /// <summary>Treasure chests on the board that can still be opened, the one being opened included.</summary>
    private static int CountClosedChests(GameContext context, Piece hero)
    {
        var count = 0;
        foreach (var piece in context.pieceAndTurnController.Pieces(PieceAndTurnController.SearchPiece.NonTurnQueue))
        {
            var interactable = piece?.GetInteractable();
            if (interactable != null && interactable.type == Interactable.Type.Chest && interactable.CanInteract(hero))
            {
                count++;
            }
        }

        return count;
    }

    private bool MayHideMimic(GameContext context, out string reason)
    {
        if (mimicsThisFloor >= maxPerFloor.Value)
        {
            reason = $"already {mimicsThisFloor} on this floor";
            return false;
        }

        var scene = context.levelLoaderAndInitializer?.GetLevelSequence()?.GetCurrentLevelSceneName() ?? string.Empty;
        LevelDefinition.LevelType type;
        try
        {
            type = context.levelLoaderAndInitializer!.GetCurrentLevelObject().levelDefinition.type;
        }
        catch (Exception)
        {
            reason = string.Empty;
            return true;
        }

        switch (type)
        {
            case LevelDefinition.LevelType.Regular:
                reason = string.Empty;
                return true;
            case LevelDefinition.LevelType.Boss:
                reason = "boss floor";
                return bossFloor.Value;
            case LevelDefinition.LevelType.CustomScene
                when scene.IndexOf("Shop", StringComparison.OrdinalIgnoreCase) < 0 && scene.IndexOf("Entrance", StringComparison.OrdinalIgnoreCase) < 0:
                reason = string.Empty;
                return true;
            default:
                reason = $"{type} floor";
                return false;
        }
    }

    private void ProcessSwaps(GameContext context)
    {
        var now = Time.unscaledTime;
        foreach (var swap in swaps.ToList())
        {
            if (!swap.ChestRemoved && now >= swap.At)
            {
                if (context.pieceAndTurnController.TryGetPiece(swap.ChestId, out var chest) && chest != null)
                {
                    RemoveChest(context, chest, swap);
                }

                swap.ChestRemoved = true;
                swap.At = now + SpawnDelaySeconds;
            }
            else if (swap.ChestRemoved && now >= swap.At)
            {
                swaps.Remove(swap);
                SpawnMimic(context, swap.Tile);
            }
        }
    }

    /// <summary>
    /// Takes the Mimic chest off the board on every client and frees its tile for walking.
    /// </summary>
    /// <remarks>
    /// 0.2.2 (Umbra's game, 9 Nov 2026): a chest marks its tile as an interactable (tile flag int.MinValue) when it
    /// is placed, and despawning the chest does not clear that flag. A hero later pushed onto the tile (by the
    /// Mimic's leap) could not walk at all: the game's move search starts by checking the hero's own tile, finds
    /// it invalid and returns no moves, so every drop snapped back. Grapple still worked. The fix uses the game's
    /// own pickup event (what picking up a gold pile sends): on every client, guests included, it clears that
    /// flag and destroys the piece on the tile. Gold 0 and no action cost; each hero shows a brief "+0".
    /// A Recovery does not carry tile flags, so clearing only the host's flag would leave guests stuck.
    /// </remarks>
    private void RemoveChest(GameContext context, Piece chest, PendingSwap swap)
    {
        var onTile = context.pieceAndTurnController.FindPieceWithPosition(chest.gridPos.min, PieceAndTurnController.SearchPiece.NonTurnQueue);
        if (onTile != null && onTile.networkID == chest.networkID)
        {
            context.serializableEventQueue.SendResponseEvent(new SerializableEventPickup
            {
                targetTile = chest.gridPos.min,
                goldAmount = 0,
                hasNoActionPointCost = true,
            });
            Logger.LogInfo($"Mimic chest {swap.ChestId} at {swap.Tile} removed on every client; its tile is walkable again.");
            return;
        }

        // Not found where the pickup event looks: despawn it as before and free the host's tile at least.
        var removed = context.pieceAndTurnController.DespawnPiece(chest);
        context.boardModel.UnsetTileFlag(chest.gridPos.min, int.MinValue);
        Logger.LogWarning($"Mimic chest {swap.ChestId} at {swap.Tile} was not found as a board object; despawned instead (accepted={removed}). " +
            "Guests' copy of the tile may stay unwalkable.");
    }

    private void SpawnMimic(GameContext context, IntPoint2D tile)
    {
        // The chest is still on the board while its removal plays out, so its tile reads as taken; the Mimic takes its place.
        var state = TransientBoardState.CreateTransientBoardState(context.boardModel, context.pieceAndTurnController);
        state.MarkTileAsUnoccupied(tile);
        var settings = state.CanSpawn(tile, (TransientBoardState.Flag)0)
            ? new PieceSpawnSettings(BoardPieceId.Mimic, Team.Two).SetSpawnTile(tile)
            : new PieceSpawnSettings(BoardPieceId.Mimic, Team.Two)
                .FindFreeSpawnTile(new TileRect(tile), context.gameDataAPI, ref state, context.boardModel, 3);
        if (!settings.IsValid())
        {
            Logger.LogWarning($"No free tile at or near {tile} for the Mimic.");
            return;
        }

        settings.SetNetworkId(context.networkController.AllocateNetworkID())
            .SetHasBloodhound(PieceSpawnSettings.BloodHoundStatus.EnableAndWakeUp);
        var note = "the game's own Mimic";
        try
        {
            var config = context.gameDataAPI.GetPieceConfig(BoardPieceId.Mimic, MotherbrainGlobalVars.CurrentConfig);
            switch (difficulty.Value)
            {
                case "Easy":
                    settings.SetOverrideHealth(Math.Max(1, (int)Math.Round(config.StartHealth * 0.75)));
                    note = "75% health";
                    break;
                case "Nasty":
                    settings.SetOverrideHealth((int)Math.Round(config.StartHealth * 1.5)).SetSpawnArmor(config.StartArmor + 2);
                    note = "150% health, +2 armor";
                    break;
            }
        }
        catch (Exception e)
        {
            Logger.LogWarning($"Could not read the Mimic's configuration; it keeps its own stats: {e.Message}");
        }

        if (!context.pieceAndTurnController.SpawnPiece(settings))
        {
            Logger.LogWarning("The game rejected the Mimic's spawn.");
            return;
        }

        mimics[settings.networkID] = new MimicRecord(Time.unscaledTime);
        context.serializableEventQueue.SendResponseEvent(new SerializableEventUpdateFog());
        context.serializableEventQueue.SendResponseEvent(SerializableEvent.CreateRecovery());
        Logger.LogInfo($"Mimic {settings.networkID} spawned at {settings.pos} ({difficulty.Value}: {note}); full sync sent.");
        Notify("The chest was a Mimic!");
    }

    /// <summary>
    /// The game takes a piece off the board here when it dies (or when something removes it). For a tracked Mimic this
    /// is the one sure moment to read its killer: a poll can miss a kill, because the piece is gone before the next
    /// check. Only notes are taken here; the reward is paid from Update.
    /// </summary>
    private static void RemovePieceFromTurnQueuePostfix(int pieceId, Piece __result)
    {
        var self = instance;
        if (self == null || __result == null || !self.mimics.TryGetValue(pieceId, out var record))
        {
            return;
        }

        try
        {
            record.Removed = true;
            record.SeenAlive = true;
            record.Dead |= __result.IsDead();
            if (__result.KillerPhotonID > 0)
            {
                record.KillerId = __result.KillerPhotonID;
            }

            if (__result.LastAttackerPhotonID > 0)
            {
                record.LastAttackerId = __result.LastAttackerPhotonID;
            }
        }
        catch (Exception e)
        {
            self.Logger.LogWarning($"Mimic {pieceId}: its removal could not be read: {e.Message}");
        }
    }

    /// <summary>
    /// Follows each Mimic: notes who last hit it, pays out once it has died and left the board, and forgets one that
    /// left the board any other way (the level ended, or the game never placed it).
    /// </summary>
    private void CheckMimics(GameContext context)
    {
        foreach (var pair in mimics.ToList())
        {
            var id = pair.Key;
            var record = pair.Value;
            if (context.pieceAndTurnController.TryGetPiece(id, out var mimic) && mimic != null)
            {
                record.SeenAlive = true;
                if (mimic.LastAttackerPhotonID > 0)
                {
                    record.LastAttackerId = mimic.LastAttackerPhotonID;
                }

                if (mimic.IsDead())
                {
                    // Dying; the game removes it once its death has played out.
                    record.Dead = true;
                    if (mimic.KillerPhotonID > 0)
                    {
                        record.KillerId = mimic.KillerPhotonID;
                    }
                }

                continue;
            }

            mimics.Remove(id);
            if (record.Dead)
            {
                Reward(context, record);
            }
            else if (record.Removed)
            {
                Logger.LogInfo($"Mimic {id} left the board without dying; no reward.");
            }
            else if (!record.SeenAlive)
            {
                if (Time.unscaledTime - record.SpawnedAt < NeverAppearedSeconds)
                {
                    mimics[id] = record; // still waiting for the game to place it
                    continue;
                }

                Logger.LogWarning($"Mimic {id} never appeared (the game skipped its spawn, most likely an occupied tile); no reward.");
            }
            else
            {
                Logger.LogInfo($"Mimic {id} is gone with the level; no reward.");
            }
        }
    }

    private void Reward(GameContext context, MimicRecord record)
    {
        var heroes = context.pieceAndTurnController.GetPlayerPieces().Where(p => p != null && !p.IsDead()).ToList();
        var cards = ParsePool();
        if (!rewardsEnabled.Value || cardsPerHero.Value <= 0 || cards.Count == 0 || heroes.Count == 0)
        {
            Notify("The Mimic is defeated.");
            return;
        }

        var killer = rewardMode.Value == "EveryHero" ? null : ResolveKiller(context, heroes, record);
        if (killer == null && rewardMode.Value != "EveryHero")
        {
            Logger.LogInfo("Mimic: no hero can be credited with the kill; the hoard is shared.");
        }

        var recipients = killer != null ? new List<Piece> { killer } : heroes;
        var shortOfRoom = false;
        foreach (var hero in recipients)
        {
            // The game drops a card that does not fit in the hand without a word; give only what fits.
            var room = hero.inventory?.FreeCardsSlots ?? cardsPerHero.Value;
            var count = Math.Min(cardsPerHero.Value, Math.Max(0, room));
            if (count < cardsPerHero.Value)
            {
                shortOfRoom = true;
                Logger.LogWarning($"Mimic reward: {hero.boardPieceId} #{hero.networkID} has room for {count} of {cardsPerHero.Value} cards.");
            }

            var given = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var key = cards[Rng.Next(cards.Count)];
                context.serializableEventQueue.SendEventRequest(
                    SerializableEvent.CreateAddCardToPiece(key, hero.networkID, isReplenishable: false, MotherTracker.Context.EnemyDrop));
                given.Add(key.ToString());
            }

            Logger.LogInfo($"Mimic reward for {hero.boardPieceId} #{hero.networkID}: {(given.Count == 0 ? "nothing (hand full)" : string.Join(", ", given))}.");
        }

        var message = recipients.Count == 1 ? "The Mimic is defeated. Its hoard goes to the hero who slew it." : "The Mimic is defeated. Its hoard is shared.";
        Notify(shortOfRoom ? message + " A full hand left some of it behind." : message);
    }

    /// <summary>
    /// The hero credited with the kill: the killer itself; the owner, when a pet or summon did it; else the hero whose
    /// turn it is; else nobody (the hoard is then shared).
    /// </summary>
    private static Piece? ResolveKiller(GameContext context, List<Piece> heroes, MimicRecord record)
    {
        var controller = context.pieceAndTurnController;
        foreach (var id in new[] { record.KillerId, record.LastAttackerId })
        {
            if (id <= 0)
            {
                continue;
            }

            var hero = heroes.FirstOrDefault(p => p.networkID == id);
            if (hero != null)
            {
                return hero;
            }

            var owner = heroes.FirstOrDefault(p => controller.GetMinionForPiece(p.networkID)?.networkID == id);
            if (owner != null)
            {
                return owner;
            }

            if (controller.TryGetPiece(id, out var attacker) && attacker?.pieceAI?.memory != null
                && attacker.pieceAI.memory.TryGetAssociatedPiece(controller, out var associated) && associated != null)
            {
                owner = heroes.FirstOrDefault(p => p.networkID == associated.networkID);
                if (owner != null)
                {
                    return owner;
                }
            }
        }

        return controller.IsPlayersTurn() ? heroes.FirstOrDefault(p => p.networkID == controller.CurrentPieceId) : null;
    }

    /// <summary>The reward pool, parsed once per setting value; names the game does not know are logged and left out.</summary>
    private List<AbilityKey> ParsePool()
    {
        if (pooledFrom == rewardPool.Value)
        {
            return pool;
        }

        pooledFrom = rewardPool.Value;
        pool = new List<AbilityKey>();
        foreach (var raw in rewardPool.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            // A number would parse as whatever card has that value; names only.
            if (char.IsDigit(name[0]) || !Enum.TryParse<AbilityKey>(name, true, out var key) || !Enum.IsDefined(typeof(AbilityKey), key))
            {
                Logger.LogWarning($"Reward pool: '{name}' is not a card the game knows; left out.");
                continue;
            }

            pool.Add(key);
        }

        if (pool.Count == 0)
        {
            Logger.LogWarning("Reward pool: no usable cards, so Mimics give no reward.");
        }

        return pool;
    }

    private void Notify(string message)
    {
        Logger.LogInfo(message);
        try
        {
            GameUI.ShowCameraMessage($"<color=#C9A227>Treacherous Chests</color>\n{message}", 5f);
        }
        catch (Exception)
        {
            // Not shown on screen here.
        }
    }

    /// <summary>What is known about one spawned Mimic.</summary>
    private sealed class MimicRecord
    {
        public MimicRecord(float spawnedAt)
        {
            SpawnedAt = spawnedAt;
        }

        public float SpawnedAt { get; }

        public bool SeenAlive { get; set; }

        public bool Removed { get; set; }

        public bool Dead { get; set; }

        public int KillerId { get; set; } = -1;

        public int LastAttackerId { get; set; } = -1;
    }

    private sealed class PendingSwap
    {
        public PendingSwap(int chestId, IntPoint2D tile, float at)
        {
            ChestId = chestId;
            Tile = tile;
            At = at;
        }

        public int ChestId { get; }

        public IntPoint2D Tile { get; }

        public float At { get; set; }

        public bool ChestRemoved { get; set; }
    }
}
