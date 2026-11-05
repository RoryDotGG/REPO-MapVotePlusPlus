using MapVote;
using static MapVote.VoteLogic;

// Assert-based self-check for the vote rules. Run: dotnet run --project tests/VoteLogicCheck
var pool = new[] { "A", "B", "C", "D" };
var rng = new Random(1234);

Dictionary<string, int> Draw(string[] ballots, string? previous, int n = 20000)
{
    var counts = new Dictionary<string, int>();
    for (int i = 0; i < n; i++)
    {
        var w = Resolve(pool, ballots, previous, rng)!;
        counts[w] = counts.GetValueOrDefault(w) + 1;
    }
    return counts;
}

void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); Console.WriteLine("ok  " + what); }

// Nobody votes -> uniform random over every map except the one just played.
var none = Draw([], previous: "A");
Check(!none.ContainsKey("A") && none.Keys.Count == 3, "no votes: random, skips previous map");

// Only some people vote -> winner is always one of their picks.
var some = Draw(["B", "C"], previous: null);
Check(some.Keys.All(k => k is "B" or "C"), "partial votes: only voted maps can win");

// One ballot each is a fair coin; three-to-one is roughly 75/25.
var weighted = Draw(["B", "B", "B", "C"], previous: null);
Check(Math.Abs(weighted["B"] / 20000f - 0.75f) < 0.02f, "votes are weighted tickets");

// A Random ballot can land anywhere (but not on the previous map).
var withRandom = Draw([RandomChoice], previous: "D");
Check(withRandom.Keys.Count == 3 && !withRandom.ContainsKey("D"), "random ballot draws from the full pool");

// Votes for a map that no longer exists (mod removed) are ignored.
Check(Draw(["ZZZ", "C"], null, 500).Keys.Single() == "C", "stale ballots are ignored");

// Odds shown in the UI match the rules and sum to 1.
var odds = Odds(pool, ["A", RandomChoice], previous: "A");
Check(Math.Abs(pool.Sum(p => odds[p]) - 1f) < 1e-5, "odds sum to 1");
Check(Math.Abs(odds["A"] - 0.5f) < 1e-5 && Math.Abs(odds["B"] - 0.5f / 3) < 1e-5, "odds split random share across eligible maps");

// A voted-for previous map is still allowed: players chose it.
Check(Draw(["A"], previous: "A", 200).Keys.Single() == "A", "explicit vote can repeat the previous map");

// No limit, or a limit that covers every map, puts the whole pool up, in order.
Check(Shortlist(pool, "A", 0, rng).SequenceEqual(pool), "limit 0 offers every map");
Check(Shortlist(pool, "A", 4, rng).SequenceEqual(pool) && Shortlist(pool, "A", 9, rng).SequenceEqual(pool),
    "a limit covering the pool offers every map");

// A smaller limit draws that many distinct maps, never the previous one, in pool order, and every other map gets a turn.
var seen = new HashSet<string>();
var orderOk = true;
for (int i = 0; i < 2000; i++)
{
    var list = Shortlist(pool, "A", 2, rng);
    orderOk &= list.Count == 2 && list.Distinct().Count() == 2 && !list.Contains("A")
        && list.SequenceEqual(list.OrderBy(m => Array.IndexOf(pool, m)));
    seen.UnionWith(list);
}
Check(orderOk, "shortlist: right size, distinct, skips previous, pool order");
Check(seen.SetEquals(["B", "C", "D"]), "shortlist: every other map can be drawn");

// With no previous map (or one that isn't in the pool) any map can be drawn.
var fresh = new HashSet<string>();
for (int i = 0; i < 2000; i++) fresh.UnionWith(Shortlist(pool, null, 1, rng));
Check(fresh.SetEquals(pool), "shortlist with no previous map can draw any map");

// Random and the no-vote draw stay on the shortlist.
var shortlist = new[] { "B", "D" };
var noVotes = new HashSet<string>();
for (int i = 0; i < 2000; i++)
{
    noVotes.Add(Resolve(shortlist, [], "A", rng)!);
    noVotes.Add(Resolve(shortlist, [RandomChoice], "A", rng)!);
}
Check(noVotes.SetEquals(shortlist), "random draws stay on the shortlist");
Check(Shortlist([], "A", 3, rng).Count == 0, "empty pool gives an empty shortlist");

// Single-map pool never deadlocks.
Check(Resolve(["A"], [], "A", rng) == "A", "single map pool");
Check(Resolve([], ["A"], null, rng) == null, "empty pool returns null");
Console.WriteLine("all checks passed");
