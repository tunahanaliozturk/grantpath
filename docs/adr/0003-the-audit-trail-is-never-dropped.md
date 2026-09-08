# 3. The decision trail is written off the request path, and never dropped

Status: accepted

## Context

Every decision has to be answerable later. Not "we log allows and denies" but "on the fourteenth of March
this person read that document, and here is the relationship path that permitted it". The question is
always asked about a date in the past, which rules out reconstructing the answer from today's tuples.

Writing a row inside the decision means a database round trip on a path with a five millisecond budget.
Writing it afterwards, without care, means losing records exactly when the system is busy, which is
exactly when somebody will want to read them.

## Decision

A bounded `Channel<DecisionRecord>` drained by a background writer that batches on whichever comes first,
a full batch or a short interval. When the queue is full the record is written inline, on the request
thread, before the call returns, and the row records that it was.

The reasoning is serialised at decision time and stored as written, together with the authorization model
id that produced it. `/authz/why` reads that row back and never re-evaluates.

## Consequences

Under sustained overload the service gets slower rather than quieter. That is the intended direction. A
trail with holes in it is worse than a slow service, because the holes are invisible and appear under
precisely the conditions that make somebody go looking.

Bounded, not unbounded. An unbounded queue does not remove backpressure, it converts it into memory growth
and then into a process that dies holding every record it had not yet written.

`grantpath.audit.queue_depth` is the metric worth alerting on. A sustained nonzero depth means the writer
is falling behind, and the fallback that protects the records does so by moving the cost onto request
latency, where it shows up later and less clearly.

Storing the model id matters more than it looks. Once the rules have moved on, a recorded path without a
model version is a set of claims nobody can check.

## How this nearly went wrong

The first implementation used `BoundedChannelFullMode.DropWrite`, which reads as though it drops the
oldest item. It does not. `TryWrite` returns **true** and throws the incoming record away, so the
synchronous fallback never ran and the counter never moved. The queue silently lost decisions while
reporting success.

`AuditTrailTests.A_full_queue_writes_the_record_inline_rather_than_dropping_it` found it on its first run,
by filling a queue of capacity one with nothing draining it. The fix is `BoundedChannelFullMode.Wait`,
whose `TryWrite` refuses without blocking. That test exists because this failure is invisible in
production until an auditor asks for a record that was never written.
