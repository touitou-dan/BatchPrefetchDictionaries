// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Dan Touitou (@touitou-dan)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using BatchPrefetchDictionaries;

namespace BatchPrefetchDictionaries.Benchmarks;

internal static class Program
{
    private const int DefaultSize = 1_000_000;
    private const int DefaultPasses = 5;
    private const int DefaultVectorSize = 16;
    private const int ValidationSize = 50_000;

    private static readonly Scenario[] Scenarios = new[]
    {
        new Scenario(
            "immutable-int-enum",
            "ImmutableDictionary<int, Payload> — struct-foreach pair enumeration, value-only accumulate (prefetched vs plain struct enumerator).",
            ScenarioRunner.RunImmutableIntEnum),
        new Scenario(
            "immutable-string-enum",
            "ImmutableDictionary<string, string> — struct-foreach pair enumeration, value-only accumulate (prefetched vs plain struct enumerator).",
            ScenarioRunner.RunImmutableStringEnum),
        new Scenario(
            "immutable-int-hash-find",
            "ImmutableDictionary<int, Payload> — batched lane-prefetch hash-find vs serial TryGetValue.",
            ScenarioRunner.RunImmutableIntHashFind),
        new Scenario(
            "immutable-string-hash-find",
            "ImmutableDictionary<string, StringPayload> — batched lane-prefetch hash-find vs serial TryGetValue (with wrapper value/key prefetch on both sides).",
            ScenarioRunner.RunImmutableStringHashFind),
        new Scenario(
            "mutable-int-hash-find",
            "Dictionary<int, Payload> — batched lane-prefetch hash-find vs serial TryGetValue.",
            ScenarioRunner.RunMutableIntHashFind),
        new Scenario(
            "mutable-string-hash-find",
            "Dictionary<string, StringPayload> — batched lane-prefetch hash-find vs serial TryGetValue (with wrapper value/key prefetch on both sides).",
            ScenarioRunner.RunMutableStringHashFind),
    };

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }

        if (args.Contains("--list"))
        {
            foreach (var s in Scenarios)
            {
                Console.WriteLine($"  {s.Name,-32} {s.Description}");
            }

            return 0;
        }

        if (args.Contains("--validate"))
        {
            return Validate();
        }

        var name = GetStringArg(args, "--scenario", null);
        if (name == null)
        {
            Console.Error.WriteLine("Error: missing --scenario <name>. Use --list to see scenarios, or --validate.");
            return 2;
        }

        var scenario = Scenarios.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (scenario == null)
        {
            Console.Error.WriteLine($"Error: unknown scenario '{name}'. Use --list.");
            return 2;
        }

        var size = GetIntArg(args, "--size", DefaultSize);
        var passes = GetIntArg(args, "--passes", DefaultPasses);
        var vectorSize = GetIntArg(args, "--vector-size", DefaultVectorSize);

        if (size <= 0) throw new ArgumentException("--size must be > 0");
        if (passes <= 0) throw new ArgumentException("--passes must be > 0");
        if (vectorSize is < 1 or > 64) throw new ArgumentException("--vector-size must be 1..64");

        var options = new RunOptions(size, passes, vectorSize);
        var result = scenario.Run(options);
        PrintPairedResult(scenario, result, options);
        return 0;
    }

    private static int Validate()
    {
        Console.WriteLine($"Validating all {Scenarios.Length} scenarios at size={ValidationSize:n0}...");
        var options = new RunOptions(ValidationSize, Passes: 1, VectorSize: 16);
        var anyFailed = false;
        foreach (var s in Scenarios)
        {
            try
            {
                var result = s.Run(options);
                var match = result.BaselineChecksum == result.CandidateChecksum;
                Console.WriteLine(
                    $"  {s.Name,-32} baseline_checksum={result.BaselineChecksum} candidate_checksum={result.CandidateChecksum} {(match ? "OK" : "MISMATCH")}");
                if (!match) anyFailed = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  {s.Name,-32} FAILED: {ex.Message}");
                anyFailed = true;
            }
        }

        Console.WriteLine(anyFailed ? "Validation FAILED." : "Validation passed.");
        return anyFailed ? 1 : 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("BatchPrefetchDictionaries — reference reproducer for batch + software-prefetch dictionary scans");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  --list                         List all scenarios.");
        Console.WriteLine("  --validate                     Build each scenario at small size and assert baseline/candidate checksums match.");
        Console.WriteLine("  --scenario <name> [options]    Run one scenario, baseline vs candidate, paired.");
        Console.WriteLine();
        Console.WriteLine("Scenario options:");
        Console.WriteLine($"  --size <N>                     Element count (default {DefaultSize:n0}).");
        Console.WriteLine($"  --passes <N>                   Measurement passes per side (default {DefaultPasses}).");
        Console.WriteLine($"  --vector-size <N>              Batch lane count (default {DefaultVectorSize}). Immutable scenarios 1..32; mutable 1..64.");
        Console.WriteLine();
        Console.WriteLine("Scenarios:");
        foreach (var s in Scenarios)
        {
            Console.WriteLine($"  {s.Name,-32} {s.Description}");
        }
    }

    private static void PrintPairedResult(Scenario scenario, PairedResult result, RunOptions options)
    {
        Console.WriteLine($"scenario={scenario.Name} size={options.Size:n0} passes={options.Passes} vector_size={options.VectorSize}");
        Console.WriteLine($"  baseline   median_seconds={result.BaselineMedianSeconds:F4} ops_per_second={result.BaselineOpsPerSecond:0.###e+00} ({result.BaselineCount:n0} ops)");
        Console.WriteLine($"  candidate  median_seconds={result.CandidateMedianSeconds:F4} ops_per_second={result.CandidateOpsPerSecond:0.###e+00} ({result.CandidateCount:n0} ops)");
        var ratio = result.BaselineMedianSeconds / result.CandidateMedianSeconds;
        Console.WriteLine($"  speedup    {ratio:F2}x");
        Console.WriteLine(
            result.BaselineChecksum == result.CandidateChecksum
                ? $"  checksum   OK ({result.BaselineChecksum})"
                : $"  checksum   MISMATCH: baseline={result.BaselineChecksum} candidate={result.CandidateChecksum}");
    }

    private static string? GetStringArg(string[] args, string name, string? defaultValue)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return defaultValue;
    }

    private static int GetIntArg(string[] args, string name, int defaultValue)
    {
        var s = GetStringArg(args, name, null);
        return s == null ? defaultValue : int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}

internal sealed record Scenario(string Name, string Description, Func<RunOptions, PairedResult> Run);

internal readonly record struct RunOptions(int Size, int Passes, int VectorSize);

internal readonly record struct PairedResult(
    long BaselineCount,
    long BaselineChecksum,
    double BaselineMedianSeconds,
    long CandidateCount,
    long CandidateChecksum,
    double CandidateMedianSeconds)
{
    public double BaselineOpsPerSecond => BaselineCount / BaselineMedianSeconds;
    public double CandidateOpsPerSecond => CandidateCount / CandidateMedianSeconds;
}

internal readonly record struct PassResult(long Count, long Checksum, double ElapsedSeconds);

internal static class Pairing
{
    public static PairedResult Run(Func<PassResult> baseline, Func<PassResult> candidate, RunOptions options)
    {
        var _ = baseline(); // warmup
        _ = candidate(); // warmup

        var baselineTimes = new double[options.Passes];
        var candidateTimes = new double[options.Passes];
        long bCount = 0, bChecksum = 0, cCount = 0, cChecksum = 0;
        for (var pass = 0; pass < options.Passes; pass++)
        {
            BenchmarkHelpers.ForceGc();
            var br = baseline();
            BenchmarkHelpers.ForceGc();
            var cr = candidate();
            baselineTimes[pass] = br.ElapsedSeconds;
            candidateTimes[pass] = cr.ElapsedSeconds;
            bCount = br.Count;
            bChecksum = br.Checksum;
            cCount = cr.Count;
            cChecksum = cr.Checksum;
        }

        return new PairedResult(
            bCount,
            bChecksum,
            Median(baselineTimes),
            cCount,
            cChecksum,
            Median(candidateTimes));
    }

    private static double Median(double[] values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : 0.5 * (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]);
    }
}

internal static class BenchmarkHelpers
{
    public static void ForceGc()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    public static int[] BuildIntKeys(int size)
    {
        var keys = new int[size];
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = unchecked((int)Mix32((uint)i + 12_345U));
        }

        return keys;
    }

    public static string[] BuildStringKeys(int size)
    {
        var keys = new string[size];
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = Mix32((uint)i + 12_345U).ToString(CultureInfo.InvariantCulture);
        }

        return keys;
    }

    public static int[] BuildCycleOrder(int size, int seed)
    {
        var order = new int[size];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        var random = new Random(seed);
        for (var i = order.Length - 1; i > 0; i--)
        {
            var swap = random.Next(i + 1);
            (order[i], order[swap]) = (order[swap], order[i]);
        }

        return order;
    }

    public static string CloneString(string value) =>
        string.Create(value.Length, value, static (dest, src) => src.AsSpan().CopyTo(dest));

    public static int ChooseCoprimeStride(int value)
    {
        var stride = Math.Min(1_048_573, value);
        while (stride > 1 && Gcd(stride, value) != 1) stride--;
        return stride;
    }

    public static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    public static void InitializeIntLaneKeys(int[] keysByIndex, int[] keys)
    {
        var stride = ChooseCoprimeStride(keysByIndex.Length);
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = keysByIndex[(int)(((long)i * stride) % keysByIndex.Length)];
        }
    }

    public static void InitializeStringLaneKeys(string[] keysByIndex, string[] keys)
    {
        var stride = ChooseCoprimeStride(keysByIndex.Length);
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = keysByIndex[(int)(((long)i * stride) % keysByIndex.Length)];
        }
    }

    public static int StartsWithOne(string value) =>
        value.Length > 0 && value[0] == '1' ? 1 : 0;

    private static uint Mix32(uint value)
    {
        value ^= value >> 16;
        value *= 0x7FEB_352D;
        value ^= value >> 15;
        value *= 0x846C_A68B;
        value ^= value >> 16;
        return value;
    }
}

internal static class ScenarioRunner
{
    private const int Seed = 12345;

    // =============== immutable-int-enum (foreach pair, value-only) =============
    // Uses the GENERIC BclCopiedImmutableDictionary<int, Payload> — the SAME
    // generic type as immutable-string-enum, just at <int, Payload>. So int and
    // string run byte-identical generic code shapes; the only difference is the
    // type arguments. Both baseline and candidate are STRUCT enumerators consumed
    // with the IDENTICAL `foreach (var kv in …)` shape — baseline = the dict's
    // own struct GetEnumerator() (no prefetch, no interface dispatch); candidate =
    // prefetched PrefetchedPairs(window). Both sides being struct enumerators, the
    // ONLY variable is the lane + software-prefetch traversal. Both accumulate
    // ONLY the value (ignore the key).
    public static PairedResult RunImmutableIntEnum(RunOptions options)
    {
        var keys = BenchmarkHelpers.BuildIntKeys(options.Size);
        var order = BenchmarkHelpers.BuildCycleOrder(options.Size, Seed);
        var dictionary = BuildImmutableIntPayload(keys, order);
        var window = options.VectorSize;

        PassResult Baseline()
        {
            var sw = Stopwatch.StartNew();
            long count = 0, checksum = 0;
            foreach (var kv in dictionary)   // struct GetEnumerator(), no prefetch
            {
                checksum += kv.Value.Value;   // pair API, accumulate value only (ignore key)
                count++;
            }

            sw.Stop();
            return new PassResult(count, checksum, sw.Elapsed.TotalSeconds);
        }

        PassResult Candidate()
        {
            var sw = Stopwatch.StartNew();
            long count = 0, checksum = 0;
            foreach (var kv in dictionary.PrefetchedPairs(window))
            {
                checksum += kv.Value.Value;   // pair API, accumulate value only (ignore key)
                count++;
            }

            sw.Stop();
            return new PassResult(count, checksum, sw.Elapsed.TotalSeconds);
        }

        return Pairing.Run(Baseline, Candidate, options);
    }

    // =============== immutable-string-enum (foreach pair, value-only) ==========
    // Structurally identical to immutable-int-enum, for <string,string>:
    // enumerate KeyValuePair<string,string> via foreach, accumulate only the
    // value object's Length field (no char-buffer access). Both sides are STRUCT
    // enumerators consumed with the IDENTICAL `foreach (var kv in …)` shape —
    // baseline = the dict's struct GetEnumerator() (no prefetch); candidate =
    // PrefetchedPairs — so the only variable is the prefetch.
    public static PairedResult RunImmutableStringEnum(RunOptions options)
    {
        var keys = BenchmarkHelpers.BuildStringKeys(options.Size);
        var order = BenchmarkHelpers.BuildCycleOrder(options.Size, Seed);
        var dictionary = BuildStringImmutable(keys, order);
        var window = options.VectorSize;

        PassResult Baseline()
        {
            var sw = Stopwatch.StartNew();
            long count = 0, checksum = 0;
            foreach (var kv in dictionary)   // struct GetEnumerator(), no prefetch
            {
                checksum += kv.Value.Length;  // pair API, touch only the value OBJECT (its Length field), not the char buffer
                count++;
            }

            sw.Stop();
            return new PassResult(count, checksum, sw.Elapsed.TotalSeconds);
        }

        PassResult Candidate()
        {
            var sw = Stopwatch.StartNew();
            long count = 0, checksum = 0;
            foreach (var kv in dictionary.PrefetchedPairs(window))
            {
                checksum += kv.Value.Length;  // pair API, touch only the value OBJECT (its Length field), not the char buffer
                count++;
            }

            sw.Stop();
            return new PassResult(count, checksum, sw.Elapsed.TotalSeconds);
        }

        return Pairing.Run(Baseline, Candidate, options);
    }

    // =============== immutable-int-hash-find ===================================
    public static PairedResult RunImmutableIntHashFind(RunOptions options)
    {
        var keys = BenchmarkHelpers.BuildIntKeys(options.Size);
        var order = BenchmarkHelpers.BuildCycleOrder(options.Size, Seed);
        var dictionary = BuildImmutableIntPayload(keys, order);
        var lookupCount = options.Size;
        var vectorSize = options.VectorSize;

        PassResult Baseline()
        {
            var keyBatch = new int[vectorSize];
            var values = new Payload[vectorSize];
            BenchmarkHelpers.InitializeIntLaneKeys(keys, keyBatch);
            long processed = 0, checksum = 0;
            var sw = Stopwatch.StartNew();
            while (processed < lookupCount)
            {
                var batch = (int)Math.Min(vectorSize, lookupCount - processed);

                // Phase 1: accumulate values
                for (var i = 0; i < batch; i++)
                {
                    if (!dictionary.TryGetValue(keyBatch[i], out var value))
                        throw new InvalidOperationException("missing key");
                    values[i] = value;
                }

                // Phase 2: prefetch each Payload object header before reading .Value
                for (var i = 0; i < batch; i++)
                {
                    Prefetch.Object(values[i]);
                }

                // Phase 3: process — chain next key from accumulated value
                for (var i = 0; i < batch; i++)
                {
                    checksum += values[i].Value;
                    keyBatch[i] = (int)values[i].Value;
                }

                processed += batch;
            }

            sw.Stop();
            return new PassResult(processed, checksum, sw.Elapsed.TotalSeconds);
        }

        PassResult Candidate()
        {
            var lookup = dictionary.CreateBatchLookup();
            var keyBatch = new int[vectorSize];
            var valueBatch = new Payload[vectorSize];
            var found = new bool[vectorSize];
            BenchmarkHelpers.InitializeIntLaneKeys(keys, keyBatch);
            long processed = 0, checksum = 0;
            var sw = Stopwatch.StartNew();
            while (processed < lookupCount)
            {
                var batch = (int)Math.Min(vectorSize, lookupCount - processed);
                var foundCount = lookup.FindBatchPrefetch(
                    keyBatch.AsSpan(0, batch),
                    valueBatch.AsSpan(0, batch),
                    found.AsSpan(0, batch));
                if (foundCount != batch) throw new InvalidOperationException("missing key");

                // Phase 2: prefetch each Payload object header before reading .Value
                for (var i = 0; i < batch; i++)
                {
                    Prefetch.Object(valueBatch[i]);
                }

                for (var i = 0; i < batch; i++)
                {
                    var v = valueBatch[i];
                    checksum += v.Value;
                    keyBatch[i] = (int)v.Value;
                }

                processed += batch;
            }

            sw.Stop();
            return new PassResult(processed, checksum, sw.Elapsed.TotalSeconds);
        }

        return Pairing.Run(Baseline, Candidate, options);
    }

    // =============== immutable-string-hash-find ================================
    // Wrapper loop is split into a lookup phase + a value-prefetch phase + a
    // value.NextKey-prefetch phase + a process phase. Both baseline and candidate
    // share the wrapper structure; the only difference is the lookup phase
    // (16x TryGetValue vs 1x FindBatchPrefetch), isolating the algorithm-level
    // speedup while letting both sides amortize the value-object and
    // value.NextKey dereference misses via software prefetch.
    public static PairedResult RunImmutableStringHashFind(RunOptions options)
    {
        var keys = BenchmarkHelpers.BuildStringKeys(options.Size);
        var order = BenchmarkHelpers.BuildCycleOrder(options.Size, Seed);
        var dictionary = BuildStringImmutablePayload(keys, order);
        var lookupCount = options.Size;
        var vectorSize = options.VectorSize;

        PassResult Baseline()
        {
            var laneKeys = new string[vectorSize];
            var values = new StringPayload[vectorSize];
            BenchmarkHelpers.InitializeStringLaneKeys(keys, laneKeys);
            long processed = 0, checksum = 0;
            var sw = Stopwatch.StartNew();
            while (processed < lookupCount)
            {
                var batch = (int)Math.Min(vectorSize, lookupCount - processed);

                // Phase 1: lookup
                for (var i = 0; i < batch; i++)
                {
                    if (!dictionary.TryGetValue(laneKeys[i], out var value))
                        throw new InvalidOperationException("missing key");
                    values[i] = value;
                }

                // Phase 2: prefetch the value object (StringPayload header + NextKey reference)
                for (var i = 0; i < batch; i++)
                {
                    Prefetch.Object(values[i]);
                }

                // Phase 3: prefetch the value.NextKey string contents
                for (var i = 0; i < batch; i++)
                {
                    Prefetch.StringChars(values[i].NextKey);
                }

                // Phase 4: process — update checksum and pick the next batch's keys
                for (var i = 0; i < batch; i++)
                {
                    var nextKey = values[i].NextKey;
                    checksum += BenchmarkHelpers.StartsWithOne(nextKey);
                    laneKeys[i] = nextKey;
                }

                processed += batch;
            }

            sw.Stop();
            return new PassResult(processed, checksum, sw.Elapsed.TotalSeconds);
        }

        PassResult Candidate()
        {
            var lookup = dictionary.CreateBatchLookup();
            var laneKeys = new string[vectorSize];
            var values = new StringPayload[vectorSize];
            var found = new bool[vectorSize];
            BenchmarkHelpers.InitializeStringLaneKeys(keys, laneKeys);
            long processed = 0, checksum = 0;
            var sw = Stopwatch.StartNew();
            while (processed < lookupCount)
            {
                var batch = (int)Math.Min(vectorSize, lookupCount - processed);

                // Phase 1: lookup (batched, prefetched tree walk)
                var foundCount = lookup.FindBatchPrefetch(
                    laneKeys.AsSpan(0, batch),
                    values.AsSpan(0, batch),
                    found.AsSpan(0, batch));
                if (foundCount != batch) throw new InvalidOperationException("missing key");

                // Phase 2: prefetch the value object
                for (var i = 0; i < batch; i++)
                {
                    Prefetch.Object(values[i]);
                }

                // Phase 3: prefetch the value.NextKey string contents
                for (var i = 0; i < batch; i++)
                {
                    Prefetch.StringChars(values[i].NextKey);
                }

                // Phase 4: process — update checksum and pick the next batch's keys
                for (var i = 0; i < batch; i++)
                {
                    var nextKey = values[i].NextKey;
                    checksum += BenchmarkHelpers.StartsWithOne(nextKey);
                    laneKeys[i] = nextKey;
                }

                processed += batch;
            }

            sw.Stop();
            return new PassResult(processed, checksum, sw.Elapsed.TotalSeconds);
        }

        return Pairing.Run(Baseline, Candidate, options);
    }

    // =============== mutable-int-hash-find =====================================
    public static PairedResult RunMutableIntHashFind(RunOptions options)
    {
        var keys = BenchmarkHelpers.BuildIntKeys(options.Size);
        var order = BenchmarkHelpers.BuildCycleOrder(options.Size, Seed);
        var dictionary = BuildIntMutable(keys, order);
        var lookupCount = options.Size;
        var vectorSize = options.VectorSize;

        PassResult Baseline()
        {
            var keyBatch = new int[vectorSize];
            var values = new Payload[vectorSize];
            BenchmarkHelpers.InitializeIntLaneKeys(keys, keyBatch);
            long processed = 0, checksum = 0;
            var sw = Stopwatch.StartNew();
            while (processed < lookupCount)
            {
                var batch = (int)Math.Min(vectorSize, lookupCount - processed);

                // Phase 1: accumulate values
                for (var i = 0; i < batch; i++)
                {
                    if (!dictionary.TryGetValue(keyBatch[i], out var value))
                        throw new InvalidOperationException("missing key");
                    values[i] = value;
                }

                // Phase 2: prefetch each Payload object header before reading .Value
                for (var i = 0; i < batch; i++)
                {
                    Prefetch.Object(values[i]);
                }

                // Phase 3: process — chain next key from accumulated value
                for (var i = 0; i < batch; i++)
                {
                    checksum += values[i].Value;
                    keyBatch[i] = (int)values[i].Value;
                }

                processed += batch;
            }

            sw.Stop();
            return new PassResult(processed, checksum, sw.Elapsed.TotalSeconds);
        }

        PassResult Candidate()
        {
            var lookup = dictionary.CreatePrefetchBatchLookup();
            var keyBatch = new int[vectorSize];
            var valueBatch = new Payload[vectorSize];
            var found = new bool[vectorSize];
            BenchmarkHelpers.InitializeIntLaneKeys(keys, keyBatch);
            long processed = 0, checksum = 0;
            var sw = Stopwatch.StartNew();
            while (processed < lookupCount)
            {
                var batch = (int)Math.Min(vectorSize, lookupCount - processed);
                var foundCount = lookup.FindBatch(
                    keyBatch.AsSpan(0, batch),
                    valueBatch.AsSpan(0, batch),
                    found.AsSpan(0, batch));
                if (foundCount != batch) throw new InvalidOperationException("missing key");

                // Phase 2: prefetch each Payload object header before reading .Value
                for (var i = 0; i < batch; i++)
                {
                    Prefetch.Object(valueBatch[i]);
                }

                for (var i = 0; i < batch; i++)
                {
                    var v = valueBatch[i];
                    checksum += v.Value;
                    keyBatch[i] = (int)v.Value;
                }

                processed += batch;
            }

            sw.Stop();
            return new PassResult(processed, checksum, sw.Elapsed.TotalSeconds);
        }

        return Pairing.Run(Baseline, Candidate, options);
    }

    // =============== mutable-string-hash-find ===================================
    // Wrapper loop is split into a lookup phase + a value-prefetch phase + a
    // value.NextKey-prefetch phase + a process phase. Both baseline and candidate
    // share the wrapper structure; the only difference is the lookup phase
    // (16x TryGetValue vs 1x FindBatch), isolating the algorithm-level speedup
    // while letting both sides amortize the value-object and value.NextKey
    // dereference misses via software prefetch.
    public static PairedResult RunMutableStringHashFind(RunOptions options)
    {
        var keys = BenchmarkHelpers.BuildStringKeys(options.Size);
        var order = BenchmarkHelpers.BuildCycleOrder(options.Size, Seed);
        var dictionary = BuildStringMutable(keys, order);
        var lookupCount = options.Size;
        var vectorSize = options.VectorSize;

        PassResult Baseline()
        {
            var laneKeys = new string[vectorSize];
            var values = new StringPayload[vectorSize];
            BenchmarkHelpers.InitializeStringLaneKeys(keys, laneKeys);
            long processed = 0, checksum = 0;
            var sw = Stopwatch.StartNew();
            while (processed < lookupCount)
            {
                var batch = (int)Math.Min(vectorSize, lookupCount - processed);

                // Phase 1: lookup
                for (var i = 0; i < batch; i++)
                {
                    if (!dictionary.TryGetValue(laneKeys[i], out var value))
                        throw new InvalidOperationException("missing key");
                    values[i] = value;
                }

                // Phase 2: prefetch the value object (StringPayload header + NextKey reference)
                for (var i = 0; i < batch; i++)
                {
                    Prefetch.Object(values[i]);
                }

                // Phase 3: prefetch the value.NextKey string contents
                for (var i = 0; i < batch; i++)
                {
                    Prefetch.StringChars(values[i].NextKey);
                }

                // Phase 4: process — update checksum and pick the next batch's keys
                for (var i = 0; i < batch; i++)
                {
                    var nextKey = values[i].NextKey;
                    checksum += BenchmarkHelpers.StartsWithOne(nextKey);
                    laneKeys[i] = nextKey;
                }

                processed += batch;
            }

            sw.Stop();
            return new PassResult(processed, checksum, sw.Elapsed.TotalSeconds);
        }

        PassResult Candidate()
        {
            var lookup = dictionary.CreatePrefetchBatchLookup();
            var laneKeys = new string[vectorSize];
            var values = new StringPayload[vectorSize];
            var found = new bool[vectorSize];
            BenchmarkHelpers.InitializeStringLaneKeys(keys, laneKeys);
            long processed = 0, checksum = 0;
            var sw = Stopwatch.StartNew();
            while (processed < lookupCount)
            {
                var batch = (int)Math.Min(vectorSize, lookupCount - processed);

                // Phase 1: lookup (batched, prefetched hash probe)
                var foundCount = lookup.FindBatch(
                    laneKeys.AsSpan(0, batch),
                    values.AsSpan(0, batch),
                    found.AsSpan(0, batch));
                if (foundCount != batch) throw new InvalidOperationException("missing key");

                // Phase 2: prefetch the value object
                for (var i = 0; i < batch; i++)
                {
                    Prefetch.Object(values[i]);
                }

                // Phase 3: prefetch the value.NextKey string contents
                for (var i = 0; i < batch; i++)
                {
                    Prefetch.StringChars(values[i].NextKey);
                }

                // Phase 4: process — update checksum and pick the next batch's keys
                for (var i = 0; i < batch; i++)
                {
                    var nextKey = values[i].NextKey;
                    checksum += BenchmarkHelpers.StartsWithOne(nextKey);
                    laneKeys[i] = nextKey;
                }

                processed += batch;
            }

            sw.Stop();
            return new PassResult(processed, checksum, sw.Elapsed.TotalSeconds);
        }

        return Pairing.Run(Baseline, Candidate, options);
    }

    // =============== Fixture builders ===========================================
    private static BclCopiedDictionary<int, Payload> BuildIntMutable(int[] keysByIndex, int[] cycleOrder)
    {
        var dictionary = new BclCopiedDictionary<int, Payload>(keysByIndex.Length);
        for (var i = 0; i < cycleOrder.Length; i++)
        {
            var current = cycleOrder[i];
            var next = cycleOrder[(i + 1) % cycleOrder.Length];
            dictionary.Add(keysByIndex[current], new Payload(keysByIndex[next]));
        }

        return dictionary;
    }

    private static BclCopiedDictionary<string, StringPayload> BuildStringMutable(string[] keysByIndex, int[] cycleOrder)
    {
        var dictionary = new BclCopiedDictionary<string, StringPayload>(keysByIndex.Length);
        for (var i = 0; i < cycleOrder.Length; i++)
        {
            var current = cycleOrder[i];
            var next = cycleOrder[(i + 1) % cycleOrder.Length];
            dictionary.Add(
                keysByIndex[current],
                new StringPayload(BenchmarkHelpers.CloneString(keysByIndex[next])));
        }

        return dictionary;
    }

    private static BclCopiedImmutableDictionary<int, Payload> BuildImmutableIntPayload(int[] keysByIndex, int[] cycleOrder)
    {
        return BclCopiedImmutableDictionary<int, Payload>.CreateRange(Pairs(keysByIndex, cycleOrder));

        static IEnumerable<KeyValuePair<int, Payload>> Pairs(int[] keysByIndex, int[] cycleOrder)
        {
            for (var i = 0; i < cycleOrder.Length; i++)
            {
                var current = cycleOrder[i];
                var next = cycleOrder[(i + 1) % cycleOrder.Length];
                yield return new KeyValuePair<int, Payload>(
                    keysByIndex[current],
                    new Payload(keysByIndex[next]));
            }
        }
    }

    private static BclCopiedImmutableDictionary<string, StringPayload> BuildStringImmutablePayload(string[] keysByIndex, int[] cycleOrder)
    {
        return BclCopiedImmutableDictionary<string, StringPayload>.CreateRange(Pairs(keysByIndex, cycleOrder));

        static IEnumerable<KeyValuePair<string, StringPayload>> Pairs(string[] keysByIndex, int[] cycleOrder)
        {
            for (var i = 0; i < cycleOrder.Length; i++)
            {
                var current = cycleOrder[i];
                var next = cycleOrder[(i + 1) % cycleOrder.Length];
                yield return new KeyValuePair<string, StringPayload>(
                    keysByIndex[current],
                    new StringPayload(BenchmarkHelpers.CloneString(keysByIndex[next])));
            }
        }
    }

    private static BclCopiedImmutableDictionary<string, string> BuildStringImmutable(string[] keysByIndex, int[] cycleOrder)
    {
        return BclCopiedImmutableDictionary<string, string>.CreateRange(Pairs(keysByIndex, cycleOrder));

        static IEnumerable<KeyValuePair<string, string>> Pairs(string[] keysByIndex, int[] cycleOrder)
        {
            for (var i = 0; i < cycleOrder.Length; i++)
            {
                var current = cycleOrder[i];
                var next = cycleOrder[(i + 1) % cycleOrder.Length];
                yield return new KeyValuePair<string, string>(
                    keysByIndex[current],
                    BenchmarkHelpers.CloneString(keysByIndex[next]));
            }
        }
    }
}
