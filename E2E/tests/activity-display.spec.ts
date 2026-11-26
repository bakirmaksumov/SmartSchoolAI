import { expect, test, type Browser, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { assignTo, createClass, createDraftAssignment, signIn, submitAssignment, unique } from "./helpers";

// The student's activity on one attempt, as the teacher sees it: copy once, paste once (blocked), two hints of which
// one is given, the hand-in, then one AI Tutor message while studying the result. The AI is the deterministic mock.
// Copy and paste are dispatched as the browser's own clipboard events (what a shortcut, the context menu or a phone's
// paste bubble produce) — the page cannot tell them apart, which is the point.
test.describe.configure({ mode: "serial" });

const mockAi = process.env.E2E_MOCK_AI_URL ?? `http://127.0.0.1:${process.env.E2E_MOCK_AI_PORT ?? 5199}`;
const shots = process.env.E2E_SHOT_DIR;
const password = "Activity-Pass-123";
const stamp = unique("activity").replace(/\s/g, "");
const className = unique("Algebra");
const studentName = `Amira ${stamp.slice(-4)}`;

let browserRef: Browser;
let teacher: Page;
let student: Page;
let assignmentId = "";
let resultId = "";

async function mock(path: string, body?: unknown): Promise<void> {
  const r = await fetch(`${mockAi}${path}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body ?? {}) });
  expect(r.ok, `mock AI ${path}`).toBe(true);
}
async function shot(page: Page, file: string): Promise<void> {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  await page.waitForTimeout(400);
  await page.screenshot({ path: resolve(shots, `${file}.png`), fullPage: true });
}
async function register(page: Page, name: string, email: string, invite?: string): Promise<void> {
  await page.goto(invite ? "/Auth/StudentRegister" : "/Auth/Register");
  if (invite) await page.locator('input[name="inviteCode"]').fill(invite);
  await page.locator('input[name="fullName"]').fill(name);
  await page.locator('input[name="email"]').fill(email);
  await page.locator('input[name="password"]').fill(password);
  await page.locator('input[name="confirmPassword"]').fill(password);
  await page.locator('button[type="submit"]').click();
  await signIn(page, email, password);
}
const nav = (i: number) => student.locator(`.question-nav .nav-btn[data-idx="${i}"]`);
const card = (key: string) => teacher.locator(`#activityCards [data-count="${key}"]`);
const rows = () => teacher.locator("#activityTable [data-activity-row]");

test.beforeAll(async ({ browser }) => {
  browserRef = browser;
  teacher = await (await browser.newContext({ viewport: { width: 1360, height: 900 } })).newPage();
  teacher.on("dialog", d => void d.accept());
});
test.afterAll(async () => { await teacher.context().close(); await student?.context().close(); });

test("setup: two questions, pasting blocked, given to the student", async () => {
  test.setTimeout(120_000);
  await mock("/__reset");
  await register(teacher, "Activity Teacher", `${stamp}.teacher@example.test`);
  const invite = await createClass(teacher, className);
  student = await (await browserRef.newContext({ viewport: { width: 1360, height: 900 } })).newPage();
  student.on("dialog", d => void d.accept());
  await register(student, studentName, `${stamp}.student@example.test`, invite);

  assignmentId = await createDraftAssignment(teacher, `Quadratics practice ${stamp.slice(-4)}`, className);
  const token = await teacher.locator('input[name="__RequestVerificationToken"]').first().inputValue();
  for (const q of [{ title: "Solve $x + 3 = 5$.", answerKey: "2" }, { title: "Solve $x^2 = 9$.", answerKey: "3" }]) {
    const r = await teacher.request.post(`/Assignments/AddQuestion/${assignmentId}`, {
      form: { questionJson: JSON.stringify({ id: randomUUID(), type: 0, difficulty: "Medium", ...q }), __RequestVerificationToken: token }
    });
    expect(r.ok()).toBe(true);
  }
  await teacher.goto(`/Assignments/Edit/${assignmentId}?tab=settings`);
  await teacher.locator("#settingsPasteCheck").check();
  await teacher.locator('#settingsForm button[type="submit"]').click();
  await expect(teacher.locator(".alert-success").first()).toBeVisible();
  await teacher.goto(`/Assignments/Edit/${assignmentId}`);
  expect(await assignTo(teacher, assignmentId)).toContain("Assigned to 1 student");
});

test("student: copy in Q1, a blocked paste in Q2, two hints (one given), hand in, then one AI Tutor message", async () => {
  test.setTimeout(180_000);
  await student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
  await expect(student.locator("#activityNotice")).toContainText("not what you copy, paste or write to the tutor");

  // Copy in question 1 (the question text), as the browser reports it.
  await student.locator('.question-panel[data-qindex="0"]').evaluate(el =>
    el.dispatchEvent(new ClipboardEvent("copy", { bubbles: true, cancelable: true })));
  // A paste into question 2's answer: the page blocks it (pasting is turned off for this assignment).
  await nav(1).click();
  await student.locator('.question-panel[data-qindex="1"] .answer-textarea').first().evaluate(el =>
    el.dispatchEvent(new ClipboardEvent("paste", { bubbles: true, cancelable: true, composed: true })));
  await expect(student.locator('.question-panel[data-qindex="1"] .paste-blocked-note')).toBeVisible();

  // Hint 1 for Q2: given.
  await student.locator('button[onclick="openTutorial()"]').first().click();
  await expect(student.locator("#tutorialMessages")).not.toBeEmpty({ timeout: 20_000 });
  await student.waitForTimeout(1000);
  await student.evaluate(() => (window as unknown as { closeTutorial: () => void }).closeTutorial());

  // Hint 2 for Q1: the tutor fails.
  await mock("/__script", { tutorStatus: 503 });
  await nav(0).click();
  await student.locator('button[onclick="openTutorial()"]').first().click();
  await expect(student.locator("#tutorialMessages")).toContainText(/unavailable|couldn't|error/i, { timeout: 20_000 });
  await student.evaluate(() => (window as unknown as { closeTutorial: () => void }).closeTutorial());
  await mock("/__reset");

  await submitAssignment(student);
  await expect(student).toHaveURL(/AssignmentResult/i, { timeout: 30_000 });
  resultId = new URL(student.url()).searchParams.get("resultId")!;

  // After the hand-in: one message to the AI Tutor while studying the result.
  await student.goto(`/Student/Chat?resultId=${resultId}&mode=hint`);
  await student.reload();                                                   // a reload is not another opening
  await student.locator("#userMessageInput").fill("Why is it 3?");
  await student.locator('form[action*="SendChatMessage"] button[type="submit"]').click();
  await expect(student).toHaveURL(/\/Student\/Chat/i);
});

test("teacher: Activity shows each action with its question, time and outcome; totals match the rows", async () => {
  await teacher.goto(`/Assignments/SubmissionDetail?resultId=${resultId}`);
  await teacher.locator('#submissionTabs [data-tab="activity"]').click();
  await expect(teacher).toHaveURL(/tab=activity/);

  await expect(card("copy")).toHaveText("1");
  await expect(card("paste")).toHaveText("1");
  await expect(card("hints")).toContainText("1 / 2 requested");
  await expect(card("tutor")).toHaveText("1");
  await teacher.locator("#tutorDetails summary").click();
  await expect(card("tutor-after")).toHaveText("1");
  await expect(teacher.locator("#activityFilters")).toContainText("Tashkent time");

  const actions = await rows().evaluateAll(rs => rs.map(r => {
    const c = r.querySelectorAll("td");
    const action = c[1].textContent!.replace("(seen in the page)", "").trim();   // the screen-reader note on browser actions
    return `${action} | ${c[2].textContent!.trim()} | ${c[3].textContent!.trim()}`;
  }));
  expect(actions).toEqual([
    "Copy | Q1 | Allowed",
    "Paste | Q2 | Blocked",
    "AI Tutor opened | Q2 | Opened",
    "Hint requested | Q2 | Hint given",
    "AI Tutor opened | Q1 | Opened",
    "Hint requested | Q1 | Error",
    "Work submitted (attempt 1) | — | Received",
    "AI Tutor opened | Work as a whole | Opened (Get a hint)",
    "AI Tutor message | Work as a whole | Answered"
  ]);
  await expect(teacher.locator("#activityTable .activity-divider")).toContainText("After submission");
  await shot(teacher, "activity-desktop");

  // A filter changes the rows and the totals together.
  await teacher.locator("#activityAction").selectOption("hint");
  await expect(teacher).toHaveURL(/kind=hint/);
  await expect(rows().filter({ hasText: "Hint requested" })).toHaveCount(2);
  await expect(card("copy")).toHaveText("0");
  await expect(card("hints")).toContainText("1 / 2 requested");
  await teacher.locator("#activityQuestion").selectOption({ label: "Q2" });
  await expect(rows().filter({ hasText: "Hint requested" })).toHaveCount(1);
  await expect(card("hints")).toContainText("1 / 1 requested");

  await teacher.setViewportSize({ width: 390, height: 844 });
  await teacher.goto(`/Assignments/SubmissionDetail?resultId=${resultId}&tab=activity`);
  const overflow = await teacher.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(1);
  await shot(teacher, "activity-phone");
  await teacher.setViewportSize({ width: 1360, height: 900 });
});

test("the Activity tab has no grading form and no script error; grades still save on the review tab", async () => {
  const errors: string[] = [];
  const onError = (e: Error) => errors.push(e.message);
  teacher.on("pageerror", onError);

  // Activity: the page's scripts run without the grading form being there.
  await teacher.goto(`/Assignments/SubmissionDetail?resultId=${resultId}&tab=activity`);
  await expect(teacher.locator("#activityTable")).toBeVisible();
  await expect(teacher.locator("#gradeForm")).toHaveCount(0);
  await teacher.waitForLoadState("load");
  expect(errors, "script errors on the Activity tab").toEqual([]);

  // Review: the teacher marks question 1 and saves; the form still collects the per-question grades.
  await teacher.goto(`/Assignments/SubmissionDetail?resultId=${resultId}`);
  await expect(teacher.locator("#gradeForm")).toBeVisible();
  await teacher.locator("#q0 .grade-correct[value=\"true\"]").check({ force: true });
  await teacher.locator("#q0 .grade-points").fill("1");
  await teacher.locator("#q0 .grade-comment").fill("Checked after the Activity fix.");
  await teacher.getByRole("button", { name: "Save grades" }).click();
  await expect(teacher.locator(".alert-success").first()).toBeVisible();
  await teacher.goto(`/Assignments/SubmissionDetail?resultId=${resultId}`);
  await expect(teacher.locator("#q0 .grade-points")).toHaveValue("1");
  await expect(teacher.locator("#q0 .grade-comment")).toHaveValue("Checked after the Activity fix.");
  expect(errors, "script errors on the review tab").toEqual([]);
  teacher.off("pageerror", onError);
});

test("another teacher cannot read the attempt's activity", async () => {
  const other = await (await browserRef.newContext()).newPage();
  await register(other, "Other Teacher", `${stamp}.other@example.test`);
  const r = await other.request.get(`/Assignments/SubmissionDetail?resultId=${resultId}&tab=activity`, { maxRedirects: 0 });
  expect(r.status()).toBe(404);
  expect(await r.text()).not.toContain(studentName);
  await other.context().close();
});
