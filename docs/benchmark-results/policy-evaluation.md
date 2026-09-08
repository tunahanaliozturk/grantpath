# Attribute policy evaluation

The attribute layer is the only part of a decision this service computes itself, so it is the only part
worth benchmarking in isolation. A whole decision is dominated by a round trip to the relationship store,
which would hide anything happening here.

Measured on a fixed machine, stated below. These numbers do not transfer to a shared build runner.

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.9106/24H2)
Intel Core Ultra 7 255H 2.00GHz, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.303
  [Host]     : .NET 10.0.11, X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.11, X64 RyuJIT x86-64-v3
```

`PolicyCount` is how many policies exist for the resource type being decided. Every one of them is
evaluated, because a deny at any priority has to be able to outrank an allow, so the cost grows with the
size of the policy set rather than with the number that match. `Nesting` distinguishes a single
comparison from a three-level tree with `allOf`, `not` and `anyOf`.

| PolicyCount | Nesting | Mean | Allocated |
|---:|:---:|---:|---:|
| 1 | simple | 68 ns | 352 B |
| 1 | nested | 183 ns | 616 B |
| 8 | simple | 435 ns | 1352 B |
| 8 | nested | 1.41 us | 3464 B |
| 64 | simple | 3.55 us | 10384 B |
| 64 | nested | 11.83 us | 27280 B |

## What this says

At a realistic policy count the evaluator is free. Eight nested policies cost 1.4 microseconds against a
decision that takes around three milliseconds end to end, so it is roughly one twentieth of one percent of
the request. Nothing here is worth optimising further, and the measurement exists to establish that rather
than to celebrate it.

The number that matters is the shape at the top end. Sixty-four nested policies for one resource type is
already an unusual amount of policy, and it costs twelve microseconds. A deployment would notice the
review burden long before it noticed the latency.

## What the first run found

The original implementation kept only the matching policy ids and looked their priorities back up while
sorting them for the explanation. That lookup is a linear scan, executed inside a comparison, which makes
ordering the matches quadratic in the number of matches.

It did not show up at one or eight policies. At sixty-four it was the dominant cost: 12.8 microseconds for
the simple case against 3.6 after the fix, and 21.3 against 11.8 for the nested one. Carrying the priority
alongside the identifier removed it, at the price of about two kilobytes more allocation per evaluation.

That trade is worth stating plainly rather than only the improved number. The benchmark existed to answer
"is the hand-written part a rounding error", and the first honest answer was "not at the top end".

## Reproducing

```bash
dotnet run -c Release --project benchmarks/GrantPath.Benchmarks -- --filter '*'
```
