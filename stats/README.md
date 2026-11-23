# MapVote++ stats server

A Cloudflare Worker with a D1 database, served at https://mvpp.rory.gg. It takes the anonymous pings hosts send (`POST /v1/ping`), adds them to totals and serves the dashboard (`/`) and its data (`/v1/stats`). [`schema.sql`](schema.sql) is everything it keeps. The README's [Anonymous stats](../README.md#anonymous-stats) section says what hosts send.

## Run it locally

```bash
npm install
npm run db:local
npm run dev
```

The dashboard is at http://127.0.0.1:8787.

## Deploy

1. `npx wrangler login`
2. `npx wrangler d1 create mapvote-stats`, then put the `database_id` it prints in `wrangler.toml`
3. `npm run db:remote`
4. `npm run deploy`
5. Open https://mvpp.rory.gg. The deploy creates the `mvpp` DNS record, so the `rory.gg` zone has to be in the account you logged in to.

Request logs stay off (`[observability] enabled = false` in `wrangler.toml`), because they would hold IP addresses. Keep them off.
