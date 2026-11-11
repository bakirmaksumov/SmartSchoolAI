# SmartSchoolAI

ASP.NET Core 8 (MVC) app with a SQLite database.

## Run with Docker Compose

```bash
cp .env.example .env        # optional: add OPENROUTER_API_KEY / RECAPTCHA_* keys
docker compose up -d --build
```

The app listens on <http://127.0.0.1:8080> (loopback only: it must sit behind a TLS-terminating
reverse proxy, see the comment in `compose.yml`). Check readiness with
`curl http://127.0.0.1:8080/health/ready`.

Two named volumes keep state across restarts and rebuilds:

| Volume | Mounted at | Holds |
| --- | --- | --- |
| `app-data` | `/app/App_Data` | SQLite database, private uploads, logs, migration backups |
| `dp-keys` | `/home/app/.aspnet` | ASP.NET Data Protection keys (sign-in cookies) |

```bash
docker compose logs -f web   # logs
docker compose down          # stop, keep data
docker compose down -v       # stop and DELETE the database and uploads
```

Secrets are passed as environment variables (`.env` is git-ignored); nothing is baked into the image.

## Container check in GitHub Actions

Every pull request to `main`, every push to `main` and every manual run executes
**Docker CI** (`.github/workflows/docker-ci.yml`). Open the repository's **Actions** tab, choose
**Docker CI** and select a run. It builds the image, starts the container on fresh volumes, waits for
`/health/ready` to return 200, loads the login page, registers and signs in a test teacher,
recreates the container on the same volumes and confirms that the account and the session still
work. It does not publish the image or deploy.

Run the same check locally (needs Docker and a free port 8080):

```bash
docker compose up -d --build
BASE_URL=http://localhost:8080 RECREATE_CMD='docker compose down && docker compose up -d' scripts/ci/docker-smoke.sh
docker compose down -v
```
