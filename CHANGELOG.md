# Changelog

## 1.1.0

- The host can limit how many maps each vote offers with the new `MapsPerVote` setting. Each vote draws that many maps at random, never the one you just played, and the list says so, for example **3 OF 5 MAPS, DRAWN AT RANDOM**. Random, and the pick when nobody votes, only land on one of those maps.
- With [REPOConfig](https://thunderstore.io/c/repo/p/nickklmao/REPOConfig/) installed, settings changed in game apply straight away. Changing **Maps Per Vote** draws the open vote's list again and keeps votes for Random or for maps still on it. If the host switches off **Vote In Lobby Menu** or **Vote In Truck** while a vote is open there, the vote closes and the next map is random.
- Players who join the lobby now pick up the vote that's already running straight away.

## 1.0.1

- The truck's vote list no longer runs off the right edge of its popup.
- The vote no longer shows up in the shop or on the truck's loading screen.
- Sending the truck off now plays the reveal, and the truck leaves once the reel lands. If nobody voted, it lands on a random map other than the last one, as in vanilla.
- The reveal is bigger and sits in the middle of the screen, with the game dimmed behind it. It fades out when the level changes instead of vanishing.

## 1.0.0

First release.

- Vote on the next level in the truck between levels (press **M**) or in the pre-game lobby.
- If nobody votes, the next map is random, as in vanilla. Otherwise the winner is drawn from the votes, one ticket per vote, and a vote for Random can land on any map.
- The map list comes from the game at runtime, so official and custom maps show up without config.
- The vote list is a native game menu built with MenuLib. It shows map thumbnails and who voted for each map.
- A slot-machine reel reveals the next map to everyone at once.
- The host runs the vote over REPOLib networked events, and players who join mid-vote catch up straight away.
