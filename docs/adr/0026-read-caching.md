# ADR-0026: Read caching with HybridCache, keys from one service

- **Status**: accepted
- **Date**: 2026-10-08

## Context

The reference slice caches its single-invoice read, and every slice copied from it will cache
something. Three things went wrong while the template grew that cache, each invisible in a
single-instance, single-tenant demo:

- **Keys built by hand drift apart.** A read and the writes that must evict it each spelled the key
  string; one typo, and a write stops evicting what the read serves.
- **A cache hit bypasses the database's filters.** Under `--use-multitenancy` the tenant is an EF
  query filter (ADR-0022). A key without the tenant serves one tenant's invoice to another, and the
  filter never runs.
- **Lifetimes nobody chose.** HybridCache's own defaults gave every entry the same lifetime in memory
  and in the distributed tier, so with several replicas a write evicted the entry in one process and
  left the others serving the old state for as long as the default allowed.

## Decision

- **`HybridCache` for reads, cache-aside, in the endpoint.** `GetOrCreateAsync` with the query handler
  as the factory: an in-memory L1 always, a Redis L2 when `--use-redis` is on (Aspire's `cache`
  resource through `AddRedisDistributedCache`). No hand-rolled cache abstraction; the Application ring
  stays unaware of caching.
- **One place builds keys: the scoped `CacheKeys` service** (`Api.Common/Caching`), injected into the
  read and every write that invalidates it. Under multi-tenancy it prefixes the tenant from
  `ITenantContext`, so a key cannot exist without the tenant; without the toggle it is instance-shaped
  all the same, so callers never change. A feature that caches adds a method there.
- **Lifetimes are stated.** `DefaultEntryOptions` in the API: 30 seconds in the L1, 5 minutes in the L2.
  The L1 lifetime is the bound on cross-replica staleness, because `RemoveAsync` reaches only the
  writing instance's L1 and the shared L2; another replica may serve the previous state, ETag
  included, for up to 30 seconds. Without Redis every replica caches on its own for the same 30 seconds.
- **Writes evict after the commit, with `CancellationToken.None`.** Eviction is post-commit work: a
  client that disconnects must not turn it into an exception after the command applied (ADR-0018).
- **A miss is never pinned.** The factory returns `null` for not-found, which HybridCache stores, so
  the endpoint removes the entry before answering 404; otherwise a later create would be answered
  404 for the full lifetime.
- **Only single-aggregate reads are cached.** Lists and paged queries go to the database: their
  invalidation would need every write to know every list it affects.

## Consequences

- A cached read and its writes cannot disagree on the key, and a tenant's cache cannot be read by
  another tenant: both are properties of `CacheKeys`, not of each endpoint's care.
- Clients of a multi-replica deployment can observe up to 30 seconds of staleness after another
  replica's write; a conditional GET (`If-None-Match`) can therefore answer 304 for that window.
  Lower `LocalCacheExpiration` if a feature cannot tolerate it, or skip the cache for that read.
- Fitness: `InvoiceReadCacheTests` (create → GET → 304 → every write evicts and retires the ETag, a
  404 is never cached), `TenantIsolationTests` (the cache is tenant-scoped, all-options leg).
