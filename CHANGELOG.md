# Changelog

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
