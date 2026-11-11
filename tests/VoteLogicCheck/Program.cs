using MapVote;
using static MapVote.VoteLogic;
using static MapVote.VoteMode;

// Assert-based self-check for the vote rules. Run: dotnet run --project tests/VoteLogicCheck
var pool = new[] { "A", "B", "C", "D" };
var rng = new Random(1234);

Dictionary<string, int> Draw(string[] ballots, string? previous, VoteMode mode = Lottery, int n = 20000)
{
    var counts = new Dictionary<string, int>();
    for (int i = 0; i < n; i++)
    {
        var w = Resolve(pool, ballots, previous, mode, rng)!;
        counts[w] = counts.GetValueOrDefault(w) + 1;
    }
    return counts;
}

void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); Console.WriteLine("ok  " + what); }
bool Near(float a, float b, float within = 1e-5f) => Math.Abs(a - b) < within;

// ---- lottery (the default) ----

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
Check(Draw(["ZZZ", "C"], null, n: 500).Keys.Single() == "C", "stale ballots are ignored");

// Odds shown in the UI match the rules and sum to 1.
var odds = Odds(pool, ["A", RandomChoice], previous: "A", Lottery);
Check(Near(pool.Sum(p => odds[p]), 1f), "odds sum to 1");
Check(Near(odds["A"], 0.5f) && Near(odds["B"], 0.5f / 3), "odds split random share across eligible maps");

// A voted-for previous map is still allowed: players chose it.
Check(Draw(["A"], previous: "A", n: 200).Keys.Single() == "A", "explicit vote can repeat the previous map");

// ---- majority ----

// The most votes wins outright, and the odds say so.
Check(Draw(["B", "B", "C"], null, Majority, 2000).Keys.Single() == "B", "majority: most votes wins");
var clear = Odds(pool, ["B", "C", "B"], null, Majority);
Check(clear["B"] == 1f && clear["C"] == 0f && clear[RandomChoice] == 0f, "majority: a clear leader shows 100%");

// A tie is a fair draw between the tied maps only; the map with fewer votes can't win.
var tie = Draw(["B", "B", "C", "C", "D"], null, Majority);
Check(tie.Keys.Count == 2 && !tie.ContainsKey("D") && Math.Abs(tie["B"] / 20000f - 0.5f) < 0.02f,
    "majority: a tie is drawn between the tied maps");
var tieOdds = Odds(pool, ["B", "B", "C", "C", "D"], null, Majority);
Check(tieOdds["B"] == 0.5f && tieOdds["C"] == 0.5f && tieOdds["D"] == 0f, "majority: tied maps split the odds");

// Random is a choice of its own. With the most votes, the map comes from the random pool, previous map skipped.
var randomWins = Draw([RandomChoice, "B", RandomChoice], previous: "A", Majority);
Check(randomWins.Keys.Count == 3 && !randomWins.ContainsKey("A"), "majority: Random with the most votes draws a random map");
var randomWinOdds = Odds(pool, [RandomChoice, "B", RandomChoice], "A", Majority);
Check(randomWinOdds[RandomChoice] == 1f && randomWinOdds["A"] == 0f && Near(randomWinOdds["B"], 1f / 3),
    "majority: Random's win is spread over the random pool");

// Tied with a map, Random is half the draw.
var randomTie = Odds(pool, [RandomChoice, "B"], "A", Majority);
Check(randomTie[RandomChoice] == 0.5f && Near(randomTie["B"], 0.5f + 0.5f / 3) && Near(randomTie["C"], 0.5f / 3)
    && randomTie["A"] == 0f, "majority: Random tied with a map is half the draw");
// Outvoted, Random counts for nothing.
var randomLoses = Odds(pool, [RandomChoice, "B", "B"], "A", Majority);
Check(randomLoses["B"] == 1f && randomLoses[RandomChoice] == 0f, "majority: an outvoted Random counts for nothing");

// Nobody voting is the same random draw as in a lottery; stale ballots are ignored; a voted previous map can win.
var noneMajority = Draw([], "A", Majority);
Check(!noneMajority.ContainsKey("A") && noneMajority.Keys.Count == 3, "majority: no votes is a random map, previous skipped");
Check(Draw(["ZZZ", "ZZZ", "C"], null, Majority, 500).Keys.Single() == "C", "majority: stale ballots are ignored");
Check(Draw(["A", "A", "B"], "A", Majority, 200).Keys.Single() == "A", "majority: a voted previous map can win");

// ---- both modes ----

// The odds the vote list and the reel show are what the draw actually does.
foreach (var mode in new[] { Lottery, Majority })
{
    foreach (var ballots in new[] { new[] { "B", "C", "C", RandomChoice }, ["B", RandomChoice], [], ["D", "D", "A", RandomChoice, RandomChoice] })
    {
        var shown = Odds(pool, ballots, "A", mode);
        var drawn = Draw(ballots, "A", mode);
        Check(Near(pool.Sum(p => shown[p]), 1f) && pool.All(p => Math.Abs(drawn.GetValueOrDefault(p) / 20000f - shown[p]) < 0.02f),
            $"{mode.ToString().ToLowerInvariant()}: odds match the draw for [{string.Join(", ", ballots)}]");
    }
}

// Lottery is exactly the 1.1.0 rule: the same odds, and from the same seed the same winners, for any ballots.
var maps = new[] { "A", "B", "C", "D", "E", "F", "G", "H" };
var gen = new Random(99);
bool sameOdds = true, sameWinners = true;
for (int i = 0; i < 5000; i++)
{
    var p = maps.Take(gen.Next(0, maps.Length + 1)).ToArray();
    var choices = p.Concat([RandomChoice, "ZZZ"]).ToArray();
    var ballots = Enumerable.Range(0, gen.Next(0, 7)).Select(_ => choices[gen.Next(choices.Length)]).ToArray();
    var prev = gen.Next(4) switch { 0 => null, 1 => "ZZZ", _ => p.Length > 0 ? p[gen.Next(p.Length)] : null };
    int seed = gen.Next();

    var now = Odds(p, ballots, prev, Lottery);
    var then = Legacy.Odds(p, ballots, prev);
    sameOdds &= now.Count == then.Count && then.All(kv => now.TryGetValue(kv.Key, out var o) && o == kv.Value);

    Random a = new(seed), b = new(seed);
    for (int k = 0; k < 5; k++) sameWinners &= Resolve(p, ballots, prev, Lottery, a) == Legacy.Resolve(p, ballots, prev, b);
}
Check(sameOdds, "lottery: odds are exactly as in 1.1.0");
Check(sameWinners, "lottery: same seed, same winners as in 1.1.0");

// ---- shortlist ----

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

// Random and the no-vote draw stay on the shortlist, in both modes.
var shortlist = new[] { "B", "D" };
var noVotes = new HashSet<string>();
for (int i = 0; i < 2000; i++)
{
    noVotes.Add(Resolve(shortlist, [], "A", Lottery, rng)!);
    noVotes.Add(Resolve(shortlist, [RandomChoice], "A", Lottery, rng)!);
    noVotes.Add(Resolve(shortlist, [RandomChoice], "A", Majority, rng)!);
}
Check(noVotes.SetEquals(shortlist), "random draws stay on the shortlist");
Check(Shortlist([], "A", 3, rng).Count == 0, "empty pool gives an empty shortlist");

// Single-map pool never deadlocks.
Check(Resolve(["A"], [], "A", Lottery, rng) == "A" && Resolve(["A"], [], "A", Majority, rng) == "A", "single map pool");
Check(Resolve([], ["A"], null, Lottery, rng) == null && Resolve([], ["A"], null, Majority, rng) == null, "empty pool returns null");
Console.WriteLine("all checks passed");
