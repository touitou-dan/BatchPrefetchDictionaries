# BatchPrefetchDictionaries

> Batch + software-prefetch scan algorithms for `ImmutableDictionary` and
> `Dictionary`. Headline: up to ~3.2× faster `ImmutableDictionary` enumeration
> and ~3× faster hash-find at 10M entries (.NET 10), same data-structure shape,
> measured strictly against an equally-shaped baseline.

This repository is the reproducer + reference implementation for a discussion
proposal on the dotnet/runtime community: tree-based and hash-based dictionaries
become memory-bound at large sizes (10M+ entries), and a lane-split + software
prefetch traversal pattern can hide most of the DRAM latency. The proposal is to
add new batch entry points (batched hash-find, prefetched enumeration) that opt
into this traversal. The underlying data structure is unchanged, so existing APIs
and on-disk/in-memory layout are untouched; the new methods sit alongside them.

## Headline results (10M entries, AMD EPYC 7763, .NET 10.0.9)

Checksums are verified bit-for-bit between baseline and candidate on every
scenario. These are the numbers the benchmark CLI prints (`--scenario <name>`).

| Scenario | Lanes | Baseline | Batch + prefetch | Speedup |
|---|---:|---:|---:|---:|
| `ImmutableDictionary<int, Payload>`, enumeration | 16 | 7.7 M/s | 24.4 M/s | 3.18× |
| `ImmutableDictionary<string, string>`, enumeration | 16 | 7.7 M/s | 19.7 M/s | 2.55× |
| `ImmutableDictionary<int, Payload>`, hash-find | 16 | 1.00 M/s | 2.93 M/s | 2.92× |
| `ImmutableDictionary<string, …>`, hash-find | 16 | 0.83 M/s | 2.45 M/s | 2.95× |
| `Dictionary<string, …>`, hash-find (mutable) | 32 | 5.0 M/s | 11.1 M/s | 2.24× |
| `Dictionary<int, Payload>`, hash-find (mutable) | 32 | 10.6 M/s | 24.6 M/s | 2.32× |

All numbers are on .NET 10 (the JIT governs the codegen being compared).

Every scenario runs on the real generic type, enumeration and immutable hash-find
on `ImmutableDictionary<TKey, TValue>`, mutable hash-find on
`Dictionary<TKey, TValue>`, with `int` and `string` key instantiations measured
as-is.

### The enumeration API is a struct `foreach`, both sides

Both the baseline and the candidate enumeration are struct enumerators consumed
with the identical `foreach (var kv in …)` shape:

- baseline = the dictionary's own struct `GetEnumerator()` (no prefetch),
- candidate = `PrefetchedPairs(window)`, a struct enumerator with the same
  lane + software-prefetch engine, delivered one `KeyValuePair` at a time. It
  buffers a burst of `window` elements (each issuing its prefetch) and drains them
  one per `MoveNext`, so the prefetch still runs ahead of the read.

Using the struct `GetEnumerator()` as the baseline, rather than a `yield`-based
`IEnumerable<T>`, is deliberate: a yield iterator pays per-element interface
dispatch that has nothing to do with prefetching, and counting that against the
baseline would inflate the speedup. With both sides as struct enumerators, the
only variable left is the lane + software-prefetch traversal.

### A note on the baseline: the in-repo BCL copy vs the real BCL

The candidate needs access to the collection's internal nodes, which the sealed
BCL types don't expose. So the repo carries `BclCopied*` types, copies of the BCL
sources (see [`NOTICE.md`](NOTICE.md)), with the internals made visible, and both
the baseline and the candidate run on those copies. Baseline and candidate
share the identical data structure, so the speedup is the algorithm, not a
structural difference.

The in-repo struct-enumerator baseline matches the genuine
`System.Collections.Immutable` `foreach` (both ~7.0 M/s), and the in-repo
`Dictionary<int, Payload>.TryGetValue` matches the genuine
`System.Collections.Generic.Dictionary` (both ~10 M/s), so the speedups are
measured against what real code gets today.

A note on the chosen lane counts. In our testing the mutable hash-find
scenarios get their best speedup around 32 lanes (short per-lane work, one
hash → one bucket → ~1 entry, so they need more in-flight lookups to saturate
memory), while the immutable scenarios are best around 16 lanes (each per-lane
step is a heavier AVL tree-walk, so 16 already hides DRAM latency; more just adds
overhead). The CLI lets you set `--vector-size` freely to find the sweet spot on
your hardware.

Your numbers will differ. AMD EPYC and Intel Xeon respond differently to
prefetch distance; Apple Silicon / ARM even more so. Please report what you
measure (issue link below).

## The honest comparison: what is and is not being measured

Every scenario is structured so the only difference between baseline and
candidate is the lane + software-prefetch traversal. Two scenario families, two
ways that is enforced:

Enumeration (`immutable-int-enum`, `immutable-string-enum`). Both sides are
struct enumerators consumed with the identical `foreach (var kv in …)` shape,
baseline = the dict's own `GetEnumerator()`, candidate = `PrefetchedPairs(window)`.
No interface dispatch on either side, identical element type, identical per-element
work (accumulate the value only). The only difference is the prefetch.

Hash-find (`immutable-*-hash-find`, `mutable-*-hash-find`). The driver wraps
both sides in an identical multi-phase loop:

```text
Phase 1 lookup   : serial TryGetValue (baseline)
                   vs FindBatch (candidate)         ← only this differs
Phase 2 prefetch : Prefetch.Object(values[i])       ← identical on both
Phase 3 prefetch : Prefetch.StringChars(NextKey)    ← identical on both (string)
Phase 4 process  : read v.Value / v.NextKey         ← identical on both
```

The candidate's `FindBatch` is kept strictly shape-equivalent to BCL
`TryGetValue` (same hash → bucket → entry-chain walk, same `EqualityComparer`):
it locates the entry, returns the value reference, and stops. It does not
prefetch the value object's heap header inside the algorithm. Any value-object or
string-char prefetching that helps the consumer happens in Phases 2 and 3 of the
wrapper, equally on both sides, which, if anything, speeds up the baseline and
makes the reported ratio more conservative.

So the measured speedup is attributable purely to the bucket/entry chase
strategy (serial vs prefetched-lookahead). A reviewer cannot object that the
candidate cheats by prefetching things the baseline cannot: every prefetch the
wrapper issues, both sides issue.

The candidate is a new batch API (you hand it a span of keys), while the
baseline calls `TryGetValue` one key at a time, and that is precisely what is being
proposed. Prefetching a lookahead is only possible once several keys are in flight,
so a batch entry point is the vehicle for the optimization. This proposal is to
add such an API (and its prefetched implementation) alongside the existing
`TryGetValue` / enumerator, for the throughput-shaped, read-heavy workloads where it
wins (a single isolated `TryGetValue` is unchanged, see
[below](#when-this-approach-helps-and-when-it-does-not)).

## Quick start

Requires the .NET 10 SDK.

```bash
# Build
dotnet build -c Release

# Smoke test (correctness, ~5 seconds)
dotnet run -c Release \
    --project benchmarks/BatchPrefetchDictionaries.Benchmarks \
    -- --validate

# Headline scenario (matches the published numbers)
dotnet run -c Release --project benchmarks/BatchPrefetchDictionaries.Benchmarks \
    -- --scenario immutable-int-enum --size 10000000 --passes 5 --vector-size 16
```

Expected output for the headline scenario on .NET 10 (AMD EPYC 7763):

```text
scenario=immutable-int-enum size=10,000,000 passes=5 vector_size=16
  baseline   median_seconds=1.4291 ops_per_second=6.998e+06 (10,000,000 ops)
  candidate  median_seconds=0.4434 ops_per_second=2.255e+07 (10,000,000 ops)
  speedup    3.22x
  checksum   OK (14568326338)
```

Both `baseline` and `candidate` run on the in-repo `BclCopied` dictionary (the real
generic type with internals exposed), so the printed speedup is the algorithm alone.
The baseline is the dict's struct `GetEnumerator()`; the candidate is the
prefetched `PrefetchedPairs` struct enumerator. See
[the baseline note](#a-note-on-the-baseline-the-in-repo-bcl-copy-vs-the-real-bcl)
for how this relates to the genuine BCL.

Replace `immutable-int-enum` with any other scenario name (run `--list` to see all 6).

## The algorithm in 6 bullets

1. Init: split the root tree into ≤ K = 16 frontier nodes. Repeatedly take a
   frontier node that has children, move it into a small auxiliary buffer
   (`_singleNodes`), and push its non-empty children back into the frontier, until
   there are K frontier nodes (or the tree can't be split further). Each surviving
   frontier node becomes the root of one independent lane; the auxiliary buffer
   of already-split nodes is drained first, before the lanes.
2. Per-lane state: two stacks, one for tree nodes (DFS frontier), one for
   collision-list nodes (same-hash chain). All state lives in
   `[InlineArray]`-typed fields on the enumerator → stack-tracked → write
   barriers short-circuit on push.
3. Advance a lane (the hot path): pop the top tree node and push its two
   children onto the lane's tree stack without prefetching them, then issue a
   single `prefetcht0` for the new top of stack, the one node that will be
   popped next (the "top-of-stack" policy). Push the node's bucket collision chain
   (prefetched) and return the bucket's first value (its key+value object prefetched
   at yield). If the collision stack is non-empty it is drained first: pop a
   collision node, push its children (prefetched), return its pair.
4. Round-robin across lanes. When a lane goes empty, remove it
   (move-last-into-its-slot) and continue with the rest.
5. Per-lane advance is `PairBatchEnumerator.TryNextPair`, it pops/pushes the
   lane stacks and returns the next prefetched element. The consumer-facing API
   wraps it as a struct `foreach` (`PrefetchedPairs(window)`): buffer a burst of
   `window` elements by calling `TryNextPair` `window` times (each issuing its
   prefetch), then deliver them one per `MoveNext`.
6. Prefetch sites: the new top-of-stack tree node after each pop
   (`PrefetchTreeNode`, one per step, *not* both children), every collision-list
   push (`PrefetchListNode`), and the key+value object of each yielded element
   (`PrefetchKeyValue`). The lookahead distance is one node per lane × K lanes in
   flight ≈ enough to hide DRAM latency (~80 to 100 ns) behind the other lanes' work.

The trick is not the prefetch instruction itself, it's the lane
structure that creates enough independent in-flight work for the prefetches
to be useful. Single-lane prefetch barely helps because the demand load arrives
before the prefetched line does.

The hash-find scenarios use the same lane-parallel prefetch shape applied to the
`TryGetValue` chain-walk: K keys are looked up at once. A first pass hashes every
key and prefetches its bucket; a second pass reads each bucket and prefetches its
first entry; then a round-robin pass walks all K entry-chains together, and each
time a lane advances to the next entry in its chain it prefetches that entry. The
latency of each prefetched entry is hidden by the work of the other K−1 lanes
processed before that lane is revisited.

## Where to find the production paths in the source

| Scenario | File | Class / method |
|---|---|---|
| Immutable enumeration, int + string (3.18× / 2.55×) | `src/BatchPrefetchDictionaries/BclCopiedImmutableDictionary.BatchPrefetch.cs` | `PrefetchedPairs` → `PrefetchedPairEnumerator.MoveNext` → `PairBatchEnumerator.TryNextPair` (`<int, Payload>` / `<string, string>`) |
| Immutable hash-find, int (2.92×) | `src/BatchPrefetchDictionaries/BclCopiedImmutableDictionary.BatchPrefetch.cs` | `BatchLookup.FindBatchPrefetch` → `FindValueTypeBatchPrefetch` (`<int, Payload>`) |
| Immutable hash-find, string (2.95×) | `src/BatchPrefetchDictionaries/BclCopiedImmutableDictionary.BatchPrefetch.cs` | `BatchLookup.FindBatchPrefetch` → `FindStringBatchPrefetch` (`<string, StringPayload>`) |
| Mutable `Dictionary<string,…>` hash-find (2.24×) | `src/BatchPrefetchDictionaries/BclCopiedDictionary.BatchPrefetch.cs` | `PrefetchBatchLookup.FindBatch` → `FindStringBatch` (`<string, StringPayload>`) |
| Mutable `Dictionary<int,…>` hash-find (2.32×) | `src/BatchPrefetchDictionaries/BclCopiedDictionary.BatchPrefetch.cs` | `PrefetchBatchLookup.FindBatch` → `FindValueTypeBatch` (`<int, Payload>`) |

The `BclCopied*.cs` files are derived from
[dotnet/runtime](https://github.com/dotnet/runtime)'s
`System.Collections.Immutable` and `System.Collections.Generic` sources (also
MIT-licensed), copied here so the proposed algorithms can sit alongside the
original BCL paths in one process for direct head-to-head comparison without
patching the runtime. See [`NOTICE.md`](NOTICE.md) for attribution details.

The new algorithm code is segregated from the upstream-derived code into
`*.BatchPrefetch.cs` sidecar files so a reviewer can diff each
`BclCopied*.cs` against its upstream original and see that the only changes
were the minimum needed to expose internal fields. Everything novel lives in
the sidecars.

## Benchmark CLI

The benchmark harness is a single per-scenario dispatcher. The full surface is:

| Flag | Description |
|---|---|
| `--list` | Print all 6 scenario names + descriptions and exit. |
| `--validate` | Build each scenario at a small size and assert `baseline.checksum == candidate.checksum`. |
| `--scenario <name>` | Run one scenario, baseline then candidate back-to-back, paired passes. |
| `--size <N>` | Element count (default 1,000,000). |
| `--passes <N>` | Measurement passes per side (default 5). Median is reported. |
| `--vector-size <N>` | Batch lane count (default 16). Immutable scenarios accept 1..32; mutable accept 1..64. |

Example, run the mutable int scenario at 10M for 5 passes at the headline lane count:

```bash
dotnet run -c Release --project benchmarks/BatchPrefetchDictionaries.Benchmarks \
    -- --scenario mutable-int-hash-find --size 10000000 --passes 5 --vector-size 32
```

Each run prints baseline median, candidate median, the speedup ratio, and a
checksum-match line. The six `--scenario` names match the six rows of the
headline table above.

## When this approach helps, and when it does NOT

✅ Helps when:
- Working set is memory-bound (≥ ~1M entries, doesn't fit in L2/L3)
- Workload is batch-shaped (caller has many keys/items to process, not one at a time)
- Read-heavy access (no concurrent mutation of the dictionary during the batch)

❌ Does NOT help when:
- Working set fits in L2/L3 (small dictionaries): prefetch overhead dominates.
- Single-element latency-sensitive paths: this is a *throughput* optimization. A single `TryGetValue` call gets no benefit.
- Ordered scans / `OrderBy` consumers: the lane-split scan returns elements in a per-lane DFS interleaving, not in key order. A merge step is needed for sorted output.
- Custom equality comparers: the batch hash-find assumes the default comparer (ordinal `string` / value-type default). The mutable path throws if you construct the dictionary with another comparer; the immutable path assumes default semantics (a production version would add the same guard).

## Hardware / runtime tested

| | |
|---|---|
| CPU | AMD EPYC 7763 (8 vCPUs visible) |
| RAM | 16 GB |
| Runtime | .NET 10.0.9, Release build |
| OS | Microsoft Azure Linux 3.0 (kernel 6.6.139.1-1.azl3, x86_64) |

For the headline ops/sec numbers, every scenario warms up (baseline + candidate
each run once untimed) before the timed passes, so the measured passes are in the
final JIT tier. For JIT disassembly / `perf record` runs you may want to
additionally set `DOTNET_TieredCompilation=0` so every method is compiled directly
at the optimized tier (no Tier-0 noise). It is not required to reproduce the
speedup numbers.

### A note on the `PrefetchTreeNode` / `PrefetchListNode` shape

These helpers don't use the obvious field-deref form
`Prefetch.Address((nint)Unsafe.AsPointer(ref node._key))`. Instead they
reinterpret the local holding the object reference as `nint` and prefetch the
object base directly:

```csharp
nint addr = Unsafe.As<SortedInt32KeyNode<HashBucket>, nint>(ref node);
Prefetch.Address(addr);
```

The field-deref form makes the .NET 10 RyuJIT re-emit a null-check load
(`cmp byte ptr [node], …`) just before the prefetch, even though the surrounding
code has already proven the reference non-null. That extra load demand-fetches the
cold node onto the critical path and defeats the software prefetch. The
base-address form never dereferences through the reference, so it cannot emit that
load. (Small AVL nodes keep their hot fields in the base cache line anyway.)

## License

MIT. See [`LICENSE`](LICENSE).

Portions of `src/BatchPrefetchDictionaries/BclCopied*.cs` are derived from
[dotnet/runtime](https://github.com/dotnet/runtime) (also MIT-licensed). See
[`NOTICE.md`](NOTICE.md).

## Related

- Upstream: [dotnet/runtime](https://github.com/dotnet/runtime), the BCL sources that `BclCopied*.cs` is derived from
