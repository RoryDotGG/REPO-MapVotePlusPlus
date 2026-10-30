using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
using Photon.Pun;
using REPOLib.Modules;
using UnityEngine;

namespace MapVote;

internal enum Phase { Closed, Open, Revealing, Locked }

internal enum VoteContext { None, LobbyMenu, Truck }

/// <summary>
/// Host-authoritative vote state. Clients send ballots to the host; the host
/// broadcasts the whole state after every change, so a late joiner only needs one sync.
/// </summary>
internal static class VoteSession
{
    public const float RevealSeconds = 4.6f;
    private const float AllVotedSeconds = 3f;

    public static Phase Phase { get; private set; }
    public static string? Winner { get; private set; }
    public static readonly Dictionary<int, string> Votes = new();
    /// <summary>Bumped on every state change so the UI can react without diffing.</summary>
    public static int Revision { get; private set; }

    private static float _deadline = -1f;
    private static VoteContext _lastContext;
    private static MenuPageLobby? _pendingLobbyStart;
    private static bool _bypassLobbyStart;
    private static readonly System.Random Rng = new();

    private static NetworkedEvent _cast = null!;
    private static NetworkedEvent _state = null!;
    private static NetworkedEvent _syncRequest = null!;

    public static float SecondsLeft => _deadline < 0f ? -1f : Mathf.Max(0f, _deadline - Time.unscaledTime);
    public static bool IsHost => SemiFunc.IsMasterClientOrSingleplayer();
    public static int LocalActor => SemiFunc.IsMultiplayer() ? PhotonNetwork.LocalPlayer.ActorNumber : 1;
    public static int PlayerCount => SemiFunc.IsMultiplayer() ? PhotonNetwork.CurrentRoom?.PlayerCount ?? 1 : 1;
    public static string? MyVote => Votes.TryGetValue(LocalActor, out var v) ? v : null;

    /// <summary>Every run level the game knows about, including ones added by other mods.</summary>
    public static List<Level> Pool => Levels.AllLevels.Where(l => l).ToList();
    public static List<string> PoolNames => Pool.Select(l => l.name).ToList();
    public static string? PreviousLevel => RunManager.instance && RunManager.instance.previousRunLevel
        ? RunManager.instance.previousRunLevel.name : null;

    public static VoteContext Context
    {
        get
        {
            var rm = RunManager.instance;
            if (!rm || !rm.levelCurrent) return VoteContext.None;
            if (rm.levelCurrent == rm.levelLobbyMenu && Plugin.VoteInLobbyMenu.Value) return VoteContext.LobbyMenu;
            if (rm.levelCurrent == rm.levelLobby && Plugin.VoteInTruck.Value && SemiFunc.LevelGenDone()) return VoteContext.Truck;
            return VoteContext.None;
        }
    }

    public static void RegisterEvents()
    {
        _cast = new NetworkedEvent("MapVotePlusPlus.Cast", OnCast);
        _state = new NetworkedEvent("MapVotePlusPlus.State", OnState);
        _syncRequest = new NetworkedEvent("MapVotePlusPlus.SyncRequest", _ => { if (IsHost) Broadcast(); });
    }

    /// <summary>Driven every frame by the UI object.</summary>
    public static void Tick()
    {
        var rm = RunManager.instance;
        if (rm && rm.levelCurrent && rm.levelCurrent == rm.levelMainMenu)
        {
            // Left the game: forget everything so a new session starts clean.
            if (Phase != Phase.Closed || Winner != null) ResetLocal();
            _lastContext = VoteContext.None;
            return;
        }

        var context = Context;
        if (context != _lastContext)
        {
            _lastContext = context;
            if (context != VoteContext.None)
            {
                if (!IsHost) _syncRequest.RaiseEvent(null!, NetworkingEvents.RaiseMasterClient, SendOptions.SendReliable);
                else if (Phase == Phase.Closed) Open();
                else Broadcast();
            }
        }

        if (!IsHost || context == VoteContext.None || _deadline < 0f || Time.unscaledTime < _deadline) return;
        if (Phase == Phase.Open) BeginReveal();
        else if (Phase == Phase.Revealing) Lock();
    }

    /// <summary>Vote for a map, or toggle the vote off by picking the same option again.</summary>
    public static void Cast(string choice)
    {
        if (Phase != Phase.Open) return;
        if (MyVote == choice) choice = "";

        // Optimistic local update so the click feels instant; the host's broadcast is the truth.
        if (choice == "") Votes.Remove(LocalActor);
        else Votes[LocalActor] = choice;
        Revision++;

        _cast.RaiseEvent(choice, NetworkingEvents.RaiseMasterClient, SendOptions.SendReliable);
    }

    public static void ClearMyVote()
    {
        if (MyVote != null) Cast(MyVote);
    }

    private static void OnCast(EventData e)
    {
        if (!IsHost || Phase != Phase.Open) return;
        var choice = e.CustomData as string ?? "";
        if (choice == "") Votes.Remove(e.Sender);
        else if (choice == VoteLogic.RandomChoice || PoolNames.Contains(choice)) Votes[e.Sender] = choice;
        else return;

        // Only the truck runs a timer. The lobby vote stays open until the host presses Start, so players who are
        // still joining get a say and anyone can change their pick right up to then.
        if (Context == VoteContext.Truck)
        {
            float now = Time.unscaledTime;
            if (_deadline < 0f && Votes.Count > 0) _deadline = now + Plugin.VoteSeconds.Value;
            if (Votes.Count >= PlayerCount && _deadline > now + AllVotedSeconds) _deadline = now + AllVotedSeconds;
        }
        Broadcast();
    }

    private static void OnState(EventData e)
    {
        if (IsHost || e.CustomData is not object[] { Length: 5 } data) return;
        Phase = (Phase)(int)data[0];
        float left = (float)data[1];
        _deadline = left < 0f ? -1f : Time.unscaledTime + left;
        Votes.Clear();
        var actors = (int[])data[2];
        var choices = (string[])data[3];
        for (int i = 0; i < actors.Length && i < choices.Length; i++) Votes[actors[i]] = choices[i];
        Winner = (string)data[4] is { Length: > 0 } w ? w : null;
        Revision++;
    }

    private static void Broadcast()
    {
        if (SemiFunc.IsMultiplayer() && PhotonNetwork.CurrentRoom != null)
        {
            // Drop ballots from players who have left.
            foreach (var gone in Votes.Keys.Where(a => PhotonNetwork.CurrentRoom.GetPlayer(a) == null).ToList())
                Votes.Remove(gone);
        }
        Revision++;
        var payload = new object[]
        {
            (int)Phase, SecondsLeft, Votes.Keys.ToArray(), Votes.Values.ToArray(), Winner ?? "",
        };
        _state.RaiseEvent(payload, NetworkingEvents.RaiseOthers, SendOptions.SendReliable);
    }

    private static void Open()
    {
        Phase = Phase.Open;
        Votes.Clear();
        Winner = null;
        _deadline = -1f;
        Broadcast();
    }

    private static void BeginReveal()
    {
        Winner = VoteLogic.Resolve(PoolNames, Votes.Values, PreviousLevel, Rng);
        Phase = Phase.Revealing;
        _deadline = Time.unscaledTime + RevealSeconds;
        Plugin.Log.LogInfo($"Vote closed with {Votes.Count} ballot(s); next map: {Winner}");
        Broadcast();
    }

    private static void Lock()
    {
        Phase = Phase.Locked;
        _deadline = -1f;
        Broadcast();

        if (_pendingLobbyStart)
        {
            var lobby = _pendingLobbyStart!;
            _pendingLobbyStart = null;
            _bypassLobbyStart = true;
            lobby.ButtonStart();
        }
    }

    private static void ResetLocal()
    {
        Phase = Phase.Closed;
        Votes.Clear();
        Winner = null;
        _deadline = -1f;
        _pendingLobbyStart = null;
        Revision++;
    }

    /// <summary>The host's Start rolls the lobby vote. Returns false to hold the start until the reveal has played.</summary>
    public static bool OnLobbyStart(MenuPageLobby lobby)
    {
        if (!IsHost) return true;
        if (_bypassLobbyStart)
        {
            _bypassLobbyStart = false;
            return true;
        }
        // The game refuses to start while someone is still joining; let it, rather than rolling without them.
        if (lobby.joiningPlayer) return true;
        switch (Phase)
        {
            case Phase.Open:
                _pendingLobbyStart = lobby;
                BeginReveal();
                return false;
            case Phase.Revealing:
                _pendingLobbyStart = lobby;
                return false;
            default:
                return true;
        }
    }

    /// <summary>
    /// Called from SetRunLevel on the host. Returns the level to load, or null to let the game pick at random
    /// (nobody voted). An unfinished vote resolves instantly so the game never waits on us.
    /// </summary>
    public static Level? ConsumeWinner(RunManager rm)
    {
        string? name = Phase switch
        {
            Phase.Locked or Phase.Revealing => Winner,
            Phase.Open when Votes.Count > 0 => VoteLogic.Resolve(PoolNames, Votes.Values, PreviousLevel, Rng),
            _ => null,
        };
        if (Phase == Phase.Closed && name == null) return null;

        var level = name == null ? null : rm.levels.FirstOrDefault(l => l && l.name == name);
        Phase = Phase.Closed;
        Votes.Clear();
        Winner = level ? level!.name : null;
        _deadline = -1f;
        _pendingLobbyStart = null;
        Broadcast();
        if (level) Plugin.Log.LogInfo($"Loading voted map {level!.name}");
        return level;
    }

    public static string PlayerName(int actor)
    {
        if (!SemiFunc.IsMultiplayer())
        {
            var local = SemiFunc.PlayerAvatarLocal();
            return local ? SemiFunc.PlayerGetName(local) : "You";
        }
        foreach (var p in SemiFunc.PlayerGetAll())
            if (p && p.photonView && p.photonView.OwnerActorNr == actor) return p.playerName;
        return PhotonNetwork.CurrentRoom?.GetPlayer(actor)?.NickName ?? "?";
    }
}
