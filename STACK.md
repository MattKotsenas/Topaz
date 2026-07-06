# Topaz fork: lean integration branch

This branch (`integration/lean`) carries the **ARM control-plane, template-deployment, and host**
fidelity fixes to Topaz - behaviour closer to Azure for resource-manager deployments and for the host
under concurrent load. It is re-founded directly on an upstream Topaz commit, so it shares a real
merge-base with `upstream/main` and future upstream syncs are an ordinary rebase.

## Scope: control plane + host only

The storage **data-plane** fidelity work (table/queue/blob endpoint behaviour, the transactional SQLite
table substrate, atomic file writes, the optional plain-HTTP storage listener) is intentionally **not**
on this branch. Those fixes live on the separate `stack/001`..`stack/029` branches for consumers that
drive Topaz's in-process storage emulation directly. Consumers that front storage with a dedicated
storage emulator do not need them, and dropping them keeps this branch small and easy to upstream.

## The lean stack

- Base: upstream Topaz commit `2e9c2ef2` (exact tree match; the previous imported snapshot had lost its
  ancestry link, which this re-founding restores).
- 17 fix-commits, grouped below. Each commit is individually upstreamable.

### Template deployment / control plane

| Fix | What it does |
|-----|--------------|
| resolve deployment templateLink | read + parse a linked deployment template so the create path round-trips it |
| storage-account sku/kind | carry `sku` and `kind` through the storage-account deployment path |
| role assignment + FIC routing | route role assignments and federated identity credentials in template deployments; normalize resource IDs; null-guard role-assignment properties |
| evaluated properties | evaluate ARM resource properties before deployment |
| copy loops + nested | handle zero-count copy loops, nested resources, and copied-resource evaluation with copy context |
| generic resource passthrough | persist + GET unmodeled deployment resources |
| templateLink network fetch | when `TOPAZ_ARTIFACT_FETCH_ENDPOINT` is set, fetch a deployment's linked template blob over HTTP (carrying the link's original account Host) instead of reading local storage, so a template hosted in an external storage backend still resolves |

### Host / infrastructure

| Fix | What it does |
|-----|--------------|
| 404 content-type | set `Content-Type` on 404 responses so error bodies are parseable |
| thread-pool prewarm | pre-grow the thread pool to keep TLS handshakes responsive under connection bursts |
| Kestrel data-rate guards | disable the minimum request/response data-rate guards |
| ASP.NET Core TODO | marker to modernize the host (would make the bespoke tracer mostly free) |

### Observability

| Fix | What it does |
|-----|--------------|
| per-request tracing | OpenTelemetry-shaped per-request tracing in the host |
| W3C trace context | adopt an incoming W3C `traceparent` so spans join the caller's trace |

## Relationship to the full stack

The full `stack/001`..`stack/029` branches (control-plane **and** storage-data-plane fixes) remain
published on this fork's origin and are unchanged. This lean branch is the subset needed when storage is
handled outside Topaz. Nothing here is force-pushed over those branches.
