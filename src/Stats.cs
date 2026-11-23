using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace MapVote;

/// <summary>
/// Anonymous usage counts, sent by the host only, for the public stats page. Each ping carries counts and settings and
/// nothing that identifies a game, player or install: no IDs, names, lobby codes or Steam data. The stats server adds
/// it to daily totals and throws it away. Hosts turn it off with ShareAnonymousStats. Every ping is written to the log
/// at debug level, so anyone can see exactly what is sent.
/// </summary>
internal sealed class Stats : MonoBehaviour
{
    // The stats Worker's custom domain, set in stats/wrangler.toml.
    internal const string Endpoint = "https://mvpp.rory.gg/v1/ping";
    private const int SlotSeconds = 1800;

    private long _lastSlot = -1;
    private float _nextCheck;

    private static Stats? _instance;

    private void Awake() => _instance = this;

    /// <summary>One heartbeat per 30-minute UTC slot while the host is in a game, so the server can count games without IDs.</summary>
    private void Update()
    {
        if (Time.unscaledTime < _nextCheck) return;
        _nextCheck = Time.unscaledTime + 60f;

        long slot = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / SlotSeconds;
        if (slot == _lastSlot || !InGame()) return;
        _lastSlot = slot;
        Send(new Dictionary<string, object>
        {
            ["t"] = "hb",
            ["players"] = VoteSession.PlayerCount,
            ["mapsPerVote"] = Plugin.MapLimit,
            ["recentSkip"] = Plugin.RecentMapsToSkip.Value,
        });
    }

    /// <summary>The host is in a lobby or a run, rather than the main menu or the tutorial.</summary>
    private static bool InGame() => VoteSession.IsHost && RunManager.instance
        && (SemiFunc.RunIsLobbyMenu() || SemiFunc.RunIsLobby() || SemiFunc.RunIsLevel() || SemiFunc.RunIsShop() || SemiFunc.RunIsArena());

    /// <summary>Host only: a vote rolled. Says which map won, how, and how many ballots each offered map got.</summary>
    internal static void Rolled(string? winner, VoteContext context, string? hostPick, IReadOnlyList<string> offered,
        IEnumerable<string> ballots)
    {
        if (winner != null && context != VoteContext.None)
            Send(StatsPayload.Vote(winner, context == VoteContext.Truck, hostPick, offered, ballots, VoteSession.PlayerCount));
    }

    private static void Send(Dictionary<string, object> ping)
    {
        if (!Plugin.ShareAnonymousStats.Value || !_instance) return;
        ping["v"] = Plugin.Version;
        ping["mode"] = Plugin.Mode.Value.ToString();
        var json = StatsPayload.Json(ping);
        Plugin.Log.LogDebug($"Anonymous stats: {json}");

        try
        {
            var request = new UnityWebRequest(Endpoint, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 10,
            };
            request.SetRequestHeader("Content-Type", "application/json");
            // Fire and forget: nothing waits on it, and a failure only means one ping fewer.
            request.SendWebRequest().completed += _ => request.Dispose();
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"Anonymous stats not sent: {e.Message}");
        }
    }
}
