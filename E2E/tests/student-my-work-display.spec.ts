import { expect, test, type Browser, type BrowserContext, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { mkdirSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { assignTo, createClass, createDraftAssignment, expandStudent, signIn, unique } from "./helpers";

// The student's way through: start on a laptop, carry on from a phone (Continue, the student's own deadline), attach
// a photo to question 3, hand in, open exactly that photo again after a reload, and read the feedback for question 3
// once the teacher has marked it. The AI is the deterministic mock (scripts/mock-ai.mjs).
test.describe.configure({ mode: "serial" });

const mockAi = process.env.E2E_MOCK_AI_URL ?? `http://127.0.0.1:${process.env.E2E_MOCK_AI_PORT ?? 5199}`;
const fixtures = resolve(process.cwd(), "fixtures");
const shots = process.env.E2E_SHOT_DIR;
const password = "MyWork-Pass-123";
const stamp = unique("mywork").replace(/\s/g, "");
const className = unique("Algebra");
const title = `Quadratics practice ${stamp.slice(-4)}`;
const studentName = `Pupil ${stamp.slice(-4)}`;
const studentEmail = `${stamp}.pupil@example.test`;

let browserRef: Browser;
let teacher: Page;
let laptop: Page;
let phone: Page;
let phoneContext: BrowserContext;
let assignmentId = "";
let resultUrl = "";
let savedPhoto = "";

async function mock(path: string, body?: unknown): Promise<void> {
  const r = await fetch(`${mockAi}${path}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body ?? {}) });
  expect(r.ok, `mock AI ${path}`).toBe(true);
}
async function aiCalls(): Promise<number> {
  return ((await (await fetch(`${mockAi}/__requests`)).json()) as unknown[]).length;
}
async function shot(page: Page, file: string): Promise<void> {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  await page.waitForTimeout(500);
  await page.screenshot({ path: resolve(shots, `${file}.png`), fullPage: true });
}
const nav = (page: Page, i: number) => page.locator(`.question-nav .nav-btn[data-idx="${i}"]`);

// Tomorrow 18:00 in school time (Tashkent, UTC+5), as the teacher's date field takes it.
const tomorrow1800 = (() => {
  const d = new Date(Date.now() + 5 * 3600e3 + 24 * 3600e3);
  return d.toISOString().slice(0, 10) + "T18:00";
})();

test.beforeAll(async ({ browser }) => {
  browserRef = browser;
  teacher = await (await browser.newContext({ viewport: { width: 1360, height: 900 } })).newPage();
  teacher.on("dialog", d => void d.accept());
});

test.afterAll(async () => {
  await teacher.context().close();
  await laptop?.context().close();
  await phoneContext?.close();
});

test("setup: five questions, given to the student with a personal deadline", async () => {
  test.setTimeout(180_000);
  await mock("/__reset");
  const email = `${stamp}.teacher@example.test`;
  await teacher.goto("/Auth/Register");
  await teacher.locator('input[name="fullName"]').fill("My Work Teacher");
  await teacher.locator('input[name="email"]').fill(email);
  await teacher.locator('input[name="password"]').fill(password);
  await teacher.locator('input[name="confirmPassword"]').fill(password);
  await teacher.locator('button[type="submit"]').click();
  await signIn(teacher, email, password);
  const invite = await createClass(teacher, className);

  laptop = await (await browserRef.newContext({ viewport: { width: 1360, height: 900 } })).newPage();
  await laptop.goto("/Auth/StudentRegister");
  await laptop.locator('input[name="inviteCode"]').fill(invite);
  await laptop.locator('input[name="fullName"]').fill(studentName);
  await laptop.locator('input[name="email"]').fill(studentEmail);
  await laptop.locator('input[name="password"]').fill(password);
  await laptop.locator('input[name="confirmPassword"]').fill(password);
  await laptop.locator('button[type="submit"]').click();
  await signIn(laptop, studentEmail, password);

  // Joined, nothing given yet.
  await laptop.goto("/Student");
  await expect(laptop.locator("#noWorkYet")).toContainText(className);

  assignmentId = await createDraftAssignment(teacher, title, className);
  const token = await teacher.locator('input[name="__RequestVerificationToken"]').first().inputValue();
  for (const q of [
    { title: "Solve $x + 3 = 5$.", answerKey: "2" },
    { title: "Solve $2x = 6$.", answerKey: "3" },
    { title: "Solve $x^2 - 5x + 6 = 0$. Show your working.", answerKey: "x = 2 or x = 3", marks: 3 },
    { title: "Solve $x - 1 = 3$.", answerKey: "4" },
    { title: "Solve $3x = 15$.", answerKey: "5" }
  ]) {
    const r = await teacher.request.post(`/Assignments/AddQuestion/${assignmentId}`, {
      form: { questionJson: JSON.stringify({ id: randomUUID(), type: 0, difficulty: "Medium", ...q }), __RequestVerificationToken: token }
    });
    expect(r.ok()).toBe(true);
  }
  expect(await assignTo(teacher, assignmentId, { students: [studentName], due: "2099-01-01T12:00" })).toContain("Assigned to 1 student");

  // The teacher gives this student their own deadline: tomorrow 18:00.
  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  await expandStudent(teacher, studentName);
  const row = teacher.locator("#recipientTable tbody.recipient", { hasText: studentName }).first();
  await row.locator('form[action*="UpdateStudentDates"] input[name="dueDate"]').fill(tomorrow1800);
  await row.locator('form[action*="UpdateStudentDates"] button[type="submit"]').click();
  await expect(teacher.locator(".alert-success").first()).toBeVisible();
});

test("laptop: start, answer question 1, the answer is saved to the account; an interrupted upload says so", async () => {
  await laptop.goto("/Student");
  await expect(laptop.locator("#nextUp")).toContainText(title);
  await expect(laptop.locator("#nextUpAction")).toHaveText(/Start assignment/);
  await laptop.locator("#nextUpAction").click();
  await laptop.waitForFunction(() => !!customElements.get("math-field"));   // the maths input is ready
  await laptop.locator('.question-panel[data-qindex="0"] .answer-textarea').first().fill("2");
  await expect(laptop.locator("#draftStatus")).toContainText("Saved to your account", { timeout: 15_000 });

  // Losing the connection during a photo upload: the error stays at that question, with Try again.
  await laptop.route("**/Student/AnalyzeHandwritten", r => r.abort("internetdisconnected"));
  laptop.on("dialog", d => void d.accept());
  await nav(laptop, 3).click();
  const block = laptop.locator('.question-panel[data-qindex="3"] .work-block');
  await block.locator("#workToggle_3").check();
  await block.locator('.work-tab-btn[data-work-tab="upload"]').click();
  await block.locator('[data-hw-open][data-qindex="3"]').click();
  await laptop.locator("#hwFileInput").setInputFiles(resolve(fixtures, "solution-correct.jpg"));
  await laptop.locator("#hwCropUseFullBtn").click();
  await laptop.locator("#hwAnalyzeBtn").click();
  const status3 = laptop.locator('.photo-status[data-qindex="3"]');
  await expect(status3).toHaveAttribute("data-state", "failed");
  await expect(status3).toContainText("interrupted");
  await expect(status3.locator(".photo-retry")).toBeVisible();
  await laptop.unroute("**/Student/AnalyzeHandwritten");
  await laptop.locator("#hwModal .btn-close").click();

  await laptop.goto("/Student");
  await expect(laptop.locator("#nextUpAction")).toHaveText(/Continue assignment/);
  await expect(laptop.locator("#nextUp [data-due]")).toContainText("Your deadline: Due tomorrow, 18:00");
  await shot(laptop, "1-my-work-desktop");
});

test("phone: Continue opens the same attempt with the laptop's answer; a photo is saved to question 3; the summary counts it", async () => {
  test.setTimeout(180_000);
  phoneContext = await browserRef.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  phone = await phoneContext.newPage();
  phone.on("dialog", d => void d.accept());
  await signIn(phone, studentEmail, password);
  await phone.goto("/Student");
  await expect(phone.locator("#nextUpAction")).toHaveText(/Continue assignment/);
  await expect(phone.locator("#nextUp [data-due]")).toContainText("Your deadline: Due tomorrow, 18:00");
  const overflow = await phone.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(1);
  await shot(phone, "1-my-work-phone");

  await phone.locator("#nextUpAction").click();
  await expect(phone.locator('#hidden_q_0')).toHaveValue(/^\$?2\$?$/);   // from the laptop
  await expect(phone.locator("#myDue")).toContainText("Your deadline: Due tomorrow, 18:00");

  // Question 3 (index 2): a photo of the working.
  await mock("/__script", {
    ocr: { detectedProblem: "", steps: [{ number: 1, text: "$(x-2)(x-3) = 0$" }, { number: 2, text: "$x = 2$ or $x = 3$" }] },
    verifyReply: { overall: "correct", agrees: false, confidence: 0.4, notes: "Please check the factorising.", steps: [] }
  });
  await nav(phone, 2).click();
  const block = phone.locator('.question-panel[data-qindex="2"] .work-block');
  await block.locator("#workToggle_2").check();
  await block.locator('.work-tab-btn[data-work-tab="upload"]').click();
  await block.locator('[data-hw-open][data-qindex="2"]').click();
  await phone.locator("#hwFileInput").setInputFiles({
    name: "q3.jpg", mimeType: "image/jpeg",
    buffer: Buffer.concat([readFileSync(resolve(fixtures, "solution-correct.jpg")), Buffer.from(randomUUID())])
  });
  await phone.locator("#hwCropUseFullBtn").click();
  await phone.locator("#hwAnalyzeBtn").click();
  await expect(phone.locator("#hwStage-edit")).toBeVisible({ timeout: 30_000 });
  await phone.locator("#hwSubmitBtn").click();
  await expect(phone.locator("#hwStage-results")).toBeVisible({ timeout: 60_000 });
  await phone.locator("#hwStage-results .btn-primary[data-bs-dismiss='modal']").click();
  const status = phone.locator('.photo-status[data-qindex="2"]');
  await expect(status).toHaveAttribute("data-state", "saved");
  await expect(status).toContainText("Photo saved to question 3");
  savedPhoto = (await status.locator("img").first().getAttribute("src"))!;

  await phone.locator("#submitBtn").click();
  await expect(phone.locator("#submitSummary")).toHaveText("2 of 5 questions answered · 1 photo attached");
  await expect(phone.locator("#confirmSubmitBtn")).toHaveText(/Submit assignment/);
  await expect(phone.locator("#backToQuestions")).toHaveText(/Back to questions/);
  await shot(phone, "2-question-photo-summary-phone");
  await phone.locator("#backToQuestions").click();
  await expect(phone.locator('#hidden_q_0')).toHaveValue(/^\$?2\$?$/);   // kept

  // The laptop sees the photo saved on the phone, and the same summary.
  await laptop.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
  await nav(laptop, 2).click();
  await expect(laptop.locator('.photo-status[data-qindex="2"]')).toContainText("Photo saved to question 3");
  await laptop.locator("#submitBtn").click();
  await expect(laptop.locator("#submitSummary")).toHaveText("2 of 5 questions answered · 1 photo attached");
  await shot(laptop, "2-question-photo-summary-desktop");
  await laptop.locator("#backToQuestions").click();
});

test("phone hands in: Work submitted (awaiting review) stays after a reload and opens the very photo; no AI call is repeated", async () => {
  await phone.locator("#submitBtn").click();
  await phone.locator("#confirmSubmitBtn").dblclick();                                   // a double press hands in once
  await expect(phone).toHaveURL(/AssignmentResult/i, { timeout: 30_000 });
  await expect(phone.locator("#workSubmitted h4")).toHaveText("Work submitted");
  await expect(phone.locator("#submittedTitle")).toHaveText(title);
  await expect(phone.locator("#submittedConfirmation")).toContainText("Received");
  await expect(phone.locator("#awaitingReview")).toContainText("Awaiting review");
  await expect(phone.locator("[data-marks], [data-score]")).toHaveCount(0);              // no marks before the result
  resultUrl = phone.url();
  await shot(phone, "3-work-submitted-phone");

  const before = await aiCalls();
  await phone.reload();
  await expect(phone.locator("#workSubmitted h4")).toHaveText("Work submitted");
  const photoLink = phone.locator('[data-submitted-question="2"] [data-view-photo]');
  await expect(photoLink).toHaveAttribute("href", savedPhoto);
  const img = await phone.request.get(savedPhoto);
  expect(img.status()).toBe(200);
  expect(img.headers()["content-type"]).toContain("image");

  await laptop.goto(resultUrl);
  await expect(laptop.locator("#awaitingReview")).toBeVisible();
  await shot(laptop, "3-work-submitted-desktop");
  await laptop.goto("/Student?section=submitted");
  await expect(laptop.locator(`[data-work-item][data-assignment="${assignmentId}"] [data-status]`)).toContainText("Submitted · awaiting review");
  expect(await aiCalls()).toBe(before);                                                   // opening pages calls no AI

  // Exactly one attempt was handed in.
  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  await expect(teacher.locator('[data-counter="submitted"] .counter-note')).toContainText("1 submitted attempt");
});

test("after the teacher marks it: Feedback → question 3 with photo, marks, the teacher's comment and who reviewed it", async () => {
  // The teacher marks question 3 and comments.
  const resultId = new URL(resultUrl).searchParams.get("resultId")!;
  await teacher.goto(`/Assignments/SubmissionDetail?resultId=${resultId}`);
  await teacher.locator('#q2 .grade-points').fill("3");
  await teacher.locator('#q2 .grade-correct[value="true"]').check({ force: true });
  await teacher.locator('#q2 .grade-comment').fill("Good factorising.");
  await teacher.getByRole("button", { name: "Save grades" }).click();
  await expect(teacher.locator(".alert-success").first()).toBeVisible();

  const before = await aiCalls();
  await phone.goto("/Student?section=feedback");
  const item = phone.locator(`[data-work-item][data-assignment="${assignmentId}"]`);
  await expect(item.locator("[data-status]")).toContainText("Feedback ready");
  await item.locator("[data-primary]").click();
  const q3 = phone.locator('.list-group-item:has([data-marks])').filter({ hasText: "x^2 - 5x + 6" }).first();
  await expect(phone.locator('[data-submitted-photo] img').first()).toBeVisible();
  await expect(phone.locator('[data-source="teacher-comment"]')).toContainText("Good factorising.");
  await expect(phone.locator('[data-source="teacher-reviewed"]').first()).toContainText("Reviewed by teacher");
  await expect(phone.locator("[data-marks]").filter({ hasText: "3 / 3" })).toHaveCount(1);
  await expect(phone.locator("[data-step-text]").first()).toHaveText("(x − 2)(x − 3) = 0");   // readable, not LaTeX
  expect(await phone.locator(".list-group").first().innerText()).not.toMatch(/\$|\\frac/);
  await shot(phone, "4-feedback-phone");

  await laptop.goto(phone.url());
  await laptop.locator('[data-source="teacher-comment"]').scrollIntoViewIfNeeded();
  await shot(laptop, "4-feedback-desktop");
  expect(await aiCalls()).toBe(before);
  void q3;
});

test("keyboard: the main action on My work is reachable and works with Enter", async () => {
  await laptop.goto("/Student?section=feedback");
  await laptop.locator(`[data-work-item][data-assignment="${assignmentId}"] [data-primary]`).focus();
  await laptop.keyboard.press("Enter");
  await expect(laptop).toHaveURL(/AssignmentResult/i);
});
