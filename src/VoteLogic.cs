using System;
using System.Collections.Generic;
using System.Linq;

namespace MapVote;

/// <summary>
/// Pure vote resolution. No Unity types so it can be checked outside the game.
/// Every ballot is one lottery ticket: nobody votes means a uniform random map,
/// otherwise the winner is drawn only from what people actually picked.
/// A "Random" ballot is a ticket that resolves to any map in the pool.
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

    /// <summary>Ballots that still point at something valid (a map that exists or Random).</summary>
    public static List<string> ValidBallots(IReadOnlyList<string> pool, IEnumerable<string> ballots) =>
        ballots.Where(b => b == RandomChoice || pool.Contains(b)).ToList();

    /// <summary>
    /// Probability of each map winning, plus the share of tickets held by Random.
    /// Values for maps sum to 1 (given a non-empty pool).
    /// </summary>
    public static Dictionary<string, float> Odds(IReadOnlyList<string> pool, IEnumerable<string> ballots, string? previous)
    {
        var odds = pool.ToDictionary(p => p, _ => 0f);
        if (pool.Count == 0) return odds;

        var valid = ValidBallots(pool, ballots);
        var randomPool = RandomPool(pool, previous);
        if (valid.Count == 0)
        {
            foreach (var map in randomPool) odds[map] = 1f / randomPool.Count;
            odds[RandomChoice] = 1f;
            return odds;
        }

        float ticket = 1f / valid.Count;
        int randomTickets = 0;
        foreach (var ballot in valid)
        {
            if (ballot == RandomChoice) randomTickets++;
            else odds[ballot] += ticket;
        }
        foreach (var map in randomPool) odds[map] += randomTickets * ticket / randomPool.Count;
        odds[RandomChoice] = randomTickets * ticket;
        return odds;
    }

    /// <summary>Draws the winning map. Returns null only for an empty pool.</summary>
    public static string? Resolve(IReadOnlyList<string> pool, IEnumerable<string> ballots, string? previous, Random rng)
    {
        if (pool.Count == 0) return null;

        var valid = ValidBallots(pool, ballots);
        var ticket = valid.Count == 0 ? RandomChoice : valid[rng.Next(valid.Count)];
        if (ticket != RandomChoice) return ticket;

        var randomPool = RandomPool(pool, previous);
        return randomPool[rng.Next(randomPool.Count)];
    }
}
