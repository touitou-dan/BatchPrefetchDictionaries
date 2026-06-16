// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Dan Touitou (@touitou-dan)

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace BatchPrefetchDictionaries;

public static class Prefetch
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void Address(IntPtr address)
    {
        if (address == IntPtr.Zero || !Sse.IsSupported)
        {
            return;
        }

        Sse.Prefetch0((byte*)address);
    }

    /// <summary>
    /// Prefetches the heap address of a reference-type object. Pulls in the object
    /// header and the first cache line (where the type's first hot field lives).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Object<T>(T value) where T : class
    {
        nint address = Unsafe.As<T, nint>(ref value);
        if (address != 0)
        {
            Address(address);
        }
    }

    /// <summary>
    /// Prefetches the character buffer of a string (where the actual chars live).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void StringChars(string? value)
    {
        if (value is null)
        {
            return;
        }

        fixed (char* chars = value)
        {
            Address((nint)chars);
        }
    }
}
