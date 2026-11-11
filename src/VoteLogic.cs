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
/// as <see cref="VoteMode"/> says. A "Random" ballot resolves to any map in the pool: in a lottery it is one ticket,
/// and in a majority vote it is a choice of its own, which can win or tie like a map.
/// </summary>
public static class VoteLogic
{
    public const string RandomChoice = "__random__";

    /// <summary>Maps a random draw can land on. Mirrors vanilla: avoid replaying the previous map when possible.</summary>
    public static List<string> RandomPool(IReadOnlyList<string> pool, string? previous)
    {
        var eligible = pool.Where(p => p != previous).ToList();
        return eligible.Count > 0 ? eligible : pool.ToList();
    }

    /// <summary>
    /// The maps put to a vote. A limit of 0, or one that covers the whole pool, offers every map. A smaller limit draws
    /// that many at random from the maps other than the previous one, kept in pool order so the list reads the same way.
    /// </summary>
    public static List<string> Shortlist(IReadOnlyList<string> pool, string? previous, int limit, Random rng)
    {
        if (limit <= 0 || limit >= pool.Count) return pool.ToList();

        var eligible = RandomPool(pool, previous);
        int take = Math.Min(limit, eligible.Count);
        for (int i = 0; i < take; i++)
        {
            int j = rng.Next(i, eligible.Count);
            (eligible[i], eligible[j]) = (eligible[j], eligible[i]);
        }
        var picked = new HashSet<string>(eligible.Take(take));
        return pool.Where(picked.Contains).ToList();
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
    public static Dictionary<string, float> Odds(IReadOnlyList<string> pool, IEnumerable<string> ballots, string? previous,
        VoteMode mode)
    {
        var odds = pool.ToDictionary(p => p, _ => 0f);
        if (pool.Count == 0) return odds;

        var tickets = Tickets(ValidBallots(pool, ballots), mode);
        var randomPool = RandomPool(pool, previous);
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

    /// <summary>Draws the winning map under <paramref name="mode"/>. Returns null only for an empty pool.</summary>
    public static string? Resolve(IReadOnlyList<string> pool, IEnumerable<string> ballots, string? previous, VoteMode mode,
        Random rng)
    {
        if (pool.Count == 0) return null;

        var tickets = Tickets(ValidBallots(pool, ballots), mode);
        var ticket = tickets.Count == 0 ? RandomChoice : tickets[rng.Next(tickets.Count)];
        if (ticket != RandomChoice) return ticket;

        var randomPool = RandomPool(pool, previous);
        return randomPool[rng.Next(randomPool.Count)];
    }
}
