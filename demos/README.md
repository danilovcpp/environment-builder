# Demo pipeline

Requests, Scheduler and Reporting are standalone .NET 10 services. Each has its own SQLite
database in `/data`; RabbitMQ persists in its separate managed volume. Requests publishes
`requests.created`; Scheduler consumes it, records a schedule and publishes `scheduler.scheduled`;
Reporting persists a projection. `/probe` returns a deterministic sorted-record digest.
`/health` returns 503 while the broker connection is unavailable; logs expose exchange progress.

The shared source is compiled into each service, with no manager assembly dependency or shared DB.
Publishing uses a durable outbox and publisher confirmations; consumers commit their dedup marker
and data before acknowledging. A broker outage retains unpublished events for reconnect.

`POST /bootstrap`, `POST /seed` (NDJSON) and `POST /business` (`{"id":"sentinel","value":42}`)
require `Idempotency-Key`. The key/action marker commits in the same FULL SQLite transaction as
the mutation. Repeating that key/action returns 204; reusing it for another action is rejected.
Keys are scoped to this service's persistent data. They survive process/container restarts.

Run `python demos/generate_fixtures.py` to regenerate deterministic descriptors and 10/1000/100000
record inputs. Map `local-path://demo` to this `demos` directory. Resolve the password secret
reference in the descriptor to a local vault entry before Start. Both RabbitMQ and API services
must reference that same entry. Package ZIPs contain manifest.json and its declared entries.
Initializers apply mandatory bootstrap before the selected immutable pack.

Docker builds use `demos` as context. `Directory.Packages.props` pins the standalone service
dependencies to the same versions as the manager. No demo route/name is compiled into the app.

Implementation references: [RabbitMQ .NET guide](https://www.rabbitmq.com/client-libraries/dotnet-api-guide)
and [publisher confirmations](https://www.rabbitmq.com/tutorials/tutorial-seven-dotnet).
