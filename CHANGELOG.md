# Changelog

## 1.0.0

First release.

- Vote on the next level in the truck between levels (press **M**) or in the pre-game lobby.
- If nobody votes, the next map is random, as in vanilla. Otherwise the winner is drawn from the votes, one ticket per vote, and a vote for Random can land on any map.
- The map list comes from the game at runtime, so official and custom maps show up without config.
- The vote list is a native game menu built with MenuLib. It shows map thumbnails and who voted for each map.
- A slot-machine reel reveals the next map to everyone at once.
- The host runs the vote over REPOLib networked events, and players who join mid-vote catch up straight away.
