# Topaz fork: stacked fix branches

This fork carries fidelity fixes to Topaz - behavior closer to Azure for the ARM control plane and the
Storage data plane (table/queue/blob), plus host hardening under concurrent load. They are maintained
as a **linear stack** of `NNN-` prefixed branches so the order is visible and upstreaming can follow
that order.

## Why a stack

Independent fixes touch overlapping files (storage queue/table/blob, deployment routing). Merging
them independently into an integration branch caused repeated merge-resolution churn. A linear stack
resolves each overlap once: each branch builds on the previous, so `git diff NNN-1..NNN` is exactly
one fix, ready to upstream in order.

## The stack

- Base: upstream `v1.7.134-beta`.
- Tip: `stack/028-sqlite-table-store` (the cumulative "all fixes" reference).
- 28 fix-group branches. Each `stack/NNN-*` branch is a prefix of the stack.

| NNN | Branch | Theme |
|----|--------|-------|
| 001 | table-legacy-and-batch | legacy Table SDK routing + OData `$batch` |
| 002 | table-entity-keys-and-upsert | entity key chars, URL-decode, 404, If-Match-less PUT = upsert |
| 003 | blob-sas-version | version-gate Blob Service SAS string-to-sign |
| 004 | queue-enqueue-and-update-message | NextVisibleTime on enqueue + update-message |
| 005 | host-404-content-type | Content-Type on 404 bodies |
| 006 | host-threadpool-prewarm | pre-grow thread pool for TLS bursts |
| 007 | storage-http-endpoint | optional plain-HTTP storage listener |
| 008 | blob-head-container-properties | answer HEAD container-properties |
| 009 | table-etag-and-conditional-update | etag-as-string, absent If-Match, persist PK/RK, match Timestamp |
| 010 | table-merge-preserves-properties | MERGE/PATCH merges instead of replacing |
| 011 | deployment-templatelink-fetch | resolve + parse linked deployment template |
| 012 | deployment-storage-sku-kind | carry sku/kind through storage deploy |
| 013 | deployment-fic-roleassignment-routing | route role assignments + FIC; normalize IDs; null-guard |
| 014 | deployment-evaluated-properties | evaluate ARM resource properties before deploy |
| 015 | deployment-copyloop-and-nested | zero-count copy loops + nested resources + copy context |
| 016 | deployment-generic-passthrough | persist + GET unmodeled deployment resources |
| 017 | queue-update-message-404 | 404 MessageNotFound updating a missing message |
| 018 | queue-toctou-doc | document the lock-free read-modify-write resurrection window |
| 019 | table-concurrent-entity-access | serialise concurrent entity access |
| 020 | host-request-tracing | OpenTelemetry-shaped per-request tracing + Kestrel data-rate relaxation |
| 021 | table-create-idempotent | concurrent table-create idempotent (409 not 500) |
| 022 | host-aspnetcore-todo | TODO marker to modernize ASP.NET Core |
| 023 | queue-atomic-dequeue | one message -> one consumer per visibility window |
| 024 | queue-message-durability | serialize + atomically persist queue message writes |
| 025 | storage-atomic-writes | shared atomic file writes + atomic table entity writes |
| 026 | queue-getmessages-visibility-floor | enforce Get Messages `visibilitytimeout >= 1s` |
| 027 | table-batch-key-urldecode | URL-decode partition/row keys in `$batch` sub-operations |
| 028 | sqlite-table-store | transactional SQLite table substrate: atomic EGT `$batch`, one Azure-format monotonic etag on every surface, queue pop-receipt validation |

## Equivalence proof (groups 001-026)

The original 26-group stack was produced by linearly replaying the fix commits onto `v1.7.134-beta`
(git rerere replayed the original conflict resolutions). The result was content-identical to the
previous tangled integration branch: both produced tree `bc652038a81f1eda72992ada5422632e6a5e5426`.
The pre-rebuild state is preserved at tag `safety/integration-pre-stack`. Groups 027-028 were added
incrementally on top of that verified base.

## Working with the stack

- Inspect one fix: `git diff stack/NNN-1.. stack/NNN-` (or `git log v1.7.134-beta..stack/001-...` for the first).
- Rebuild or extend (history rewrite): keep `rerere.enabled=true`, create a safety tag first, and
  gate on tree identity against the current tip before replacing anything.
- Upstreaming follows stack order; cherry-pick if a fix must go independently.

## Guardrails

Push only to `MattKotsenas/Topaz`. No internal or employer names in source or commit messages.
Force-pushing or replacing existing published branches requires explicit owner approval; the stack
branches above were created as new local refs only.
