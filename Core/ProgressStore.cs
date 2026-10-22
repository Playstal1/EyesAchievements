using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace EyesAchievements.Core;

internal sealed class ProgressStore
{
    internal const int Version = 1;
    internal const int MaxEntryBytes = 24576;
    internal const int MaxEntries = 16384;
    internal const int MaxStoreBytes = 8 * 1024 * 1024;
    internal Dictionary<string, Dictionary<string, string>> Entries { get; } = new(StringComparer.Ordinal);
    private int _count;
    private int _bytes;

    internal string? Get(string address, string achievement) => Entries.TryGetValue(address, out var values) && values.TryGetValue(achievement, out var state) ? state : null;

    internal void Set(string address, string achievement, string state)
    {
        ValidateKey(achievement);
        if (string.IsNullOrEmpty(address) || address.Length > 65) throw new ArgumentException("Invalid progress address.", nameof(address));
        ValidateState(state);
        bool exists = Entries.TryGetValue(address, out var values) && values.ContainsKey(achievement);
        if (!exists && _count >= MaxEntries) throw new InvalidDataException("Too many achievement entries.");
        int bytes = System.Text.Encoding.UTF8.GetByteCount(state);
        int previousBytes = exists ? System.Text.Encoding.UTF8.GetByteCount(values![achievement]) : 0;
        if ((long)_bytes + bytes - previousBytes > MaxStoreBytes) throw new InvalidDataException("Achievement store exceeds its 8 MiB state limit.");
        if (values is null) Entries.Add(address, values = new(StringComparer.Ordinal));
        if (!exists) _count++;
        _bytes += bytes - previousBytes;
        values[achievement] = state;
    }

    internal static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 192 || key.IndexOf('/') <= 0 || key.EndsWith('/')) throw new ArgumentException("An achievement key must be ModName/Name and at most 192 characters.", nameof(key));
        foreach (char c in key) if (!(char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '.')) throw new ArgumentException("Invalid achievement key.", nameof(key));
    }

    internal static void ValidateState(string state)
    {
        if (state is null || System.Text.Encoding.UTF8.GetByteCount(state) > MaxEntryBytes) throw new InvalidDataException("Achievement state exceeds the supported 24 KiB limit.");
        using var document = JsonDocument.Parse(state, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Achievement state must be an object.");
    }

    internal string Serialize() => JsonSerializer.Serialize(new Envelope { Version = Version, Entries = Entries });

    internal static ProgressStore Parse(string json)
    {
        var data = JsonSerializer.Deserialize<Envelope>(json, new JsonSerializerOptions { MaxDepth = 36 }) ?? throw new InvalidDataException("Missing progress data.");
        if (data.Version != Version) throw new InvalidDataException($"Unsupported achievement data version {data.Version}.");
        var store = new ProgressStore();
        int count = 0;
        foreach (var address in data.Entries ?? throw new InvalidDataException("Missing entries."))
            foreach (var entry in address.Value ?? throw new InvalidDataException("Missing address data."))
            {
                if (++count > MaxEntries) throw new InvalidDataException("Too many achievement entries.");
                store.Set(address.Key, entry.Key, entry.Value);
            }
        return store;
    }

    internal void CopyFrom(ProgressStore other)
    {
        Entries.Clear();
        foreach (var address in other.Entries) Entries.Add(address.Key, new(address.Value, StringComparer.Ordinal));
        _count = other._count;
        _bytes = other._bytes;
    }

    private sealed class Envelope
    {
        public int Version { get; set; }
        public Dictionary<string, Dictionary<string, string>>? Entries { get; set; }
    }
}
