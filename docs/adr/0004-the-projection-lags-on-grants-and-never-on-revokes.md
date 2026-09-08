# 4. The row-level security projection may lag on a grant and never on a revoke

Status: accepted

## Context

The facade refuses to serve a document the subject may not read. That is application-layer discipline, and
application-layer discipline is a convention rather than a boundary. A reporting job, an ad hoc query, or
a code path that forgets to call the authorization service reaches the content table directly and gets
everything.

Postgres row-level security fixes that, but a policy cannot traverse a relationship graph living in
another process. It needs a flat table it can join against.

## Decision

`document_permission_cache` holds `(document, subject, relation)` triples. A policy on the `documents`
table joins to it and to nothing else. The table is derived, disposable and rebuildable from the
relationship store at any time.

Its correctness rule is deliberately asymmetric:

| Change | Behaviour | Why |
|---|---|---|
| Grant | May take until the next poll to appear, bounded by the poll interval | Being briefly too strict costs a retry |
| Revoke | Rows are removed in the same request that revokes the tuple, before any poll | Being briefly too permissive costs a disclosure |

Rebuilding a subject deletes and reinserts inside one transaction, so there is no window in which the old
rows and the new rows are both visible. The changelog cursor advances only after every affected subject
has been rebuilt, so a crash repeats a batch rather than skipping one, and rebuilding a subject twice
produces the same rows.

## Consequences

The revoke path over-removes on purpose: everything for that subject goes, and the next poll puts back
whatever they are still entitled to. Precision there would mean resolving the blast radius of a revocation
synchronously, which is the expensive half of the problem, to save a table write.

A structural change, one that moves a resource between parents, rebuilds every subject the projection
already knows about. Inheritance means the blast radius of moving a folder is everyone who could see
anything inside it. That is coarse, it is stated as a limitation in `docs/operations.md` rather than
dressed up, and a system with a large user base would resolve the affected set from the moved subtree.

The relationship store itself has no such lag. It is the source of truth, the decision path holds no cache
in front of it, and a revoked relationship is refused on the very next call.

## How this nearly went wrong

The policy was written, `FORCE ROW LEVEL SECURITY` was set, and the tests showed every subject reading
every document. Postgres exempts `SUPERUSER` and `BYPASSRLS` roles from every policy, and the container
image logs in as a superuser, which is also how most services connect in practice. The protection read
correctly in the schema and enforced nothing at all.

The migration now creates a `NOSUPERUSER NOBYPASSRLS` role, grants it read on the two tables, grants
membership to the login user, and the query issues `SET LOCAL ROLE` before it runs. `RevocationTests`
asserts the whole thing from the outside: a subject the projection has never heard of sees nothing.
