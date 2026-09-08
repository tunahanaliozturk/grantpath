# 5. The service authorizes its own administration, and explains every decision it makes

Status: accepted

## Context

Two questions that look unrelated turn out to have the same answer.

The first is who may write relationship tuples and attribute policies. An admin API with its own notion of
who counts as an admin, a config list or a role claim, is a second permission system, and the second one
is never the one anybody audits.

The second is what `/authz/why` should return. The honest answer to "why was this allowed three weeks ago"
cannot be produced by re-running today's rules, and a path recorded without the model version behind it is
a set of claims nobody can check.

## Decision

**Administration is a relationship.** The model carries a `platform` type with an `administrator`
relation, and every admin route is gated by `Check(subject, administrator, platform:grantpath)` through
the same uncached decision path as everything else.

**The path is resolved at decision time.** On an allowed check the facade resolves the containment chain,
which is cached and usually free, and asks the relationship store about every level of that chain in one
batched call. The answer records where the grant originated, which is the outermost level that grants,
because everything below it inherits.

Resolution is a switch, `GrantPath:Decision:ResolveRelationshipPath`, default on. It is not free: it adds
one batched round trip to a path with a five millisecond budget, and the README publishes the measured
cost with it on and off rather than claiming it is negligible.

## Consequences

Granting somebody the right to administer the system is a tuple write, recorded in the same audit trail,
revocable the same way, and effective on the next call because the gate is not cached. `SelfAdministrationTests`
asserts the full round trip, including that removing the tuple removes access to the admin API.

A refused administrative call is itself a decision with a request id, returned in the problem response, so
the attempt is in the trail alongside everything else rather than only in a log line.

The recorded explanation names the level that granted, every level that did not, which attribute policies
matched, which one decided, and the model id. That is enough for a review to disagree with the outcome,
which is the point: a decision nobody can argue with is not auditable, it is just asserted.

Explaining a denial costs the same round trip as explaining an allow. That is deliberate. An auditor
needs to distinguish "nobody granted this at any level" from "the check never ran", and only a recorded
chain of non-granting levels does that.

## Alternatives considered

**Resolve the path lazily, when somebody asks.** Cheaper, and wrong. The rules and the tuples move; an
explanation produced later describes a system that may not be the one that made the decision.

**Record only the verdict and the inputs.** Cheapest, and it answers a different question. "Deny" without
a path is not a reason.

**A separate admin credential.** Simpler to build, and it puts the most dangerous capability in the system
outside the mechanism built to govern capability.
