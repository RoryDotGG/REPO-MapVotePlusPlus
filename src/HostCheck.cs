using ExitGames.Client.Photon;
using Photon.Pun;
using UnityEngine;

namespace MapVote;

/// <summary>Whether this player can take part in the host's vote, and if not, why not.</summary>
internal enum HostSupport { Ok, Missing, SwitchedOff, Mismatch, NoReply }

/// <summary>
/// Tells a host without MapVote++ (or with one that can't talk to ours, or with the vote switched off) apart from a vote
/// that just hasn't reached this player yet, so the vote list can say why there is nothing to vote on instead of
/// waiting forever. The host puts its version and settings in the Photon room's properties, which every player gets on
/// joining whatever mods they run. The vote's own events can't be trusted for this: REPOLib numbers them in
/// registration order, so a different mod list can leave them going nowhere.
/// </summary>
internal static class HostCheck
{
    /// <summary>Bump when the vote's network messages change in a way older versions can't read.</summary>
    public const int Protocol = 1;
    /// <summary>How long a player waits for the host's vote to reach them before saying it isn't coming.</summary>
    private const float ReplySeconds = 8f;

    private const string HostKey = "MapVotePlusPlus.Host", VersionKey = "MapVotePlusPlus.Version",
        ProtocolKey = "MapVotePlusPlus.Protocol", LobbyKey = "MapVotePlusPlus.Lobby", TruckKey = "MapVotePlusPlus.Truck";

    public static HostSupport Support { get; private set; }
    /// <summary>The host's MapVote++ version, when it published one.</summary>
    public static string? HostVersion { get; private set; }

    /// <summary>What to tell a player who can't vote: a status line and a hint. Null when they can.</summary>
    public static (string Status, string Hint)? Problem { get; private set; }

    private static bool _heard;
    private static float _askedAt = -1f;
    private static string? _loggedVersion;
    private static (string Room, int Actor, bool Lobby, bool Truck)? _published;

    /// <summary>This player asked the host for its vote: start waiting for the reply.</summary>
    public static void Asked() => _askedAt = Time.unscaledTime;

    /// <summary>The host's vote reached this player, so the host runs MapVote++ and its events get through.</summary>
    public static void Heard() => _heard = true;

    /// <summary>Left the game: the next host is checked from scratch. Returns true if that changed what players see.</summary>
    public static bool Forget()
    {
        _heard = false;
        _askedAt = -1f;
        _published = null;
        _loggedVersion = HostVersion = null;
        return Set(HostSupport.Ok);
    }

    /// <summary>
    /// Every frame: the host publishes, everyone else checks what it published. Returns true when <see cref="Support"/>
    /// changed, so the vote list redraws.
    /// </summary>
    public static bool Update(VoteContext context, bool isHost)
    {
        if (isHost)
        {
            Publish();
            return Set(HostSupport.Ok);
        }
        return Set(Check(context));
    }

    /// <summary>The host: put its version and vote settings in the room, again whenever a setting changes.</summary>
    private static void Publish()
    {
        var room = PhotonNetwork.CurrentRoom;
        // Not PhotonNetwork alone: a player who is still joining looks like the host to the game until they are in.
        if (!PhotonNetwork.InRoom || !PhotonNetwork.IsMasterClient || room == null) return;

        int actor = PhotonNetwork.LocalPlayer.ActorNumber;
        bool lobby = Plugin.VoteInLobbyMenu.Value, truck = Plugin.VoteInTruck.Value;
        // Room properties only update once the server echoes them back, so remember what was sent rather than
        // comparing with the room, which would send them again every frame until then.
        var mark = (room.Name, actor, lobby, truck);
        if (_published == mark) return;
        _published = mark;
        room.SetCustomProperties(new Hashtable
        {
            { HostKey, actor },
            { VersionKey, Plugin.Version },
            { ProtocolKey, Protocol },
            { LobbyKey, lobby },
            { TruckKey, truck },
        });
    }

    /// <summary>
    /// Everyone else: read what the host published, and how long its vote has taken to reach us. A protocol mismatch
    /// counts even with no vote on screen, so the host's state is never read in a layout we don't know.
    /// </summary>
    private static HostSupport Check(VoteContext context)
    {
        var room = PhotonNetwork.CurrentRoom;
        if (room == null) return HostSupport.Ok; // still joining: nothing to check yet

        var props = room.CustomProperties;
        // Published by whoever is host now, not a host who has since left.
        bool published = Get(props, HostKey) is int actor && actor == room.MasterClientId;
        HostVersion = published ? Get(props, VersionKey) as string : null;
        bool waited = _askedAt >= 0f && Time.unscaledTime - _askedAt > ReplySeconds;

        // Nothing published, yet the host's vote reached us: a MapVote++ from before hosts published their version.
        if (!published) return context == VoteContext.None || _heard || !waited ? HostSupport.Ok : HostSupport.Missing;
        if (Get(props, ProtocolKey) is not int protocol || protocol != Protocol) return HostSupport.Mismatch;

        if (HostVersion != Plugin.Version && HostVersion != _loggedVersion)
        {
            _loggedVersion = HostVersion;
            Plugin.Log.LogInfo($"Host runs MapVote++ {HostVersion}, you have {Plugin.Version}. They can still vote together.");
        }
        if (context == VoteContext.None) return HostSupport.Ok;
        if (Get(props, context == VoteContext.Truck ? TruckKey : LobbyKey) is not true) return HostSupport.SwitchedOff;
        return _heard || !waited ? HostSupport.Ok : HostSupport.NoReply;
    }

    /// <summary>A room property, or null. Checks the key first: Photon's Hashtable hasn't always had the same base class.</summary>
    private static object? Get(Hashtable props, string key) => props.ContainsKey(key) ? props[key] : null;

    /// <summary>Built once per change, not every frame the vote list asks for it.</summary>
    private static bool Set(HostSupport support)
    {
        if (support == Support) return false;
        Support = support;
        Problem = support switch
        {
            HostSupport.Missing => ("HOST DOESN'T HAVE MAPVOTE++", "NEXT MAP IS RANDOM"),
            HostSupport.SwitchedOff => ("HOST HAS TURNED THIS VOTE OFF", "NEXT MAP IS RANDOM"),
            HostSupport.Mismatch => ($"HOST HAS V{HostVersion ?? "?"}, YOU HAVE V{Plugin.Version}", "MATCH VERSIONS TO VOTE"),
            HostSupport.NoReply => ("NO REPLY FROM THE HOST'S VOTE", "MATCH THE HOST'S MOD LIST"),
            _ => null,
        };
        if (Problem is { } problem) Plugin.Log.LogWarning($"Can't vote: {problem.Status.ToLowerInvariant()}");
        return true;
    }
}
