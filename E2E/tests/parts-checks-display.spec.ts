import { expect, test, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { copyFileSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { createClass, createDraftAssignment, publishAndAssignAll, signIn, submitAssignment, unique } from "./helpers";

// Questions with parts on one page, and checks of an answer and of its working that must not go stale. The model's
// answers come from the mock AI (scripts/mock-ai.mjs); the application around it is real. What the app SENDS to the
// model is read back from the mock. Live-model accuracy is not measured here.
test.describe.configure({ mode: "serial" });

const mockAi = process.env.E2E_MOCK_AI_URL ?? `http://127.0.0.1:${process.env.E2E_MOCK_AI_PORT ?? 5199}`;
const fixtures = resolve(process.cwd(), "fixtures");
const screenshotDir = process.env.E2E_SHOT_DIR;
const password = "Parts-Pass-123";
const className = unique("E2E Parts Class");

const diagram = "data:image/svg+xml;base64," + Buffer.from(
  '<svg xmlns="http://www.w3.org/2000/svg" width="160" height="80"><rect width="160" height="80" fill="#e8eaff"/><text x="14" y="46" font-size="22">A (0, 7)</text></svg>'
).toString("base64");

const stemTitle = "A curve has equation $y = x^2 - 4x + 7$ and meets the $y$-axis at the point $A$, as shown in the diagram.";
const partA = "(a) Find the $y$-coordinate of $A$.";
const partB = "(b) Find the equation of the tangent to the curve at $A$.";

// The regression fixture from the student's screenshot: y = 2cx + 3 and y = cx² + 3x − c.
const choiceTitle = "The line y = 2cx + 3 and the curve y = cx² + 3x − c are given, where c is a constant. Which statement is true?";
const choiceOptions = [
  "The line and curve intersect only when c > 0",
  "The line and curve intersect for all values of c",
  "The line and curve never intersect",
  "The line and curve intersect only when c = 0"
];
const optionB = choiceOptions[1];

declare const QUESTIONS_DATA: { id: string; groupId?: string; title: string }[];

// Question indexes: 0 shared stem, 1 part (a), 2 part (b), 3 choice without a key, 4 choice with the key "B".
const STEM = 0, A = 1, B = 2, NO_KEY = 3, KEYED = 4;

let teacher: Page;
let student: Page;
let assignmentId = "";

async function mock(path: string, body?: unknown): Promise<void> {
  const response = await fetch(`${mockAi}${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body ?? {})
  });
  expect(response.ok, `mock AI ${path}`).toBe(true);
}

type Seen = { kind: string; system: string; user: string };
async function seen(kind: string): Promise<Seen[]> {
  return (await fetch(`${mockAi}/__requests?kind=${kind}`)).json() as Promise<Seen[]>;
}

async function shot(page: Page, name: string): Promise<void> {
  if (!screenshotDir) return;
  mkdirSync(screenshotDir, { recursive: true });
  await page.screenshot({ path: resolve(screenshotDir, `${name}.png`) });
}

const panel = (i: number) => student.locator(`.question-panel[data-qindex="${i}"]`);
const feedback = (i: number) => panel(i).locator(".check-feedback");
const pill = (lead: number) => student.locator(`.question-nav .nav-btn[data-idx="${lead}"]`);
const gameModal = () => student.locator("#waitingGameModal");
const openAssignment = () => student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);

async function setAnswer(i: number, value: string): Promise<void> {
  await panel(i).locator(".answer-textarea").first().fill(value);
}

async function check(i: number): Promise<void> {
  await panel(i).locator(".check-answer-btn").click();
}

// An AI-judged check shows the waiting window; open the result when it is ready.
async function seeResult(): Promise<void> {
  await expect(gameModal().locator(".wg-reveal-btn")).toBeVisible({ timeout: 30_000 });
  await gameModal().locator(".wg-reveal-btn").click();
  await expect(gameModal()).toBeHidden();
}

async function choose(i: number, letter: string): Promise<void> {
  await panel(i).locator(`.option-card[data-value="${letter}"]`).click();
}

// Opens "Solve with photo" for one question and reads the fixture photo.
async function readPhoto(i: number): Promise<void> {
  const block = panel(i).locator(".work-block");
  const toggle = block.locator(`#workToggle_${i}`);
  if (!(await toggle.isChecked())) await toggle.check();
  await block.locator('.work-tab-btn[data-work-tab="upload"]').click();

  await student.evaluate(() => {
    const w = window as unknown as { __hwShown: boolean };
    w.__hwShown = false;
    document.getElementById("hwModal")!.addEventListener("shown.bs.modal", () => { w.__hwShown = true; }, { once: true });
  });
  await block.locator(`[data-hw-open][data-qindex="${i}"]`).click();
  await student.waitForFunction(() => (window as unknown as { __hwShown: boolean }).__hwShown);

  await student.locator("#hwFileInput").setInputFiles(resolve(fixtures, "solution-correct.jpg"));
  await student.locator("#hwCropUseFullBtn").click();
  await expect(student.locator("#hwAnalyzeBtn")).toBeEnabled();
  await student.locator("#hwAnalyzeBtn").click();
  await expect(student.locator("#hwStage-edit")).toBeVisible({ timeout: 30_000 });
}

// Confirms the recognized steps, submits them and waits for the verdict in the dialog, then returns to the question.
async function submitPhotoWork(): Promise<void> {
  await student.locator("#hwSubmitBtn").click();
  await expect(student.locator("#hwStage-results")).toBeVisible({ timeout: 60_000 });
  await student.locator("#hwStage-results .btn-primary[data-bs-dismiss='modal']").click();
  await expect(student.locator("#hwModal")).toBeHidden();
}

test.beforeAll(async ({ browser }) => {
  teacher = await (await browser.newContext()).newPage();
  student = await (await browser.newContext({ viewport: { width: 1280, height: 900 } })).newPage();
  // Leaving a page with unsubmitted answers asks for confirmation; accept it so the tests can move on.
  student.on("dialog", dialog => void dialog.accept());
});

test.afterAll(async () => {
  await teacher.context().close();
  await student.context().close();
});

test("setup: synthetic accounts and an assignment with a question in two parts and two choice questions", async () => {
  test.setTimeout(240_000);
  const stamp = unique("parts").replace(/\s/g, "");
  const teacherEmail = `${stamp}.teacher@example.test`;
  const studentEmail = `${stamp}.student@example.test`;

  await teacher.goto("/Auth/Register");
  await teacher.locator('input[name="fullName"]').fill("Parts Teacher");
  await teacher.locator('input[name="email"]').fill(teacherEmail);
  await teacher.locator('input[name="password"]').fill(password);
  await teacher.locator('input[name="confirmPassword"]').fill(password);
  await teacher.locator('button[type="submit"]').click();
  await expect(teacher).toHaveURL(/\/Auth\/Login/);
  await signIn(teacher, teacherEmail, password);
  await expect(teacher).not.toHaveURL(/\/Auth\/Login/);

  const inviteCode = await createClass(teacher, className);

  await student.goto("/Auth/StudentRegister");
  await student.locator('input[name="inviteCode"]').fill(inviteCode);
  await student.locator('input[name="fullName"]').fill("Parts Student");
  await student.locator('input[name="email"]').fill(studentEmail);
  await student.locator('input[name="password"]').fill(password);
  await student.locator('input[name="confirmPassword"]').fill(password);
  await student.locator('button[type="submit"]').click();
  await expect(student).toHaveURL(/\/Auth\/Login/);
  await signIn(student, studentEmail, password);
  await expect(student).toHaveURL(/\/Student\/?$/);

  assignmentId = await createDraftAssignment(teacher, unique("E2E Parts"), className);

  const token = await teacher.locator('input[name="__RequestVerificationToken"]').first().inputValue();
  const add = async (question: Record<string, unknown>) => {
    const response = await teacher.request.post(`/Assignments/AddQuestion/${assignmentId}`, {
      form: { questionJson: JSON.stringify({ difficulty: "Medium", ...question }), __RequestVerificationToken: token }
    });
    expect(response.ok(), "AddQuestion").toBe(true);
  };

  // Parts use the existing GroupId support: the parts point at the shared question.
  const stemId = randomUUID();
  await add({ id: stemId, title: stemTitle, type: 0, answerKey: "", imagePath: diagram });
  await add({ id: randomUUID(), groupId: stemId, title: partA, type: 0, answerKey: "7" });
  await add({ id: randomUUID(), groupId: stemId, title: partB, type: 0, answerKey: "-4x+7" });
  await add({ id: randomUUID(), title: choiceTitle, type: 4, options: choiceOptions, answerKey: "", requireWork: true });
  await add({ id: randomUUID(), title: choiceTitle, type: 4, options: choiceOptions, answerKey: "B", requireWork: true });

  await teacher.goto(`/Assignments/Edit/${assignmentId}`);
  await publishAndAssignAll(teacher, assignmentId);
});

// ── 1. Parts (a) and (b) on one page ─────────────────────────────────────────
test("parts (a) and (b) appear together under the shared stem and diagram; each keeps its own data", async () => {
  test.setTimeout(240_000);
  await mock("/__reset");
  await mock("/__script", { ocr: { detectedProblem: "", steps: [{ number: 1, text: "$y' = 2x - 4$" }, { number: 2, text: "$y = -4x + 7$" }] } });

  await openAssignment();

  // One navigation entry per top-level question: the stem and its parts are a single page.
  await expect(student.locator(".question-nav .nav-btn[data-idx]")).toHaveCount(3);
  await expect(pill(STEM)).toBeVisible();
  await expect(student.locator(`.question-nav .nav-btn[data-idx="${A}"], .question-nav .nav-btn[data-idx="${B}"]`)).toHaveCount(0);

  // Without pressing Next: the stem once, with its diagram once, then (a) and (b) in order, each with its own tools.
  await expect(student.locator(".question-panel.active")).toHaveCount(3);
  await expect(student.locator(`#qtext_${STEM}`)).toContainText("meets the");
  await expect(student.locator(`.question-page[data-page="${STEM}"] img[alt="Diagram"]`)).toHaveCount(1);
  await expect(student.locator(`.question-page[data-page="${STEM}"] .question-title`)).toHaveCount(3);
  await expect(panel(A).locator(".question-header")).toContainText("Question 1a");
  await expect(panel(B).locator(".question-header")).toContainText("Question 1b");
  await expect(student.locator(`#qtext_${B}`)).toContainText("tangent");
  for (const part of [A, B]) {
    await expect(panel(part).locator(".answer-textarea").first()).toBeVisible();
    await expect(panel(part).locator(".check-answer-btn")).toBeVisible();
    await expect(panel(part).locator(".work-block")).toHaveCount(1);
    await expect(panel(part).locator(".check-feedback")).toHaveCount(1);
  }
  expect(await panel(STEM).locator(".answer-textarea").count(), "the stem has nothing to answer").toBe(0);
  await shot(student, "parts-page");

  // Part (a) answered: the page is partially answered. Part (b) too: answered.
  await setAnswer(A, "5");
  await expect(pill(STEM)).toHaveClass(/partial/);
  await setAnswer(B, "-4x+7");
  await expect(pill(STEM)).not.toHaveClass(/partial/);
  await expect(pill(STEM)).toHaveClass(/answered/);

  // Different answers, different verdicts: (a) is wrong, (b) is right.
  await check(A);
  await expect(feedback(A)).toContainText("not correct");
  await check(B);
  await expect(feedback(B)).toContainText("Your answer is correct");

  // A photo for part (b) belongs to part (b): its question text is what the grader is given.
  await readPhoto(B);
  await submitPhotoWork();
  const graded = (await seen("eval")).at(-1)!;
  expect(graded.user).toContain("tangent to the curve");
  expect(graded.user).not.toContain("coordinate of");
  await expect(feedback(B)).toContainText("Your answer and working are correct.");
  await expect(feedback(A)).toContainText("not correct");   // (a) is untouched

  // After a reload each part still has its own draft, its own feedback, and the page its status.
  await student.reload();
  await expect(student.locator(`#hidden_q_${A}`)).toHaveValue(/5/);
  await expect(student.locator(`#hidden_q_${B}`)).toHaveValue(/4x/);
  expect(await student.locator(`#hidden_q_${A}`).inputValue()).not.toBe(await student.locator(`#hidden_q_${B}`).inputValue());
  await expect(feedback(A)).toContainText("not correct");
  await expect(feedback(B)).toContainText("Your answer and working are correct.");
  await expect(pill(STEM)).toHaveClass(/wrong/);   // one part is wrong, so the page is
  await shot(student, "parts-reload");
});

// ── 2. The stale red banner ──────────────────────────────────────────────────
test("select B, check, upload correct working, confirm and submit it: the stale red banner clears and stays cleared", async () => {
  test.setTimeout(240_000);
  await mock("/__reset");
  await mock("/__script", {
    // No teacher key on this question, so the AI judges the option: its first verdict is wrong (the red banner),
    // the verdict it is asked for again together with the working is right.
    answerReply: [
      { correct: false, feedback: "Your answer is not correct. Check your work and try again." },
      { correct: true, feedback: "That statement is true for every value of c." }
    ],
    ocr: { detectedProblem: "", steps: [
      { number: 1, text: "$\\Delta = (3 - 2c)^2 + 4c^2 = 8c^2 + 9 - 12c$" },
      { number: 2, text: "For $c = 0$ the equation is $3x = 0$, so there is still an intersection." }
    ] }
  });

  await openAssignment();
  await pill(NO_KEY).click();
  await choose(NO_KEY, "B");
  await check(NO_KEY);
  await seeResult();

  // 1) The initial banner is a wrong-option verdict (not a missing-working message), and the AI got the whole question.
  await expect(feedback(NO_KEY)).toHaveClass(/alert-danger/);
  await expect(feedback(NO_KEY)).toContainText("not correct");
  const first = (await seen("answer")).at(-1)!;
  for (const [i, text] of choiceOptions.entries()) expect(first.user).toContain(`${String.fromCharCode(65 + i)}. ${text}`);
  expect(first.user).toContain(`Selected option(s): B. ${optionB}`);
  await shot(student, "stale-banner-before");

  // 2) Upload the handwritten solution, confirm the recognized steps, submit them for checking.
  await readPhoto(NO_KEY);
  const steps = await student.locator("#hwStepsList .hw-step-input").evaluateAll(l => l.map(e => (e as HTMLInputElement).value));
  expect(steps).toHaveLength(2);
  await submitPhotoWork();

  // 3) Back on the question: no red, no tutor nudge, and the verdict covers the answer and the working.
  await expect(feedback(NO_KEY)).toHaveClass(/alert-success/);
  await expect(feedback(NO_KEY)).toContainText("Your answer and working are correct.");
  await expect(feedback(NO_KEY)).not.toHaveClass(/alert-danger/);
  await expect(panel(NO_KEY).locator(".check-tutor-nudge")).toBeHidden();
  await expect(pill(NO_KEY)).toHaveClass(/correct/);
  await expect(pill(NO_KEY)).not.toHaveClass(/wrong/);
  await shot(student, "stale-banner-after");

  // The working was sent with the selected option, and the option was judged again on its own, with every choice.
  const answers = await seen("answer");
  expect(answers.length).toBe(2);
  expect(answers[1].user).toContain(`Selected option(s): B. ${optionB}`);
  expect(answers[1].user).toContain("Choices:");

  // 4) The state survives a reload.
  await student.reload();
  await expect(feedback(NO_KEY)).toContainText("Your answer and working are correct.");
  await expect(feedback(NO_KEY)).toHaveClass(/alert-success/);
  await expect(pill(NO_KEY)).toHaveClass(/correct/);
});

test("with a teacher key: the missing-working message, then correct working, then a working error, are each their own message", async () => {
  test.setTimeout(240_000);
  await mock("/__reset");

  await openAssignment();
  await pill(KEYED).click();
  await choose(KEYED, "B");
  await check(KEYED);
  await expect(feedback(KEYED)).toContainText("Your choice is correct. Add your working to complete this question.");
  await expect(feedback(KEYED)).not.toHaveClass(/alert-danger/);

  // Working with an error: the choice is still right, and the error is explained apart from it.
  await mock("/__script", {
    ocr: { detectedProblem: "", steps: [{ number: 1, text: "$\\Delta = 8c^2 + 9 < 0$" }] },
    evalReply: {
      steps: [{ number: 1, text: "$\\Delta = 8c^2 + 9 < 0$", status: "incorrect", explanation: "8c² + 9 is always positive, so the discriminant is greater than 0." }],
      overall: "incorrect", expectedAnswer: "B", summary: "The discriminant is positive."
    },
    verifyReply: { overall: "incorrect", agrees: true, confidence: 0.9, notes: "", steps: [] }
  });
  await readPhoto(KEYED);
  await submitPhotoWork();
  await expect(feedback(KEYED)).toHaveClass(/alert-warning/);
  await expect(feedback(KEYED)).toContainText("Your answer is correct, but your working has a mistake.");
  await expect(feedback(KEYED)).toContainText("always positive");
  await expect(feedback(KEYED)).not.toContainText("not correct. ");

  // Correct working replaces it.
  await mock("/__script", {
    ocr: { detectedProblem: "", steps: [{ number: 1, text: "$\\Delta = 8c^2 + 9 > 0$" }] },
    evalReply: null,
    verifyReply: null
  });
  await readPhoto(KEYED);   // a new photo replaces the earlier one
  await submitPhotoWork();
  await expect(feedback(KEYED)).toHaveClass(/alert-success/);
  await expect(feedback(KEYED)).toContainText("Your answer and working are correct.");
});

// ── 3. A wrong option stays wrong; a delayed result cannot reach another part ─
test("a wrong selected option stays wrong even when the photo checker accepts the working", async () => {
  test.setTimeout(240_000);
  await mock("/__reset");
  await student.evaluate(() => localStorage.clear());

  await openAssignment();
  await pill(KEYED).click();
  await choose(KEYED, "A");
  await check(KEYED);
  await expect(feedback(KEYED)).toHaveClass(/alert-danger/);
  await expect(feedback(KEYED)).toContainText("not correct");

  await readPhoto(KEYED);   // the mock grader accepts the working
  await submitPhotoWork();

  await expect(feedback(KEYED)).toHaveClass(/alert-danger/);
  await expect(feedback(KEYED)).not.toContainText("working are correct");
  await expect(pill(KEYED)).toHaveClass(/wrong/);
  await expect(pill(KEYED)).not.toHaveClass(/correct/);
  const graded = (await seen("eval")).at(-1)!;
  expect(graded.user, "the working itself was accepted").toContain("Student's steps");

  // Changing the answer makes the old verdict stale at once.
  await choose(KEYED, "B");
  await expect(feedback(KEYED)).toBeHidden();
  await expect(pill(KEYED)).not.toHaveClass(/wrong/);
});

test("a result that arrives late for part (a) cannot update part (b)", async () => {
  test.setTimeout(240_000);
  await mock("/__reset");
  await mock("/__script", { evalDelayMs: 5000 });
  await student.evaluate(() => localStorage.clear());

  await openAssignment();
  await setAnswer(A, "7");
  await check(A);
  await expect(feedback(A)).toContainText("Your answer is correct");

  // Start grading (a)'s working, then close the dialog and move on to (b) while it is still running.
  await readPhoto(A);
  await student.locator("#hwSubmitBtn").click();
  await expect(student.locator("#hwStage-loading")).toBeVisible();
  await student.locator("#hwModal .btn-close").click();
  await expect(student.locator("#hwModal")).toBeHidden();

  await mock("/__script", { evalDelayMs: 0, ocr: { detectedProblem: "", steps: [{ number: 1, text: "$y = -4x + 7$" }] } });
  await readPhoto(B);
  await expect(student.locator("#hwStepsList .hw-step-input").first()).toHaveValue("y = −4x + 7");

  // (a)'s verdict lands now, for (a): its banner changes, (b) is untouched and its dialog keeps its own steps.
  await expect(feedback(A)).toContainText("Your answer and working are correct.", { timeout: 30_000 });
  await expect(student.locator("#hwStage-edit")).toBeVisible();
  await expect(student.locator("#hwStage-results")).toBeHidden();
  await expect(student.locator("#hwStepsList .hw-step-input").first()).toHaveValue("y = −4x + 7");
  await expect(feedback(B)).toBeHidden();
  await expect(pill(STEM)).not.toHaveClass(/wrong/);
  await student.locator("#hwModal .btn-close").click();
  await expect(student.locator("#hwModal")).toBeHidden();
});

// ── 4. Results and the teacher's review keep each part apart ─────────────────
test("results and the teacher's review show each part with its own label, answer and working", async () => {
  test.setTimeout(240_000);
  await mock("/__reset");
  await student.evaluate(() => localStorage.clear());

  await openAssignment();
  await setAnswer(A, "7");
  await setAnswer(B, "-4x+7");
  await submitAssignment(student);
  await expect(student).toHaveURL(/\/Student\/AssignmentResult/i);

  const body = student.locator("body");
  await expect(body).toContainText("Question 1a");
  await expect(body).toContainText("Question 1b");
  await expect(student.locator(`img[alt="Question diagram"]`)).toHaveCount(1);   // the shared diagram once
  const stemCard = student.locator(".list-group-item.bg-light");
  await expect(stemCard).toHaveCount(1);
  expect(await stemCard.innerText(), "the stem is context, not a marked item").not.toContain("N/A");

  const resultId = new URL(student.url()).searchParams.get("resultId")!;
  await teacher.goto(`/Assignments/SubmissionDetail?resultId=${resultId}`);
  const review = teacher.locator("body");
  await expect(review).toContainText("shared question");
  await expect(review.locator(".avatar-initial", { hasText: /^\s*1a\s*$/ })).toHaveCount(1);
  await expect(review.locator(".avatar-initial", { hasText: /^\s*1b\s*$/ })).toHaveCount(1);
});

// ── 5. The import keeps clearly identified parts together ────────────────────
test("a copy of the sample paper is imported with its parts grouped; ambiguous ones are left for the teacher", async () => {
  test.setTimeout(240_000);
  const copy = join(tmpdir(), `copy-of-paper-${Date.now()}.pdf`);
  copyFileSync(resolve(fixtures, "mock-pure-math-2025.pdf"), copy);   // the original is never touched

  await mock("/__reset");
  await mock("/__script", {
    importReply: [
      { number: 1, title: "Solve $2x + 3 = 11$.", type: "ShortAnswer", options: [], answerKey: "4", difficulty: "Easy", hasDiagram: false, validationWarning: null },
      { number: 2, part: "a", stem: "The curve $y = x^2 - 4x + 7$ meets the $y$-axis at $A$, as shown.", title: "Find the $y$-coordinate of $A$.", type: "ShortAnswer", options: [], answerKey: "7", difficulty: "Medium", hasDiagram: true, diagramHint: "A parabola crossing the y-axis at A", validationWarning: null },
      { number: 2, part: "b", stem: "The curve $y = x^2 - 4x + 7$ meets the $y$-axis at $A$, as shown.", title: "Find the equation of the tangent to the curve at $A$.", type: "ShortAnswer", options: [], answerKey: "y=-4x+7", difficulty: "Medium", hasDiagram: true, diagramHint: "A parabola crossing the y-axis at A", validationWarning: null },
      // A lone "(b)" with no sibling and no stem: not grouped, flagged for the teacher.
      { number: 3, part: "b", stem: null, title: "(b) Find the gradient of the line.", type: "ShortAnswer", options: [], answerKey: "2", difficulty: "Medium", hasDiagram: false, validationWarning: null }
    ]
  });

  const draftId = await createDraftAssignment(teacher, unique("E2E Paper copy"), className);
  await teacher.getByRole("button", { name: "Add questions" }).first().click();
  await teacher.evaluate(() => {
    const w = window as unknown as { __pdfShown: boolean };
    w.__pdfShown = false;
    document.getElementById("pdfImportModal")!.addEventListener("shown.bs.modal", () => { w.__pdfShown = true; }, { once: true });
  });
  await teacher.locator("button[onclick=\"openImportModal('pdf')\"]").click();
  await teacher.waitForFunction(() => (window as unknown as { __pdfShown: boolean }).__pdfShown);
  await teacher.locator("#pdfFileInput").setInputFiles(copy);
  await teacher.locator("#pdfAnalyseBtn").click();

  const rows = teacher.locator("#pdfQuestionList .list-group-item");
  await expect(rows).toHaveCount(4, { timeout: 60_000 });
  await expect(rows.nth(1)).toContainText("part of a shared question");
  await expect(rows.nth(2)).toContainText("part of a shared question");
  await expect(rows.nth(3)).not.toContainText("part of a shared question");
  await expect(rows.nth(3)).toContainText("one part of a larger question");   // left for the teacher to decide
  await shot(teacher, "import-parts");

  await teacher.locator("#pdfAddBtn").click();
  await teacher.waitForLoadState("load");
  await expect.poll(
    () => teacher.evaluate(() => (typeof QUESTIONS_DATA === "undefined" ? -1 : QUESTIONS_DATA.length)).catch(() => -1),
    { timeout: 60_000 }
  ).toBe(5);   // 1 + a shared question with 2 parts + 1

  const stored = await teacher.evaluate(() => (QUESTIONS_DATA as unknown as { id: string; groupId?: string; title: string }[]).map(q => ({ id: q.id, groupId: q.groupId ?? null, title: q.title })));
  const shared = stored.find(q => q.title.includes("meets the"))!;
  expect(shared.groupId, "the shared question is the parent").toBeNull();
  expect(stored.filter(q => q.groupId === shared.id).map(q => q.title)).toEqual([
    "Find the $y$-coordinate of $A$.", "Find the equation of the tangent to the curve at $A$."
  ]);
  expect(stored.find(q => q.title.startsWith("(b) Find the gradient"))!.groupId, "the ambiguous one stays as listed").toBeNull();

  // Published, the student sees the parts together: three pages, not five questions.
  await publishAndAssignAll(teacher, draftId);
  await student.goto(`/Student/TakeAssignment?assignmentId=${draftId}`);
  await expect(student.locator(".question-nav .nav-btn[data-idx]")).toHaveCount(3);
});
