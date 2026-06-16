// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Dan Touitou (@touitou-dan)

namespace BatchPrefetchDictionaries;

public sealed class Payload
{
    public long Value;

    public Payload(long value)
    {
        Value = value;
    }
}

public sealed class StringPayload
{
    public string NextKey;

    public StringPayload(string nextKey)
    {
        NextKey = nextKey;
    }
}
