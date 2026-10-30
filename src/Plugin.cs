using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MapVote;

[BepInPlugin(Guid, Name, Version)]
[BepInDependency("REPOLib", BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "RoryDotGG.MapVotePlusPlus";
    public const string Name = "MapVote++";
    public const string Version = "1.0.0";

    internal static ManualLogSource Log = null!;
    internal static ConfigEntry<Key> ToggleKey = null!;
    internal static ConfigEntry<int> VoteSeconds = null!;
    internal static ConfigEntry<bool> VoteInLobbyMenu = null!;
    internal static ConfigEntry<bool> VoteInTruck = null!;

    private void Awake()
    {
        Log = Logger;
        ToggleKey = Config.Bind("Controls", "ToggleKey", Key.M, "Opens and closes the map vote board.");
        VoteSeconds = Config.Bind("Voting", "VoteSeconds", 25,
            new ConfigDescription("Truck only: seconds the vote stays open after the first ballot. Ends early once everyone has voted. The lobby vote stays open until the host presses Start. Host's value is used.",
                new AcceptableValueRange<int>(5, 120)));
        VoteInLobbyMenu = Config.Bind("Voting", "VoteInLobbyMenu", true, "Show the vote in the pre-game lobby menu.");
        VoteInTruck = Config.Bind("Voting", "VoteInTruck", true, "Show the vote in the truck between levels.");

        VoteSession.RegisterEvents();
        new Harmony(Guid).PatchAll(typeof(Patches));

        // The BepInEx manager object is not safe to hang UI off in R.E.P.O.; use our own persistent object.
        var host = new GameObject("MapVotePlusPlus");
        host.hideFlags = HideFlags.HideAndDontSave;
        DontDestroyOnLoad(host);
        host.AddComponent<VoteUi>();

        Log.LogInfo($"{Name} {Version} loaded");
    }
}

internal static class Patches
{
    /// <summary>Host only: replace the random run-level pick with the vote result.</summary>
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.SetRunLevel))]
    [HarmonyPrefix]
    private static bool SetRunLevel(RunManager __instance)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return true;
        var level = VoteSession.ConsumeWinner(__instance);
        if (level == null) return true;

        __instance.levelCurrent = level;
        __instance.previousRunLevel = level;
        return false;
    }

    /// <summary>Host pressing Start in the lobby menu plays the reveal first, then starts for real.</summary>
    [HarmonyPatch(typeof(MenuPageLobby), nameof(MenuPageLobby.ButtonStart))]
    [HarmonyPrefix]
    private static bool LobbyStart(MenuPageLobby __instance) => VoteSession.OnLobbyStart(__instance);
}
