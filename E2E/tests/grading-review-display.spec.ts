import { expect, test, type Browser, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { mkdirSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { assignTo, createClass, createDraftAssignment, signIn, submitAssignment, unique } from "./helpers";

// A photographed solution: the student sees partial marks; the teacher sees the photo, the steps, the marks by
// criterion, the doubtful line and what the checking cost; a marking cut short by the AI keeps the work and can be
// run again from the teacher's page. The AI is the deterministic mock (scripts/mock-ai.mjs).
test.describe.configure({ mode: "serial" });

const mockAi = process.env.E2E_MOCK_AI_URL ?? `http://127.0.0.1:${process.env.E2E_MOCK_AI_PORT ?? 5199}`;
const fixtures = resolve(process.cwd(), "fixtures");
const shots = process.env.E2E_SHOT_DIR;
const password = "Review-Pass-123";
const stamp = unique("review").replace(/\s/g, "");
const className = unique("E2E Review");

let browserRef: Browser;
let teacher: Page;
let assignmentId = "";
let inviteCode = "";
const results: Record<string, string> = {};
const pupils: Record<string, Page> = {};
const dialogs: string[] = [];

async function mock(path: string, body?: unknown): Promise<void> {
  const r = await fetch(`${mockAi}${path}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body ?? {}) });
  expect(r.ok, `mock AI ${path}`).toBe(true);
}
async function requests(kind: string): Promise<number> {
  const all = await (await fetch(`${mockAi}/__requests`)).json() as { kind: string }[];
  return all.filter(r => r.kind === kind).length;
}
async function shot(page: Page, file: string): Promise<void> {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  await page.waitForTimeout(500);
  await page.screenshot({ path: resolve(shots, `${file}.png`), fullPage: true });
}

async function student(key: string): Promise<Page> {
  const page = await (await browserRef.newContext()).newPage();
  page.on("dialog", d => { dialogs.push(d.message()); void d.accept(); });
  const email = `${stamp}.${key}@example.test`;
  await page.goto("/Auth/StudentRegister");
  await page.locator('input[name="inviteCode"]').fill(inviteCode);
  await page.locator('input[name="fullName"]').fill(`Pupil ${key}`);
  await page.locator('input[name="email"]').fill(email);
  await page.locator('input[name="password"]').fill(password);
  await page.locator('input[name="confirmPassword"]').fill(password);
  await page.locator('button[type="submit"]').click();
  await signIn(page, email, password);
  return page;
}

async function open(key: string): Promise<Page> {
  const page = pupils[key];
  await page.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
  await expect(page.locator("#assignmentForm")).toBeVisible();
  return page;
}

/** Photographs the working for question 0 (a new photo each time) and waits for the reading. */
async function readPhoto(page: Page): Promise<void> {
  const block = page.locator('.question-panel[data-qindex="0"] .work-block');
  if (!(await block.locator("#workToggle_0").isChecked())) await block.locator("#workToggle_0").check();
  await block.locator('.work-tab-btn[data-work-tab="upload"]').click();
  await page.evaluate(() => {
    (window as unknown as { __hw: boolean }).__hw = false;
    document.getElementById("hwModal")!.addEventListener("shown.bs.modal", () => { (window as unknown as { __hw: boolean }).__hw = true; }, { once: true });
  });
  await block.locator('[data-hw-open][data-qindex="0"]').click();
  await page.waitForFunction(() => (window as unknown as { __hw: boolean }).__hw);
  await page.locator("#hwFileInput").setInputFiles({
    name: "solution.jpg", mimeType: "image/jpeg",
    buffer: Buffer.concat([readFileSync(resolve(fixtures, "solution-correct.jpg")), Buffer.from(randomUUID())])
  });
  await page.locator("#hwCropUseFullBtn").click();
  await page.locator("#hwAnalyzeBtn").click();
  await expect(page.locator("#hwStage-edit")).toBeVisible({ timeout: 30_000 });
}

async function handIn(key: string, page: Page): Promise<void> {
  await page.locator("#hwModal .btn-close").click().catch(() => undefined);
  await submitAssignment(page);
  await expect(page).toHaveURL(/AssignmentResult/i, { timeout: 30_000 });
  results[key] = new URL(page.url()).searchParams.get("resultId")!;
}

test.beforeAll(async ({ browser }) => {
  browserRef = browser;
  teacher = await (await browser.newContext({ viewport: { width: 1360, height: 900 } })).newPage();
});

test.afterAll(async () => { await teacher.context().close(); });

test("setup: a 3-mark question with the teacher's mark scheme", async () => {
  test.setTimeout(180_000);
  await mock("/__reset");
  const email = `${stamp}.teacher@example.test`;
  await teacher.goto("/Auth/Register");
  await teacher.locator('input[name="fullName"]').fill("Review Teacher");
  await teacher.locator('input[name="email"]').fill(email);
  await teacher.locator('input[name="password"]').fill(password);
  await teacher.locator('input[name="confirmPassword"]').fill(password);
  await teacher.locator('button[type="submit"]').click();
  await signIn(teacher, email, password);
  inviteCode = await createClass(teacher, className);
  pupils.a = await student("a");
  pupils.b = await student("b");

  assignmentId = await createDraftAssignment(teacher, `Linear equations ${stamp}`, className);
  const token = await teacher.locator('input[name="__RequestVerificationToken"]').first().inputValue();
  const r = await teacher.request.post(`/Assignments/AddQuestion/${assignmentId}`, {
    form: {
      questionJson: JSON.stringify({ id: randomUUID(), title: "Solve $2x + 6 = 0$.", type: 0, answerKey: "x = -3", marks: 3,
                                     markScheme: "M1 subtracts 6; M1 divides by 2; A1 x = -3", difficulty: "Medium" }),
      __RequestVerificationToken: token
    }
  });
  expect(r.ok()).toBe(true);
  expect(await assignTo(teacher, assignmentId)).toContain("Assigned to 2 students");
});

test("partial marks: the student sees 2 of 3; the teacher sees photo, steps, criteria, the doubtful sign and the cost", async () => {
  test.setTimeout(180_000);
  await mock("/__script", {
    ocr: { detectedProblem: "", steps: [{ number: 1, text: "$2x = -6$" }, { number: 2, text: "$x = 3$" },
                                        { number: 3, text: String.raw`$\begin{cases} x = 3 \\ x \neq 0 \end{cases}$` }] },
    evalReply: {
      criteria: [
        { criterion: "M1 subtracts 6", maxMarks: 1, awarded: 1, explanation: "Correct.", evidence: "line 1: $2x = -6$", step: 1 },
        { criterion: "M1 divides by 2", maxMarks: 1, awarded: 1, explanation: "Correct.", evidence: "line 2", step: 2 },
        { criterion: "A1 x = -3", maxMarks: 1, awarded: 0, explanation: "The answer reads x = 3; −6 ÷ 2 = −3.", evidence: "line 2: $x = 3$", step: 2 }
      ],
      steps: [{ number: 1, status: "correct" }, { number: 2, status: "incorrect", explanation: "−6 ÷ 2 = −3." }],
      overall: "partial", expectedAnswer: "x = -3", summary: "Good method; check the sign of the answer.",
      transcriptionIssues: [{ step: 2, transcribed: "3", onPhoto: "-3", certain: false, marksAffected: 1, note: "a faint stroke before 3" }],
      needsTeacher: true, teacherNote: "Line 2: the sign before 3 is unclear. 1 mark depends on it."
    },
    verifyReply: { overall: "incorrect", agrees: true, confidence: 0.9, notes: "", steps: [] }
  });

  const a = await open("a");
  await readPhoto(a);
  await a.locator("#hwSubmitBtn").click();
  await expect(a.locator("#hwStage-results")).toBeVisible({ timeout: 60_000 });
  await expect(a.locator("#hwResults .alert").first()).toContainText("Partly correct");
  await expect(a.locator("#hwResults .hw-marks")).toHaveText("· 2 of 3 marks");
  await handIn("a", a);
  await a.context().close();

  await teacher.goto(`/Assignments/SubmissionDetail?resultId=${results.a}`);
  const q = teacher.locator("#q0");
  await expect(q.locator('img[alt="Solution photo"]')).toBeVisible();                     // the original photo
  // Maths is shown as maths, never as LaTeX source: simple lines as readable text, a system of cases typeset.
  await expect(q.locator("[data-step-text]").first()).toHaveText("2x = −6");
  await expect(q.locator("[data-step='3'] .katex")).toBeVisible();
  expect(await q.innerText()).not.toMatch(/\$(?!\d)|\\frac|\\begin|\\neq/);          // ("$0.006" is the cost, in dollars)
  await expect(q.locator("[data-criteria] tbody tr")).toHaveCount(3);
  await expect(q.locator("[data-criteria]")).toContainText("A1 x = -3");
  await expect(q.locator(".disputed-step[data-step='2']")).toBeVisible();                // the doubtful line stands out
  await expect(q.locator("[data-issue]")).toContainText("On the photo: “-3”, read as “3” (not certain) — 1 mark(s) depend on it.");
  await expect(q.locator(".alert-warning")).toContainText("Line 2: the sign before 3 is unclear. 1 mark depends on it.");
  await expect(q.locator("[data-ocr] summary")).toContainText("google/gemini-3.1-pro-preview");
  await expect(q.locator("[data-ai-cost]")).toContainText("3 calls");                      // reading, marking, second check
  await expect(q.locator("[data-ai-cost]")).toContainText("cost $0.006");
  await expect(q.locator(".grade-points")).toHaveValue("2");                              // the teacher can still change it
  await shot(teacher, "review-partial-marks");

  // Reopening the page costs nothing.
  const before = (await requests("ocr")) + (await requests("eval"));
  await teacher.reload();
  expect((await requests("ocr")) + (await requests("eval"))).toBe(before);
});

test("the AI fails while marking: the work is kept (not 0) and the teacher runs only the missing stage again", async () => {
  test.setTimeout(180_000);
  await mock("/__reset");
  await mock("/__script", { evalStatus: 503 });

  const b = await open("b");
  await readPhoto(b);
  await b.locator("#hwSubmitBtn").click();
  await expect.poll(() => dialogs.some(d => d.includes("Marking did not finish")), { timeout: 60_000 }).toBe(true);
  expect(dialogs.find(d => d.includes("Marking did not finish"))).toContain("Your work is saved");
  await expect(b.locator("#hwStage-edit")).toBeVisible();                                 // the steps are still there
  await handIn("b", b);
  await b.context().close();

  await teacher.goto(`/Assignments/SubmissionDetail?resultId=${results.b}`);
  const q = teacher.locator("#q0");
  await expect(q.locator('img[alt="Solution photo"]')).toBeVisible();
  await expect(q.locator(".alert-warning")).toContainText("AI marking did not finish (the photo was read; the marking was not)");
  await expect(q.locator("[data-retry]")).toBeVisible();
  await expect(teacher.locator("[data-score-pending]")).toContainText("Not fully marked");        // not "0%"
  await expect(q.locator(".grade-points")).toHaveValue("");                               // not marked yet ≠ 0
  await expect(q.locator(".grade-points")).toHaveAttribute("placeholder", "not marked");
  await shot(teacher, "review-unfinished");
  expect(await requests("ocr")).toBe(1);

  await mock("/__script", { evalStatus: null });
  await q.locator("[data-retry]").click();
  await expect(teacher.locator(".alert-success").first()).toContainText("AI marking finished: 3 of 3 marks.");
  await expect(teacher.locator("#q0 [data-retry]")).toHaveCount(0);
  await expect(teacher.locator("#q0 .grade-points")).toHaveValue("3");
  await expect(teacher.locator("[data-score-pending]")).toHaveCount(0);
  expect(await requests("ocr")).toBe(1);                                                  // the photo was not read again
  await shot(teacher, "review-after-retry");
});
