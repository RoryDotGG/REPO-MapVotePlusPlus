<p align="center">
  <img src="https://raw.githubusercontent.com/RoryDotGG/REPO-MapVotePlusPlus/main/docs/banner.png" alt="MapVote++: pick the next level in R.E.P.O." width="100%">
</p>

<p align="center">
  <a href="https://github.com/RoryDotGG/REPO-MapVotePlusPlus/blob/main/LICENSE"><img alt="MIT licence" src="https://img.shields.io/badge/licence-MIT-FFB31A?style=flat-square&labelColor=141414"></a>
  <img alt="BepInEx 5" src="https://img.shields.io/badge/BepInEx-5-FFB31A?style=flat-square&labelColor=141414">
  <img alt="REPOLib 4.3" src="https://img.shields.io/badge/REPOLib-4.3-FFB31A?style=flat-square&labelColor=141414">
  <img alt="MenuLib 2.6" src="https://img.shields.io/badge/MenuLib-2.6-FFB31A?style=flat-square&labelColor=141414">
</p>

**MapVote++** lets your crew vote on the next level, in the truck between levels or in the pre-game lobby. It reads the map list from the game at runtime, so new official maps and custom maps show up without any config.

## The rules

- **Nobody votes:** a random map, never the one you just played (same as vanilla).
- **Some people vote:** the winner is drawn only from the maps they picked.
- **Each vote is one ticket:** 3 votes for A and 1 for B gives A a 75% chance and B 25%.
- **Voting Random:** a ticket that can land on any map.
- **Live tally:** every map shows who voted for it, so the result is never a surprise.

## What it looks like

The vote list is part of the game's own menu, built with MenuLib, so it uses R.E.P.O.'s font, buttons and cursor. The reveal and the truck's HUD box sit on top of the game:

![Reveal mock-up](https://raw.githubusercontent.com/RoryDotGG/REPO-MapVotePlusPlus/main/docs/mock-reveal.jpg)

<sub>Mock-up drawn from the UI's layout values.</sub>

## How to vote

| Where | What you see | Controls |
| --- | --- | --- |
| Truck | A **MAP VOTE** box bottom-right that pulses until you vote | **M** opens the vote menu. Click a map, or press `1`-`9` (`R` for Random). `M` or `Esc` closes. |
| Lobby menu | A **MAP VOTE** panel to the right of the player list | Click a map, or press `1`-`9` (`R` for Random). |

- Click your pick again to clear your vote.
- **Lobby:** there's no timer. Anyone can vote or change their vote, including players who join late, until the host presses **Start Game**. The reveal then plays, and the game starts once it lands.
- **Truck:** the countdown (25s by default) starts at the first vote and drops to 3s once everyone has voted. If the truck leaves before the timer ends, the vote resolves instantly with the same rules.
- The reveal is a slot-machine reel at the top of the screen that lands on the next map for everyone at once. The board doesn't show the winner until the reel lands.

## Install

**Thunderstore mod manager (r2modman, Gale):** download the zip from [Releases](https://github.com/RoryDotGG/REPO-MapVotePlusPlus/releases), then use **Settings > Import local mod**. Install **REPOLib** and **MenuLib** from the Online tab if you don't already have them.

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
- **Map list.** Maps come from `REPOLib.Modules.Levels.AllLevels`. Names and accent colours (fog colour) are read from each `Level` asset.
- **UI.** The vote list is made of the game's own menu buttons via MenuLib. It's docked on the lobby page, and opens as a popup page in the truck, where the game frees the cursor and holds the player while it's open. The reveal reel and HUD box are drawn in code on an overlay canvas, with procedural sprites and no asset bundle.

| File | Purpose |
| --- | --- |
| [`src/VoteLogic.cs`](src/VoteLogic.cs) | Pure vote rules (no Unity), covered by `tests/VoteLogicCheck` |
| [`src/VoteSession.cs`](src/VoteSession.cs) | State, networking, host timers, level override |
| [`src/NativeBoard.cs`](src/NativeBoard.cs) | The vote list, as a game menu (MenuLib) |
| [`src/VoteUi.cs`](src/VoteUi.cs) | HUD box, reveal reel, hotkeys |
| [`src/UiKit.cs`](src/UiKit.cs), [`src/MapInfo.cs`](src/MapInfo.cs) | Palette, procedural sprites, per-map display data |
| [`src/Compat.cs`](src/Compat.cs) | Calls game methods whose signatures change between updates |

## Build

Requires the .NET SDK (8 or later). The first build downloads MenuLib from Thunderstore into `obj/`; it isn't on NuGet, and it's GPL-3.0, so it isn't bundled here.

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
