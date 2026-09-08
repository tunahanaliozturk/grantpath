# 2. Two engines, combined by a rule that attributes can only narrow

Status: accepted

## Context

Relationships answer "is this person connected to this thing, directly or through the hierarchy". They do
not answer "and is there anything about right now that forbids it anyway": a document marked confidential
and a reader without clearance, a request from outside the corporate network, a contractor past their end
date. Those are attribute questions.

The obvious move is to put both in one engine. Every attempt to do that ends with a policy language that
can also grant, and at that point there are two ways to get access and only one of them is the one people
audit.

## Decision

Two independent engines and one fixed combination rule:

```
allow  =  relationship allows  AND  attributes do not object
```

The relationship graph is OpenFGA. The attribute layer is a small evaluator in `src/GrantPath.Abac`: JSON
policies with a resource type, an effect, a priority and a condition tree, compiled once when loaded.

An attribute policy with effect `Allow` is not a grant. It is a statement that this policy set raises no
objection, and it matters only because it can outrank a lower-priority deny. Among matching policies the
highest priority wins, and a tie goes to deny.

## Consequences

Deny by default falls out of the same rule rather than being a separate feature. A resource with no tuples
produces a relationship deny, and nothing downstream can turn that into an allow, so the absence of data
is never read as permission. That is asserted directly in `TransitiveAccessTests`, on a resource that
exists inside a real hierarchy and has nobody related to it.

The rule is fixed in code rather than configurable. A configurable combination is a second permission
system with worse documentation.

The cost is that a genuinely attribute-driven grant cannot be expressed. Somebody who should see a
document because of a property rather than a relationship needs a relationship written for them. That is
the intended trade: the set of people who can reach a resource is always answerable from the relationship
store alone.

The one narrowing rule that does belong in the graph, "this person is specifically excluded from this
document", is expressed there as a subtraction, because a relationship graph only ever grants. See the
`blocked` relation in `model/authorization-model.fga`.

The four combinations are pinned by `NarrowingTests`, including the one that matters most: an attribute
policy saying allow, at the highest priority, against a subject with no relationship at all, still denies.
