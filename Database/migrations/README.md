# Database migrations

SQL migrations applied automatically by the `mssql-importer` service (after the `.bak` restore) and
tracked in `TGlobal_gsp.dbo._SchemaMigrations`. Each file is applied once, in filename order.

## Conventions

- Name files `NNN_short_description.sql` (zero-padded, e.g. `001_...`, `002_...`) so they sort correctly.
- **Start each file by selecting its database**: `USE [TGame_gsp];` or `USE [TGlobal_gsp];` (the runner
  connects to `master`).
- **Make every migration idempotent and re-runnable** (guard with `IF ... EXISTS` / `IF COL_LENGTH(...)`),
  so a partially-applied or hand-run migration can't wedge the importer.
- Separate batches with `GO` where needed (e.g. before/after `ALTER PROCEDURE`).
- To reference the `.env` SA password without committing the secret, use the sqlcmd variable
  `'$(SA_PASSWORD)'` — the runner passes it via `-v SA_PASSWORD=...`. (The value must not contain a
  single quote, since it's substituted into a string literal.)
- A failing migration aborts the importer and is **not** recorded, so it will be retried next run.

## Running

`025_login_email_two_factor.sql` is required by the Login email-OTP implementation. It creates durable
per-account challenge/rate-limit state and the administrator-only `TLoginTwoFactorConfigure` procedure.
It does not enroll users automatically or modify the baseline `TLogin` procedure. Configure SMTP and
independently verify the recipient before enabling an account; see
[`TLoginSvr.Net`](../../Servers/TLoginSvr.Net/README.md#email-two-factor-authentication).

Migrations run on every `docker compose up`. To apply new ones against an already-running stack:

```bash
docker compose run --rm mssql-importer
```

To re-apply a specific migration during development, delete its row first:

```sql
DELETE FROM TGlobal_gsp.dbo._SchemaMigrations WHERE Id = N'001_example.sql';
```
