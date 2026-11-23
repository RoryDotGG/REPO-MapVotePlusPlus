# MapVote++ stats server

A Cloudflare Worker with a D1 database, served at https://mvpp.rory.gg. It takes the anonymous pings hosts send (`POST /v1/ping`), adds them to totals and serves the dashboard (`/`), its data (`/v1/stats`) and the level art in [`public/maps`](public/maps), the game's own intro cards. [`schema.sql`](schema.sql) is everything it keeps. The README's [Anonymous stats](../README.md#anonymous-stats) section says what hosts send.

## Run it locally

```bash
npm install
npm run db:local
npm run dev
```

The dashboard is at http://127.0.0.1:8787. `npm test` runs the Worker on a fresh local database and checks what it accepts, rejects, names, caches and serves. CI runs it on every pull request.

## Deploy

Pushing a version tag deploys it: the Release workflow checks the Worker, applies `schema.sql`, deploys, and only then publishes the mod. So the server always takes a new version's pings first, and it has to keep taking pings from older versions.

The workflow needs a `CLOUDFLARE_API_TOKEN` repository secret. To make it:

1. In the Cloudflare dashboard, go to **Account API tokens** and select **Create Token**.
2. Start from the **Edit Cloudflare Workers** template.
3. Add the permission **Account → D1 → Edit**.
4. Limit **Account Resources** to the account in `wrangler.toml` and **Zone Resources** to `rory.gg`.
5. Save it in GitHub under **Settings → Secrets and variables → Actions** as `CLOUDFLARE_API_TOKEN`.

To deploy by hand instead, run `npx wrangler login`, then `npm run db:remote` and `npm run deploy`. The deploy creates the `mvpp` DNS record, so the `rory.gg` zone has to be in the account you logged in to.

Request logs stay off (`[observability] enabled = false` in `wrangler.toml`), because they would hold IP addresses. Keep them off.
