import { defineConfig, devices } from "@playwright/test";
import { resolve } from "node:path";

// Two ways to run:
//  - `npm test` (default): starts the app from a fresh build on an isolated E2E database,
//    plus a second instance of it wired to the mock AI for the "display" project.
//  - Container mode (E2E_BASE_URL set): only the "display" project runs, against an app that is
//    already running, e.g. the Docker container in CI. Only the mock AI is started here.
const externalBaseUrl = process.env.E2E_BASE_URL;
const e2eDatabase = process.env.E2E_DB_PATH;
const buildDirectory = process.env.E2E_BUILD_DIR;

if (!externalBaseUrl && (!e2eDatabase || !buildDirectory)) {
  throw new Error("E2E setup is missing. Run the suite with `npm test`.");
}

const baseURL = "http://127.0.0.1:5197";
const mockAiPort = Number(process.env.E2E_MOCK_AI_PORT ?? 5199);
const mockAiHost = process.env.E2E_MOCK_AI_HOST ?? "127.0.0.1";
const mockAiUrl = `http://127.0.0.1:${mockAiPort}`;
const displayBaseURL = externalBaseUrl ?? "http://127.0.0.1:5198";

const repositoryDirectory = resolve(process.cwd(), "..");
const applicationAssembly = buildDirectory
  ? resolve(buildDirectory, "app", "Debug", "net8.0", "SmartSchoolAI.dll")
  : "";

const logs = process.env.E2E_SERVER_LOG ? "pipe" : "ignore";

// The application under test. `extraEnv` points the second instance at the mock AI.
function appServer(url: string, extraEnv: Record<string, string>) {
  return {
    command: `dotnet "${applicationAssembly}"`,
    cwd: repositoryDirectory,
    url,
    reuseExistingServer: false,
    // Set E2E_SERVER_LOG=1 to print the application's console log (useful for AI import failures).
    stdout: logs as "pipe" | "ignore",
    stderr: logs as "pipe" | "ignore",
    timeout: 180_000,
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: "Development",
      DOTNET_ENVIRONMENT: "Development",
      ASPNETCORE_URLS: url,
      ConnectionStrings__Default: `Data Source=${e2eDatabase}`,
      ReCaptcha__SiteKey: "",
      ReCaptcha__SecretKey: "",
      OpenRouter__ApiKey: process.env.E2E_OPENROUTER_API_KEY ?? "",
      RateLimit__LoginPermitLimit: "1000",
      ...extraEnv
    } as Record<string, string>
  };
}

const mockAi = {
  command: "node scripts/mock-ai.mjs",
  url: `${mockAiUrl}/health`,
  reuseExistingServer: false,
  stdout: logs as "pipe" | "ignore",
  stderr: logs as "pipe" | "ignore",
  timeout: 30_000,
  env: { ...process.env, E2E_MOCK_AI_PORT: String(mockAiPort), E2E_MOCK_AI_HOST: mockAiHost } as Record<string, string>
};

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
  projects: [
    // Everything except the display tests runs against the normal instance (real AI when a key is present).
    { name: "default", testIgnore: /display\.spec\.ts$/ },
    // The tutor/OCR display tests run against an instance whose AI provider is the deterministic mock,
    // whatever E2E_AI is, so they never depend on a live provider.
    { name: "display", testMatch: /display\.spec\.ts$/, use: { baseURL: displayBaseURL } }
  ],
  webServer: externalBaseUrl
    ? [mockAi]
    : [
        appServer(baseURL, {}),
        appServer(displayBaseURL, {
          OpenRouter__ApiKey: "e2e-mock-key",
          OpenRouter__BaseUrl: mockAiUrl
        }),
        mockAi
      ]
});
