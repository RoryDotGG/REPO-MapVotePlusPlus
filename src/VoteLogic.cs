using System;
using System.Collections.Generic;
using System.Linq;

namespace MapVote;

/// <summary>How the ballots pick the next map. The host's setting, sent to players with the vote.</summary>
public enum VoteMode
{
    /// <summary>Every ballot is one ticket in a draw, so 3 votes to 1 is 75/25.</summary>
    Lottery,
    /// <summary>The choice with the most ballots wins. A tie is drawn at random between the tied choices.</summary>
    Majority,
}

/// <summary>
/// Pure vote resolution. No Unity types so it can be checked outside the game.
/// Nobody voting means a uniform random map. Otherwise the winner comes only from what people actually picked,
/// as <see cref="VoteMode"/> says. A "Random" ballot resolves to any map in the random pool: in a lottery it is one
/// ticket, and in a majority vote it is a choice of its own, which can win or tie like a map.
/// Random draws skip the recent maps while there are others to pick from (<see cref="RandomPool"/>).
/// </summary>
public static class VoteLogic
{
    public const string RandomChoice = "__random__";

    /// <summary>A comma-separated list of map names, each trimmed, blanks dropped. It matches names in any case.</summary>
    public static HashSet<string> NameList(string? list) => new(
        (list ?? "").Split(',').Select(n => n.Trim()).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The pool without the excluded maps, in pool order. A map is excluded when <paramref name="excluded"/> has its
    /// level name or its display name, in any case. If that would leave no maps, nothing is excluded, so there is
    /// always something to vote on.
    /// </summary>
    public static List<string> Allowed(IReadOnlyList<string> pool, IEnumerable<string> excluded, Func<string, string> displayName)
    {
        var names = new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return pool.ToList();
        var allowed = pool.Where(m => !names.Contains(m) && !names.Contains(displayName(m))).ToList();
        return allowed.Count > 0 ? allowed : pool.ToList();
    }

    /// <summary>
    /// The maps random draws skip, most recent first: the game's previous level, then up to
    /// <paramref name="count"/> - 1 levels played before it this run. A count of 1 is vanilla's rule, the previous
    /// level alone (or nothing, when the game has none).
    /// </summary>
    /// <param name="played">Levels played this run, most recent first.</param>
    public static List<string> Recent(string? previous, IEnumerable<string> played, int count)
    {
        var recent = new List<string>();
        if (count <= 0) return recent;
        if (previous != null) recent.Add(previous);
        recent.AddRange(played.Where(m => m != previous).Distinct().Take(count - 1));
        return recent;
    }

    /// <summary>
    /// Maps a random draw can land on: the pool without the recent maps. If that would leave none, the maps played
    /// longest ago come back first, so the one just played is the last to return. With only the previous map in
    /// <paramref name="recent"/>, this is vanilla's rule.
    /// </summary>
    public static List<string> RandomPool(IReadOnlyList<string> pool, IReadOnlyList<string> recent) => Skip(pool, recent, 1);

    /// <summary>
    /// The pool without as many recent maps as it can lose and still keep <paramref name="keep"/> maps. They are skipped
    /// most recent first, so the ones played longest ago are the first to stay in.
    /// </summary>
    private static List<string> Skip(IReadOnlyList<string> pool, IReadOnlyList<string> recent, int keep)
    {
        var eligible = pool.ToList();
        foreach (var map in recent)
        {
            var without = eligible.Where(p => p != map).ToList();
            if (without.Count < keep) break;
            eligible = without;
        }
        return eligible;
    }

    /// <summary>
    /// The maps put to a vote. A limit of 0, or one that covers the whole pool, offers every map. A smaller limit draws
    /// that many at random, skipping the recent maps. If that leaves too few, the ones played longest ago come back
    /// first, so the vote still offers the full number. The list is kept in pool order so it reads the same way.
    /// </summary>
    public static List<string> Shortlist(IReadOnlyList<string> pool, IReadOnlyList<string> recent, int limit, Random rng)
    {
        if (limit <= 0 || limit >= pool.Count) return pool.ToList();

        var eligible = Skip(pool, recent, limit);
        int take = Math.Min(limit, eligible.Count);
        for (int i = 0; i < take; i++)
        {
            int j = rng.Next(i, eligible.Count);
            (eligible[i], eligible[j]) = (eligible[j], eligible[i]);
        }
        var picked = new HashSet<string>(eligible.Take(take));
        return pool.Where(picked.Contains).ToList();
    }

    /// <summary>
    /// Whether a draw with nobody voting picks from the same maps as the game's own pick, which is every map in
    /// <paramref name="gamePool"/> but <paramref name="previous"/>. If so, the host can leave the pick to the game. If
    /// not (a shortlist, excluded maps, or skipping other than the one previous map), the host has to draw it.
    /// </summary>
    public static bool DrawsLikeGame(IReadOnlyList<string> pool, IReadOnlyList<string> recent, IReadOnlyList<string> gamePool,
        string? previous)
    {
        var game = RandomPool(gamePool, previous == null ? Array.Empty<string>() : new[] { previous });
        return new HashSet<string>(RandomPool(pool, recent)).SetEquals(game);
    }

    /// <summary>Ballots that still point at something valid (a map that exists or Random).</summary>
    public static List<string> ValidBallots(IReadOnlyList<string> pool, IEnumerable<string> ballots) =>
        ballots.Where(b => b == RandomChoice || pool.Contains(b)).ToList();

    /// <summary>
    /// The tickets the winner is drawn from, one of which is picked at random. In a lottery that is every valid ballot.
    /// In a majority vote it is one ticket for each choice tied for the most ballots, so a clear leader always wins.
    /// </summary>
    private static List<string> Tickets(List<string> valid, VoteMode mode)
    {
        if (mode != VoteMode.Majority || valid.Count == 0) return valid;
        // GroupBy keeps the order choices were first voted for, so the same ballots always give the same tickets.
        var tallies = valid.GroupBy(b => b).Select(g => (Choice: g.Key, Votes: g.Count())).ToList();
        int most = tallies.Max(t => t.Votes);
        return tallies.Where(t => t.Votes == most).Select(t => t.Choice).ToList();
    }

    /// <summary>
    /// Probability of each map winning under <paramref name="mode"/>, plus the chance the winner comes from Random.
    /// Values for maps sum to 1 (given a non-empty pool).
    /// </summary>
    public static Dictionary<string, float> Odds(IReadOnlyList<string> pool, IEnumerable<string> ballots,
        IReadOnlyList<string> recent, VoteMode mode)
    {
        var odds = pool.ToDictionary(p => p, _ => 0f);
        if (pool.Count == 0) return odds;

        var tickets = Tickets(ValidBallots(pool, ballots), mode);
        var randomPool = RandomPool(pool, recent);
        if (tickets.Count == 0)
        {
            foreach (var map in randomPool) odds[map] = 1f / randomPool.Count;
            odds[RandomChoice] = 1f;
            return odds;
        }

        float ticket = 1f / tickets.Count;
        int randomTickets = 0;
        foreach (var choice in tickets)
        {
            if (choice == RandomChoice) randomTickets++;
            else odds[choice] += ticket;
        }
        foreach (var map in randomPool) odds[map] += randomTickets * ticket / randomPool.Count;
        odds[RandomChoice] = randomTickets * ticket;
        return odds;
    }

    /// <summary>
    /// The map the host's own pick sends everyone to, whatever the votes say. A map on the list is the next map, even a
    /// recent one: the host chose it on purpose. Random draws from the random pool, as a vote for Random would. Null for
    /// a map that isn't on the list, and for an empty list.
    /// </summary>
    public static string? ResolveHostPick(IReadOnlyList<string> pool, string choice, IReadOnlyList<string> recent, Random rng)
    {
        if (choice != RandomChoice) return pool.Contains(choice) ? choice : null;
        var randomPool = RandomPool(pool, recent);
        return randomPool.Count > 0 ? randomPool[rng.Next(randomPool.Count)] : null;
    }

    /// <summary>Draws the winning map under <paramref name="mode"/>. Returns null only for an empty pool.</summary>
    public static string? Resolve(IReadOnlyList<string> pool, IEnumerable<string> ballots, IReadOnlyList<string> recent,
        VoteMode mode, Random rng)
    {
        if (pool.Count == 0) return null;

        var tickets = Tickets(ValidBallots(pool, ballots), mode);
        var ticket = tickets.Count == 0 ? RandomChoice : tickets[rng.Next(tickets.Count)];
        if (ticket != RandomChoice) return ticket;

        var randomPool = RandomPool(pool, recent);
        return randomPool[rng.Next(randomPool.Count)];
    }
}
