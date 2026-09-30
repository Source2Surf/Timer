# Timer.Backend.Contracts

This project contains the versioned, read-only wire contract for the Timer HTTP
backend. It deliberately has no reference to `Timer.Shared`, ModSharp, a data
access library, or any server implementation.

## v1 wire rules

- The current API identifier is `v1` (`ApiVersions.Current`). Response envelopes
  repeat it as `apiVersion` so a client can reject an incompatible payload.
- Every external 64-bit identifier (`mapId`, `id`, `recordId`, and `steamId`) is
  an unsigned decimal string. This avoids JSON number precision and signed
  `BIGINT` differences between clients.
- Durations are non-negative values carried in signed 64-bit integer microseconds and use a `*Micros` suffix
  (`timeMicros` and `totalPlayTimeMicros`). No floating-point duration is sent
  over the wire.
- `runDate` is a JSON integer containing Unix milliseconds in UTC. Producers
  normalize database time values to UTC before conversion; the database schema
  remains unchanged.
  This replaces the earlier development-branch ISO JSON representation. Clients
  consuming saved responses must reparse that field, but no SQL data conversion
  script is necessary.
- Collections are arrays. `null` is omitted by `BackendJsonContext`; an empty
  collection should be represented by `[]` when the field is present.
- v1 has no wire enums. Future enum-like values must be stable strings, not
  ordinal integers; unknown values must be safely ignored by clients.
- The map catalog returns canonical map-name strings only. Fetch an individual
  profile when tiers or aggregate statistics are needed; this keeps catalog reads
  to one projected database query.
- v1 record lists are deliberately limit-only. Cursor fields will be added only
  together with a storage query that can honor them.
- Points rank and player-map statistics use dedicated response types so clients
  never need to bind implementation-local or anonymous JSON shapes.

The DTOs are read-only models. Authentication, authorization, write commands,
idempotency keys, persistence mapping, and conversion from game-native units
belong to the backend/client implementations and are intentionally absent.
