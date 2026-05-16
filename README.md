# Dodo.Unique

Time-bounded string canonicalization for .NET. Returns one canonical `string` instance per equal value so repeated reads share memory and become reference-equal.

Inspired by Go's [`unique`](https://pkg.go.dev/unique) package. .NET `String.Intern` is *not* used — it pins for process lifetime and offers no eviction. This pool keeps each entry for at least `minRetention` after its last access, then evicts via a two-tier hot/cold rotation.

## Packages

| Package | Contents | Depends on |
|---|---|---|
| `Dodo.Unique` | `UniqueStringPool` + `UniqueStringConverter` (System.Text.Json) | BCL only |
| `Dodo.Unique.NewtonsoftJson` | `UniqueJsonStringConverter` for Newtonsoft.Json | `Dodo.Unique`, `Newtonsoft.Json` |

Target frameworks: `net9.0`, `net10.0`. The hot path relies on `ConcurrentDictionary` / `FrozenDictionary` `GetAlternateLookup<ReadOnlySpan<char>>()`, which were added in .NET 9.

## Usage

### Direct

```csharp
using Dodo.Unique;

var pool = new UniqueStringPool(minRetention: TimeSpan.FromMinutes(30));

var a = pool.Make("Pepperoni");
var b = pool.Make("Pepperoni");
Debug.Assert(ReferenceEquals(a, b));

// span overload for zero-allocation lookup
ReadOnlySpan<char> span = "Pepperoni".AsSpan();
var c = pool.Make(span);
Debug.Assert(ReferenceEquals(a, c));
```

Or via the options class when readability or extra knobs matter:

```csharp
var pool = new UniqueStringPool(new UniqueStringPoolOptions
{
    MinRetention = TimeSpan.FromMinutes(30),
    MaxLength = 256,
});
```

### System.Text.Json

```csharp
using System.Text.Json;
using Dodo.Unique;

var pool = new UniqueStringPool(TimeSpan.FromMinutes(30));
var options = new JsonSerializerOptions
{
    Converters = { new UniqueStringConverter(pool) },
};

var items = JsonSerializer.Deserialize<MenuItem[]>(json, options);
// repeating string field values now share a canonical instance
```

`UniqueStringConverter` also has a convenience ctor that builds a private pool from `UniqueStringPoolOptions`:

```csharp
var converter = new UniqueStringConverter(new UniqueStringPoolOptions
{
    MinRetention = TimeSpan.FromMinutes(30),
    MaxLength = 256,
});
```

The converter decodes JSON strings directly into a stack-allocated buffer (default 256 chars) and falls back to `Utf8JsonReader.GetString()` for longer values. The buffer size is configurable up to a 1024-char (2 KB) hard ceiling — raise it only if your pool's `MaxLength` is higher and you have measured a real benefit:

```csharp
var pool = new UniqueStringPool(TimeSpan.FromMinutes(30), maxLength: 512);
var converter = new UniqueStringConverter(pool, stackBufferLength: pool.MaxLength);
```

Both `Read` and `Write` are wired (`Read` canonicalizes; `Write` passes through unchanged), so the converter is safe to attach to round-trip serializer options.

### Newtonsoft.Json

```csharp
using Dodo.Unique;
using Dodo.Unique.NewtonsoftJson;
using Newtonsoft.Json;

var pool = new UniqueStringPool(TimeSpan.FromMinutes(30));
var settings = new JsonSerializerSettings
{
    Converters = { new UniqueJsonStringConverter(pool) },
};

var items = JsonConvert.DeserializeObject<MenuItem[]>(json, settings);
```

`ReadJson` canonicalizes via the pool; `WriteJson` passes the value through unchanged, so the converter is safe on round-trip settings.

## Retention semantics

`minRetention` is a **lower bound**, not an upper bound. A value returned by `Make` is guaranteed to remain reference-equal to subsequent equal-value `Make` calls for at least `minRetention` from the moment of last access. Idle entries are evicted between `minRetention` and `2 * minRetention` later.

This is the opposite of `MemoryCache` / Caffeine / Guava semantics, but right for canonicalization: if entries are evicted too eagerly, two reads of the same logical value return different `string` instances and the canonicalization benefit is lost.

Worst-case live entry count is bounded by unique inserts during `2 * minRetention`. Bound per-entry cost with the `maxLength` parameter (default 256).

## Why TTL, not `WeakReference<string>`?

Go's `unique` package uses weak refs + GC. The same approach in .NET has three problems that this pool avoids:

- **Reclaim is GC-coupled, not access-coupled.** `WeakReference<T>` clears only when a GC condemns the target's generation and no strong root remains. A string promoted to Gen 2 survives until the next full GC, regardless of when the cache last touched it. Timing also depends on the runtime's GC mode and configuration (workstation vs server, background vs blocking, `GCLatencyMode`, region/segment layout), so steady-state retention shifts across deployments and load patterns instead of tracking access.
- **Per-entry cost is structural.** Each entry adds a managed `WeakReference` wrapper, a slot in the native GC handle table, and per-collection scan work inside the GC pause. The cost scales with pool size and isn't amortizable.
- **Literal / interned / FOH strings are immortal.** Since .NET 8, string literals live on the Frozen Object Heap and are strongly rooted forever; `WeakReference` to them never clears. A WR-backed pool silently behaves differently for runtime-built vs literal strings.

This pool trades GC-coupled eviction for a predictable two-tier rotation: a bounded dictionary footprint, no handle-table tax, and behavior that doesn't depend on generation aging or GC configuration.

## Build & test

```bash
dotnet build Dodo.Unique.slnx
```

Tests use [TUnit](https://github.com/thomhurst/TUnit) (Microsoft.Testing.Platform). Run each test project as an executable:

```bash
dotnet run --project tests/Dodo.Unique.Tests -c Release
dotnet run --project tests/Dodo.Unique.NewtonsoftJson.Tests -c Release
```
