# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[semantic versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-09-08

First release. Everything below is covered by the test suites or by a published measurement.

### Authorization

- `POST /authz/check` combining a relationship decision from OpenFGA with an attribute decision from a
  purpose-built evaluator, under a fixed rule: allow only if the relationship graph allows and the
  attribute layer does not object.
- Deny by default as a property of that rule rather than a separate feature. A resource with no tuples
  denies every relation for every subject.
- Four-level inheritance across org, workspace, folder and document, resolved in a single call with no
  tuple required on the leaf, and carrying the role rather than flattening it.
- An explicit `blocked` relation that subtracts from whatever was inherited, scoped to the one document
  that names it.
- Attribute policies as JSON condition trees with `allOf`, `anyOf`, `not` and ten comparison operators,
  compiled once on load, resolved by priority with ties going to deny.

### Explainability

- Every decision recorded with the relationship path resolved at the time, the attribute policies that
  matched, the one that decided, the latency, and the authorization model id that produced it.
- `GET /authz/why/{requestId}` reads that record back and never re-evaluates.
- `GET /authz/decisions` for filtering the trail by subject, resource, decision and time, paged by
  timestamp rather than offset.
- Path resolution is a switch, `GrantPath:Decision:ResolveRelationshipPath`, with its cost measured and
  published rather than described as negligible.

### Durability

- Decisions queued to a bounded channel and written in batches off the request path.
- A full queue writes the record inline on the request thread and marks the row as such. No decision is
  ever dropped, and no load run has lost one.

### Defence in depth

- A flattened permission projection maintained from the OpenFGA changelog, consumed by a Postgres
  row-level security policy on the content table.
- Asymmetric convergence: a grant may lag by one poll interval, a revocation is removed in the request
  that performs it.
- A `NOSUPERUSER NOBYPASSRLS` role that the query switches to, so the policy applies even though the
  service connects as the database owner.

### Administration

- Tuple read and write, and full CRUD for attribute policies, every route gated by
  `Check(subject, administrator, platform:grantpath)` through the same uncached decision path.
- A refused administrative call returns the request id of the decision that refused it.
- Policies are compiled when written, so a malformed condition is rejected at the admin API rather than
  when the next decision tries to load it.

### Operations

- OpenTelemetry metrics for decision latency, decision outcomes, audit queue depth, synchronous audit
  writes, projection lag and applied changes, plus a span per decision.
- `/health/live` and `/health/ready`, the latter checking both Postgres and the relationship store.
- `docker compose up` for the whole stack, an Aspire AppHost for the same with a dashboard, a load harness
  and a k6 profile, and a runbook in `docs/operations.md`.
- An offline licence audit over every package in the restored tree, run in CI.
