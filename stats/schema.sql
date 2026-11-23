-- Counts only. A ping is added to these totals and thrown away: no row is ever one game, player or install.
-- Activity and settings are kept apart on purpose, so a rare combination of settings can't be followed slot to slot.

-- Games and players per 30-minute UTC slot (slot = unix seconds / 1800). Each host sends one heartbeat a slot.
CREATE TABLE IF NOT EXISTS activity (
  slot INTEGER PRIMARY KEY,
  games INTEGER NOT NULL DEFAULT 0,
  players INTEGER NOT NULL DEFAULT 0
);

-- Heartbeats per day by mod version and host settings.
CREATE TABLE IF NOT EXISTS settings (
  day TEXT NOT NULL,
  version TEXT NOT NULL,
  mode TEXT NOT NULL,
  maps_per_vote INTEGER NOT NULL,
  recent_skip INTEGER NOT NULL,
  n INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (day, version, mode, maps_per_vote, recent_skip)
);

-- Rolled votes per day: the winning map, where the vote was and how it was won.
CREATE TABLE IF NOT EXISTS picks (
  day TEXT NOT NULL,
  map TEXT NOT NULL,
  context TEXT NOT NULL,
  how TEXT NOT NULL,
  n INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (day, map, context, how)
);

-- Per map per day: how many votes offered it and how many ballots it got.
CREATE TABLE IF NOT EXISTS offers (
  day TEXT NOT NULL,
  map TEXT NOT NULL,
  offered INTEGER NOT NULL DEFAULT 0,
  ballots INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (day, map)
);
