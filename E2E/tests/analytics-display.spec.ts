import { expect, test, type Browser, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { assignTo, createClass, createDraftAssignment, signIn, submitAssignment, unique } from "./helpers";

// Class analytics on the first day (work given to 8 of 12, nothing handed in) and once work is graded:
// the cards, the donut, the lists they open and the Questions tab agree. The AI is the deterministic mock.
test.describe.configure({ mode: "serial" });

const mockAi = process.env.E2E_MOCK_AI_URL ?? `http://127.0.0.1:${process.env.E2E_MOCK_AI_PORT ?? 5199}`;
const shots = process.env.E2E_SHOT_DIR;
const password = "Analytics-Pass-123";
const stamp = unique("analytics").replace(/\s/g, "");
const className = unique("E2E Algebra");
const keys = ["A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L"];
const given = keys.slice(0, 8);                 // A–H get the work; I–L are in the class but were not given it
const name = (k: string) => `Pupil ${k} ${stamp.slice(-4)}`;

let browserRef: Browser;
let teacher: Page;
let classId = "";
let inviteCode = "";
let practiceId = "";
let homeworkTitle = "";
const pupils: Record<string, Page> = {};

// Questions: 0 shared stem, 1 = (a) key 4 [1 mark], 2 = (b) key 10 [3 marks], 3 = Q2 key 3 [1 mark].
const PAGE_OF: Record<number, number> = { 1: 0, 2: 0, 3: 3 };

async function mock(path: string, body?: unknown): Promise<void> {
  const r = await fetch(`${mockAi}${path}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body ?? {}) });
  expect(r.ok, `mock AI ${path}`).toBe(true);
}

async function shot(page: Page, file: string): Promise<void> {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  await page.waitForTimeout(600);
  await page.screenshot({ path: resolve(shots, `${file}.png`), fullPage: true });
}

async function register(key: string): Promise<void> {
  const page = await (await browserRef.newContext()).newPage();
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
}

async function handIn(key: string, answers: Record<number, string>): Promise<void> {
  const page = pupils[key];
  await page.goto(`/Student/TakeAssignment?assignmentId=${practiceId}`);
  await expect(page.locator("#assignmentForm")).toBeVisible();
  for (const [index, value] of Object.entries(answers)) {
    await page.locator(`.question-nav .nav-btn[data-idx="${PAGE_OF[Number(index)]}"]`).click();
    await page.locator(`.question-panel[data-qindex="${index}"] .answer-textarea`).first().fill(value);
  }
  await submitAssignment(page);
  await expect(page).toHaveURL(/AssignmentResult/i, { timeout: 30_000 });
  await expect(page.locator("#submittedConfirmation")).toContainText("Submitted.");
}

const analytics = (q = "") => `/Analytics?classId=${classId}${q}`;
const card = (key: string) => teacher.locator(`[data-card="${key}"]`);
const legendCount = (key: string) => teacher.locator(`#statusLegend [data-segment="${key}"] [data-count]`);

test.beforeAll(async ({ browser }) => {
  browserRef = browser;
  teacher = await (await browser.newContext({ viewport: { width: 1360, height: 900 } })).newPage();
  teacher.on("dialog", d => void d.accept());
});

test.afterAll(async () => {
  await teacher.context().close();
  for (const p of Object.values(pupils)) await p.context().close();
});

test("setup: 12 students; practice and homework given to 8 of them", async () => {
  test.setTimeout(300_000);
  await mock("/__reset");
  const email = `${stamp}.teacher@example.test`;
  await teacher.goto("/Auth/Register");
  await teacher.locator('input[name="fullName"]').fill("Analytics Teacher");
  await teacher.locator('input[name="email"]').fill(email);
  await teacher.locator('input[name="password"]').fill(password);
  await teacher.locator('input[name="confirmPassword"]').fill(password);
  await teacher.locator('button[type="submit"]').click();
  await signIn(teacher, email, password);
  inviteCode = await createClass(teacher, className);
  for (const k of keys) await register(k);

  const due = new Date(Date.now() + 7 * 864e5).toISOString().slice(0, 10) + "T17:00";
  const addQuestions = async (id: string, list: Record<string, unknown>[]) => {
    const token = await teacher.locator('input[name="__RequestVerificationToken"]').first().inputValue();
    for (const q of list) {
      const r = await teacher.request.post(`/Assignments/AddQuestion/${id}`, {
        form: { questionJson: JSON.stringify({ difficulty: "Medium", ...q }), __RequestVerificationToken: token }
      });
      expect(r.ok()).toBe(true);
    }
  };

  practiceId = await createDraftAssignment(teacher, `Functions ${stamp}`, className);
  const stem = randomUUID();
  await addQuestions(practiceId, [
    { id: stem, title: "Let $f(x) = x^2$.", type: 0, answerKey: "" },
    { id: randomUUID(), groupId: stem, title: "Find $f(2)$.", type: 0, answerKey: "4" },
    { id: randomUUID(), groupId: stem, title: "Find $f(3) + f(1)$.", type: 0, answerKey: "10", marks: 3 },
    { id: randomUUID(), title: "Solve $2x = 6$.", type: 0, answerKey: "3" }
  ]);
  expect(await assignTo(teacher, practiceId, { students: given.map(name), due })).toContain("Assigned to 8 students");
  await teacher.goto("/Analytics");
  classId = (await teacher.locator("#fClass option", { hasText: className }).getAttribute("value"))!;

  // A homework for the same eight, nobody hands it in during the test.
  homeworkTitle = `Quadratics homework ${stamp}`;
  await teacher.goto("/Assignments/Create");
  await teacher.locator('input[name="title"]').fill(homeworkTitle);
  await teacher.locator("#classSelect").selectOption({ label: className });
  await teacher.locator("#typeSelect").selectOption("1");
  await teacher.locator("#deadlineInput").fill(due);
  await teacher.getByRole("button", { name: "Save and add questions" }).click();
  const homeworkId = teacher.url().match(/\/Assignments\/Edit\/([0-9a-f-]+)/i)![1];
  await addQuestions(homeworkId, [{ id: randomUUID(), title: "Solve $x^2 = 9$.", type: 0, answerKey: "3" }]);
  expect(await assignTo(teacher, homeworkId, { students: given.map(name) })).toContain("Assigned to 8 students");
});

test("day one: work is given, nothing handed in — the page shows who has it and when it is due", async () => {
  await teacher.goto(analytics());
  await expect(card("assigned").locator("[data-value]")).toHaveText("16");
  await expect(card("submitted")).toContainText("0 / 16");
  await expect(card("awaiting").locator("[data-value]")).toHaveText("0");
  await expect(card("average")).toContainText("No graded work yet");
  await expect(legendCount("not-submitted")).toHaveText("16");
  await expect(teacher.locator("#scoreBars")).toContainText("No grades yet");
  await expect(teacher.locator("#teacherActions")).toContainText("0 of 8 submitted");
  await expect(teacher.locator("#teacherActions")).toContainText("due");
  await shot(teacher, "analytics-day-one");

  // Practice only: "Assigned 8 · Submitted 0 of 8", and the 8 recipients one click away.
  await teacher.locator("#fType").selectOption("practice");
  await expect(teacher).toHaveURL(/type=practice/);
  await expect(card("assigned").locator("[data-value]")).toHaveText("8");
  await expect(card("submitted")).toContainText("0 / 8");
  await teacher.locator("#teacherActions a", { hasText: "View 8 students" }).click();
  await expect(teacher.locator("#workCount")).toHaveAttribute("data-count", "8");
  await expect(teacher.locator("[data-work-row]")).toHaveCount(8);
  for (const k of keys.slice(8)) await expect(teacher.locator("#workList")).not.toContainText(name(k));

  // Students: the four who were not given anything say so, and are not counted as missing work.
  await teacher.goto(analytics("&tab=students&type=practice"));
  await expect(teacher.locator("[data-student-row]")).toHaveCount(12);
  await expect(teacher.locator("[data-student-row]", { hasText: "No assignments yet" })).toHaveCount(4);
});

test("after hand-ins: 16 given, 6 submitted (5 graded, 1 awaiting review), 10 not submitted — the same everywhere", async () => {
  test.setTimeout(300_000);
  await handIn("A", { 1: "4", 2: "10", 3: "3" });                           // 100%, no AI needed
  await handIn("B", { 1: "4", 2: "10", 3: "3" });                           // 100%
  await mock("/__script", { answerReply: { correct: false, marks: 2, feedback: "Part right." } });
  await handIn("C", { 1: "4", 2: "9", 3: "3" });                            // (b) 2 of 3 → 80%
  await mock("/__script", { answerReply: { correct: false, marks: 0, feedback: "Not right." } });
  await handIn("D", { 1: "4", 2: "10", 3: "7" });                           // Q2 0 → 80%
  // A real 0%: only Q2 answered, wrongly (the mock marks one item per request, so one AI-marked answer each).
  await handIn("E", { 3: "7" });
  await mock("/__script", { answerStatus: 500 });
  await handIn("F", { 1: "4", 2: "9", 3: "3" });                            // the AI fails on (b): awaiting review
  await mock("/__reset");

  // Marking runs after the hand-in is saved; wait until nothing is left with the AI.
  await expect.poll(async () => {
    await teacher.goto(analytics());
    return (await card("awaiting").locator("[data-value]").innerText()).trim();
  }, { timeout: 60_000 }).toBe("1");

  await expect(card("assigned").locator("[data-value]")).toHaveText("16");
  await expect(card("submitted")).toContainText("6 / 16");
  await expect(card("average")).toContainText("72%");                       // (100+100+80+80+0) / 5
  await expect(card("average")).toContainText("5 graded");
  await expect(legendCount("not-submitted")).toHaveText("10");
  await expect(legendCount("awaiting")).toHaveText("1");
  await expect(legendCount("graded")).toHaveText("5");
  await expect(teacher.locator("#scoreBars")).toContainText("72%");
  await expect(teacher.locator("#scoreBars")).toContainText("No grades yet");  // the homework: no grades ≠ 0%
  await shot(teacher, "analytics-graded");

  // Each count opens a list of exactly that many.
  for (const [segment, count] of [["not-submitted", 10], ["awaiting", 1], ["graded", 5]] as const) {
    await teacher.goto(analytics());
    await teacher.locator(`#statusLegend [data-segment="${segment}"]`).click();
    await expect(teacher.locator("#workCount")).toHaveAttribute("data-count", String(count));
    await expect(teacher.locator("[data-work-row]")).toHaveCount(count);
  }
  await teacher.goto(analytics());
  await card("submitted").click();
  await expect(teacher.locator("[data-work-row]")).toHaveCount(6);
  await shot(teacher, "analytics-work-list");

  // The keyboard reaches a card and opens it.
  await teacher.goto(analytics());
  await card("awaiting").focus();
  await teacher.keyboard.press("Enter");
  await expect(teacher).toHaveURL(/status=awaiting/);
  await expect(teacher.locator("[data-work-row]")).toHaveCount(1);
  await expect(teacher.locator("[data-work-row]")).toContainText(name("F"));
});

test("questions: marks, parts and formulas, how many answers are graded, and each answer one click away", async () => {
  await teacher.goto(analytics(`&tab=questions&questionAssignment=${practiceId}`));
  const b = teacher.locator('[data-question="2"]');
  await expect(b.locator(".q-label")).toHaveText("(b)");
  await expect(b.locator("[data-graded]")).toHaveText("4 of 5 graded");     // E left (b) blank; F's (b) waits for the teacher
  await expect(b).toContainText("of 3");                                     // marks out of 3
  await expect(teacher.locator(".katex").first()).toBeVisible();             // formulas are rendered
  await b.locator(".question-head").click();
  const panel = teacher.locator("#answers-2");
  await expect(panel).toBeVisible();
  await expect(panel.locator("tbody tr")).toHaveCount(5);
  await expect(panel.locator("tr", { hasText: name("F") })).toContainText("needs review");
  await expect(panel.locator("tr", { hasText: name("C") })).toContainText("2 / 3");
  await shot(teacher, "analytics-questions");
});

test("students: Student Work and back keep the filters; another teacher cannot open the class", async () => {
  await teacher.goto(analytics("&tab=students&type=practice"));
  await expect(teacher.locator("[data-student-row]", { hasText: name("E") })).toContainText("0%");
  await teacher.locator("#studentFilter").fill(name("C"));
  await expect(teacher.locator("[data-student-row]:not(.d-none)")).toHaveCount(1);
  await teacher.locator("[data-student-row]:not(.d-none) a.fw-semibold").click();
  await expect(teacher).toHaveURL(/\/Classes\/StudentWork/);
  await teacher.locator("#studentWorkBack").click();
  await expect(teacher).toHaveURL(/\/Analytics\?.*tab=students.*type=practice/);

  // Old links still work.
  await teacher.goto(`/Analytics?classId=${classId}&tab=assessment`);
  await expect(teacher.locator("#fType")).toHaveValue("homework");
  await expect(card("assigned").locator("[data-value]")).toHaveText("8");

  // Another teacher: not found, nothing of the class shown.
  const other = await (await browserRef.newContext()).newPage();
  const email = `${stamp}.other@example.test`;
  await other.goto("/Auth/Register");
  await other.locator('input[name="fullName"]').fill("Other Teacher");
  await other.locator('input[name="email"]').fill(email);
  await other.locator('input[name="password"]').fill(password);
  await other.locator('input[name="confirmPassword"]').fill(password);
  await other.locator('button[type="submit"]').click();
  await signIn(other, email, password);
  for (const tab of ["overview", "students", "questions", "work"]) {
    const r = await other.request.get(`/Analytics?classId=${classId}&tab=${tab}`, { maxRedirects: 0 });
    expect(r.status(), tab).toBe(404);
    expect(await r.text()).not.toContain(name("A"));
  }
  await other.context().close();
});

test("phone width: cards and charts fit, no sideways scrolling", async () => {
  await teacher.setViewportSize({ width: 390, height: 844 });
  await teacher.goto(analytics());
  const overflow = await teacher.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(1);
  await expect(card("submitted")).toBeVisible();
  await expect(teacher.locator("#statusLegend")).toBeVisible();
  await shot(teacher, "analytics-phone");
  await teacher.setViewportSize({ width: 1360, height: 900 });
});
