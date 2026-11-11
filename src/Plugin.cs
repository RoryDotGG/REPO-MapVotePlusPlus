using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using MenuLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MapVote;

[BepInPlugin(Guid, Name, Version)]
[BepInDependency("REPOLib", BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency("nickklmao.menulib", BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "RoryDotGG.MapVotePlusPlus";
    public const string Name = "MapVote++";
    /// <summary>Set from Version in MapVotePlusPlus.csproj at build time. Bump it there.</summary>
    public const string Version = BuildInfo.Version;

    internal static ManualLogSource Log = null!;
    internal static ConfigEntry<Key> ToggleKey = null!;
    internal static ConfigEntry<bool> VoteInLobbyMenu = null!;
    internal static ConfigEntry<bool> VoteInTruck = null!;
    internal static ConfigEntry<bool> ShowVoteOnArrival = null!;
    internal static ConfigEntry<string> MapsPerVote = null!;
    internal static ConfigEntry<VoteMode> Mode = null!;
    internal static ConfigEntry<int> RecentMapsToSkip = null!;

    /// <summary>The most RecentMapsToSkip goes up to, and how many played maps the host keeps track of.</summary>
    internal const int MaxRecentMaps = 10;

    /// <summary>MapsPerVote as a number, or 0 when it offers every map.</summary>
    internal static int MapLimit => int.TryParse(MapsPerVote.Value, out var n) ? n : 0;

    private void Awake()
    {
        Log = Logger;
        ToggleKey = Config.Bind("Controls", "ToggleKey", Key.M, "Opens and closes the map vote in the truck. In the lobby it's always on screen.");
        VoteInLobbyMenu = Config.Bind("Voting", "VoteInLobbyMenu", true, "Show the vote in the pre-game lobby menu.");
        VoteInTruck = Config.Bind("Voting", "VoteInTruck", true,
            "Show the vote in the truck between levels. It has no timer: it rolls when someone sends the truck off.");
        ShowVoteOnArrival = Config.Bind("Voting", "ShowVoteOnArrival", true,
            "Open the truck vote by itself when you arrive in the truck, so you pick a map or skip before you get going. Off: open it with the toggle key. Your own setting, not the host's.");
        // A list rather than a number so REPOConfig's in-game slider reads "All": it doesn't show descriptions.
        var mapChoices = new[] { "All" }.Concat(Enumerable.Range(2, 19).Select(n => n.ToString())).ToArray();
        MapsPerVote = Config.Bind("Voting", "MapsPerVote", "All",
            new ConfigDescription("How many maps each vote offers. Every time a vote opens, this many are drawn at random, skipping the recent maps (see RecentMapsToSkip), and Random only lands on one of them. All offers every map. The host's value is used, and changing it mid-vote draws the list again.",
                new AcceptableValueList<string>(mapChoices)));
        RecentMapsToSkip = Config.Bind("Voting", "RecentMapsToSkip", 1,
            new ConfigDescription("How many of the maps played most recently this run are kept out of random draws: Random, the pick when nobody votes, and the maps MapsPerVote draws. 1 skips the map you just played, as in vanilla. 0 lets it come straight back. If too few other maps are left, the ones played longest ago come back first. A vote for a recent map still counts. It applies with voting off too. The host's value is used, and changing it mid-vote draws the list again.",
                new AcceptableValueRange<int>(0, MaxRecentMaps)));
        Mode = Config.Bind("Voting", "VoteMode", VoteMode.Lottery,
            "How the votes pick the next map. Lottery: each vote is a ticket in a draw, so 3 votes to 1 is a 75% chance. Majority: the map with the most votes wins, and a tie is drawn at random between the tied maps. A vote for Random counts as a choice of its own: if it wins, the map is random. The host's value is used.");

        // REPOConfig changes settings mid-game, so apply them to the vote that is open now, not just the next one.
        MapsPerVote.SettingChanged += (_, _) => VoteSession.Redraw();
        Mode.SettingChanged += (_, _) => VoteSession.RulesChanged();
        RecentMapsToSkip.SettingChanged += (_, _) => VoteSession.Redraw();
        VoteInLobbyMenu.SettingChanged += (_, _) => VoteSession.CloseIfSwitchedOff();
        VoteInTruck.SettingChanged += (_, _) => VoteSession.CloseIfSwitchedOff();

        VoteSession.RegisterEvents();
        MenuAPI.AddElementToLobbyMenu(NativeBoard.BuildLobby);
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

    /// <summary>Host only: note the level the run moves on to, whoever picked it, for RecentMapsToSkip.</summary>
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.SetRunLevel))]
    [HarmonyPostfix]
    private static void RunLevelSet(RunManager __instance)
    {
        if (SemiFunc.IsMasterClientOrSingleplayer()) VoteSession.OnRunLevel(__instance.levelCurrent);
    }

    /// <summary>The game starts a run afresh (new game, loaded save, game over): the last run's maps aren't recent now.</summary>
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.ResetProgress))]
    [HarmonyPostfix]
    private static void RunReset() => VoteSession.ForgetPlayed();

    /// <summary>Host pressing Start in the lobby menu plays the reveal first, then starts for real.</summary>
    [HarmonyPatch(typeof(MenuPageLobby), nameof(MenuPageLobby.ButtonStart))]
    [HarmonyPrefix]
    private static bool LobbyStart(MenuPageLobby __instance) => VoteSession.OnLobbyStart(__instance);

    /// <summary>Someone sent the truck off from its screen: roll the vote now.</summary>
    [HarmonyPatch(typeof(TruckScreenText), nameof(TruckScreenText.ChatMessageLobby))]
    [HarmonyPostfix]
    private static void TruckSent() => VoteSession.OnTruckSent();

    /// <summary>Host only: the truck screen's last line drives off; hold it until the reel has landed.</summary>
    [HarmonyPatch(typeof(TruckScreenText), nameof(TruckScreenText.GotoNextLevel))]
    [HarmonyPrefix]
    private static bool TruckDepart(TruckScreenText __instance) => VoteSession.OnTruckDepart(__instance);
}
