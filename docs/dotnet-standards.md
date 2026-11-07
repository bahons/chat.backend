# .NET implementation standards for Codex

- Target **supported .NET LTS SDK** pinned in `global.json`, one C# language version across solution, nullable enabled and warnings as errors.
- Organize API/Application/Domain/Infrastructure/Contracts by **dependency direction**, not random Helpers folder: Domain never references API/EF; Application never references concrete Infrastructure.
- Use EF Core with Npgsql: explicit indexes/uniques/foreign keys, migration scripts reviewed before applying to production. Avoid `EnsureCreated` for deployment schema.
- Auth: ASP.NET Core authorization policies + resource based checks with DB membership verification; Hub methods must repeat authorization.
- Background workers via `IHostedService`/`BackgroundService`; Outbox transaction, idempotent dispatch, cancellation/health, structured logs.
- SignalR groups are not authentication, not storage, and not guaranteed message delivery. Disconnection → REST cursor reconciliation.
- Use `ILogger`, `ProblemDetails`-compatible internal handling mapped to the versioned REST error envelope; never log full messages, passwords, SDP, private documents.
- Run `dotnet format`, build/test in CI and realistic PostgreSQL integration tests, not purely EF InMemory substitutes.
