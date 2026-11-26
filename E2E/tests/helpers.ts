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
  await page.getByRole("button", { name: /Join a class/i }).first().click();
  const modal = page.locator("#joinClassModal");
  await modal.locator('input[name="inviteCode"]').fill(inviteCode);
  await modal.getByRole("button", { name: "Join Class" }).click();
  await expect(page.getByRole("link", { name: className, exact: true }).first()).toBeVisible();
}

export async function createDraftAssignment(page: Page, title: string, className: string): Promise<string> {
  await page.goto("/Assignments/Create");
  await page.locator('input[name="title"]').fill(title);
  await page.locator('textarea[name="description"]').fill("Created by the E2E suite.");
  await page.locator("#classSelect").selectOption({ label: className });
  await page.locator("#typeSelect").selectOption("0");
  await page.getByRole("button", { name: "Save and add questions" }).click();
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

export interface AssignOptions {
  /** Whole class (default) or only these students, by full name. */
  students?: string[];
  /** For the whole class: what happens to students who join later. Default: the suggestion for the type. */
  lateJoin?: "auto" | "approval";
  /** For a draft: when students may start / the due date, as datetime-local values ("2099-01-01T10:00"). */
  opens?: string;
  due?: string;
}

/**
 * The teacher's single "Assign to N students" action from the assignment editor (or the assignment page for
 * an active one). Returns the success message, e.g. "Assigned to 3 students from Year 12 IT."
 */
export async function assignTo(page: Page, assignmentId: string, options: AssignOptions = {}): Promise<string> {
  if (!new RegExp(`/Assignments/(Edit|Details)/${assignmentId}`, "i").test(page.url())) {
    await page.goto(`/Assignments/Edit/${assignmentId}`);
  }
  await page.locator("#openAssignBtn").click();
  const modal = page.locator("#assignModal");
  await expect(modal).toBeVisible();

  if (options.students) {
    await modal.locator("#audSelected").check();
    for (const name of options.students) {
      await modal.locator(".assign-student", { hasText: name }).locator('input[type="checkbox"]').check();
    }
  } else {
    await modal.locator("#audEntire").check();
    if (options.lateJoin === "auto") await modal.locator("#lateAuto").check();
    if (options.lateJoin === "approval") await modal.locator("#lateApproval").check();
  }
  if (options.opens !== undefined) await modal.locator("#assignOpens").fill(options.opens);
  if (options.due !== undefined) await modal.locator("#assignDue").fill(options.due);

  await modal.locator("#assignSubmit").click();
  await expect(page).toHaveURL(new RegExp(`/Assignments/Details/${assignmentId}`, "i"));
  const success = page.locator(".alert-success").first();
  await expect(success).toContainText(/(Assigned to|Added|No one new)/);
  return (await success.innerText()).trim();
}

/**
 * Assigns a draft to the whole class in one action (formerly Assign, then Assign All). Like Assign All, it gives
 * the work to the students in the class now; students who join later only get a request for the teacher, never
 * the work itself, so tests that add a student afterwards still see an unassigned student.
 */
export async function publishAndAssignAll(page: Page, assignmentId: string): Promise<void> {
  await assignTo(page, assignmentId, { lateJoin: "approval" });
  await expect(page.locator("#liveCounters")).toBeVisible();
}

/**
 * On the assignment page, opens a student's row so their attempts, dates and extra actions (statistics, reset,
 * remove) are visible. Without a name, the first student.
 */
export async function expandStudent(page: Page, name?: string): Promise<void> {
  const row = name
    ? page.locator("#recipientTable tbody.recipient", { hasText: name }).first()
    : page.locator("#recipientTable tbody.recipient").first();
  const toggle = row.locator(".attempt-toggle");
  if ((await toggle.getAttribute("aria-expanded")) !== "true") await toggle.click();
  await expect(row.locator(".collapse.show")).toBeVisible();
}

export async function submitAssignment(page: Page): Promise<void> {
  await page.getByRole("button", { name: "Submit Assignment" }).click();
  await page.locator("#confirmSubmitBtn").click();
}
