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

// Single-map pool never deadlocks.
Check(Resolve(["A"], [], "A", rng) == "A", "single map pool");
Check(Resolve([], ["A"], null, rng) == null, "empty pool returns null");
Console.WriteLine("all checks passed");
