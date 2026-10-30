<p align="center">
  <img src="https://raw.githubusercontent.com/RoryDotGG/REPO-MapVotePlusPlus/main/docs/banner.png" alt="MapVote++: pick the next level in R.E.P.O." width="100%">
</p>

<p align="center">
  <a href="https://github.com/RoryDotGG/REPO-MapVotePlusPlus/blob/main/LICENSE"><img alt="MIT licence" src="https://img.shields.io/badge/licence-MIT-FFB31A?style=flat-square&labelColor=141414"></a>
  <img alt="BepInEx 5" src="https://img.shields.io/badge/BepInEx-5-FFB31A?style=flat-square&labelColor=141414">
  <img alt="REPOLib 4.3" src="https://img.shields.io/badge/REPOLib-4.3-FFB31A?style=flat-square&labelColor=141414">
  <img alt="No MenuLib" src="https://img.shields.io/badge/MenuLib-not%20needed-FF5A1F?style=flat-square&labelColor=141414">
</p>

**MapVote++** lets your crew vote on the next level, in the truck between levels or in the pre-game lobby. It reads the map list from the game at runtime, so new official maps and custom maps show up without any config.

## The rules

- **Nobody votes:** a random map, never the one you just played (same as vanilla).
- **Some people vote:** the winner is drawn only from the maps they picked.
- **Each vote is one ticket:** 3 votes for A and 1 for B gives A a 75% chance and B 25%.
- **Voting Random:** a ticket that can land on any map.
- **Live odds:** every row shows its current win chance, so the result is never a surprise.

## What it looks like

| Vote board (truck) | Reveal and HUD box |
| --- | --- |
| ![Vote board mock-up](https://raw.githubusercontent.com/RoryDotGG/REPO-MapVotePlusPlus/main/docs/mock-board.jpg) | ![Reveal mock-up](https://raw.githubusercontent.com/RoryDotGG/REPO-MapVotePlusPlus/main/docs/mock-reveal.jpg) |

<sub>Mock-ups drawn from the UI's layout values. In game the mod uses R.E.P.O.'s own font and each map's loading-screen art as its thumbnail.</sub>

## How to vote

| Where | What you see | Controls |
| --- | --- | --- |
| Truck | A **MAP VOTE** box bottom-right that pulses until you vote | **M** opens the board. Click a map, or press `1`-`9` (`R` for Random). `M` or `Esc` closes. |
| Lobby menu | The board docked on the right | Click a map. `M` hides or shows it. |

- Right-click, or click your pick again, to clear your vote.
- The countdown (25s by default) starts at the first vote and drops to 3s once everyone has voted.
- When it ends, a slot-machine reel spins at the top of the screen and lands on the next map for everyone at once.
- In the lobby, pressing **Start** early plays the reveal first, then starts the game.
- If the truck leaves before the timer ends, the vote resolves instantly with the same rules.

## Install

**Thunderstore mod manager (r2modman, Gale):** download the zip from [Releases](https://github.com/RoryDotGG/REPO-MapVotePlusPlus/releases), then use **Settings > Import local mod**. Install **REPOLib** from the Online tab if you don't already have it.

**Manual:** put `MapVotePlusPlus.dll` in `BepInEx/plugins/MapVotePlusPlus/`.

Everyone in the lobby needs the mod. The host runs the vote.

## Config

`BepInEx/config/RoryDotGG.MapVotePlusPlus.cfg`

| Key | Default | Notes |
| --- | --- | --- |
| `Controls.ToggleKey` | `M` | Any Unity Input System key name. |
| `Voting.VoteSeconds` | `25` | 5 to 120. The host's value is used. |
| `Voting.VoteInLobbyMenu` | `true` | |
| `Voting.VoteInTruck` | `true` | |

## How it works

- **Host-authoritative.** Clients send their votes to the host over REPOLib `NetworkedEvent`s. The host draws the winner and broadcasts the full vote state, so players who join mid-vote catch up immediately.
- **Map loading.** The host patches `RunManager.SetRunLevel` to load the winner. The game's own level sync then tells every client which map it is.
- **Map list.** Maps come from `REPOLib.Modules.Levels.AllLevels`. Names, thumbnails (loading art) and accent colours (fog colour) are read from each `Level` asset.
- **UI.** It is built in code on its own overlay canvas: procedural sprites, no asset bundle and no MenuLib.

| File | Purpose |
| --- | --- |
| [`src/VoteLogic.cs`](src/VoteLogic.cs) | Pure vote rules (no Unity), covered by `tests/VoteLogicCheck` |
| [`src/VoteSession.cs`](src/VoteSession.cs) | State, networking, host timers, level override |
| [`src/VoteUi.cs`](src/VoteUi.cs) | Board, HUD box, reveal reel |
| [`src/UiKit.cs`](src/UiKit.cs), [`src/MapInfo.cs`](src/MapInfo.cs) | Palette, procedural sprites, per-map display data |

## Build

Requires the .NET SDK (8 or later).

```bash
dotnet build -c Release
```

To copy the DLL straight into a mod manager profile on build:

```bash
dotnet build -c Release -p:PluginsDir="$HOME/path/to/profile/BepInEx/plugins"
```

Run the vote-rule self-check (no game needed):

```bash
dotnet run --project tests/VoteLogicCheck
```

## Compatibility

- **Everyone needs the mod.** REPOLib assigns network event codes in registration order, so matching mod lists are safest.
- **One map-vote mod at a time.** Don't run it alongside another map-vote mod: both patch `RunManager.SetRunLevel`.

## Licence

[MIT](LICENSE)
