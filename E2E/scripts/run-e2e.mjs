import { randomBytes } from "node:crypto";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync } from "node:fs";
import { dirname, isAbsolute, join, relative, resolve } from "node:path";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";

const e2eDirectory = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const repositoryDirectory = resolve(e2eDirectory, "..");
const dataDirectory = resolve(e2eDirectory, ".e2e-data");
const databasePath = resolve(dataDirectory, "smartschool-e2e.db");
const buildDirectory = mkdtempSync(join(tmpdir(), "smartschoolai-e2e-build-"));
const applicationDatabase = resolve(repositoryDirectory, "smartschool.db");

function assertIsolatedDatabasePath() {
  const relativePath = relative(dataDirectory, databasePath);

  if (
    relativePath !== "smartschool-e2e.db" ||
    relativePath.startsWith("..") ||
    isAbsolute(relativePath) ||
    databasePath.toLowerCase() === applicationDatabase.toLowerCase()
  ) {
    throw new Error(`Refusing unsafe E2E database path: ${databasePath}`);
  }
}

function removeDatabaseFiles() {
  for (const path of [databasePath, `${databasePath}-shm`, `${databasePath}-wal`]) {
    rmSync(path, { force: true });
  }
}

function run(command, args, env) {
  const result = spawnSync(command, args, {
    cwd: e2eDirectory,
    env,
    stdio: "inherit"
  });

  if (result.error) {
    throw result.error;
  }

  return result.status ?? 1;
}

assertIsolatedDatabasePath();
mkdirSync(dataDirectory, { recursive: true });
removeDatabaseFiles();

const env = {
  ...process.env,
  E2E_DATA_DIR: dataDirectory,
  E2E_BUILD_DIR: buildDirectory,
  E2E_DB_PATH: databasePath,
  E2E_TEACHER_PASSWORD: `${randomBytes(24).toString("base64url")}Aa1!`,
  E2E_STUDENT_PASSWORD: `${randomBytes(24).toString("base64url")}Aa1!`,
  E2E_ADMIN_PASSWORD: `${randomBytes(24).toString("base64url")}Aa1!`
};

// Reuse the developer's local OpenRouter key (dotnet user-secrets) so the AI-backed flows are
// exercised for real. The key is passed to the test server's environment only and never printed.
// Set E2E_AI=0 to run without AI calls.
function readLocalAiKey() {
  if (process.env.E2E_AI === "0") return "";
  if (process.env.E2E_OPENROUTER_API_KEY) return process.env.E2E_OPENROUTER_API_KEY;

  const result = spawnSync("dotnet", ["user-secrets", "list", "--project", resolve(repositoryDirectory, "SmartSchoolAI.csproj")], {
    encoding: "utf8"
  });
  if (result.status !== 0) return "";

  const line = result.stdout.split(/\r?\n/).find(l => l.startsWith("OpenRouter:ApiKey"));
  return line ? line.slice(line.indexOf("=") + 1).trim() : "";
}

env.E2E_OPENROUTER_API_KEY = readLocalAiKey();
console.log(env.E2E_OPENROUTER_API_KEY
  ? "AI key found in user-secrets: AI-backed checks are enabled."
  : "No AI key available: AI-backed checks fall back to the offline behaviour.");

let exitCode = 1;

try {
  const seedProject = resolve(e2eDirectory, "Seed", "SmartSchoolAI.E2E.Seed.csproj");
  exitCode = run("dotnet", ["run", "--project", seedProject, "--", databasePath], env);

  if (exitCode === 0) {
    const playwrightCli = resolve(e2eDirectory, "node_modules", "@playwright", "test", "cli.js");
    exitCode = run(process.execPath, [playwrightCli, "test", ...process.argv.slice(2)], env);
  }
} catch (error) {
  console.error(error);
  exitCode = 1;
} finally {
  removeDatabaseFiles();
  rmSync(buildDirectory, { recursive: true, force: true });
}

process.exitCode = exitCode;
