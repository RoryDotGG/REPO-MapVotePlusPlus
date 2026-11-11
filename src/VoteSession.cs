using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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

    public static Phase Phase { get; private set; }
    public static string? Winner { get; private set; }
    public static readonly Dictionary<int, string> Votes = new();
    /// <summary>Bumped on every state change so the UI can react without diffing.</summary>
    public static int Revision { get; private set; }

    /// <summary>When the reveal ends. Neither vote has a countdown: they roll when the host starts or the truck leaves.</summary>
    private static float _deadline = -1f;
    private static List<string> _candidates = new();
    private static VoteContext _lastContext;
    private static bool _lastIsHost;
    private static System.Action? _afterReveal;
    private static readonly System.Random Rng = new();
    // The host's rules for the vote that is up. The host sets them when the vote opens, and again if it changes a
    // setting while the vote is open, so they don't change under the reveal. Players get them with the vote.
    private static VoteMode _mode;
    private static IReadOnlyList<string>? _recent;
    /// <summary>How many maps ExcludedMaps left for the vote's list to be drawn from. 0 when the host hasn't said.</summary>
    private static int _allowedCount;

    /// <summary>Host only: the run levels played this run, most recent first, for RecentMapsToSkip.</summary>
    private static readonly List<string> Played = new();
    /// <summary>What the host last wrote to the log about ExcludedMaps, so it says it once rather than every vote.</summary>
    private static string? _exclusionNote;

    // The pool is read every frame while a vote list is on screen, so it is built once and kept until AllLevels changes.
    // PoolSource is AllLevels as it was at the last build, destroyed and null entries included, to compare against.
    private static readonly List<Level> PoolSource = new();
    private static IReadOnlyList<Level> _pool = System.Array.Empty<Level>();
    private static IReadOnlyList<string> _poolNames = System.Array.Empty<string>();
    private static Dictionary<string, Level> _poolByName = new();

    private static NetworkedEvent _cast = null!;
    private static NetworkedEvent _state = null!;
    private static NetworkedEvent _syncRequest = null!;

    public static float SecondsLeft => _deadline < 0f ? -1f : Mathf.Max(0f, _deadline - Time.unscaledTime);
    public static bool IsHost => SemiFunc.IsMasterClientOrSingleplayer();
    public static int LocalActor => SemiFunc.IsMultiplayer() ? PhotonNetwork.LocalPlayer.ActorNumber : 1;
    public static int PlayerCount => SemiFunc.IsMultiplayer() ? PhotonNetwork.CurrentRoom?.PlayerCount ?? 1 : 1;
    public static string? MyVote => Votes.TryGetValue(LocalActor, out var v) ? v : null;

    /// <summary>Every run level the game knows about, including ones added by other mods. Destroyed levels are left out.</summary>
    public static IReadOnlyList<Level> Pool
    {
        get
        {
            SyncPool();
            return _pool;
        }
    }

    /// <summary>The asset names of <see cref="Pool"/>, in the same order.</summary>
    public static IReadOnlyList<string> PoolNames
    {
        get
        {
            SyncPool();
            return _poolNames;
        }
    }

    /// <summary>Finds a level in <see cref="Pool"/> by asset name. If two share a name, the first one wins.</summary>
    public static bool TryGetPoolLevel(string name, [NotNullWhen(true)] out Level? level)
    {
        SyncPool();
        return _poolByName.TryGetValue(name, out level);
    }

    /// <summary>
    /// The maps up for this vote: a random few when the host limits MapsPerVote, otherwise all of them. Votes, Random
    /// and the no-vote draw all stay within it. It is the whole pool until the host's first vote reaches us, and it
    /// keeps the last vote's maps until the next vote opens, so the list doesn't change under the reveal.
    /// </summary>
    public static IReadOnlyList<string> Candidates => _candidates.Count > 0 ? _candidates : PoolNames;
    public static string? PreviousLevel => RunManager.instance && RunManager.instance.previousRunLevel
        ? RunManager.instance.previousRunLevel.name : null;

    /// <summary>
    /// How the ballots pick the winner (the host's VoteMode). Lottery until the host's vote reaches us, and from a host
    /// older than VoteMode, which only had the lottery.
    /// </summary>
    public static VoteMode Mode => _mode;

    /// <summary>
    /// The maps random draws skip where they can, most recent first: the host's RecentMapsToSkip list once its vote
    /// reaches us. Before that, and from a host older than the setting, it is vanilla's rule: the previous level alone.
    /// </summary>
    public static IReadOnlyList<string> Recent => _recent ?? VanillaRecent;

    private static IReadOnlyList<string> VanillaRecent =>
        PreviousLevel is { } previous ? new[] { previous } : System.Array.Empty<string>();

    /// <summary>
    /// Each map's chance of winning this vote, plus Random's share, under the host's rules. Use this rather than
    /// <see cref="VoteLogic.Odds"/>, so the odds players see always follow the rules the host draws with.
    /// </summary>
    public static Dictionary<string, float> Odds() => VoteLogic.Odds(Candidates, Votes.Values, Recent, Mode);

    private static string? Resolve() => VoteLogic.Resolve(Candidates, Votes.Values, Recent, Mode, Rng);

    /// <summary>The host's recent maps as its settings say now, rather than as the open vote took them.</summary>
    private static List<string> HostRecent() => VoteLogic.Recent(PreviousLevel, Played, Plugin.RecentMapsToSkip.Value);

    /// <summary>
    /// How many maps the host's rules allow: the pool without ExcludedMaps. A shortlist is drawn from these, so a vote
    /// list could say "3 OF" this many. Before the host's vote reaches us, and from older hosts, it is the whole pool.
    /// </summary>
    public static int MapsAllowed => _allowedCount > 0 ? _allowedCount : PoolNames.Count;

    /// <summary>
    /// The host: the maps its ExcludedMaps leaves in, matched by level name or by the name the vote list shows. Writes
    /// to the log what it leaves out and any name that matches no map, once each time that changes.
    /// </summary>
    private static List<string> AllowedMaps()
    {
        var pool = PoolNames;
        var excluded = VoteLogic.NameList(Plugin.ExcludedMaps.Value);
        var allowed = VoteLogic.Allowed(pool, excluded, MapInfo.DisplayName);
        if (excluded.Count == 0)
        {
            _exclusionNote = null;
            return allowed;
        }

        var left = pool.Where(m => !allowed.Contains(m)).ToList();
        var unknown = excluded.Where(n => !pool.Any(m => excluded.Comparer.Equals(m, n) || excluded.Comparer.Equals(MapInfo.DisplayName(m), n))).ToList();
        var note = $"{string.Join(",", left)}|{string.Join(",", unknown)}|{allowed.Count}";
        if (note == _exclusionNote) return allowed;
        _exclusionNote = note;

        static string Describe(string map) => $"{MapInfo.DisplayName(map)} ({map})";
        if (left.Count > 0) Plugin.Log.LogInfo($"ExcludedMaps leaves out {string.Join(", ", left.Select(Describe))}");
        else if (unknown.Count < excluded.Count) Plugin.Log.LogWarning("ExcludedMaps names every map, so it is ignored: the vote needs maps to vote on");
        if (unknown.Count > 0)
            Plugin.Log.LogWarning($"ExcludedMaps has no map called {string.Join(", ", unknown)}. The maps are {string.Join(", ", pool.Select(Describe))}");
        return allowed;
    }

    /// <summary>Host only, from SetRunLevel: the run is moving on to this level.</summary>
    public static void OnRunLevel(Level? level)
    {
        if (!level) return;
        var name = level!.name;
        Played.Remove(name);
        Played.Insert(0, name);
        if (Played.Count > Plugin.MaxRecentMaps) Played.RemoveRange(Plugin.MaxRecentMaps, Played.Count - Plugin.MaxRecentMaps);
    }

    /// <summary>A new run (new game, loaded save, game over): the last run's maps no longer count as recent.</summary>
    public static void ForgetPlayed() => Played.Clear();

    /// <summary>
    /// Rebuilds the cached pool if AllLevels has changed since the last build: a level added or removed (custom maps
    /// can register late) or one destroyed. Checking is one pass over the list and allocates nothing. Each build makes
    /// new read-only lists, so anyone still holding the old ones keeps a list that doesn't change under them.
    /// </summary>
    private static void SyncPool()
    {
        var all = Levels.AllLevels;
        if (!PoolStale(all)) return;

        PoolSource.Clear();
        var levels = new List<Level>(all.Count);
        var names = new List<string>(all.Count);
        var byName = new Dictionary<string, Level>(all.Count);
        for (int i = 0; i < all.Count; i++)
        {
            var level = all[i];
            PoolSource.Add(level);
            if (!level) continue;
            var name = level.name;
            levels.Add(level);
            names.Add(name);
            byName.TryAdd(name, level);
        }
        _pool = levels.AsReadOnly();
        _poolNames = names.AsReadOnly();
        _poolByName = byName;
    }

    private static bool PoolStale(IReadOnlyList<Level> all)
    {
        if (all.Count != PoolSource.Count) return true;
        int live = 0;
        for (int i = 0; i < all.Count; i++)
        {
            var level = all[i];
            if (!ReferenceEquals(level, PoolSource[i])) return true;
            if (level) live++;
        }
        // Same entries as last time. A destroyed level never comes back, so if one has gone since, fewer are live.
        return live != _pool.Count;
    }

    public static VoteContext Context
    {
        get
        {
            var rm = RunManager.instance;
            if (!rm || !rm.levelCurrent) return VoteContext.None;
            if (rm.levelCurrent == rm.levelLobbyMenu && Plugin.VoteInLobbyMenu.Value) return VoteContext.LobbyMenu;
            if (rm.levelCurrent == rm.levelLobby && Plugin.VoteInTruck.Value && InTruck(rm)) return VoteContext.Truck;
            return VoteContext.None;
        }
    }

    /// <summary>
    /// The truck scene is loaded and its loading screen has gone. levelCurrent alone isn't enough: it switches to the
    /// truck as soon as the shop ends, while the shop is still on screen, and stays there through the loading screen.
    /// </summary>
    private static bool InTruck(RunManager rm)
    {
        var generator = LevelGenerator.Instance;
        var director = GameDirector.instance;
        return generator && generator.Generated && generator.Level == rm.levelLobby
            && director && director.currentState is GameDirector.gameState.Main or GameDirector.gameState.Death;
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
            Played.Clear();
            if (HostCheck.Forget()) Revision++;
            _lastContext = VoteContext.None;
            return;
        }

        var context = Context;
        bool isHost = IsHost;
        // Joining, the lobby level is set before the room is joined, and until then the game counts as singleplayer, so
        // the player looks like the host. A context change alone would never ask the real host for the vote.
        if (context != _lastContext || isHost != _lastIsHost)
        {
            // Looking like the host, the joiner opened a vote of its own. Drop it now the real host is known: left up, it
            // would take clicks that go nowhere when the host has no MapVote++ to answer.
            if (_lastIsHost && !isHost) ResetLocal();
            _lastContext = context;
            _lastIsHost = isHost;
            if (context != VoteContext.None)
            {
                if (!isHost)
                {
                    _syncRequest.RaiseEvent(null!, NetworkingEvents.RaiseMasterClient, SendOptions.SendReliable);
                    HostCheck.Asked();
                }
                else if (Phase == Phase.Closed) Open();
                else Broadcast();
            }
        }
        if (HostCheck.Update(context, isHost)) Revision++;

        if (!isHost || context == VoteContext.None || _deadline < 0f || Time.unscaledTime < _deadline) return;
        if (Phase == Phase.Revealing) Lock();
    }

    /// <summary>Vote for a map, or toggle the vote off by picking the same option again.</summary>
    public static void Cast(string choice)
    {
        if (Phase != Phase.Open || HostCheck.Support != HostSupport.Ok) return;
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
        else if (choice == VoteLogic.RandomChoice || Candidates.Contains(choice)) Votes[e.Sender] = choice;
        else return;

        // No timer: the vote stays open until the host presses Start or someone sends the truck off, so anyone can
        // change their pick right up to then, and players who are still joining the lobby get a say.
        Broadcast();
    }

    private static void OnState(EventData e)
    {
        if (IsHost || e.CustomData is not object[] { Length: >= 5 } data) return;
        // A host on a different protocol may lay the state out differently: don't read it as ours.
        if (HostCheck.Support == HostSupport.Mismatch) return;
        HostCheck.Heard();
        Phase = (Phase)(int)data[0];
        float left = (float)data[1];
        _deadline = left < 0f ? -1f : Time.unscaledTime + left;
        Votes.Clear();
        var actors = (int[])data[2];
        var choices = (string[])data[3];
        for (int i = 0; i < actors.Length && i < choices.Length; i++) Votes[actors[i]] = choices[i];
        Winner = (string)data[4] is { Length: > 0 } w ? w : null;
        _candidates = data.Length > 5 && data[5] is string[] candidates ? candidates.ToList() : new List<string>();
        // Fields after the list came after 1.1.0, which doesn't read them. A host without them only has the lottery.
        _mode = data.Length > 6 && data[6] is int mode && System.Enum.IsDefined(typeof(VoteMode), mode)
            ? (VoteMode)mode : VoteMode.Lottery;
        _recent = data.Length > 7 && data[7] is string[] recent ? recent : null;
        _allowedCount = data.Length > 8 && data[8] is int allowed ? allowed : 0;
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
            (int)Phase, SecondsLeft, Votes.Keys.ToArray(), Votes.Values.ToArray(), Winner ?? "", _candidates.ToArray(),
            // New fields go on the end: older versions read the ones above by position and ignore the rest.
            (int)_mode, Recent.ToArray(), _allowedCount,
        };
        _state.RaiseEvent(payload, NetworkingEvents.RaiseOthers, SendOptions.SendReliable);
    }

    private static void Open()
    {
        Phase = Phase.Open;
        Votes.Clear();
        Winner = null;
        _deadline = -1f;
        Draw();
        Broadcast();
    }

    private static void Draw()
    {
        TakeRules();
        var allowed = AllowedMaps();
        _allowedCount = allowed.Count;
        _candidates = VoteLogic.Shortlist(allowed, Recent, Plugin.MapLimit, Rng);
        if (Shortlisted) Plugin.Log.LogInfo($"Vote is on {_candidates.Count} map(s): {string.Join(", ", _candidates)}");
        if (Recent.Count > 1) Plugin.Log.LogInfo($"Random draws skip recent maps where they can: {string.Join(", ", Recent)}");
    }

    /// <summary>The host: read its settings into the vote's rules, which go to players with the vote.</summary>
    private static void TakeRules()
    {
        _mode = Plugin.Mode.Value;
        _recent = HostRecent();
    }

    /// <summary>
    /// The host changed MapsPerVote, RecentMapsToSkip or ExcludedMaps mid-game (REPOConfig): draw the open vote's list
    /// again so it applies now. Votes for Random or a map that is still on the list stand; the rest are dropped.
    /// </summary>
    public static void Redraw()
    {
        if (!IsHost || Phase != Phase.Open) return;
        Draw();
        foreach (var actor in Votes.Where(v => v.Value != VoteLogic.RandomChoice && !_candidates.Contains(v.Value)).Select(v => v.Key).ToList())
            Votes.Remove(actor);
        Broadcast();
    }

    /// <summary>
    /// The host changed VoteMode mid-game (REPOConfig): the open vote follows it now, and every player's odds with it.
    /// Once the vote has rolled, the winner stands and the change waits for the next vote.
    /// </summary>
    public static void RulesChanged()
    {
        if (!IsHost || Phase != Phase.Open) return;
        TakeRules();
        Broadcast();
    }

    /// <summary>
    /// The host switched off voting where the vote is open (REPOConfig): close it, so the game picks at random as it
    /// would with the vote off. Switching it back on opens a fresh vote through <see cref="Tick"/>.
    /// </summary>
    public static void CloseIfSwitchedOff()
    {
        if (!IsHost || Phase != Phase.Open || Context != VoteContext.None) return;
        Phase = Phase.Closed;
        Votes.Clear();
        Winner = null;
        _deadline = -1f;
        Broadcast();
    }

    /// <summary>
    /// The host drew a few maps (MapsPerVote) rather than putting every allowed one up. Players see it too: the list is
    /// synced. A list that is shorter only because of ExcludedMaps isn't a draw, so this is false for it.
    /// </summary>
    public static bool Shortlisted => _candidates.Count > 0 && _candidates.Count < MapsAllowed;

    private static void BeginReveal()
    {
        Winner = Resolve();
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

        // Let through whatever was held for the reveal. The vote is locked now, so it isn't held again.
        var resume = _afterReveal;
        _afterReveal = null;
        resume?.Invoke();
    }

    private static void ResetLocal()
    {
        Phase = Phase.Closed;
        Votes.Clear();
        Winner = null;
        _deadline = -1f;
        _afterReveal = null;
        _candidates = new List<string>();
        _mode = VoteMode.Lottery;
        _recent = null;
        _allowedCount = 0;
        Revision++;
    }

    /// <summary>The host's Start rolls the lobby vote. Returns false to hold the start until the reveal has played.</summary>
    public static bool OnLobbyStart(MenuPageLobby lobby)
    {
        if (!IsHost) return true;
        // The game refuses to start while someone is still joining; let it, rather than rolling without them.
        if (lobby.joiningPlayer) return true;
        return HoldForReveal(() => { if (lobby) lobby.ButtonStart(); });
    }

    /// <summary>Sending the truck off ends the vote, so roll it there and then: the reel spins while the screen types.</summary>
    public static void OnTruckSent()
    {
        if (IsHost && Context == VoteContext.Truck && Phase == Phase.Open) BeginReveal();
    }

    /// <summary>The truck waits for the reel to land before it leaves. Returns false to hold the departure.</summary>
    public static bool OnTruckDepart(TruckScreenText screen)
    {
        if (!IsHost || Context != VoteContext.Truck) return true;
        return HoldForReveal(() => { if (screen) screen.GotoNextLevel(); });
    }

    /// <summary>
    /// Rolls the vote if it is still open and runs <paramref name="resume"/> once the reel has landed.
    /// Returns false while holding; true when there is nothing to wait for. Nobody voting still rolls (a random map).
    /// </summary>
    private static bool HoldForReveal(System.Action resume)
    {
        if (Phase is not (Phase.Open or Phase.Revealing)) return true;
        _afterReveal = resume;
        if (Phase == Phase.Open) BeginReveal();
        return false;
    }

    /// <summary>
    /// Called from SetRunLevel on the host. Returns the level to load, or null to let the game pick at random. An
    /// unfinished vote resolves instantly so the game never waits on us. The game is only left to pick when nobody
    /// voted and it would pick from the same maps as the host's rules do. A shortlist, excluded maps, or skipping
    /// anything other than the one previous map always resolve here, so the next map keeps to the rules the vote showed.
    /// </summary>
    public static Level? ConsumeWinner(RunManager rm)
    {
        if (Phase == Phase.Closed) return DrawWithoutVote(rm);

        string? name = Phase switch
        {
            Phase.Locked or Phase.Revealing => Winner,
            Phase.Open when Votes.Count > 0 || !DrawsLikeGame(Candidates, Recent) => Resolve(),
            _ => null,
        };

        var level = Find(rm, name);
        Phase = Phase.Closed;
        Votes.Clear();
        Winner = level ? level!.name : null;
        _deadline = -1f;
        _afterReveal = null;
        Broadcast();
        if (level) Plugin.Log.LogInfo($"Loading voted map {level!.name}");
        return level;
    }

    /// <summary>
    /// No vote this time (it is switched off here). The game picks, unless the host's rules would pick from other maps:
    /// then the host draws, so ExcludedMaps and RecentMapsToSkip hold with voting off too.
    /// </summary>
    private static Level? DrawWithoutVote(RunManager rm)
    {
        var pool = AllowedMaps();
        var recent = HostRecent();
        if (DrawsLikeGame(pool, recent)) return null;

        var level = Find(rm, VoteLogic.Resolve(pool, System.Array.Empty<string>(), recent, Plugin.Mode.Value, Rng));
        if (level) Plugin.Log.LogInfo($"No vote here: loading {level!.name}, drawn by the host's map rules");
        return level;
    }

    /// <summary>Whether the game's own pick would come from the same maps as a random draw under these rules.</summary>
    private static bool DrawsLikeGame(IReadOnlyList<string> pool, IReadOnlyList<string> recent) =>
        VoteLogic.DrawsLikeGame(pool, recent, PoolNames, PreviousLevel);

    private static Level? Find(RunManager rm, string? name) =>
        name == null ? null : rm.levels.FirstOrDefault(l => l && l.name == name);

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
