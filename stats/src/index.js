// MapVote++ anonymous stats. The host's game posts a ping, the Worker adds it to daily counts and throws it away.
// Nothing that identifies a game, player or install is read or written: no IDs, no names, no IP addresses.
import dashboard from "./dashboard.html";

const SLOT_SECONDS = 1800;
const RANDOM = "__random__";
const MODES = ["Lottery", "Majority"];
const CONTEXTS = ["lobby", "truck"];
const HOWS = ["vote", "none", "host", "hostRandom"];
const MAX_BODY_BYTES = 8192;
// With MapsPerVote "All" a vote offers the whole pool, so this allows big modded pools. Each offer is a D1 row written.
const MAX_OFFERS = 100;
// A map has to come up this often in the window before the dashboard names it, so an unreleased map stays private.
const MIN_OFFERS_TO_NAME = 20;
// ...and on this many days, so one burst of fake pings can't put a name on the page.
const MIN_DAYS_TO_NAME = 3;
// Level names look like "Level - Manor". Anything else, such as a link or emoji, is counted but only ever shown as Other maps.
const NAMEABLE = /^[\p{L}\p{N} '&()_-]+$/u;

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    if (url.pathname === "/v1/ping" && request.method === "POST") return ping(request, env);
    if (url.pathname === "/v1/stats" && request.method === "GET") return cachedStats(url, env, ctx);
    if (url.pathname === "/" && request.method === "GET")
      return new Response(dashboard, { headers: { "content-type": "text/html; charset=utf-8", "cache-control": "public, max-age=300" } });
    return new Response("Not found", { status: 404 });
  },
};

async function ping(request, env) {
  // Keyed by IP only inside Cloudflare's rate limiter, which forgets it after the period. It is never stored here.
  const ip = request.headers.get("cf-connecting-ip") ?? "";
  if (env.LIMIT && !(await env.LIMIT.limit({ key: ip })).success) return new Response(null, { status: 429 });

  const text = await readBody(request, MAX_BODY_BYTES);
  if (text === null) return new Response(null, { status: 413 });
  let body;
  try {
    body = JSON.parse(text);
  } catch {
    return new Response(null, { status: 400 });
  }

  const statements = body?.t === "hb" ? heartbeat(env, body) : body?.t === "vote" ? vote(env, body) : null;
  if (!statements) return new Response(null, { status: 400 });
  await env.DB.batch(statements);
  return new Response(null, { status: 204 });
}

/** Returns the body as text, or null if it's over max bytes. Stops reading there, so a huge body is never buffered. */
async function readBody(request, max) {
  if (Number(request.headers.get("content-length")) > max) return null;
  if (!request.body) return "";
  const chunks = [];
  let size = 0;
  for await (const chunk of request.body) {
    size += chunk.byteLength;
    if (size > max) return null;
    chunks.push(chunk);
  }
  return new Blob(chunks).text();
}

/** Worker responses skip Cloudflare's cache, so each data centre keeps the stats for 5 minutes instead of asking D1 per load. */
async function cachedStats(url, env, ctx) {
  // One key whatever the query string, so ?x=1, ?x=2... can't force fresh D1 reads.
  const key = new Request(`${url.origin}/v1/stats`);
  const hit = await caches.default.match(key);
  if (hit) return hit;
  const response = json(await stats(env), 300);
  ctx.waitUntil(caches.default.put(key, response.clone()));
  return response;
}

/** Returns the D1 statements for a valid heartbeat, or null. */
function heartbeat(env, b, now = Date.now()) {
  if (!common(b) || !int(b.mapsPerVote, 0, 64) || !int(b.recentSkip, 0, 10)) return null;
  const slot = Math.floor(now / 1000 / SLOT_SECONDS);
  return [
    env.DB.prepare(
      "INSERT INTO activity (slot, games, players) VALUES (?1, 1, ?2) ON CONFLICT (slot) DO UPDATE SET games = games + 1, players = players + ?2",
    ).bind(slot, b.players),
    env.DB.prepare(
      "INSERT INTO settings (day, version, mode, maps_per_vote, recent_skip, n) VALUES (?1, ?2, ?3, ?4, ?5, 1) ON CONFLICT DO UPDATE SET n = n + 1",
    ).bind(day(now), b.v, b.mode, b.mapsPerVote, b.recentSkip),
  ];
}

/** Returns the D1 statements for a valid rolled vote, or null. */
function vote(env, b, now = Date.now()) {
  if (!common(b) || !CONTEXTS.includes(b.context) || !HOWS.includes(b.how)) return null;
  if (!b.offers || typeof b.offers !== "object" || Array.isArray(b.offers)) return null;
  const offers = Object.entries(b.offers);
  if (offers.length < 1 || offers.length > MAX_OFFERS) return null;
  let total = 0;
  for (const [map, ballots] of offers) {
    if (!mapName(map) || !int(ballots, 0, b.players)) return null;
    total += ballots;
  }
  if (total > b.players || !mapName(b.winner) || b.winner === RANDOM || !Object.hasOwn(b.offers, b.winner)) return null;

  const d = day(now);
  const statements = [
    env.DB.prepare(
      "INSERT INTO picks (day, map, context, how, n) VALUES (?1, ?2, ?3, ?4, 1) ON CONFLICT DO UPDATE SET n = n + 1",
    ).bind(d, b.winner, b.context, b.how),
  ];
  for (const [map, ballots] of offers)
    statements.push(
      env.DB.prepare(
        "INSERT INTO offers (day, map, offered, ballots) VALUES (?1, ?2, 1, ?3) ON CONFLICT DO UPDATE SET offered = offered + 1, ballots = ballots + ?3",
      ).bind(d, map, ballots),
    );
  return statements;
}

function common(b) {
  return typeof b.v === "string" && /^\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(b.v) && int(b.players, 1, 64) && MODES.includes(b.mode);
}

function int(n, min, max) {
  return Number.isInteger(n) && n >= min && n <= max;
}

function mapName(s) {
  return typeof s === "string" && s.length >= 1 && s.length <= 64 && !/[\u0000-\u001f\u007f]/.test(s);
}

function day(ms) {
  return new Date(ms).toISOString().slice(0, 10);
}

async function stats(env, now = Date.now()) {
  const slot = Math.floor(now / 1000 / SLOT_SECONDS);
  const since30 = day(now - 29 * 86400000);
  const since7 = day(now - 6 * 86400000);
  const q = (sql, ...args) => env.DB.prepare(sql).bind(...args);

  const [activity, offers, wins, how, rolls, settings] = (
    await env.DB.batch([
      q("SELECT slot, games, players FROM activity WHERE slot >= ?1 ORDER BY slot", slot - 7 * 48),
      q("SELECT map, SUM(offered) AS offered, SUM(ballots) AS ballots, COUNT(*) AS days FROM offers WHERE day >= ?1 GROUP BY map", since30),
      q("SELECT map, SUM(n) AS wins FROM picks WHERE day >= ?1 GROUP BY map", since30),
      q("SELECT context, how, SUM(n) AS n FROM picks WHERE day >= ?1 GROUP BY context, how", since30),
      q("SELECT day, SUM(n) AS n FROM picks WHERE day >= ?1 GROUP BY day ORDER BY day", since30),
      q("SELECT version, mode, maps_per_vote, recent_skip, SUM(n) AS n FROM settings WHERE day >= ?1 GROUP BY version, mode, maps_per_vote, recent_skip", since7),
    ])
  ).map((r) => r.results);

  // The last whole slot is "now": the current one is still filling up.
  const last = activity.find((a) => a.slot === slot - 1) ?? { games: 0, players: 0 };

  const winsByMap = Object.fromEntries(wins.map((w) => [w.map, w.wins]));
  const maps = [];
  const other = { map: null, offered: 0, ballots: 0, wins: 0 };
  let random = null;
  for (const o of offers) {
    const row = { map: o.map, offered: o.offered, ballots: o.ballots, wins: winsByMap[o.map] ?? 0 };
    if (o.map === RANDOM) random = row;
    else if (o.offered >= MIN_OFFERS_TO_NAME && o.days >= MIN_DAYS_TO_NAME && NAMEABLE.test(o.map)) maps.push(row);
    else for (const k of ["offered", "ballots", "wins"]) other[k] += row[k];
  }
  maps.sort((a, b) => b.wins - a.wins || b.offered - a.offered);
  if (other.offered) maps.push(other);

  const tally = (key) => {
    const out = {};
    for (const s of settings) out[s[key]] = (out[s[key]] ?? 0) + s.n;
    return out;
  };

  return {
    generated: new Date(now).toISOString(),
    now: { games: last.games, players: last.players },
    activity: activity.map((a) => [a.slot * SLOT_SECONDS * 1000, a.games, a.players]),
    rolls,
    maps,
    random,
    how,
    versions: tally("version"),
    modes: tally("mode"),
    mapsPerVote: tally("maps_per_vote"),
    recentSkip: tally("recent_skip"),
  };
}

function json(data, maxAge) {
  return new Response(JSON.stringify(data), {
    headers: { "content-type": "application/json", "cache-control": `public, max-age=${maxAge}` },
  });
}
