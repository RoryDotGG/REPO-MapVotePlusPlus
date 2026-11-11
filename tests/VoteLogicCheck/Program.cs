using MapVote;
using static MapVote.VoteLogic;
using static MapVote.VoteMode;

// Assert-based self-check for the vote rules. Run: dotnet run --project tests/VoteLogicCheck
var pool = new[] { "A", "B", "C", "D" };
var rng = new Random(1234);

// Most checks skip only the previous map, as vanilla does: recent is that one map, or nothing.
Dictionary<string, int> Draw(string[] ballots, string[] recent, VoteMode mode = Lottery, int n = 20000)
{
    var counts = new Dictionary<string, int>();
    for (int i = 0; i < n; i++)
    {
        var w = Resolve(pool, ballots, recent, mode, rng)!;
        counts[w] = counts.GetValueOrDefault(w) + 1;
    }
    return counts;
}

void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); Console.WriteLine("ok  " + what); }
bool Near(float a, float b, float within = 1e-5f) => Math.Abs(a - b) < within;
bool Same(IEnumerable<string> a, params string[] b) => a.SequenceEqual(b);

// ---- lottery (the default) ----

// Nobody votes -> uniform random over every map except the one just played.
var none = Draw([], recent: ["A"]);
Check(!none.ContainsKey("A") && none.Keys.Count == 3, "no votes: random, skips previous map");

// Only some people vote -> winner is always one of their picks.
var some = Draw(["B", "C"], recent: []);
Check(some.Keys.All(k => k is "B" or "C"), "partial votes: only voted maps can win");

// One ballot each is a fair coin; three-to-one is roughly 75/25.
var weighted = Draw(["B", "B", "B", "C"], recent: []);
Check(Math.Abs(weighted["B"] / 20000f - 0.75f) < 0.02f, "votes are weighted tickets");

// A Random ballot can land anywhere (but not on the previous map).
var withRandom = Draw([RandomChoice], recent: ["D"]);
Check(withRandom.Keys.Count == 3 && !withRandom.ContainsKey("D"), "random ballot draws from the full pool");

// Votes for a map that no longer exists (mod removed) are ignored.
Check(Draw(["ZZZ", "C"], [], n: 500).Keys.Single() == "C", "stale ballots are ignored");

// Odds shown in the UI match the rules and sum to 1.
var odds = Odds(pool, ["A", RandomChoice], recent: ["A"], Lottery);
Check(Near(pool.Sum(p => odds[p]), 1f), "odds sum to 1");
Check(Near(odds["A"], 0.5f) && Near(odds["B"], 0.5f / 3), "odds split random share across eligible maps");

// A voted-for previous map is still allowed: players chose it.
Check(Draw(["A"], recent: ["A"], n: 200).Keys.Single() == "A", "explicit vote can repeat the previous map");

// ---- majority ----

// The most votes wins outright, and the odds say so.
Check(Draw(["B", "B", "C"], [], Majority, 2000).Keys.Single() == "B", "majority: most votes wins");
var clear = Odds(pool, ["B", "C", "B"], [], Majority);
Check(clear["B"] == 1f && clear["C"] == 0f && clear[RandomChoice] == 0f, "majority: a clear leader shows 100%");

// A tie is a fair draw between the tied maps only; the map with fewer votes can't win.
var tie = Draw(["B", "B", "C", "C", "D"], [], Majority);
Check(tie.Keys.Count == 2 && !tie.ContainsKey("D") && Math.Abs(tie["B"] / 20000f - 0.5f) < 0.02f,
    "majority: a tie is drawn between the tied maps");
var tieOdds = Odds(pool, ["B", "B", "C", "C", "D"], [], Majority);
Check(tieOdds["B"] == 0.5f && tieOdds["C"] == 0.5f && tieOdds["D"] == 0f, "majority: tied maps split the odds");

// Random is a choice of its own. With the most votes, the map comes from the random pool, previous map skipped.
var randomWins = Draw([RandomChoice, "B", RandomChoice], recent: ["A"], Majority);
Check(randomWins.Keys.Count == 3 && !randomWins.ContainsKey("A"), "majority: Random with the most votes draws a random map");
var randomWinOdds = Odds(pool, [RandomChoice, "B", RandomChoice], ["A"], Majority);
Check(randomWinOdds[RandomChoice] == 1f && randomWinOdds["A"] == 0f && Near(randomWinOdds["B"], 1f / 3),
    "majority: Random's win is spread over the random pool");

// Tied with a map, Random is half the draw.
var randomTie = Odds(pool, [RandomChoice, "B"], ["A"], Majority);
Check(randomTie[RandomChoice] == 0.5f && Near(randomTie["B"], 0.5f + 0.5f / 3) && Near(randomTie["C"], 0.5f / 3)
    && randomTie["A"] == 0f, "majority: Random tied with a map is half the draw");
// Outvoted, Random counts for nothing.
var randomLoses = Odds(pool, [RandomChoice, "B", "B"], ["A"], Majority);
Check(randomLoses["B"] == 1f && randomLoses[RandomChoice] == 0f, "majority: an outvoted Random counts for nothing");

// Nobody voting is the same random draw as in a lottery; stale ballots are ignored; a voted previous map can win.
var noneMajority = Draw([], ["A"], Majority);
Check(!noneMajority.ContainsKey("A") && noneMajority.Keys.Count == 3, "majority: no votes is a random map, previous skipped");
Check(Draw(["ZZZ", "ZZZ", "C"], [], Majority, 500).Keys.Single() == "C", "majority: stale ballots are ignored");
Check(Draw(["A", "A", "B"], ["A"], Majority, 200).Keys.Single() == "A", "majority: a voted previous map can win");

// ---- recent maps ----

// The list random draws skip: the previous map first, then the ones played before it this run.
Check(Same(Recent("A", ["A", "B", "C"], 1), "A"), "recent: 1 is the previous map alone, as in vanilla");
Check(Same(Recent(null, ["B", "C"], 1)), "recent: 1 with no previous map skips nothing, as in vanilla");
Check(Recent("A", ["A", "B"], 0).Count == 0, "recent: 0 skips nothing");
Check(Same(Recent("C", ["C", "B", "B", "A", "D"], 3), "C", "B", "A"), "recent: the last few played, most recent first, no repeats");
Check(Same(Recent("A", ["B"], 5), "A", "B"), "recent: fewer played than asked is all of them");

// Random, and the pick when nobody votes, skip every recent map, in both modes, and the odds say so.
foreach (var mode in new[] { Lottery, Majority })
{
    var label = mode.ToString().ToLowerInvariant();
    Check(Draw([], ["D", "C"], mode).Keys.Order().SequenceEqual(["A", "B"]), $"recent ({label}): no votes skips every recent map");
    Check(Draw([RandomChoice], ["D", "C"], mode).Keys.Order().SequenceEqual(["A", "B"]), $"recent ({label}): Random skips every recent map");
    var recentOdds = Odds(pool, [RandomChoice], ["D", "C"], mode);
    Check(recentOdds["C"] == 0f && recentOdds["D"] == 0f && Near(recentOdds["A"], 0.5f), $"recent ({label}): odds leave recent maps out");
}

// When every map is recent, the ones played longest ago come back first, so there is always a map.
Check(Same(RandomPool(pool, ["D", "C", "B", "A"]), "A"), "recent: every map recent, the one played longest ago comes back");
Check(Draw([], ["D", "C", "B", "A"], n: 500).Keys.Single() == "A", "recent: no-vote draw still finds a map when every map is recent");
Check(Same(RandomPool(["A"], ["A"]), "A"), "recent: a single map still plays when it's recent");
Check(Same(RandomPool(pool, ["ZZZ", "D"]), "A", "B", "C"), "recent: maps no longer in the pool are ignored");

// A vote for a recent map still counts.
Check(Draw(["C"], ["D", "C"], n: 200).Keys.Single() == "C", "recent: a vote for a recent map still counts");

// A shortlist skips recent maps. If too few others are left, the ones played longest ago come back to fill it.
var six = new[] { "A", "B", "C", "D", "E", "F" };
bool skipsAll = true, fills = true, fillsMore = true;
for (int i = 0; i < 500; i++)
{
    skipsAll &= Same(Shortlist(six, ["A", "B", "C"], 3, rng), "D", "E", "F");
    fills &= Same(Shortlist(six, ["A", "B", "C"], 4, rng), "C", "D", "E", "F");
    fillsMore &= Same(Shortlist(six, ["A", "B", "C"], 5, rng), "B", "C", "D", "E", "F");
}
Check(skipsAll, "recent shortlist: skips every recent map");
Check(fills && fillsMore, "recent shortlist: too few others, so the maps played longest ago come back to fill it");

// The game's own pick skips only the previous map, so the host leaves it the pick only when the rules agree.
Check(DrawsLikeGame(pool, ["A"], pool, "A") && DrawsLikeGame(pool, [], pool, null), "game pick: default rules are the game's own pick");
Check(!DrawsLikeGame(pool, ["A", "B"], pool, "A"), "game pick: skipping two maps means the host draws");
Check(!DrawsLikeGame(pool, [], pool, "A"), "game pick: skipping none means the host draws");
Check(!DrawsLikeGame(["B", "C"], ["A"], pool, "A"), "game pick: a shortlist means the host draws");
Check(DrawsLikeGame(["B", "C", "D"], ["A"], pool, "A"), "game pick: every map but the previous one is the game's pick");
Check(DrawsLikeGame(pool, ["A", "ZZZ"], pool, "A"), "game pick: recent maps no longer in the pool don't count");
Check(DrawsLikeGame(["A", "B"], ["A", "B"], ["A", "B"], "A"), "game pick: two maps, both recent, comes to the same");

// ---- excluded maps ----

// The setting is a comma-separated list, trimmed, matched in any case.
var listed = NameList(" Level - Manor , headman manor,, ,Museum ");
Check(listed.Count == 3 && listed.Contains("LEVEL - MANOR") && listed.Contains("museum"), "excluded: a comma-separated list, trimmed, any case");
Check(NameList("").Count == 0 && NameList(" , ").Count == 0 && NameList(null).Count == 0, "excluded: an empty list names nothing");

// A map is excluded by its level name or by the name the vote list shows.
var levels = new[] { "Level - Manor", "Level - Wizard", "Level - Arctic", "Level - Museum" };
string Shown(string level) => level switch
{
    "Level - Manor" => "HEADMAN MANOR",
    "Level - Wizard" => "SWIFTBROOM ACADEMY",
    "Level - Arctic" => "MCJANNEK STATION",
    _ => "MUSEUM OF HUMAN ART",
};
Check(Same(Allowed(levels, NameList("level - manor"), Shown), "Level - Wizard", "Level - Arctic", "Level - Museum"),
    "excluded: by level name, any case");
Check(Same(Allowed(levels, NameList("Swiftbroom Academy, mcjannek station"), Shown), "Level - Manor", "Level - Museum"),
    "excluded: by the name the vote list shows, any case");
Check(Same(Allowed(levels, NameList("Nowhere, Manor"), Shown), levels), "excluded: a name that matches no map changes nothing");
Check(Same(Allowed(levels, [], Shown), levels), "excluded: nothing excluded offers every map");

// Excluding every map is ignored, so there is still something to vote on.
Check(Same(Allowed(levels, NameList("Headman Manor, Level - Wizard, Level - Arctic, museum of human art"), Shown), levels),
    "excluded: excluding every map is ignored");
Check(Allowed([], NameList("Level - Manor"), Shown).Count == 0, "excluded: an empty pool stays empty");
// levels as the recent list: Manor played last, Museum longest ago.
Check(Resolve(Allowed(levels, NameList(string.Join(",", levels)), Shown), [], levels, Majority, rng) == "Level - Museum",
    "excluded: every map excluded and every map recent still picks a map");

// Excluded maps never come up: never on a shortlist, never drawn by Random or with nobody voting, in either mode,
// and a stale vote for one (cast before the host excluded it) is ignored.
var allowedLevels = Allowed(levels, NameList("Headman Manor, Level - Museum"), Shown);
bool neverListed = true, neverDrawn = true;
for (int i = 0; i < 2000; i++)
{
    neverListed &= Shortlist(allowedLevels, ["Level - Wizard"], 1, rng).All(m => m is "Level - Wizard" or "Level - Arctic");
    foreach (var mode in new[] { Lottery, Majority })
        foreach (var ballots in new[] { Array.Empty<string>(), [RandomChoice], ["Level - Manor", "Level - Manor", "Level - Wizard"] })
            neverDrawn &= Resolve(allowedLevels, ballots, ["Level - Wizard"], mode, rng) is "Level - Wizard" or "Level - Arctic";
}
Check(neverListed, "excluded: never on a shortlist");
Check(neverDrawn, "excluded: never drawn, and votes for one are ignored");
var excludedOdds = Odds(allowedLevels, [RandomChoice], ["Level - Wizard"], Lottery);
Check(!excludedOdds.ContainsKey("Level - Manor") && excludedOdds["Level - Arctic"] == 1f, "excluded: no odds for an excluded map");

// With maps excluded the host draws the next map itself, unless the only map left out is the previous one anyway.
Check(!DrawsLikeGame(allowedLevels, ["Level - Wizard"], levels, "Level - Wizard"), "game pick: excluded maps mean the host draws");
Check(DrawsLikeGame(Allowed(levels, NameList("Level - Wizard"), Shown), ["Level - Wizard"], levels, "Level - Wizard"),
    "game pick: excluding only the previous map is still the game's pick");

// ---- both modes ----

// The odds the vote list and the reel show are what the draw actually does.
foreach (var mode in new[] { Lottery, Majority })
{
    foreach (var ballots in new[] { new[] { "B", "C", "C", RandomChoice }, ["B", RandomChoice], [], ["D", "D", "A", RandomChoice, RandomChoice] })
    {
        foreach (var recent in new[] { new[] { "A" }, ["A", "B"] })
        {
            var shown = Odds(pool, ballots, recent, mode);
            var drawn = Draw(ballots, recent, mode);
            Check(Near(pool.Sum(p => shown[p]), 1f) && pool.All(p => Math.Abs(drawn.GetValueOrDefault(p) / 20000f - shown[p]) < 0.02f),
                $"{mode.ToString().ToLowerInvariant()}: odds match the draw for [{string.Join(", ", ballots)}], skipping {string.Join(", ", recent)}");
        }
    }
}

// The default settings (Lottery, skip one) are exactly the 1.1.0 rule: the same odds, random pool and, from the same
// seed, the same shortlists and winners, whatever the ballots and whatever has been played.
var maps = new[] { "A", "B", "C", "D", "E", "F", "G", "H" };
var gen = new Random(99);
bool sameOdds = true, samePool = true, sameShortlists = true, sameWinners = true;
for (int i = 0; i < 5000; i++)
{
    var p = maps.Take(gen.Next(0, maps.Length + 1)).ToArray();
    var choices = p.Concat([RandomChoice, "ZZZ"]).ToArray();
    var ballots = Enumerable.Range(0, gen.Next(0, 7)).Select(_ => choices[gen.Next(choices.Length)]).ToArray();
    var prev = gen.Next(4) switch { 0 => null, 1 => "ZZZ", _ => p.Length > 0 ? p[gen.Next(p.Length)] : null };
    var played = Enumerable.Range(0, gen.Next(0, 5)).Select(_ => maps[gen.Next(maps.Length)]).ToArray();
    var recent = Recent(prev, played, 1);
    int limit = gen.Next(0, maps.Length + 2), seed = gen.Next();

    var now = Odds(p, ballots, recent, Lottery);
    var then = Legacy.Odds(p, ballots, prev);
    sameOdds &= now.Count == then.Count && then.All(kv => now.TryGetValue(kv.Key, out var o) && o == kv.Value);
    samePool &= RandomPool(p, recent).SequenceEqual(Legacy.RandomPool(p, prev));
    sameShortlists &= Shortlist(p, recent, limit, new Random(seed)).SequenceEqual(Legacy.Shortlist(p, prev, limit, new Random(seed)));

    Random a = new(seed), b = new(seed);
    for (int k = 0; k < 5; k++) sameWinners &= Resolve(p, ballots, recent, Lottery, a) == Legacy.Resolve(p, ballots, prev, b);
}
Check(sameOdds, "default rules: odds are exactly as in 1.1.0");
Check(samePool, "default rules: random pool is exactly as in 1.1.0");
Check(sameShortlists, "default rules: same seed, same shortlists as in 1.1.0");
Check(sameWinners, "default rules: same seed, same winners as in 1.1.0");

// ---- shortlist ----

// No limit, or a limit that covers every map, puts the whole pool up, in order.
Check(Shortlist(pool, ["A"], 0, rng).SequenceEqual(pool), "limit 0 offers every map");
Check(Shortlist(pool, ["A"], 4, rng).SequenceEqual(pool) && Shortlist(pool, ["A"], 9, rng).SequenceEqual(pool),
    "a limit covering the pool offers every map");

// A smaller limit draws that many distinct maps, never the previous one, in pool order, and every other map gets a turn.
var seen = new HashSet<string>();
var orderOk = true;
for (int i = 0; i < 2000; i++)
{
    var list = Shortlist(pool, ["A"], 2, rng);
    orderOk &= list.Count == 2 && list.Distinct().Count() == 2 && !list.Contains("A")
        && list.SequenceEqual(list.OrderBy(m => Array.IndexOf(pool, m)));
    seen.UnionWith(list);
}
Check(orderOk, "shortlist: right size, distinct, skips previous, pool order");
Check(seen.SetEquals(["B", "C", "D"]), "shortlist: every other map can be drawn");

// With no previous map (or one that isn't in the pool) any map can be drawn.
var fresh = new HashSet<string>();
for (int i = 0; i < 2000; i++) fresh.UnionWith(Shortlist(pool, [], 1, rng));
Check(fresh.SetEquals(pool), "shortlist with no previous map can draw any map");

// Random and the no-vote draw stay on the shortlist, in both modes.
var shortlist = new[] { "B", "D" };
var noVotes = new HashSet<string>();
for (int i = 0; i < 2000; i++)
{
    noVotes.Add(Resolve(shortlist, [], ["A"], Lottery, rng)!);
    noVotes.Add(Resolve(shortlist, [RandomChoice], ["A"], Lottery, rng)!);
    noVotes.Add(Resolve(shortlist, [RandomChoice], ["A"], Majority, rng)!);
}
Check(noVotes.SetEquals(shortlist), "random draws stay on the shortlist");
Check(Shortlist([], ["A"], 3, rng).Count == 0, "empty pool gives an empty shortlist");

// Single-map pool never deadlocks.
Check(Resolve(["A"], [], ["A"], Lottery, rng) == "A" && Resolve(["A"], [], ["A"], Majority, rng) == "A", "single map pool");
Check(Resolve([], ["A"], [], Lottery, rng) == null && Resolve([], ["A"], [], Majority, rng) == null, "empty pool returns null");
Console.WriteLine("all checks passed");
