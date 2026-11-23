using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MapVote;

/// <summary>Builds the anonymous stats pings. Kept free of Unity so tests/VoteLogicCheck can run it.</summary>
internal static class StatsPayload
{
    /// <summary>A rolled vote: the winner, where, how it was won, and the ballots each offered map (and Random) got.</summary>
    public static Dictionary<string, object> Vote(string winner, bool truck, string? hostPick, IEnumerable<string> offered,
        IEnumerable<string> ballots, int players)
    {
        var offers = offered.Distinct().ToDictionary(m => m, _ => 0);
        offers[VoteLogic.RandomChoice] = 0;
        foreach (var b in ballots)
            if (offers.ContainsKey(b)) offers[b]++;

        return new Dictionary<string, object>
        {
            ["t"] = "vote",
            ["players"] = players,
            ["context"] = truck ? "truck" : "lobby",
            ["how"] = hostPick == VoteLogic.RandomChoice ? "hostRandom"
                : hostPick != null ? "host"
                : offers.Values.Sum() == 0 ? "none" : "vote",
            ["winner"] = winner,
            ["offers"] = offers,
        };
    }

    /// <summary>Just enough JSON for a ping: strings, ints and maps of them.</summary>
    public static string Json(object value) => value switch
    {
        string s => Quote(s),
        int n => n.ToString(CultureInfo.InvariantCulture),
        IDictionary<string, int> map => "{" + string.Join(",", map.Select(kv => Quote(kv.Key) + ":" + Json(kv.Value))) + "}",
        IDictionary<string, object> obj => "{" + string.Join(",", obj.Select(kv => Quote(kv.Key) + ":" + Json(kv.Value))) + "}",
        _ => throw new ArgumentException($"Can't write {value.GetType().Name} as JSON"),
    };

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                < ' ' => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        return sb.Append('"').ToString();
    }
}
