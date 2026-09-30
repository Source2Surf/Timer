# Timer.Backend.Rpc.Contracts

Versioned MagicOnion contracts shared by the Timer plugin and the standalone backend.

- MessagePack numeric keys are wire ABI. Existing keys and enum values must never be
  reordered or reused; incompatible changes require a new service version.
- `ServerId` is absent; globally unique `SubmissionId` values provide idempotency.
- `StyleFactor` is deliberately absent because the backend resolves scoring policy.
- Times on the wire are integers (`TimeMicros` and Unix milliseconds); conversion to the
  legacy floating-point SQL columns happens only inside the current storage adapter.

The project contains contracts only. It must not reference ModSharp, SqlSugar, the REST
DTO assembly, or a backend implementation.
