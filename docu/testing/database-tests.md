# Database tests

The .NET test project (`tests/Kaimo_File_Server.Tests`) runs against two database backends.

## Sqlite (default)

`DatabaseTestBase` (`tests/Kaimo_File_Server.Tests/Infrastructure/`) stands up an in-memory Sqlite
database with the production EF schema and real repositories. It covers constraints, persistence
round trips and repository logic, and runs on every `dotnet test` without any setup.

Sqlite cannot reproduce what the cross-process channel depends on: the retrying execution strategy
(`EnableRetryOnFailure`), serializable isolation with its serialization failures (40001), row locks
and advisory locks. These are tested against PostgreSQL.

## PostgreSQL (opt-in)

Tests marked `[PostgresFact]` and derived from `PostgresTestBase` run only when `KAIMO_TEST_PG` holds
a connection string of a PostgreSQL user that may create databases; otherwise they are reported as
skipped. Each test class instance creates its own database (`kaimo_test_<guid>`), applies the
production schema and drops it afterwards. Contexts are configured through the same
`ServiceCollectionExtensions.UseKaimoNpgsql` and `BuildConnectionString` the processes use, so the
production retry behavior is under test.

A throw-away server is enough:

```bash
docker run -d --rm --name kaimo_test_pg -p 127.0.0.1:55432:5432 -e POSTGRES_PASSWORD=test postgres:17-alpine
```

```bash
KAIMO_TEST_PG="Host=127.0.0.1;Port=55432;Username=postgres;Password=test" dotnet test tests/Kaimo_File_Server.Tests
```

`PostgresDatabaseCommunicationTests` currently covers:

| Scenario | How it is provoked |
|---|---|
| Samba lease renewal during a rename transaction runs the rename once | A command interceptor renews the lease on a second connection right after the rename read its receipt |
| Replay after a lost COMMIT acknowledgement (notification event insert, notification completion, refresh-token rotation, cloud-sync lease acquisition, rename without event receipt) | A transaction interceptor lets the first COMMIT through and then throws a transient I/O error, so the execution strategy replays the unit against the committed state |
| Replay of a single auto-committed statement (notification claim) | A command interceptor lets the first UPDATE through and then throws a transient I/O error, so the strategy runs the statement again |
| Replay after a rolled-back COMMIT (rename without event receipt) | A transaction interceptor throws a transient I/O error before the first COMMIT, so the attempt rolls back and the strategy replays it |
| Samba lease renewal while the event is re-claimed under a row lock | A second connection holds the receipt row and changes its lease while the renewal waits for the lock |
