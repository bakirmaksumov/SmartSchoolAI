import { expect, test, type Browser, type BrowserContext, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { copyFileSync, mkdirSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { assignTo, createClass, createDraftAssignment, expandStudent, signIn, submitAssignment, unique } from "./helpers";

// The assignment page's statuses, counters, live board and CSV, and a hand-in that is only confirmed once saved.
// Acceptance data: 12 recipients — A two submitted attempts, B a draft with a photo, C a final hand-in at 0%, the
// rest not started. The AI is the deterministic mock (scripts/mock-ai.mjs).
test.describe.configure({ mode: "serial" });

const mockAi = process.env.E2E_MOCK_AI_URL ?? `http://127.0.0.1:${process.env.E2E_MOCK_AI_PORT ?? 5199}`;
const fixtures = resolve(process.cwd(), "fixtures");
const shots = process.env.E2E_SHOT_DIR;
const password = "Details-Pass-123";
const stamp = unique("details").replace(/\s/g, "");
const className = unique("E2E Pure Maths");

let browserRef: Browser;
let teacher: Page;
let assignmentId = "";
let inviteCode = "";
const pupils: Record<string, Page> = {};
const name = (k: string) => `Pupil ${k} ${stamp.slice(-4)}`;

// Question indexes: 0 shared stem, 1 = 1a (key 4), 2 = 1b (no key), 3 = Q2 (key 3), 4 = Q3 (nobody answers).
const PAGE_OF: Record<number, number> = { 1: 0, 2: 0, 3: 3, 4: 4 };

async function mock(path: string, body?: unknown): Promise<void> {
  const r = await fetch(`${mockAi}${path}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body ?? {}) });
  expect(r.ok, `mock AI ${path}`).toBe(true);
}

async function shot(page: Page, file: string): Promise<void> {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  await page.waitForTimeout(500);
  await page.screenshot({ path: resolve(shots, `${file}.png`), fullPage: true });
}

async function register(key: string): Promise<Page> {
  const page = await (await browserRef.newContext()).newPage();
  page.on("dialog", d => void d.accept());
  const email = `${stamp}.${key.toLowerCase()}@example.test`;
  await page.goto("/Auth/StudentRegister");
  await page.locator('input[name="inviteCode"]').fill(inviteCode);
  await page.locator('input[name="fullName"]').fill(name(key));
  await page.locator('input[name="email"]').fill(email);
  await page.locator('input[name="password"]').fill(password);
  await page.locator('input[name="confirmPassword"]').fill(password);
  await page.locator('button[type="submit"]').click();
  await expect(page).toHaveURL(/\/Auth\/Login/);
  await signIn(page, email, password);
  pupils[key] = page;
  return page;
}

async function open(key: string): Promise<Page> {
  const page = pupils[key];
  await page.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
  await expect(page.locator("#assignmentForm")).toBeVisible();
  return page;
}

async function answer(page: Page, index: number, value: string): Promise<void> {
  await page.locator(`.question-nav .nav-btn[data-idx="${PAGE_OF[index]}"]`).click();
  await page.locator(`.question-panel[data-qindex="${index}"] .answer-textarea`).first().fill(value);
}

async function handIn(page: Page): Promise<void> {
  await submitAssignment(page);
  await expect(page).toHaveURL(/AssignmentResult/i, { timeout: 30_000 });
  await expect(page.locator("#submittedConfirmation")).toContainText("Submitted.");
}

/** Checks a photo of the working for part (a): this saves an attempt in progress, nothing is handed in. */
async function photoDraft(page: Page): Promise<void> {
  const block = page.locator('.question-panel[data-qindex="1"] .work-block');
  await page.locator('.question-nav .nav-btn[data-idx="0"]').click();
  if (!(await block.locator("#workToggle_1").isChecked())) await block.locator("#workToggle_1").check();
  await block.locator('.work-tab-btn[data-work-tab="upload"]').click();
  await page.evaluate(() => {
    (window as unknown as { __hw: boolean }).__hw = false;
    document.getElementById("hwModal")!.addEventListener("shown.bs.modal", () => { (window as unknown as { __hw: boolean }).__hw = true; }, { once: true });
  });
  await block.locator('[data-hw-open][data-qindex="1"]').click();
  await page.waitForFunction(() => (window as unknown as { __hw: boolean }).__hw);
  await page.locator("#hwFileInput").setInputFiles(resolve(fixtures, "solution-correct.jpg"));
  await page.locator("#hwCropUseFullBtn").click();
  await page.locator("#hwAnalyzeBtn").click();
  await expect(page.locator("#hwStage-edit")).toBeVisible({ timeout: 30_000 });
  await page.locator("#hwSubmitBtn").click();
  await expect(page.locator("#hwStage-results")).toBeVisible({ timeout: 60_000 });
}

const counter = (page: Page, key: string) => page.locator(`[data-counter="${key}"] .counter-value`);
const row = (page: Page, key: string) => page.locator("#recipientTable tbody.recipient", { hasText: name(key) }).first();

test.beforeAll(async ({ browser }) => {
  browserRef = browser;
  teacher = await (await browser.newContext({ viewport: { width: 1360, height: 900 } })).newPage();
  teacher.on("dialog", d => void d.accept());
});

test.afterAll(async () => {
  await teacher.context().close();
  for (const p of Object.values(pupils)) await p.context().close();
});

test("setup: 12 recipients, an assignment with formulas and parts, unlimited attempts", async () => {
  test.setTimeout(300_000);
  await mock("/__reset");
  const email = `${stamp}.teacher@example.test`;
  await teacher.goto("/Auth/Register");
  await teacher.locator('input[name="fullName"]').fill("Details Teacher");
  await teacher.locator('input[name="email"]').fill(email);
  await teacher.locator('input[name="password"]').fill(password);
  await teacher.locator('input[name="confirmPassword"]').fill(password);
  await teacher.locator('button[type="submit"]').click();
  await signIn(teacher, email, password);
  inviteCode = await createClass(teacher, className);
  for (const k of ["A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L"]) await register(k);

  assignmentId = await createDraftAssignment(teacher, `Practice for MT2 ${stamp}`, className);
  const token = await teacher.locator('input[name="__RequestVerificationToken"]').first().inputValue();
  const add = async (q: Record<string, unknown>) => {
    const r = await teacher.request.post(`/Assignments/AddQuestion/${assignmentId}`, {
      form: { questionJson: JSON.stringify({ difficulty: "Medium", ...q }), __RequestVerificationToken: token }
    });
    expect(r.ok()).toBe(true);
  };
  const stem = randomUUID();
  await add({ id: stem, title: "Let $f(x) = x^2$.", type: 0, answerKey: "" });
  await add({ id: randomUUID(), groupId: stem, title: "Find $f(2)$.", type: 0, answerKey: "4" });
  await add({ id: randomUUID(), groupId: stem, title: "Explain why $f(-2) = f(2)$.", type: 3, answerKey: "" });
  await add({ id: randomUUID(), title: "Solve $2x = 6$.", type: 0, answerKey: "3" });
  await add({ id: randomUUID(), title: "Write $\\frac{1}{2}$ as a decimal.", type: 0, answerKey: "0.5" });

  await teacher.goto(`/Assignments/Edit/${assignmentId}?tab=settings`);
  await teacher.locator('#settingsForm input[name="maxAttempts"]').fill("0");
  await teacher.locator('#settingsForm button[type="submit"]').click();
  expect(await assignTo(teacher, assignmentId)).toContain("Assigned to 12 students");
});

test("A hands in twice, C hands in 0%, B leaves a draft with a photo — the page agrees with itself", async () => {
  test.setTimeout(240_000);
  await mock("/__script", { answerReply: { correct: false, marks: 0, feedback: "Not right." } });

  let a = await open("A");
  await answer(a, 1, "5");
  await handIn(a);
  a = await open("A");
  await answer(a, 1, "4");
  await answer(a, 3, "3");
  await handIn(a);

  const c = await open("C");
  await answer(c, 3, "7");                  // wrong, judged by the AI mock: 0%
  await handIn(c);
  await expect(c.locator("body")).toContainText("0%");

  // B: a photo of the working for part (a), checked but never handed in.
  await photoDraft(await open("B"));

  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  await expect(counter(teacher, "all")).toHaveText("12");
  await expect(counter(teacher, "submitted")).toHaveText("2");
  await expect(counter(teacher, "not-submitted")).toHaveText("10");
  await expect(teacher.locator('[data-counter="submitted"] .counter-note')).toHaveText("3 submitted attempts");
  await expect(teacher.locator('[data-counter="not-submitted"] .counter-note')).toHaveText("1 in progress · 9 not started");

  await expect(row(teacher, "B").locator(".submission-cell")).toContainText("In progress");
  await expect(row(teacher, "B").locator(".score-cell")).toHaveText("—");          // no score for a draft
  await expect(row(teacher, "C").locator(".submission-cell")).toContainText("Submitted");
  await expect(row(teacher, "C").locator(".score-cell")).toHaveText("0%");
  await expect(row(teacher, "A").locator(".submission-cell")).toContainText("2 attempts");
  await expect(row(teacher, "D").locator(".submission-cell")).toContainText("Not started");

  await expandStudent(teacher, name("B"));
  const bPanel = row(teacher, "B").locator(".collapse.show");
  await expect(bPanel).toContainText("In progress · not submitted");
  await expect(bPanel).toContainText("photo");
  await expect(bPanel).not.toContainText("Submitted ");                            // a draft is never shown as handed in
  await expandStudent(teacher, name("A"));
  await expect(row(teacher, "A").locator(".attempt-item")).toHaveCount(2);
  await shot(teacher, "tab-students");

  // The counters filter the rows.
  await teacher.locator('#liveCounters [data-filter="submitted"]').click();
  await expect(teacher.locator("#recipientTable tbody.recipient:not(.d-none)")).toHaveCount(2);
  await expect(teacher).toHaveURL(/filter=submitted/);
  await teacher.locator('#liveCounters [data-filter="not-submitted"]').click();
  await expect(teacher.locator("#recipientTable tbody.recipient:not(.d-none)")).toHaveCount(10);
  await teacher.locator("#clearFilter").click();
});

test("B hands in while the teacher's page is open: row and counters update, tab, filter and focus stay", async () => {
  test.setTimeout(180_000);
  // A separate, recorded window for the teacher: the evidence of "In progress → Submitted" on an open page.
  const recordDir = resolve(shots ?? resolve(process.cwd(), "test-results"), "video-tmp");
  const ctx: BrowserContext = await browserRef.newContext({ viewport: { width: 1280, height: 800 }, recordVideo: { dir: recordDir, size: { width: 1280, height: 800 } } });
  const live = await ctx.newPage();
  await live.goto("/Auth/Login");
  await live.getByLabel("Email").fill(`${stamp}.teacher@example.test`);
  await live.getByLabel("Password").fill(password);
  await live.getByRole("button", { name: "Sign in" }).click();
  await live.goto(`/Assignments/Details/${assignmentId}?tab=students&filter=not-submitted`);
  await live.evaluate(() => { (window as unknown as { __noReload: boolean }).__noReload = true; });
  await expandStudent(live, name("B"));
  await live.locator("#studentSearch").fill("pupil");
  await live.locator("#studentSearch").focus();
  await expect(row(live, "B").locator(".submission-cell")).toContainText("In progress");
  await live.waitForTimeout(1500);

  const b = await open("B");
  await handIn(b);

  // The board notices on its own (polling every 10 s), without reloading the page.
  await expect(counter(live, "submitted")).toHaveText("3", { timeout: 30_000 });
  await expect(counter(live, "not-submitted")).toHaveText("9");
  await expect(row(live, "B").locator(".submission-cell")).toContainText("Submitted");
  await expect(live.locator("#liveAnnouncer")).toContainText("updated");
  expect(await live.evaluate(() => (window as unknown as { __noReload?: boolean }).__noReload)).toBe(true);
  expect(await live.evaluate(() => document.activeElement?.id)).toBe("studentSearch");   // focus kept
  await expect(live.locator("#studentSearch")).toHaveValue("pupil");                      // search kept
  await expect(live).toHaveURL(/tab=students/);                                           // tab kept
  await expect(live).toHaveURL(/filter=not-submitted/);                                   // filter kept
  await expect(row(live, "B")).toBeHidden();                                              // B now leaves the "not submitted" filter
  await live.locator("#clearFilter").click();
  await expect(row(live, "B").locator(".collapse.show")).toBeVisible();                   // B's attempts stay open
  await live.waitForTimeout(1500);

  const video = live.video();
  await ctx.close();
  if (video && shots) copyFileSync(await video.path(), resolve(shots, "live-in-progress-to-submitted.webm"));
});

test("a new attempt started after handing in keeps the student 'Submitted'; the draft is in the history", async () => {
  test.setTimeout(120_000);
  await photoDraft(await open("A"));         // attempt 3, in progress

  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  await expect(row(teacher, "A").locator(".submission-cell")).toContainText("Submitted");
  await expect(row(teacher, "A").locator(".score-cell")).not.toHaveText("—");     // the newest SUBMITTED attempt's score
  await expandStudent(teacher, name("A"));
  const items = row(teacher, "A").locator(".attempt-item");
  await expect(items).toHaveCount(3);
  await expect(items.first()).toContainText("Attempt 3");
  await expect(items.first()).toContainText("In progress · not submitted");
  await expect(counter(teacher, "submitted")).toHaveText("3");                     // still A, B, C
});

test("Submit when the AI fails: the work is handed in anyway and the student is told so", async () => {
  test.setTimeout(120_000);
  await mock("/__script", { answerStatus: 500 });
  const d = await open("D");
  await answer(d, 2, "Because squaring removes the sign.");   // no key: only the AI could mark it
  await handIn(d);
  await mock("/__script", { answerStatus: null });

  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  await expect(row(teacher, "D").locator(".submission-cell")).toContainText("Submitted");
  // The AI's marking runs after the save; the answer it could not mark waits for the teacher.
  await expect.poll(async () => {
    await teacher.goto(`/Assignments/Details/${assignmentId}`);
    return (await row(teacher, "D").locator(".review-cell").innerText()).toLowerCase();
  }, { timeout: 30_000 }).toContain("needs review");
  await expect(row(teacher, "D").locator(".main-action")).toHaveText("Review work");
  await expect(counter(teacher, "needs-review")).not.toHaveText("0");
});

test("the same press of Submit sent twice at once: one attempt, both answers point to it", async () => {
  const e = await open("E");
  await answer(e, 1, "4");
  const form = await e.evaluate(() => Object.fromEntries(new FormData(document.getElementById("assignmentForm") as HTMLFormElement) as unknown as Iterable<[string, string]>));
  const send = () => e.request.post("/Student/SubmitAssignment", { form, headers: { "X-Requested-With": "fetch" } });
  const [r1, r2] = await Promise.all([send(), send()]);
  expect(r1.status()).toBe(200);
  expect(r2.status()).toBe(200);
  const [j1, j2] = [await r1.json(), await r2.json()];
  expect(j1.ok && j2.ok).toBe(true);
  expect(j1.resultUrl).toBe(j2.resultUrl);

  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  await expect(row(teacher, "E").locator(".submission-cell")).toContainText("Submitted");
  await expect(row(teacher, "E").locator(".submission-cell")).not.toContainText("attempts");
});

test("a lost connection on Submit: a clear error, answers kept, and 'Try again' hands in once", async () => {
  const f = await open("F");
  await answer(f, 1, "4");
  let blocked = true;
  await f.route("**/Student/SubmitAssignment", route => blocked ? route.abort("connectionreset") : route.continue());

  await f.getByRole("button", { name: "Submit Assignment" }).click();
  await f.locator("#confirmSubmitBtn").click();
  await expect(f.locator("#submitError")).toBeVisible();
  await expect(f.locator("#submitErrorText")).toContainText("Your answers are still on this page");
  await expect(f.locator("#confirmSubmitLabel")).toHaveText("Try again");
  await expect(f).toHaveURL(/TakeAssignment/);
  const draft = await f.evaluate(() => Object.keys(localStorage).filter(k => k.startsWith("quizDraft:")).length);
  expect(draft).toBe(1);

  blocked = false;
  await f.locator("#confirmSubmitBtn").click();
  await expect(f).toHaveURL(/AssignmentResult/i, { timeout: 30_000 });
  await expect(f.locator("#submittedConfirmation")).toContainText("Submitted.");

  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  await expect(row(teacher, "F").locator(".submission-cell")).toContainText("Submitted");
  await expect(row(teacher, "F").locator(".submission-cell")).not.toContainText("attempts");
});

test("personal due date, anonymous mode in expanded rows, and a CSV with the same numbers", async () => {
  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  await expandStudent(teacher, name("G"));
  const panel = row(teacher, "G").locator(".collapse.show");
  await panel.locator('input[name="dueDate"]').fill("2099-11-28T17:00");
  await panel.getByRole("button", { name: "Save" }).click();
  await expect(teacher.locator(".alert-success")).toContainText("dates updated");
  await expandStudent(teacher, name("G"));
  await expect(row(teacher, "G").locator(".collapse.show")).toContainText("28 Nov 2099 17:00");
  await expect(row(teacher, "G").locator(".collapse.show")).toContainText("Personal");

  await teacher.locator("#anonToggle").check();
  const blurred = async (loc: ReturnType<Page["locator"]>) => (await loc.evaluate(el => getComputedStyle(el).filter)).includes("blur");
  expect(await blurred(row(teacher, "G").locator(".name-cell .js-anon").first())).toBe(true);
  // An expanded row names the student only where it has no attempt yet ("No attempt yet — <name>"): blurred too.
  await expandStudent(teacher, name("H"));
  expect(await blurred(row(teacher, "H").locator(".collapse.show .js-anon").first())).toBe(true);
  await teacher.reload();
  await expect(teacher.locator("#anonToggle")).toBeChecked();                     // kept for this tab
  await teacher.locator("#anonToggle").uncheck();

  const submitted = Number(await counter(teacher, "submitted").innerText());
  const download = teacher.waitForEvent("download");
  await teacher.locator("#exportCsv").click();
  const csv = readFileSync((await (await download).path())!, "utf8");
  const lines = csv.trim().split(/\r?\n/).slice(1).filter(l => l.includes(",Current,"));
  expect(lines).toHaveLength(12);
  expect(lines.filter(l => l.includes(",Submitted,"))).toHaveLength(submitted);
  expect(lines.find(l => l.includes(name("C")))).toContain(",Submitted,0,");
});

test("question performance: formulas, parts under their shared text, and 'No answers' apart from a fraction", async () => {
  await teacher.goto(`/Assignments/Details/${assignmentId}?tab=questions`);
  const pane = teacher.locator("#paneQuestions");
  await expect(pane).toBeVisible();
  await expect(pane.locator("#questionUnit")).toContainText("students");
  await expect(pane.locator(".katex").first()).toBeVisible();                     // formulas rendered
  await expect(pane.locator(".qs-stem")).toHaveCount(1);
  await expect(pane.locator(".qs-part")).toHaveCount(2);
  await expect(pane.locator('.qs-row[data-question="4"] .qs-none')).toHaveText("No answers");
  await expect(pane.locator('.qs-row[data-question="1"] .qs-fraction')).toContainText(/\d+ of \d+ correct/);
  await shot(teacher, "tab-questions");

  await teacher.locator('#detailTabs [data-tab="details"]').click();
  await expect(teacher).toHaveURL(/tab=details/);
  await expect(teacher.locator("#paneDetails")).toContainText("Unlimited");
  await expect(teacher.locator("#paneDetails")).toContainText("Tashkent time (UTC+05:00)");
  await expect(teacher.locator("#lateJoinCard")).toBeVisible();
  await shot(teacher, "tab-details");
});

test("reduced motion: no transitions or animations on the page", async () => {
  const ctx = await browserRef.newContext({ reducedMotion: "reduce" });
  const page = await ctx.newPage();
  await page.goto("/Auth/Login");
  await page.getByLabel("Email").fill(`${stamp}.teacher@example.test`);
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.goto(`/Assignments/Details/${assignmentId}`);
  const icon = row(page, "A").locator(".attempt-toggle i");
  expect(await icon.evaluate(el => getComputedStyle(el).transitionDuration)).toBe("0s");
  await ctx.close();

  expect(await row(teacher, "A").locator(".attempt-toggle i").evaluate(el => getComputedStyle(el).transitionDuration)).toBe("0.15s");
});

test("phone width: each student's status and main button are visible without scrolling sideways", async () => {
  await teacher.setViewportSize({ width: 375, height: 800 });
  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  const c = row(teacher, "C");
  await c.scrollIntoViewIfNeeded();
  await expect(c.locator(".submission-cell")).toBeInViewport({ ratio: 0.5 });
  await expect(c.locator(".main-action")).toBeVisible();
  const box = (await c.locator(".main-action").boundingBox())!;
  expect(box.x + box.width).toBeLessThanOrEqual(375);
  expect(await teacher.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(375 + 1);
  await teacher.setViewportSize({ width: 1360, height: 900 });
});
