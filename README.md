# GrantPath

Fine-grained authorization with relationships, attributes that can only narrow, and a decision you can
still explain three weeks later.

Deny by default, correct inheritance four levels deep with no tuple anywhere near the leaf, and a durable
reason for every answer, at **p50 4.85 ms and p99 8.77 ms while serving 770 decisions a second** with
explanations switched on.

```
org:acme  →  workspace:engineering  →  folder:handbooks  →  document:onboarding
```

Alice is a member of the org. That is the only tuple she has. Nothing is written about the workspace, the
folder or the document in connection with her, and she can read the document. Take the org tuple away and
the very next call refuses, including the query that goes straight to Postgres and never speaks to this
service at all.

## Why this exists

A role check stops saying anything useful the moment a product has structure. "Alice can view this
document because she is in the workspace that owns the folder that holds it" is a statement about a graph,
and in most codebases it ends up as a chain of joins, written slightly differently in every service that
needs it, each one a slightly wrong reimplementation of the same rule.

Relationships alone are not enough either. "Not if the document is confidential and the reader has no
clearance" is not a relationship, it is a property of the request. The usual answer is one engine that
does both, and it always grows the ability to grant, at which point there are two ways to get access and
only one of them is audited.

GrantPath keeps them apart and combines them with a rule that cannot be configured:

```
allow  =  the relationship graph allows  AND  the attribute layer does not object
```

An attribute policy can refuse. It can never manufacture access that no relationship justifies. That is a
property of the code rather than a convention policy authors are trusted to follow, and it is what makes
the set of people who can reach a resource answerable from the relationship store alone.

## What is worth looking at

| | |
|---|---|
| The combination rule and the explanation it builds | [`AuthorizationFacade.cs`](src/GrantPath.Api/Authorization/AuthorizationFacade.cs) |
| The relationship model, including the one narrowing rule | [`authorization-model.fga`](model/authorization-model.fga) |
| Attribute conditions, compiled once and evaluated in nanoseconds | [`ConditionNode.cs`](src/GrantPath.Abac/ConditionNode.cs) |
| The audit queue and why it never drops | [`DecisionAuditWriter.cs`](src/GrantPath.Api/Auditing/DecisionAuditWriter.cs) |
| Row-level security that a superuser cannot walk past | [`20260904125745_RowLevelSecurity.cs`](src/GrantPath.Data/Migrations/20260904125745_RowLevelSecurity.cs) |
| The headline proof | [`TransitiveAccessTests.cs`](tests/GrantPath.IntegrationTests/TransitiveAccessTests.cs) |

## Quick start

```bash
docker compose up -d --build
```

Postgres, OpenFGA, and the service, with the schema applied, the relationship model written and the
demonstration domain seeded. Then walk [`GrantPath.http`](GrantPath.http) from the top, or:

```bash
# Mallory is related to nothing. Deny, with no policy saying so.
curl -s localhost:5120/authz/check -H 'content-type: application/json' \
  -d '{"subject":"user:mallory","relation":"viewer","resource":"document:onboarding"}'

# Alice holds one org-level tuple. Read reason.grantedAt in the answer.
curl -s localhost:5120/authz/check -H 'content-type: application/json' \
  -d '{"subject":"user:alice","relation":"viewer","resource":"document:onboarding"}'
```

The second answer carries the whole chain:

```json
{
  "allowed": true,
  "relationshipAllowed": true,
  "attributeVerdict": "NoOpinion",
  "reason": {
    "relationshipPath": [
      { "resource": "document:onboarding",     "relation": "viewer", "grants": true },
      { "resource": "folder:handbooks",        "relation": "viewer", "grants": true },
      { "resource": "workspace:engineering",   "relation": "viewer", "grants": true },
      { "resource": "org:acme",                "relation": "viewer", "grants": true }
    ],
    "grantedAt": "org:acme",
    "summary": "Allowed: the grant comes from org:acme, and no policy forbids it."
  }
}
```

For the Aspire dashboard with traces and metrics: `dotnet run --project src/GrantPath.AppHost`.

## The four things this proves

**Nothing is permitted by default.** A resource with no tuples denies every relation for every subject.
Not because a policy says so, but because the combination rule has no branch that turns an absent
relationship into an allow. `TransitiveAccessTests` asserts it on a resource that exists inside a real
hierarchy and has nobody related to it.

**Inheritance is real, and it carries the role.** One `member` tuple on the org reaches a document four
levels down. The same tuple does not grant `editor`, because being able to read a folder has never implied
being able to change what is in it. An explicit `blocked` tuple on one document beats everything it
inherited, and only on that document: a relationship graph only ever grants, so the domain's one narrowing
rule is written as a subtraction in the model.

**Attributes narrow and never widen.** The full matrix is pinned by `NarrowingTests`, including the case
that matters most: an attribute policy saying allow, at the highest priority, against a subject with no
relationship at all, still denies.

**Every decision can be explained afterwards.** `/authz/why/{requestId}` reads the row that was written at
decision time. It does not re-run today's rules against today's data, because that answers a different
question and answers it confidently. The record carries the path, the policies, and the authorization
model id that produced it, so an explanation stays legible after the rules have moved on.

## Defence in depth

`/demo/documents` runs a plain `SELECT` with no check, no facade and no filtering in application code. It
still returns only what the subject may read, because a row-level security policy on the table joins to a
flattened projection of the relationship graph.

That projection is deliberately asymmetric. A new grant may take until the next poll to appear, which is
inconvenient. A revocation is removed in the request that performs it, before any poll, because a database
path briefly more permissive than the truth is the one failure it must not have.

Getting this right took two attempts. The first one enforced nothing: Postgres exempts `SUPERUSER` and
`BYPASSRLS` roles from every policy, and the container image logs in as a superuser, as most services do.
The policy read correctly and every subject saw every document. The migration now creates a role with
neither attribute and the query switches to it for the duration of the transaction. `RevocationTests`
asserts the result from the outside.

## Numbers

Measured on an Intel Core Ultra 7 255H, everything on one laptop, service and load generator competing for
the same cores. Full tables and method in [docs/benchmark-results](docs/benchmark-results).

| | p50 | p99 | Throughput |
|---|---:|---:|---:|
| Explanations on, 4 callers | 4.85 ms | 8.77 ms | 770/s |
| Explanations on, 8 callers | 6.89 ms | 11.58 ms | 1120/s |
| Explanations off, 4 callers | 2.84 ms | 4.88 ms | 1318/s |
| Explanations off, 8 callers | 3.17 ms | 8.13 ms | 2261/s |

Explainability costs about a millisecond per decision and roughly half the throughput ceiling: one extra
batched round trip to the relationship store. It is a switch rather than a constant, and the price is
published rather than described as negligible.

Every load run sampled 250 request ids through `/authz/why` afterwards and found all of them, including
the run at 3015 decisions a second.

The attribute evaluator itself is not the cost. Eight nested policies take 1.4 microseconds against a
three millisecond decision. The benchmark existed to establish that, and its first run found something
worth fixing: ranking matched policies did a linear lookup inside the sort comparator, which is quadratic
in the number of matches and cost 12.8 microseconds at sixty-four policies against 3.6 after the fix.

## Tests

```bash
dotnet run --project tests/GrantPath.UnitTests          # 44, pure logic
dotnet run --project tests/GrantPath.IntegrationTests   # 28, real Postgres and real OpenFGA
```

The integration suite starts both containers with Testcontainers and runs the service on a real socket.
Nothing about OpenFGA's relationship evaluation or Postgres row-level security is faked, because those are
the behaviours being proven and a substitute would only prove the substitute was configured.

One unit test parses `model/authorization-model.fga` and asserts every type and relation in it exists in
the model that actually runs, and the reverse. The readable model is documentation, documentation drifts,
and the drift that matters is a relation added in code and never explained.

## Design decisions

1. [OpenFGA rather than a hand-rolled relationship engine](docs/adr/0001-openfga-rather-than-a-hand-rolled-engine.md)
2. [Two engines, combined by a rule that attributes can only narrow](docs/adr/0002-two-engines-and-attributes-only-narrow.md)
3. [The decision trail is written off the request path, and never dropped](docs/adr/0003-the-audit-trail-is-never-dropped.md)
4. [The projection may lag on a grant and never on a revoke](docs/adr/0004-the-projection-lags-on-grants-and-never-on-revokes.md)
5. [The service authorizes its own administration, and explains every decision](docs/adr/0005-the-service-authorizes-its-own-administration.md)

Operational detail, alert thresholds and the runbook are in [docs/operations.md](docs/operations.md),
including an honest list of what this does not do.

## Licensing

Every dependency at every depth is permissively licensed, and a build step proves it rather than a README
claiming it. `dotnet run --project tools/GrantPath.LicenseAudit -- .` reads the licence expression or
licence file of all 178 packages in the restored tree and fails the build on anything carrying a fee, a
subscription or a field-of-use restriction. It is the reason this project uses OpenFGA rather than a
commercially licensed alternative, and the reason Aspire is pinned below 13.5.

MIT.
