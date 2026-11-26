# Backup and restore before an update

The app keeps three things that are not in the code. Back up all three before every update,
and check the copy by restoring it somewhere else, never on the live server.

| What | Docker (`compose.yml`) | Folder deploy (Windows/IIS, `dotnet SmartSchoolAI.dll`) |
|---|---|---|
| Database (SQLite) | volume `app-data` → `/app/App_Data/smartschool.db` | `smartschool.db` in the app folder (plus `-wal`/`-shm` if present) |
| Uploads: answer photos, drawings, question images, avatars | volume `app-data` → `/app/App_Data/{submissions,question-files,avatars}` | `App_Data\submissions`, `App_Data\question-files`, `App_Data\avatars` |
| Data Protection keys (sign-in cookies, anti-forgery) | volume `dp-keys` → `/home/app/.aspnet` | `%LOCALAPPDATA%\ASP.NET\DataProtection-Keys` of the account the app runs as |

If you lose the keys, nobody loses data, but everyone is signed out and forms open before the update fail once.

The app also copies the database to `App_Data/backups/smartschool_<UTC time>.db` by itself, just before it applies a
database change (`Database.Migrate()` in `Program.cs`). That copy is a safety net, not a backup: it sits next to the
original and has no uploads or keys.

## 1. Back up (app stopped, so the database file is consistent)

**Docker**

```bash
docker compose stop web
stamp=$(date +%Y%m%d-%H%M)
docker run --rm -v smartschoolai_app-data:/data -v "$PWD/backup-$stamp":/out alpine tar czf /out/app-data.tgz -C /data .
docker run --rm -v smartschoolai_dp-keys:/data  -v "$PWD/backup-$stamp":/out alpine tar czf /out/dp-keys.tgz  -C /data .
docker compose start web
```

(`docker volume ls` shows the exact volume names — they start with the compose project name.)

**Folder deploy (PowerShell)**

```powershell
Stop-WebAppPool SmartSchoolAI        # or stop the service / process that runs the app
$b = "D:\Backups\SmartSchoolAI-$(Get-Date -Format yyyyMMdd-HHmm)"; New-Item -ItemType Directory $b | Out-Null
Copy-Item D:\Sites\SmartSchoolAI\smartschool.db* $b
Copy-Item D:\Sites\SmartSchoolAI\App_Data $b -Recurse
Copy-Item "$env:LOCALAPPDATA\ASP.NET\DataProtection-Keys" $b -Recurse   # run as the app's account
Start-WebAppPool SmartSchoolAI
```

Keep the copy off the server too.

## 2. Check the copy, in a separate environment

Restore the copy into a fresh place, start the **new** version on it, and compare. Nothing here touches the live
database or the live Docker volumes.

```bash
# Separate compose project = separate, empty volumes.
export COMPOSE_PROJECT_NAME=restorecheck
docker compose up -d --no-start
docker run --rm -v restorecheck_app-data:/data -v "$PWD/backup-$stamp":/in alpine tar xzf /in/app-data.tgz -C /data
docker run --rm -v restorecheck_dp-keys:/data  -v "$PWD/backup-$stamp":/in alpine tar xzf /in/dp-keys.tgz  -C /data
```

Before starting it, take a snapshot of the restored database (Python 3, read-only):

```bash
tar xzf backup-$stamp/app-data.tgz -C /tmp/restore     # a plain copy to read
python scripts/migration-check/dbcheck.py snapshot /tmp/restore/smartschool.db before.json --files /tmp/restore
```

Change the published port in a copy of `compose.yml` (e.g. `127.0.0.1:8090:8080`) so it cannot clash with the live
site, start it (`docker compose up -d`), wait for `http://127.0.0.1:8090/health/ready`, stop it, copy the database out
of the `restorecheck_app-data` volume the same way, and compare:

```bash
python scripts/migration-check/dbcheck.py snapshot <restored-after>/smartschool.db after.json --files <restored-after>
python scripts/migration-check/dbcheck.py compare before.json after.json
```

`compare` checks every old row column by column, what each student can open, and every uploaded file (size and
SHA-256). It must end with "No differences". Then sign in to the restored copy with a real account and open a
submission with a photo. Remove the check environment afterwards: `docker compose down -v` (with
`COMPOSE_PROJECT_NAME=restorecheck` still set, so only its own volumes go).

## 3. Roll back

Stop the app, put the backed-up `smartschool.db` (and `-wal`/`-shm`), `App_Data` and keys back, and start the
**previous** version. A database already migrated by a newer version must not be opened by an older one — always
restore the backup taken before the update.
