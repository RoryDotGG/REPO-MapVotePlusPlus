using MapVote;

/// <summary>
/// The vote rules as 1.1.0 shipped them, kept unchanged so the check can show that the default settings (Lottery,
/// skip only the previous map, nothing excluded) still pick exactly the same maps from the same random numbers.
/// Don't update this to match <see cref="VoteLogic"/>: it is the record of the old behaviour.
/// </summary>
internal static class Legacy
{
    public static List<string> RandomPool(IReadOnlyList<string> pool, string? previous)
    {
        var eligible = pool.Where(p => p != previous).ToList();
        return eligible.Count > 0 ? eligible : pool.ToList();
    }

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

    public static List<string> ValidBallots(IReadOnlyList<string> pool, IEnumerable<string> ballots) =>
        ballots.Where(b => b == VoteLogic.RandomChoice || pool.Contains(b)).ToList();

    public static Dictionary<string, float> Odds(IReadOnlyList<string> pool, IEnumerable<string> ballots, string? previous)
    {
        var odds = pool.ToDictionary(p => p, _ => 0f);
        if (pool.Count == 0) return odds;

        var valid = ValidBallots(pool, ballots);
        var randomPool = RandomPool(pool, previous);
        if (valid.Count == 0)
        {
            foreach (var map in randomPool) odds[map] = 1f / randomPool.Count;
            odds[VoteLogic.RandomChoice] = 1f;
            return odds;
        }

        float ticket = 1f / valid.Count;
        int randomTickets = 0;
        foreach (var ballot in valid)
        {
            if (ballot == VoteLogic.RandomChoice) randomTickets++;
            else odds[ballot] += ticket;
        }
        foreach (var map in randomPool) odds[map] += randomTickets * ticket / randomPool.Count;
        odds[VoteLogic.RandomChoice] = randomTickets * ticket;
        return odds;
    }

    public static string? Resolve(IReadOnlyList<string> pool, IEnumerable<string> ballots, string? previous, Random rng)
    {
        if (pool.Count == 0) return null;

        var valid = ValidBallots(pool, ballots);
        var ticket = valid.Count == 0 ? VoteLogic.RandomChoice : valid[rng.Next(valid.Count)];
        if (ticket != VoteLogic.RandomChoice) return ticket;

        var randomPool = RandomPool(pool, previous);
        return randomPool[rng.Next(randomPool.Count)];
    }
}
