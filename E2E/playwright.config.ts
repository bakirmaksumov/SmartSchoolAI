import { defineConfig, devices } from "@playwright/test";
import { resolve } from "node:path";

const baseURL = "http://127.0.0.1:5197";
const e2eDatabase = process.env.E2E_DB_PATH;
const buildDirectory = process.env.E2E_BUILD_DIR;

if (!e2eDatabase || !buildDirectory) {
  throw new Error("E2E setup is missing. Run the suite with `npm test`.");
}

const repositoryDirectory = resolve(process.cwd(), "..");
const applicationAssembly = resolve(buildDirectory, "app", "Debug", "net8.0", "SmartSchoolAI.dll");

export default defineConfig({
  testDir: "./tests",
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  workers: 1,
  timeout: 120_000,
  expect: { timeout: 10_000 },
  reporter: "list",
  use: {
    ...devices["Desktop Chrome"],
    baseURL,
    trace: "retain-on-failure",
    launchOptions: { slowMo: Number(process.env.PW_SLOWMO ?? 0) }
  },
  webServer: {
    command: `dotnet "${applicationAssembly}"`,
    cwd: repositoryDirectory,
    url: baseURL,
    reuseExistingServer: false,
    // Set E2E_SERVER_LOG=1 to print the application's console log (useful for AI import failures).
    stdout: process.env.E2E_SERVER_LOG ? "pipe" : "ignore",
    stderr: process.env.E2E_SERVER_LOG ? "pipe" : "ignore",
    timeout: 180_000,
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: "Development",
      DOTNET_ENVIRONMENT: "Development",
      ASPNETCORE_URLS: baseURL,
      ConnectionStrings__Default: `Data Source=${e2eDatabase}`,
      ReCaptcha__SiteKey: "",
      ReCaptcha__SecretKey: "",
      OpenRouter__ApiKey: process.env.E2E_OPENROUTER_API_KEY ?? "",
      RateLimit__LoginPermitLimit: "1000"
    }
  }
});
