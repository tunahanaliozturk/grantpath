# Running GrantPath

What to configure, what to watch, and what to do when something goes wrong.

## Configuration

Only the connection string and the relationship store address have no usable default.

| Setting | Default | Notes |
|---|---|---|
| `ConnectionStrings:grantpath` | none | Postgres. The service will not start without it. |
| `GrantPath:Rebac:ApiUrl` | `http://localhost:8080` | Where OpenFGA is listening. |
| `GrantPath:Rebac:StoreName` | `grantpath` | Created on first start if absent. The model is written and pinned at startup. |
| `GrantPath:Decision:ResolveRelationshipPath` | `true` | Whether a decision resolves the path that produced it. Costs one batched round trip. See the measured price in [decision-latency.md](benchmark-results/decision-latency.md). |
| `GrantPath:Decision:ContainmentCacheLifetime` | 5 minutes | Backstop only. The changelog reader evicts an entry when a parent actually moves. |
| `GrantPath:Decision:PolicyCacheLifetime` | 30 seconds | Backstop only. The admin endpoints refresh on write. |
| `GrantPath:Audit:QueueCapacity` | 8192 | Decisions that may be waiting to be written. Full means the next one is written inline. |
| `GrantPath:Audit:BatchSize` | 200 | Rows per insert. |
| `GrantPath:Audit:FlushInterval` | 250 ms | How long the writer waits for a batch to fill. |
| `GrantPath:Projection:Enabled` | `true` | Whether the changelog reader runs. |
| `GrantPath:Projection:PollInterval` | 500 ms | The bound on how long a **grant** takes to reach the database path. Not the bound on a revoke. |
| `GrantPath:Callers:ApiKeys` | empty | Keys that identify a calling service. |
| `GrantPath:Callers:AllowAnonymous` | `false` | Development and tests only. True means anyone who can reach the port can ask about any subject. |
| `GrantPath:Database:MigrateOnStartup` | `false` | Leave false anywhere real. |
| `GrantPath:Database:SeedDemoData` | `false` | Creates the demonstration org and accounts. Never in production. |

## Schema changes

`MigrateOnStartup` stays off in production. A rolling deploy runs two versions side by side for a minute
or two, and a migration racing itself across instances is a poor way to discover that.

```bash
dotnet ef migrations script --idempotent \
  --project src/GrantPath.Data \
  --startup-project src/GrantPath.Api \
  --output migration.sql
```

Read it, then apply it. One migration in this repository does more than create tables: `RowLevelSecurity`
creates the `grantpath_reader` role, grants it read on two tables, and grants membership to the login
user. If the deployment uses a different login user, that grant has to be repeated for it, or the demo
query path fails with a permission error rather than silently returning everything.

## What to watch

| Signal | Meaning |
|---|---|
| `grantpath.audit.queue_depth` | **The one to alert on.** A sustained nonzero depth means the writer is falling behind, and the fallback that protects records moves the cost onto request latency where it is harder to see. |
| `grantpath.audit.synchronous_writes` | Decisions written on the request thread because the queue was full. Should be zero. |
| `grantpath.check.duration` | Histogram, tagged by relation, resource type and decision. Read p99, not the mean. |
| `grantpath.decision.total` | Counter by outcome. A sudden change in the deny ratio is usually a client bug or a bad policy, not an attack. |
| `grantpath.projection.lag` | Age of the oldest relationship change the projection has not applied. Above five seconds means the database path is further behind than the design promises. |
| `grantpath.projection.changes` | Changes applied. Flat at zero while tuples are being written means the reader has stopped. |

Spans are named `authz.check`, tagged with `authz.subject`, `authz.relation`, `authz.resource`,
`authz.decision` and `authz.latency_ms`.

Health: `/health/live` says the process is up. `/health/ready` additionally checks Postgres and asks the
relationship store a question, because an instance that cannot reach OpenFGA cannot make a single
decision and should not receive traffic.

Suggested alerts:

- `grantpath.audit.queue_depth` above zero for two minutes.
- `grantpath.audit.synchronous_writes` rate above zero at all.
- p99 of `grantpath.check.duration` above 50 ms for five minutes.
- `grantpath.projection.lag` above 5 seconds.

## Runbook

### Somebody can see something they should not

First establish whether the authorization service agrees. Ask it directly:

```http
POST /authz/check
{ "subject": "user:alice", "relation": "viewer", "resource": "document:handbook" }
```

Read `reason.relationshipPath` in the answer. It lists every level from the document up to the org and
says which of them granted. `grantedAt` names the level the permission originates from, and it is only
populated when the relationship check actually allowed.

If the service says deny and the user is still seeing the document, the read did not go through the
service. Check the projection for a stale row:

```sql
SELECT * FROM document_permission_cache WHERE subject = 'user:alice';
```

If a row is there that should not be, the changelog reader is stuck. See below.

If the service says allow and it should not, the tuple that grants it is named in the path. Remove it
through the admin API, which also clears the projection rows in the same request.

### Why was this allowed three weeks ago

```http
GET /authz/why/{requestId}
```

This reads the recorded row and does not re-evaluate anything. It returns the relationship path as it was
resolved at the time, which attribute policies matched, which one decided, and the authorization model id
that produced it. If the model id is not the current one, the rules have changed since, and the recorded
path is the only honest account of what happened.

To find the request ids in the first place:

```http
GET /authz/decisions?resource=document:handbook&limit=50
GET /authz/decisions?subject=user:alice&decision=Deny&limit=50
```

### The changelog reader has stopped

Symptom: `grantpath.projection.lag` climbing, or new grants never appearing in `/demo/documents`.

The cursor advances only after a pass succeeds, so a failing pass repeats rather than skipping. Look for
the warning from `ProjectionService`. If the cursor itself is wrong, clear it and let the reader rebuild
from the beginning:

```sql
UPDATE changelog_cursor SET continuation_token = NULL WHERE id = 1;
```

Rebuilding a subject is idempotent, so replaying the log produces the same rows.

A revocation does not depend on this reader. It removes the projection rows in the request that performs
it, which is why the database path is never more permissive than the relationship store even while the
reader is down.

### The audit queue is filling

The writer is slower than the decision rate, which in practice means Postgres is slow or the batch size is
too small for the load. Raise `GrantPath:Audit:BatchSize`, check for lock contention on `authz_decisions`,
and confirm the table's indexes have not been dropped.

Nothing is lost while this is happening. The fallback writes inline, and the row records that it did, so
`SELECT count(*) FROM authz_decisions WHERE written_synchronously` tells you how long the queue has been
full.

### The service will not start

If the log says the connection string is not configured, it never received `ConnectionStrings:grantpath`.

If startup hangs or fails reaching OpenFGA, the relationship store is unreachable. The engine connects
once, at startup, because it resolves the store and writes the authorization model; there is no lazy
retry. Fix the address or the store and restart.

## Known limitations

- **Structural changes rebuild every known subject.** Moving a folder between workspaces changes what
  everyone under it can see, and the reader responds by rebuilding every subject already in the
  projection. That is fine at demonstration scale and wrong at a large one, where the affected set should
  be resolved from the moved subtree instead.
- **The projection materialises one relation.** `GrantPath:Projection:Relation`, `viewer` by default. A
  deployment needing row-level security over editing as well needs a row per relation.
- **The caller gate is a shared key.** This service authorizes; it does not authenticate people. In a real
  deployment the caller would present a token from the same identity provider that authenticated the
  subject, and the key is the smallest honest stand-in for that.
- **No consistency token.** OpenFGA supports read-after-write consistency controls that this service does
  not use. Two writes in rapid succession followed by a check can in principle observe the earlier state.
  The revocation path does not depend on this, because it removes projection rows directly.
- **Single region.** One Postgres holds the application tables and OpenFGA's store. A multi-region
  deployment needs a plan for both, and this service does not have one.
