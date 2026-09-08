# 1. OpenFGA rather than a hand-rolled relationship engine

Status: accepted

## Context

The domain needs an answer to questions like "may Alice read this document, given that she is a member of
the org that owns the workspace that owns the folder that holds it". That is a question about a graph, not
about a role, and the usual way it gets answered in a codebase is a chain of joins written slightly
differently in each service that needs it.

Google's Zanzibar paper describes a purpose-built engine for exactly this. OpenFGA is its open-source
implementation, under Apache-2.0.

## Decision

Self-hosted OpenFGA, reached over its HTTP API through `OpenFga.Sdk`, with the model defined once and
pinned by id.

## Consequences

Writing the traversal by hand would have been a worse version of something that already exists and has
been reviewed by people who think about nothing else. The interesting failure modes in relationship
evaluation are not obvious ones: cycles, negation combined with inheritance, and the interaction between a
computed userset and a tuple-to-userset rule. An implementation that looks right and is subtly wrong is
the expected outcome, and it fails open.

What is given up is a network hop on every decision. That is the cost the latency budget is built around,
and it is why the decision path holds no cache: a revoked relationship has to stop granting access on the
very next call, and a cache in front of the check is the natural way to break that without noticing.

The SDK is pre-1.0. Its request and response types change between releases, so every call to it goes
through `IRelationshipEngine` in `src/GrantPath.Rebac` and nothing else in the tree references the SDK.
An upgrade is then a change to one implementation rather than to every call site. See ADR 2 for why the
model is built in typed C# rather than parsed from the DSL.

## Alternatives considered

**A hand-written traversal over Postgres.** Rejected above. It also puts the correctness of the whole
product on code with no external review.

**SpiceDB.** A credible Zanzibar implementation with a good story on consistency tokens. Rejected because
its .NET client is thinner and the project already carries one pre-1.0 client; two would be worse.

**Casbin.** Solves a different problem well. Its model is policy-matching rather than a relationship
graph, and expressing four-level inheritance in it means writing the traversal anyway.
