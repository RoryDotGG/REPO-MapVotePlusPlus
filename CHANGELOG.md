# Changelog

## 1.2.0

- The host can choose how the votes pick the next map with the new `VoteMode` setting. **Lottery** works as before: each vote is a ticket, so 3 votes to 1 is a 75% chance. With **Majority**, the map with the most votes wins, and a tie is drawn at random between the tied maps.
- The new `RecentMapsToSkip` setting keeps the last few maps played this run out of Random, out of the pick when nobody votes, and out of the maps `MapsPerVote` draws. The default of 1 skips only the map you just played, as before.
- The new `ExcludedMaps` setting leaves maps out of the vote and out of random draws altogether, for example to drop a level you don't like from a custom map pack. Name each map as the vote list shows it or by its level name.
- The host can pick the next map outright, for a stream or a planned run, by holding **Shift** and clicking it or pressing its number. The reveal plays straight away and lands on that map with **HOST'S PICK** under it. **Shift** with Random rolls a random map instead. Players' clicks are always votes.
- Once someone has voted, each map's row shows its chance of winning, so you can see how far ahead the leader really is.
- A long map list in the lobby now scrolls instead of shrinking. Up to ten rows show at once, and the mouse wheel moves the list.
- The reveal reel shows each map's art behind its name, and says how the winner won when it lands, for example **3 OF 4 VOTES**.
- The truck vote no longer has a countdown, and the `VoteSeconds` setting is gone. The vote stays open until someone sends the truck off, and then the reveal plays.
- When you arrive in the truck, the vote opens by itself so you can pick a map or skip. Turn off `ShowVoteOnArrival` to open it yourself with **M** instead.
- If the host doesn't have MapVote++, has switched the vote off, or runs a version that can't talk to yours, the vote list now says so instead of waiting for the host forever.
- The vote list does less work every frame.
- Players and hosts on 1.1.0 can still vote together with 1.2.0.

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
