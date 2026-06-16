# NOTICE

This repository contains source files that are derived from the
[.NET runtime](https://github.com/dotnet/runtime) project, which is also
licensed under the MIT License.

## Derived files

The following files in `src/BatchPrefetchDictionaries/` are direct copies of
internal types from `dotnet/runtime`, modified to expose hooks needed by the
batch + software-prefetch experiments documented in this repository:

| File | Derived from |
|---|---|
| `BclCopiedImmutableDictionary.cs` | `System.Collections.Immutable.ImmutableDictionary<TKey, TValue>` and its internal `SortedInt32KeyNode<T>` / hash-bucket types |
| `BclCopiedDictionary.cs` | `System.Collections.Generic.Dictionary<TKey, TValue>` and its internal `Entry`, `_buckets`, `_entries` layout |
| `BclHashHelpers.cs` | `System.Collections.HashHelpers` (prime-table + Fibonacci hashing helpers) |

These files were copied so the proposed batch + prefetch algorithms can sit
**alongside** the original BCL paths in a single process, enabling direct
head-to-head benchmarking without patching the runtime itself.

## Modifications

Relative to the upstream sources, the following changes were made:

1. **Type names prefixed `BclCopied`** so they coexist with the real
   `System.Collections.*` types in the same process.
2. **`internal` accessibility raised to `public`** on members the benchmark
   needs to call directly (e.g. the AVL `Root` / `_entries` / `_buckets`
   fields, and helper iteration methods).
3. **New methods added** that implement the batch + prefetch variants
   (in the `*.BatchPrefetch.cs` sidecar files). The original BCL methods
   are preserved unmodified so they can serve as the baseline.
4. **No production behavior changes** were made to the methods that mirror the
   public BCL surface (`TryGetValue`, `Add`, `ContainsKey`, the default
   enumerator, etc.). Any deviation would invalidate the head-to-head
   measurements.

## License of derived material

Both this project and the upstream .NET runtime are MIT-licensed, so the
files are compatible. The .NET runtime license is available at:

> https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

Copyright (c) .NET Foundation and Contributors.

## Unaffected files

The remaining files in this repository — `Payload.cs`, `Prefetch.cs`, and all
benchmark / docs files — are original to this project and covered by the
[`LICENSE`](LICENSE) at the repository root.
