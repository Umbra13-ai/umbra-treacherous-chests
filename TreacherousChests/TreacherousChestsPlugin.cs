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
    internal const string PluginVersion = "0.1.1";

    private const float SwapDelaySeconds = 1f;
    private const float SpawnDelaySeconds = 0.5f;

    private static readonly FieldInfo? GameContextField =
        typeof(GameHub).GetField("gameContext", BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly System.Random Rng = new(Guid.NewGuid().GetHashCode());

    private static TreacherousChestsPlugin? instance;

    private readonly HashSet<int> checkedChests = new();
    private readonly Dictionary<int, int> mimics = new(); // Mimic network ID -> last hero that hit it
    private readonly List<PendingSwap> swaps = new();

    private ConfigEntry<bool> modEnabled = null!;
    private ConfigEntry<int> chancePercent = null!;
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
        maxPerFloor = Config.Bind("Mimics", "MaxPerFloor", 1, new ConfigDescription(
            "The most Mimics on one floor.", new AcceptableValueRange<int>(0, 10)));
        firstChestSafe = Config.Bind("Mimics", "FirstChestSafe", true,
            "True: the first chest opened on each floor is never a Mimic.");
        bossFloor = Config.Bind("Mimics", "BossFloor", false, "Whether chests on the boss floor can be Mimics.");
        difficulty = Config.Bind("Mimics", "Difficulty", "Normal", new ConfigDescription(
            "Easy: 75% health. Normal: the game's own Mimic. Nasty: 150% health and 2 armor. The game's own scaling by party size applies on top.",
            new AcceptableValueList<string>("Easy", "Normal", "Nasty")));
        rewardsEnabled = Config.Bind("Rewards", "Enabled", true, "Whether defeating a Mimic pays out cards.");
        rewardMode = Config.Bind("Rewards", "Mode", "Killer", new ConfigDescription(
            "Killer: the hero who lands the killing blow gets the cards (the hero whose turn it is if a pet or trap did it). " +
            "EveryHero: every living hero gets them.",
            new AcceptableValueList<string>("Killer", "EveryHero")));
        cardsPerHero = Config.Bind("Rewards", "CardsPerHero", 2, new ConfigDescription(
            "Cards each rewarded hero gets, drawn at random from the pool.", new AcceptableValueRange<int>(0, 5)));
        rewardPool = Config.Bind("Rewards", "Pool",
            "Strength, Speed, Invulnerability, DamageResistPotion, ExtraActionPotion, VigorPotion, LuckPotion, InvisibilityPotion, Rejuvenation",
            "Comma-separated cards (the game's ability names) the reward is drawn from.");

        harmony = new Harmony(PluginId);
        harmony.Patch(
            AccessTools.Method(typeof(InteractEventHandler), nameof(InteractEventHandler.BeforeResponse)),
            postfix: new HarmonyMethod(typeof(TreacherousChestsPlugin), nameof(InteractBeforeResponsePostfix)));
        Logger.LogInfo($"{PluginName} {PluginVersion} loaded: Mimic chance {chancePercent.Value}%, " +
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
            var context = GameContextField?.GetValue(null) as GameContext;
            var playing = context != null && !GameHub.NoActiveLevel && context.pieceAndTurnController != null
                && context.gameStateMachine.GetCurrentGameState() == GameStates.PlayingState;
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
            if (chest == null || interactable == null || !self.checkedChests.Add(chest.networkID))
            {
                return;
            }

            // Treasure chests only: other chest-like pieces (gold, water bottle, vortex dust) do not look like chests in the game.
            if (interactable.type != Interactable.Type.Chest)
            {
                return;
            }

            self.chestsOpened++;
            if (!self.MayHideMimic(context, out var reason))
            {
                self.Logger.LogInfo($"Chest {chest.networkID} opened: no Mimic possible ({reason}).");
                return;
            }

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
                    var removed = context.pieceAndTurnController.DespawnPiece(chest);
                    Logger.LogInfo($"Mimic chest {swap.ChestId} at {swap.Tile} removed, accepted={removed}.");
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

    private void SpawnMimic(GameContext context, IntPoint2D tile)
    {
        var state = TransientBoardState.CreateTransientBoardState(context.boardModel, context.pieceAndTurnController);
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

        mimics[settings.networkID] = -1;
        context.serializableEventQueue.SendResponseEvent(new SerializableEventUpdateFog());
        context.serializableEventQueue.SendResponseEvent(SerializableEvent.CreateRecovery());
        Logger.LogInfo($"Mimic {settings.networkID} spawned at {settings.pos} ({difficulty.Value}: {note}); full sync sent.");
        Notify("The chest was a Mimic!");
    }

    /// <summary>Notes who last hit each Mimic, and pays out when one is defeated.</summary>
    private void CheckMimics(GameContext context)
    {
        foreach (var id in mimics.Keys.ToList())
        {
            if (context.pieceAndTurnController.TryGetPiece(id, out var mimic) && mimic != null && !mimic.IsDead())
            {
                mimics[id] = mimic.LastAttackerPhotonID;
                continue;
            }

            var lastAttacker = mimic != null ? mimic.LastAttackerPhotonID : mimics[id];
            mimics.Remove(id);
            Reward(context, lastAttacker);
        }
    }

    private void Reward(GameContext context, int lastAttacker)
    {
        var heroes = context.pieceAndTurnController.GetPlayerPieces().Where(p => p != null && !p.IsDead()).ToList();
        var pool = rewardPool.Value.Split(',')
            .Select(name => Enum.TryParse<AbilityKey>(name.Trim(), true, out var key) ? key : (AbilityKey?)null)
            .Where(key => key.HasValue).Select(key => key!.Value).ToList();
        if (!rewardsEnabled.Value || cardsPerHero.Value <= 0 || pool.Count == 0 || heroes.Count == 0)
        {
            Notify("The Mimic is defeated.");
            return;
        }

        List<Piece> recipients;
        if (rewardMode.Value == "EveryHero")
        {
            recipients = heroes;
        }
        else
        {
            var killer = heroes.FirstOrDefault(p => p.networkID == lastAttacker)
                ?? heroes.FirstOrDefault(p => context.pieceAndTurnController.IsPlayersTurn() && p.networkID == context.pieceAndTurnController.CurrentPieceId)
                ?? heroes[0];
            recipients = new List<Piece> { killer };
        }

        foreach (var hero in recipients)
        {
            var cards = new List<string>();
            for (var i = 0; i < cardsPerHero.Value; i++)
            {
                var key = pool[Rng.Next(pool.Count)];
                context.serializableEventQueue.SendEventRequest(
                    SerializableEvent.CreateAddCardToPiece(key, hero.networkID, isReplenishable: false, MotherTracker.Context.EnemyDrop));
                cards.Add(key.ToString());
            }

            Logger.LogInfo($"Mimic reward for {hero.boardPieceId} #{hero.networkID}: {string.Join(", ", cards)}.");
        }

        Notify(recipients.Count == 1 ? "The Mimic is defeated. Its hoard goes to the hero who slew it." : "The Mimic is defeated. Its hoard is shared.");
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
