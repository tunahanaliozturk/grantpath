# Decision latency and throughput

Measured against the `docker compose` stack on the machine below, using
`load/GrantPath.LoadTests`. Everything runs on one laptop, so the relationship store, the database and the
service compete for the same cores. A deployment with the components on separate hosts would look
different in both directions: lower contention, higher network cost.

```
Windows 11 (10.0.26100.9106/24H2), Intel Core Ultra 7 255H, 16 physical cores
Docker Desktop 29.5.3, containers on the WSL2 backend
.NET 10.0.11, Release, service and load generator on the same machine
Postgres 18, OpenFGA v1.19.0 with the Postgres datastore
```

The harness is a closed loop: N callers each issue the next request as soon as the previous one returns.
That measures a throughput ceiling well, and it measures latency honestly only below saturation. At 32
callers the service is past capacity and the percentiles are queueing rather than service time, which is
why the low-concurrency rows are the ones worth reading for latency.

## With the relationship path resolved (the default)

| Callers | Throughput | p50 | p95 | p99 |
|---:|---:|---:|---:|---:|
| 1 | 293/s | 3.22 ms | 4.46 ms | 5.18 ms |
| 4 | 770/s | 4.85 ms | 6.85 ms | 8.77 ms |
| 8 | 1120/s | 6.89 ms | 9.71 ms | 11.58 ms |
| 32 | 1165/s | 26.04 ms | 41.83 ms | 57.53 ms |

## With path resolution turned off

`GrantPath:Decision:ResolveRelationshipPath = false`. The decision is identical; only the explanation is
dropped.

| Callers | Throughput | p50 | p95 | p99 |
|---:|---:|---:|---:|---:|
| 1 | 447/s | 2.08 ms | 3.23 ms | 3.91 ms |
| 4 | 1318/s | 2.84 ms | 3.90 ms | 4.88 ms |
| 8 | 2261/s | 3.17 ms | 5.21 ms | 8.13 ms |
| 32 | 3015/s | 9.82 ms | 17.23 ms | 22.03 ms |

## What this says

**The target is met with explanations on.** The requirement was p50 under 5 ms and p99 under 20 ms at 500
decisions per second. At 4 callers the service is doing 770 per second at p50 4.85 ms and p99 8.77 ms, and
at 8 callers it is doing 1120 per second while p99 is still under 12 ms.

**Explainability costs about a millisecond and roughly half the ceiling.** One extra batched round trip to
the relationship store per decision: 3.22 ms against 2.08 ms at a single caller, and 1165 against 3015
decisions per second at the top. That is the price of being able to answer "why" three weeks later, it is
a switch rather than a constant, and it is published rather than described as negligible.

**Nothing was dropped from the audit trail at any point.** Every run sampled 250 request ids through
`/authz/why` after finishing, including the 3015 per second run, and found all of them. The queue-full
fallback is covered separately by an integration test that fills a queue of capacity one.

## Reproducing

```bash
docker compose up -d --build
dotnet run -c Release --project load/GrantPath.LoadTests -- \
  http://localhost:5120 user:alice viewer document:onboarding 3000 4
```

For arrival-rate scheduling rather than a closed loop, `load/authz-check.js` runs the same profile under
k6 with a fixed request rate and threshold assertions.
