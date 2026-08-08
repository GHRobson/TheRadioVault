# Coding Standards

- Keep domain rules out of WPF event handlers.
- Use explicit `Mode=OneWay` for bindings to calculated/read-only properties.
- Database changes require idempotent migrations and a pre-migration backup.
- Never overwrite user-edited metadata silently.
- Avoid per-record database connections inside large loops where a set-based query is practical.
- New web endpoints must be token protected, LAN restricted and must not accept filesystem paths.

## Jobs and events

- Long-running or cancellable work must use `IBackgroundJobQueue` and report progress through `BackgroundJobContext`.
- Work must check the supplied cancellation token at safe boundaries and must not leave a partially committed database transaction.
- Publish typed application events only after the underlying state change succeeds.
- Event subscribers must not assume a UI thread; WPF subscribers must marshal through `Dispatcher`.
- Prefer one semantic aggregate event for a bulk operation, but retain per-record events when clients need record-level invalidation. Desktop refreshes must be coalesced.
- Web mutations must be explicit allow-listed operations through `IWebArchiveProvider`; never expose arbitrary SQL, file paths or generic reflection-based actions.
