import { expect, type Page } from "@playwright/test";

function requiredEnvironmentVariable(name: string): string {
  const value = process.env[name];
  if (!value) {
    throw new Error(`${name} is missing. Run the suite with \`npm test\`.`);
  }

  return value;
}

// Passwords are read when first used, so specs that register their own accounts (the display
// tests, which also run against a container with no seeded users) can import this module.
export const accounts = {
  teacher: {
    email: "e2e.teacher@example.test",
    get password() { return requiredEnvironmentVariable("E2E_TEACHER_PASSWORD"); },
    name: "E2E Teacher"
  },
  student: {
    email: "e2e.student@example.test",
    get password() { return requiredEnvironmentVariable("E2E_STUDENT_PASSWORD"); },
    name: "E2E Student"
  },
  admin: {
    email: "e2e.admin@example.test",
    get password() { return requiredEnvironmentVariable("E2E_ADMIN_PASSWORD"); },
    name: "E2E Admin"
  }
};

export type Role = keyof typeof accounts;

// True when run-e2e.mjs found a local OpenRouter key, so AI-backed flows are really called.
export const aiEnabled = Boolean(process.env.E2E_OPENROUTER_API_KEY);

export const unique = (prefix: string) => `${prefix} ${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;

export async function signIn(page: Page, email: string, password: string): Promise<void> {
  await page.goto("/Auth/Login");
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Sign in" }).click();
}

export async function signInAs(page: Page, role: Role): Promise<void> {
  await signIn(page, accounts[role].email, accounts[role].password);
  await expect(page).not.toHaveURL(/\/Auth\/Login/);
}

export async function createClass(page: Page, name: string): Promise<string> {
  await page.goto("/Classes/Create");
  await page.locator('input[name="name"]').fill(name);
  await page.locator('input[name="subject"]').fill("Mathematics");
  await page.locator('select[name="gradeLevel"]').selectOption("8");
  await page.locator('select[name="academicYear"]').selectOption("2026-2027");
  await page.getByRole("button", { name: "Create Class" }).click();
  await expect(page).toHaveURL(/\/Classes\/?$/);

  const card = page.locator(".card").filter({ has: page.getByRole("heading", { name, exact: true }) });
  const inviteText = await card.locator(".badge.bg-label-info").innerText();
  const code = inviteText.match(/[A-Z0-9]{6}/)?.[0];
  expect(code, "class card should show a six-character invite code").toBeTruthy();
  return code!;
}

export async function joinClass(page: Page, inviteCode: string, className: string): Promise<void> {
  await page.goto("/Student");
  await page.getByRole("button", { name: /Join a Class/ }).first().click();
  const modal = page.locator("#joinClassModal");
  await modal.locator('input[name="inviteCode"]').fill(inviteCode);
  await modal.getByRole("button", { name: "Join Class" }).click();
  await expect(page.getByText(className, { exact: true }).first()).toBeVisible();
}

export async function createDraftAssignment(page: Page, title: string, className: string): Promise<string> {
  await page.goto("/Assignments/Create");
  await page.locator('input[name="title"]').fill(title);
  await page.locator('textarea[name="description"]').fill("Created by the E2E suite.");
  await page.locator("#classSelect").selectOption({ label: className });
  await page.locator("#typeSelect").selectOption("0");
  await page.getByRole("button", { name: "Save as Draft" }).click();
  await expect(page).toHaveURL(/\/Assignments\/Edit\/[0-9a-f-]+/i);
  await page.waitForLoadState("load");
  return page.url().match(/\/Assignments\/Edit\/([0-9a-f-]+)/i)![1];
}

export type QuestionKind = "Text/Numeric" | "Single choice" | "Multiple choice";

export interface QuestionSpec {
  kind: QuestionKind;
  text: string;
  answerKey: string;
  options?: string[];
  /** Text to wait for in the question list when it differs from `text` (e.g. `$…$` is rendered as maths there). */
  listText?: string;
}

export async function addQuestion(page: Page, q: QuestionSpec): Promise<void> {
  await page.waitForLoadState("load");
  await page.getByRole("button", { name: "Add questions" }).first().click();
  await page.locator("#scratchTypeGrid").getByRole("button", { name: q.kind }).click();
  const frame = page.frameLocator("#scratchFrame");
  await frame.locator("#questionEditor").waitFor();
  // The submit handler that copies the editor into the hidden `title` field is attached by a
  // script at the end of the iframe page; saving before it runs posts an empty title.
  await frame.locator("body").evaluate(() =>
    document.readyState === "complete"
      ? undefined
      : new Promise<void>(resolve => window.addEventListener("load", () => resolve(), { once: true })));
  // Occasionally the iframe posts an empty `title` and the server answers with the form plus
  // "Question text is required." (cause not pinned down). Refill and save again in that case.
  const fillAndSave = async () => {
    await frame.locator("#questionEditor").fill(q.text);
    if (q.options) {
      await frame.locator('input[name="options"]').nth(1).waitFor();
      for (let i = 0; i < q.options.length; i++) {
        if ((await frame.locator('input[name="options"]').count()) <= i) {
          await frame.getByRole("button", { name: /Add Option/i }).click();
        }
        await frame.locator('input[name="options"]').nth(i).fill(q.options[i]);
      }
    }
    await frame.locator('input[name="answerKey"]').fill(q.answerKey);
    await frame.getByRole("button", { name: "Save Question" }).click();
  };

  for (let attempt = 1; ; attempt++) {
    await fillAndSave();
    try {
      await expect(page.locator("#questionsList")).toContainText(q.listText ?? q.text, { timeout: 8_000 });
      break;
    } catch (error) {
      if (attempt >= 3) throw error;
    }
  }
  await expect(page.locator("#scratchModal")).toBeHidden();
  await page.waitForLoadState("load");
}

export async function publishAndAssignAll(page: Page, assignmentId: string): Promise<void> {
  page.once("dialog", dialog => dialog.accept());
  await page.getByRole("button", { name: /^Assign/ }).click();
  await expect(page).toHaveURL(new RegExp(`/Assignments/Details/${assignmentId}`, "i"));
  await page.getByRole("button", { name: "Assign All" }).click();
  await expect(page.getByRole("heading", { name: /Assigned Students \(\d+\)/ })).toBeVisible();
}

export async function submitAssignment(page: Page): Promise<void> {
  await page.getByRole("button", { name: "Submit Assignment" }).click();
  await page.locator("#submitConfirmModal").getByRole("button", { name: "Yes, submit" }).click();
}
