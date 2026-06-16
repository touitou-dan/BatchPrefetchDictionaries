// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Derived from dotnet/runtime; modified for this repository (internals exposed
// for benchmarking). See NOTICE.md for attribution.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BatchPrefetchDictionaries;

/// <summary>
/// Focused copy of the hash table sizing helpers used by dotnet/runtime's Dictionary implementation.
/// </summary>
internal static class BclHashHelpers
{
    private const int MaxPrimeArrayLength = 0x7FFFFFC3;
    private const int HashPrime = 101;

    public const uint HashCollisionThreshold = 100;

    private static ReadOnlySpan<int> Primes => new[]
    {
        3, 7, 11, 17, 23, 29, 37, 47, 59, 71, 89, 107, 131, 163, 197, 239, 293, 353, 431, 521, 631, 761, 919,
        1103, 1327, 1597, 1931, 2333, 2801, 3371, 4049, 4861, 5839, 7013, 8419, 10103, 12143, 14591,
        17519, 21023, 25229, 30293, 36353, 43627, 52361, 62851, 75431, 90523, 108631, 130363, 156437,
        187751, 225307, 270371, 324449, 389357, 467237, 560689, 672827, 807403, 968897, 1162687, 1395263,
        1674319, 2009191, 2411033, 2893249, 3471899, 4166287, 4999559, 5999471, 7199369
    };

    public static int GetPrime(int minimum)
    {
        if (minimum < 0)
        {
            throw new ArgumentException("Hash table capacity overflow.", nameof(minimum));
        }

        foreach (var prime in Primes)
        {
            if (prime >= minimum)
            {
                return prime;
            }
        }

        for (var candidate = minimum | 1; candidate < int.MaxValue; candidate += 2)
        {
            if (IsPrime(candidate) && (candidate - 1) % HashPrime != 0)
            {
                return candidate;
            }
        }

        return minimum;
    }

    public static int ExpandPrime(int oldSize)
    {
        var newSize = 2 * oldSize;
        if ((uint)newSize > MaxPrimeArrayLength && MaxPrimeArrayLength > oldSize)
        {
            Debug.Assert(MaxPrimeArrayLength == GetPrime(MaxPrimeArrayLength));
            return MaxPrimeArrayLength;
        }

        return GetPrime(newSize);
    }

    public static ulong GetFastModMultiplier(uint divisor)
    {
        return ulong.MaxValue / divisor + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint FastMod(uint value, uint divisor, ulong multiplier)
    {
        var highbits = (uint)(((((multiplier * value) >> 32) + 1) * divisor) >> 32);
        Debug.Assert(highbits == value % divisor);
        return highbits;
    }

    private static bool IsPrime(int candidate)
    {
        if ((candidate & 1) == 0)
        {
            return candidate == 2;
        }

        var limit = (int)Math.Sqrt(candidate);
        for (var divisor = 3; divisor <= limit; divisor += 2)
        {
            if (candidate % divisor == 0)
            {
                return false;
            }
        }

        return true;
    }
}
