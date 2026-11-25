import { expect, test, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { createClass, createDraftAssignment, publishAndAssignAll, signIn, submitAssignment, unique } from "./helpers";

// Marks per question ("[4]" on exam papers) and questions the student answers by drawing on a sketch pad.
// The model's verdicts come from the mock AI (scripts/mock-ai.mjs); the application around it is real.
test.describe.configure({ mode: "serial" });

const mockAi = process.env.E2E_MOCK_AI_URL ?? `http://127.0.0.1:${process.env.E2E_MOCK_AI_PORT ?? 5199}`;
const screenshotDir = process.env.E2E_SHOT_DIR;
const password = "Sketch-Pass-123";
const className = unique("E2E Sketch Class");

const diagram = "data:image/svg+xml;base64," + Buffer.from(
  '<svg xmlns="http://www.w3.org/2000/svg" width="400" height="300"><rect width="400" height="300" fill="#ffffff"/>' +
  '<line x1="200" y1="0" x2="200" y2="300" stroke="#000"/><line x1="0" y1="150" x2="400" y2="150" stroke="#000"/>' +
  '<path d="M 60 20 Q 200 150 340 280" stroke="#000" fill="none" stroke-width="2"/><text x="300" y="270">y = f(x)</text></svg>'
).toString("base64");

// Question indexes: 0 shared stem with the diagram, 1 part (a) drawn [1], 2 part (b) typed [4], 3 typed [3].
const STEM = 0, A = 1, B = 2, C = 3;

let teacher: Page;
let student: Page;
let assignmentId = "";
let studentName = "Sketch Student";

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
  await page.screenshot({ path: resolve(screenshotDir, `${name}.png`), fullPage: true });
}

const panel = (i: number) => student.locator(`.question-panel[data-qindex="${i}"]`);
const pad = () => panel(A).locator(".sketch-widget");
const pill = (lead: number) => student.locator(`.question-nav .nav-btn[data-idx="${lead}"]`);

// Drags the mouse across the sketch canvas through points given as fractions of its size.
async function drawOnPad(points: [number, number][]): Promise<void> {
  const canvas = pad().locator("canvas");
  await canvas.scrollIntoViewIfNeeded();
  const box = (await canvas.boundingBox())!;
  const at = ([x, y]: [number, number]) => [box.x + x * box.width, box.y + y * box.height] as const;
  await student.mouse.move(...at(points[0]));
  await student.mouse.down();
  for (const p of points.slice(1)) await student.mouse.move(...at(p), { steps: 8 });
  await student.mouse.up();
}

test.beforeAll(async ({ browser }) => {
  teacher = await (await browser.newContext()).newPage();
  student = await (await browser.newContext({ viewport: { width: 1280, height: 900 } })).newPage();
  student.on("dialog", dialog => void dialog.accept());
});

test.afterAll(async () => {
  await teacher.context().close();
  await student.context().close();
});

test("setup: accounts and an assignment with marks and a drawing part", async () => {
  test.setTimeout(240_000);
  const stamp = unique("sketch").replace(/\s/g, "");
  const teacherEmail = `${stamp}.teacher@example.test`;
  const studentEmail = `${stamp}.student@example.test`;

  await teacher.goto("/Auth/Register");
  await teacher.locator('input[name="fullName"]').fill("Sketch Teacher");
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
  await student.locator('input[name="fullName"]').fill(studentName);
  await student.locator('input[name="email"]').fill(studentEmail);
  await student.locator('input[name="password"]').fill(password);
  await student.locator('input[name="confirmPassword"]').fill(password);
  await student.locator('button[type="submit"]').click();
  await expect(student).toHaveURL(/\/Auth\/Login/);
  await signIn(student, studentEmail, password);
  await expect(student).toHaveURL(/\/Student\/?$/);

  assignmentId = await createDraftAssignment(teacher, unique("E2E Sketch"), className);

  const token = await teacher.locator('input[name="__RequestVerificationToken"]').first().inputValue();
  const add = async (question: Record<string, unknown>) => {
    const response = await teacher.request.post(`/Assignments/AddQuestion/${assignmentId}`, {
      form: { questionJson: JSON.stringify({ difficulty: "Medium", ...question }), __RequestVerificationToken: token }
    });
    expect(response.ok(), "AddQuestion").toBe(true);
  };

  const stemId = randomUUID();
  await add({ id: stemId, title: "The diagram shows the graph of $y = f(x)$.", type: 0, answerKey: "", imagePath: diagram });
  await add({ id: randomUUID(), groupId: stemId, title: "On this diagram sketch the graph of $y = f^{-1}(x)$.", type: 3,
              answerKey: "Reflection of the curve in the line y = x", marks: 1, requireDrawing: true });
  await add({ id: randomUUID(), groupId: stemId, title: "Solve $2x - 1 = 7$.", type: 0, answerKey: "4", marks: 4 });
  await add({ id: randomUUID(), title: "Find the value of $k$.", type: 0, answerKey: "7", marks: 3 });
});

test("teacher sees marks, the total, and can switch the drawing answer on and off", async () => {
  await teacher.goto(`/Assignments/Edit/${assignmentId}`);
  await expect(teacher.getByText("Total: 8 marks")).toBeVisible();

  const card = (i: number) => teacher.locator(`.question-card[data-index="${i}"]`);
  await expect(card(A)).toContainText("[1 mark]");
  await expect(card(A)).toContainText("Drawing");
  await expect(card(B)).toContainText("[4 marks]");
  await expect(card(C)).toContainText("[3 marks]");
  await expect(card(STEM)).not.toContainText("mark");                       // a shared stem carries no marks
  await expect(card(STEM).locator(`#requireDrawing_${STEM}`)).toHaveCount(0);
  await expect(card(A).locator(`#requireDrawing_${A}`)).toBeChecked();

  // The switch round-trips through the server and keeps the other fields.
  await card(C).locator(`#requireDrawing_${C}`).check();
  await expect(card(C).locator(`#requireDrawing_${C}`)).toBeChecked();
  await expect(card(C)).toContainText("Drawing");
  await card(C).locator(`#requireDrawing_${C}`).uncheck();
  await expect(card(C).locator(`#requireDrawing_${C}`)).not.toBeChecked();
  await expect(card(C)).toContainText("[3 marks]");

  // The edit drawer shows and saves marks.
  await card(C).locator('button[title="Edit"]').click();
  await expect(teacher.locator("#eq_marks")).toHaveValue("3");
  await expect(teacher.locator("#eq_requireDrawing")).not.toBeChecked();
  await teacher.locator("#eq_marks").fill("2");
  await teacher.locator("#editPanel .btn-primary").click();
  await expect(card(C)).toContainText("[2 marks]");
  await expect(teacher.getByText("Total: 7 marks")).toBeVisible();
  await shot(teacher, "sketch-editor");

  // "Preview as student" shows the same sketch pad, over the shared diagram, and it can be drawn on.
  await teacher.goto(`/Assignments/Preview/${assignmentId}`);
  const previewPad = teacher.locator(`.question-panel[data-qindex="${A}"] .sketch-widget`);
  await expect(teacher.locator(`.question-panel[data-qindex="${A}"] .question-header`)).toContainText("Draw your answer");
  await expect(previewPad).toBeVisible();
  await expect.poll(() => previewPad.locator("canvas").evaluate(c => (c as HTMLCanvasElement).height)).toBe(600);
  await previewPad.locator("canvas").scrollIntoViewIfNeeded();
  const box = (await previewPad.locator("canvas").boundingBox())!;
  await teacher.mouse.move(box.x + box.width * 0.2, box.y + box.height * 0.8);
  await teacher.mouse.down();
  await teacher.mouse.move(box.x + box.width * 0.8, box.y + box.height * 0.2, { steps: 8 });
  await teacher.mouse.up();
  await expect(previewPad.locator(".sketch-undo")).toBeEnabled();
  await teacher.goto(`/Assignments/Edit/${assignmentId}`);

  await publishAndAssignAll(teacher, assignmentId);
});

test("student draws on the diagram; the drawing survives a reload and can be erased and undone", async () => {
  await mock("/__reset");
  await student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);

  await expect(panel(A).locator(".question-header")).toContainText("Draw your answer");
  await expect(panel(A).locator(".question-header")).toContainText("[1 mark]");
  await expect(panel(B).locator(".question-header")).toContainText("[4 marks]");
  await expect(pad()).toBeVisible();
  await expect(panel(B).locator(".sketch-widget")).toHaveCount(0);
  await expect(panel(A).locator(".check-answer-btn")).toHaveCount(0);    // drawings are marked after submitting

  // The pad takes the diagram's proportions (400×300 → 800×600) and starts empty.
  await expect.poll(() => pad().locator("canvas").evaluate(c => (c as HTMLCanvasElement).height)).toBe(600);
  await expect(student.locator(`#sketchpng_${A}`)).toHaveValue("");
  await expect(pad().locator(".sketch-undo")).toBeDisabled();

  await drawOnPad([[0.1, 0.85], [0.3, 0.6], [0.5, 0.5], [0.8, 0.1]]);
  await expect(student.locator(`#sketchpng_${A}`)).toHaveValue(/^data:image\/png;base64,/);
  await expect(pad().locator(".sketch-undo")).toBeEnabled();

  // A straight line in red as a second stroke.
  await pad().locator('.sketch-tool[data-tool="line"]').click();
  await pad().locator('.sketch-color[data-color="#dc2626"]').click();
  await drawOnPad([[0.05, 0.05], [0.95, 0.95]]);

  // Both strokes are kept in the draft and come back after a reload.
  await student.reload();
  await expect.poll(async () => JSON.parse(await student.evaluate(i => (window as any).QuizAnswers.get(`sketch_${i}`), A) || "[]").length).toBe(2);
  await expect(student.locator(`#sketchpng_${A}`)).toHaveValue(/^data:image\/png;base64,/);

  // The eraser removes whole strokes and never the diagram; undo brings a stroke back.
  await pad().locator('.sketch-tool[data-tool="eraser"]').click();
  await drawOnPad([[0.48, 0.48], [0.52, 0.52]]);                         // crosses both strokes
  await expect.poll(async () => (await student.evaluate(i => (window as any).QuizAnswers.get(`sketch_${i}`), A)) || "").toBe("");
  await expect(student.locator(`#sketchpng_${A}`)).toHaveValue("");
  await pad().locator(".sketch-undo").click();
  await expect.poll(async () => JSON.parse(await student.evaluate(i => (window as any).QuizAnswers.get(`sketch_${i}`), A) || "[]").length).toBe(2);
  await expect(student.locator(`#sketchpng_${A}`)).toHaveValue(/^data:image\/png;base64,/);
  await shot(student, "sketch-pad");
});

test("submitting marks the drawing from the picture and weighs every question by its marks", async () => {
  test.setTimeout(120_000);
  await mock("/__script", {
    sketchReply: { correct: true, marks: 1, confidence: 0.9, feedback: "The curve is reflected in y = x." },
    answerReply: { correct: false, marks: 1, feedback: "The method is right but the value is wrong." }
  });

  await panel(B).locator(".answer-textarea").first().fill("4");                     // matches the key: 4 of 4
  await student.locator(`.question-nav .nav-btn[data-idx="${C}"]`).click();
  await panel(C).locator(".answer-textarea").first().fill("5");                     // AI gives 1 of 2
  await expect(pill(STEM)).toHaveClass(/answered/);                                 // the drawing counts as an answer

  await submitAssignment(student);
  await expect(student).toHaveURL(/AssignmentResult/, { timeout: 60_000 });

  // What the model was asked: the shared text and the part, the marks, and the picture.
  const sketchCalls = await seen("sketch");
  expect(sketchCalls).toHaveLength(1);
  expect(sketchCalls[0].user).toContain("The diagram shows the graph");
  expect(sketchCalls[0].user).toContain("f^{-1}(x)");
  expect(sketchCalls[0].user).toContain("Marks available: 1");
  const answerCalls = await seen("answer");
  expect(answerCalls).toHaveLength(1);                                             // only the answer the key could not decide
  expect(answerCalls[0].user).toContain("Marks available: 2");

  // A drawing always waits for the teacher: the student sees the hand-in confirmed and their own drawing, and no
  // marks until the result is available. (The 86% — (1 + 4 + 1) of 7 marks — is checked on the teacher's page.)
  await expect(student.locator("#awaitingReview")).toContainText("Awaiting review");
  await expect(student.locator('img[alt^="Your drawing"]')).toBeVisible();
  await expect(student.locator("[data-score]")).toHaveCount(0);
  await shot(student, "sketch-result");
});

test("teacher sees the drawing flagged for review and marks out of each question's marks", async () => {
  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  const href = await teacher.locator('a[href*="/Assignments/SubmissionDetail"]').first().getAttribute("href");
  await teacher.goto(href!);

  const drawing = teacher.locator('img[alt="Student\'s drawing"]');
  await expect(drawing).toBeVisible();
  const src = await drawing.getAttribute("src");
  expect(src).toMatch(/^\/uploads\/submissions\/[0-9a-f-]+\/[0-9a-f-]+\.png$/);
  const file = await teacher.request.get(src!);
  expect(file.status()).toBe(200);
  expect(file.headers()["content-type"]).toContain("image/png");

  await expect(teacher.getByText("Needs your review").first()).toBeVisible();
  await expect(teacher.getByText("86%").first()).toBeVisible();                       // (1 + 4 + 1) of 7 marks
  await expect(teacher.getByText("the AI's mark is only a first guess")).toBeVisible();
  await expect(teacher.locator(`.grade-points[data-idx="${B}"]`)).toHaveAttribute("data-max", "4");
  await expect(teacher.locator(`.grade-points[data-idx="${B}"]`)).toHaveValue("4");
  await expect(teacher.getByText("Marks (out of 2)")).toBeVisible();

  // More marks than the question is worth are refused by the form; within range they are saved.
  const marksC = teacher.locator(`.grade-points[data-idx="${C}"]`);
  await marksC.fill("9");
  expect(await marksC.evaluate(el => (el as HTMLInputElement).validity.valid)).toBe(false);
  await marksC.fill("1.5");
  await teacher.getByRole("button", { name: /Save grades/i }).click();
  await expect(teacher.getByText("Grades saved.")).toBeVisible();
  await expect(teacher.locator(`.grade-points[data-idx="${C}"]`)).toHaveValue("1.5");
  await shot(teacher, "sketch-teacher-review");
});

test("a question with parts can be added in one step and needs no shared text", async () => {
  const id = await createDraftAssignment(teacher, unique("E2E Parts Only"), className);

  await teacher.getByRole("button", { name: "Add questions" }).first().click();
  await teacher.locator("#addWithPartsForm select[name='parts']").selectOption("2");
  await teacher.locator("#addWithPartsForm").getByRole("button", { name: "Add" }).click();
  await expect(teacher.locator(".question-card")).toHaveCount(3);
  await expect(teacher.locator('.question-card[data-index="0"]')).toContainText("No shared text");
  await expect(teacher.locator('.question-card[data-index="1"]')).toContainText("Q1a");
  await expect(teacher.locator('.question-card[data-index="2"]')).toContainText("Q1b");

  // The parent saves with no text; each part gets its own wording and marks.
  const edit = async (index: number, title: string, marks?: string) => {
    await teacher.locator(`.question-card[data-index="${index}"] button[title="Edit"]`).click();
    await teacher.locator("#eq_title").fill(title);
    if (marks) await teacher.locator("#eq_marks").fill(marks);
    await teacher.locator("#editPanel .btn-primary").click();
    await teacher.waitForLoadState("load");
  };
  await edit(0, "");
  await expect(teacher.locator("#eq_saveError")).toBeHidden();
  await edit(1, "The graph of y = f(x) is transformed to the graph of y = 2f(x - 1). Describe fully the two transformations.", "3");
  await edit(2, "The curve y = sin 2x - 5x is reflected in the y-axis. Write down the equation of the transformed curve.", "2");

  await expect(teacher.locator('.question-card[data-index="1"]')).toContainText("[3 marks]");
  await expect(teacher.locator('.question-card[data-index="2"]')).toContainText("[2 marks]");
  await expect(teacher.getByText("Total: 5 marks")).toBeVisible();

  // The student sees "Question 1" with parts 1a and 1b, and part (b) carries none of part (a)'s text.
  await teacher.goto(`/Assignments/Preview/${id}`);
  const part = (i: number) => teacher.locator(`.question-panel[data-qindex="${i}"]`);
  await expect(teacher.locator(".question-nav .nav-btn[data-idx]")).toHaveCount(1);
  await expect(part(0).locator(".question-header")).toContainText("Question 1");
  await expect(part(0).locator(".answer-textarea")).toHaveCount(0);                 // nothing to answer on the parent
  await expect(part(1).locator(".question-header")).toContainText("Question 1a");
  await expect(part(1).locator(".question-header")).toContainText("[3 marks]");
  await expect(part(2).locator(".question-header")).toContainText("Question 1b");
  await expect(part(2).locator(".question-header")).toContainText("[2 marks]");
  await expect(part(2)).not.toContainText("2f(x - 1)");
  await expect(part(0)).not.toContainText("2f(x - 1)");
  await shot(teacher, "parts-only-preview");
});

test("switching a question's options keeps the editor where the teacher was, not at the top", async () => {
  // A short window so the last card sits well below the top of the page.
  await teacher.setViewportSize({ width: 1280, height: 500 });
  await teacher.goto("/Assignments");
  const href = await teacher.locator('a[href*="/Assignments/Edit/"]').first().getAttribute("href");
  await teacher.goto(href!);

  const last = teacher.locator(".question-card").last();
  const index = await last.getAttribute("data-index");
  await last.scrollIntoViewIfNeeded();
  const before = await teacher.evaluate(() => window.scrollY);
  expect(before).toBeGreaterThan(200);

  const toggle = teacher.locator(`#requireWork_${index}`);
  const wasOn = await toggle.isChecked();
  await Promise.all([teacher.waitForNavigation(), toggle.click()]);
  await expect(teacher.locator(`#requireWork_${index}`)).toBeChecked({ checked: !wasOn });

  await expect.poll(() => teacher.evaluate(() => window.scrollY)).toBeGreaterThan(before - 80);
  await expect(teacher.locator(".question-card").last()).toBeInViewport();

  // Opening the editor afresh from elsewhere still starts at the top.
  await teacher.goto("/Assignments");
  await teacher.goto(href!);
  expect(await teacher.evaluate(() => window.scrollY)).toBe(0);
  await teacher.setViewportSize({ width: 1280, height: 720 });
});
