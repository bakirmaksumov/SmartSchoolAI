import { expect, test, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { addQuestion, createClass, createDraftAssignment, publishAndAssignAll, signIn, submitAssignment, unique } from "./helpers";

// The waiting games and the "Type solution" step editor. As in tutor-ocr-display.spec.ts the AI is the scripted
// mock (scripts/mock-ai.mjs), but everything else is the real application: the browser code, the controllers, the
// background grading job and its polling. Runs on its own synthetic accounts, so it also works against a container.
test.describe.configure({ mode: "serial" });

const mockAi = process.env.E2E_MOCK_AI_URL ?? `http://127.0.0.1:${process.env.E2E_MOCK_AI_PORT ?? 5199}`;
const fixtures = resolve(process.cwd(), "fixtures");
const screenshotDir = process.env.E2E_SHOT_DIR;
const password = "Games-Pass-123";
const className = unique("E2E Games Class");

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

async function evalRequests(): Promise<{ user: string }[]> {
  return (await fetch(`${mockAi}/__requests?kind=eval`)).json() as Promise<{ user: string }[]>;
}

async function shot(page: Page, name: string): Promise<void> {
  if (!screenshotDir) return;
  mkdirSync(screenshotDir, { recursive: true });
  await page.screenshot({ path: resolve(screenshotDir, `${name}.png`) });
}

const forceGame = (name: string) => student.evaluate(game => (window as any).WaitingGame.setNextGame(game), name);

async function openAssignment(): Promise<void> {
  await student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
}

// Opens the "Solve with photo" dialog for a question and starts reading the fixture photo.
async function startPhotoReading(questionIndex: number): Promise<void> {
  await student.locator(`.nav-btn[data-idx="${questionIndex}"]`).click();
  const block = student.locator(".work-block").nth(questionIndex);
  await block.locator(`#workToggle_${questionIndex}`).check();
  await block.locator('.work-tab-btn[data-work-tab="upload"]').click();

  // The dialog resets itself once it has fully opened, so wait for that before choosing the file.
  await student.evaluate(() => {
    const w = window as unknown as { __hwShown: boolean };
    w.__hwShown = false;
    document.getElementById("hwModal")!.addEventListener("shown.bs.modal", () => { w.__hwShown = true; }, { once: true });
  });
  await block.locator(`[data-hw-open][data-qindex="${questionIndex}"]`).click();
  await student.waitForFunction(() => (window as unknown as { __hwShown: boolean }).__hwShown);

  await student.locator("#hwFileInput").setInputFiles(resolve(fixtures, "solution-correct.jpg"));
  await student.locator("#hwCropUseFullBtn").click();
  await expect(student.locator("#hwAnalyzeBtn")).toBeEnabled();
  await student.locator("#hwAnalyzeBtn").click();
}

// Shows the step editor of a question and returns its inputs.
async function openStepEditor(questionIndex: number) {
  await student.locator(`.nav-btn[data-idx="${questionIndex}"]`).click();
  const toggle = student.locator(`#workToggle_${questionIndex}`);
  if (!(await toggle.isChecked())) await toggle.check();
  await student.locator(".work-block").nth(questionIndex).locator('.work-tab-btn[data-work-tab="type"]').click();
  return student.locator(`.type-steps-list[data-qindex="${questionIndex}"] .type-step-input`);
}

const checkButton = (questionIndex: number) => student.locator(`.type-step-check[data-qindex="${questionIndex}"]`);
const gameModal = () => student.locator("#waitingGameModal");

test.beforeAll(async ({ browser }) => {
  teacher = await (await browser.newContext()).newPage();
  student = await (await browser.newContext({ viewport: { width: 1280, height: 800 } })).newPage();
});

test.afterAll(async () => {
  await teacher.context().close();
  await student.context().close();
});

test("setup: synthetic teacher and student, an assignment with two questions", async () => {
  test.setTimeout(240_000);
  const stamp = unique("games").replace(/\s/g, "");
  const teacherEmail = `${stamp}.teacher@example.test`;
  const studentEmail = `${stamp}.student@example.test`;

  await teacher.goto("/Auth/Register");
  await teacher.locator('input[name="fullName"]').fill("Games Teacher");
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
  await student.locator('input[name="fullName"]').fill("Games Student");
  await student.locator('input[name="email"]').fill(studentEmail);
  await student.locator('input[name="password"]').fill(password);
  await student.locator('input[name="confirmPassword"]').fill(password);
  await student.locator('button[type="submit"]').click();
  await expect(student).toHaveURL(/\/Auth\/Login/);
  await signIn(student, studentEmail, password);
  await expect(student).toHaveURL(/\/Student\/?$/);

  assignmentId = await createDraftAssignment(teacher, unique("E2E Games"), className);
  await addQuestion(teacher, { kind: "Text/Numeric", text: "Describe the transformation that maps y = f(x) onto y = g(x).", answerKey: "stretch" });
  await addQuestion(teacher, { kind: "Text/Numeric", text: "Solve the equation 4x - 3 = 2 and give every solution.", answerKey: "x = 5/4" });

  // "Check my work" is offered when instant feedback is on.
  await teacher.goto(`/Assignments/Edit/${assignmentId}?tab=settings`);
  const instant = teacher.locator('#settingsForm input[name="showInstantFeedback"], #settingsForm input[name="ShowsInstantFeedback"], #settingsInstantFeedback');
  if (await instant.count()) await instant.first().check();
  await teacher.locator('#settingsForm button[type="submit"]').click();
  await expect(teacher).toHaveURL(/\/Assignments\/?(\?.*)?$/);

  await teacher.goto(`/Assignments/Edit/${assignmentId}`);
  await publishAndAssignAll(teacher, assignmentId);
});

// ── 1. Reading the photo: the game starts by itself ──────────────────────────
test("while the photo is read, tic-tac-toe has already made its move; the editor opens without any play", async () => {
  test.setTimeout(120_000);
  await mock("/__reset");
  await mock("/__script", { ocrDelayMs: 5000, ocr: { detectedProblem: "", steps: [{ number: 1, text: "$x = 5/4$" }] } });

  await openAssignment();
  await forceGame("tictactoe");
  await startPhotoReading(1);

  const board = student.locator("#hwWaitBoard");
  await expect(student.locator("#hwStage-loading")).toBeVisible();
  await expect(board.locator(".wg-o"), "the computer's O is already on the board").toHaveCount(1);
  await expect(board.locator(".wg-x")).toHaveCount(0);
  await expect(student.locator("#hwWaitStatus")).toContainText("Computer played O");
  await shot(student, "game-ttt");

  // A move whose reply is still pending must not land in a restarted round.
  await board.locator(".wg-cell:not(:disabled)").first().click();
  await expect(student.locator("#hwWaitStatus")).toContainText("thinking");
  await student.locator("#hwWaitNewGame").click();
  await student.waitForTimeout(700);
  await expect(board.locator(".wg-o"), "one fresh O, no stale reply").toHaveCount(1);
  await expect(board.locator(".wg-x")).toHaveCount(0);

  // Nobody has clicked anything else: the OCR answer opens the editor on its own, and the game is torn down.
  await expect(student.locator("#hwStage-edit")).toBeVisible({ timeout: 30_000 });
  await expect(board.locator(".wg-cell")).toHaveCount(0);
  await expect(student.locator("#hwStepsList .hw-step-input").first()).toHaveValue("x = 5/4");
});

// ── 2. Checking steps: the other two games, rotation, opening the result at any time ─────
test("rock-paper-scissors: the computer has chosen before the student, and the choice stays hidden", async () => {
  test.setTimeout(120_000);
  await mock("/__reset");
  await mock("/__script", { evalDelayMs: 5000 });

  await openAssignment();
  const steps = await openStepEditor(1);
  await steps.first().fill("4x = 5");
  await forceGame("rps");
  await checkButton(1).click();

  await expect(gameModal()).toBeVisible();
  await expect(gameModal().locator(".wg-game-status")).toContainText("Computer has chosen");
  await expect(gameModal().locator(".wg-rps-pick"), "the choice is hidden").toHaveText("?");
  await student.waitForTimeout(450);   // let the modal finish fading in
  await shot(student, "game-rps");

  await gameModal().locator('.wg-rps-btn[data-move="paper"]').click();
  await expect(gameModal().locator(".wg-rps-pick")).not.toHaveText("?");
  await expect(gameModal().locator(".wg-game-status")).toContainText(/win|tie/i);

  // "New game" restarts the same game with a fresh hidden choice.
  await gameModal().locator(".wg-newgame-btn").click();
  await expect(gameModal().locator(".wg-rps-pick")).toHaveText("?");
  await expect(gameModal().locator(".wg-board")).toHaveAttribute("data-game", "rps");

  await expect(gameModal().locator(".wg-reveal-btn")).toBeVisible({ timeout: 30_000 });
  await gameModal().locator(".wg-reveal-btn").click();
  await expect(student.locator(`.type-step-results[data-qindex="1"] .hw-result-step`).first()).toBeVisible();
});

test("repeat-the-sequence: the result can be opened in the middle of the round and the game stops", async () => {
  test.setTimeout(120_000);
  await mock("/__reset");
  await mock("/__script", { evalDelayMs: 600 });

  await openAssignment();
  const steps = await openStepEditor(1);
  await steps.first().fill("4x = 5");
  await forceGame("sequence");
  await checkButton(1).click();

  await expect(gameModal().locator(".wg-board")).toHaveAttribute("data-game", "sequence");
  await expect(gameModal().locator(".wg-game-status")).toContainText("Watch the sequence");
  await expect(gameModal().locator(".wg-pad")).toHaveCount(4);
  await student.waitForTimeout(450);   // let the modal finish fading in
  await shot(student, "game-sequence");

  // The verdict arrives while the computer is still showing the sequence: the way out is offered at once.
  await expect(gameModal().locator(".wg-reveal-btn")).toBeVisible({ timeout: 15_000 });
  await expect(gameModal().locator(".wg-game-status")).toContainText("Watch the sequence");
  await gameModal().locator(".wg-reveal-btn").click();

  await expect(gameModal()).toBeHidden();
  await expect(student.locator(`.type-step-results[data-qindex="1"] .hw-result-step`).first()).toBeVisible();
  // Closing the window ended the round: nothing is left on the board for a stale timer to change.
  expect(await student.locator("#waitingGameModal .wg-board").evaluate(el => el.children.length)).toBe(0);
});

test("restarting the sequence game starts a clean round that can be completed", async () => {
  test.setTimeout(120_000);
  await mock("/__reset");
  await mock("/__script", { evalDelayMs: 9000 });

  await openAssignment();
  const steps = await openStepEditor(1);
  await steps.first().fill("4x = 5");
  await forceGame("sequence");
  await checkButton(1).click();
  await expect(gameModal().locator(".wg-pad")).toHaveCount(4);

  // Let the first flash happen, then restart mid-show and count the flashes of the new round only.
  await student.waitForTimeout(1000);
  await student.evaluate(() => {
    const w = window as unknown as { __lit: number[] };
    w.__lit = [];
    const board = document.querySelector("#waitingGameModal .wg-board")!;
    new MutationObserver(records => {
      for (const r of records) {
        const el = r.target as HTMLElement;
        if (el.classList?.contains("wg-pad") && el.classList.contains("wg-lit") && !(r.oldValue ?? "").includes("wg-lit")) {
          w.__lit.push(Number(el.dataset.pad));
        }
      }
    }).observe(board, { subtree: true, attributes: true, attributeFilter: ["class"], attributeOldValue: true });
  });
  await gameModal().locator(".wg-newgame-btn").click();

  await expect(gameModal().locator(".wg-game-status")).toContainText("Your turn", { timeout: 10_000 });
  const board = gameModal().locator(".wg-board");
  const sequence = (await board.getAttribute("data-sequence"))!.split(",").map(Number);
  const flashed = await student.evaluate(() => (window as unknown as { __lit: number[] }).__lit);
  expect(flashed, "only the new round was shown").toEqual(sequence);

  for (const pad of sequence) await gameModal().locator(`.wg-pad[data-pad="${pad}"]`).click();
  await expect(gameModal().locator(".wg-game-status")).toContainText("You remembered it all");

  await expect(gameModal().locator(".wg-reveal-btn")).toBeVisible({ timeout: 30_000 });
  await gameModal().locator(".wg-reveal-btn").click();
  await expect(gameModal()).toBeHidden();
});

test("the next wait never repeats the previous game, and all three games come up", async () => {
  await openAssignment();
  const picks = await student.evaluate(() => {
    const board = document.createElement("div");
    document.body.appendChild(board);
    const host = (window as any).WaitingGame.attach(board, null);
    const seen: string[] = [];
    for (let i = 0; i < 80; i++) { host.newGame(); seen.push(board.dataset.game!); }
    host.stop();
    board.remove();
    return seen;
  });
  for (let i = 1; i < picks.length; i++) expect(picks[i], `wait ${i} repeated the game`).not.toBe(picks[i - 1]);
  expect(new Set(picks)).toEqual(new Set(["tictactoe", "rps", "sequence"]));
});

test("steps saved by an older version ($…$ LaTeX) show as readable text and keep their meaning", async () => {
  test.setTimeout(120_000);
  await mock("/__reset");
  await mock("/__script", { evalDelayMs: 0 });

  // The page keeps its draft in localStorage under "quizDraft:<assignmentId>". Seed the format an older version
  // wrote (LaTeX in "$…$", one step per line) and reload, as a returning student would.
  const old = "$y_{1,2} = \\pm 2$\n$x_1 = 2.5 \\quad x_2 = \\frac{1}{2}$";
  await openAssignment();
  await student.evaluate(value => {
    const id = (document.getElementById("assignmentId") as HTMLInputElement).value;
    localStorage.setItem(`quizDraft:${id}`, JSON.stringify({ work_1: value }));
  }, old);
  await student.reload();
  const steps = await openStepEditor(1);
  await expect(steps.nth(0)).toHaveValue("y₁,₂ = ±2");
  await expect(steps.nth(1)).toHaveValue("x₁ = 2.5   x₂ = 1/2");

  // Untouched, they go to the grader as the original LaTeX; an edited step goes as typed.
  await steps.nth(1).fill("x₁ = 5/2   x₂ = 1/2");
  await checkButton(1).click();
  await expect(gameModal().locator(".wg-reveal-btn")).toBeVisible({ timeout: 30_000 });
  const sent = (await evalRequests()).at(-1)!.user.split("Student's steps:")[1].trim().split("\n");
  expect(sent[0]).toBe("1. $y_{1,2} = \\pm 2$");
  expect(sent[1]).toBe("2. x₁ = 5/2   x₂ = 1/2");
  await gameModal().locator(".wg-reveal-btn").click();
});

// ── 3. Phone width ────────────────────────────────────────────────────────────
test("at 375px the waiting screen and the step editor fit the screen", async () => {
  test.setTimeout(120_000);
  await mock("/__reset");
  await mock("/__script", { ocrDelayMs: 6000, evalDelayMs: 6000, ocr: { detectedProblem: "", steps: [{ number: 1, text: "$x = 1$" }] } });

  await student.setViewportSize({ width: 375, height: 740 });
  await openAssignment();
  const pageOverflowBefore = await student.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);

  const fits = (selector: string) => student.evaluate(sel => {
    const el = document.querySelector(sel)!;
    const r = el.getBoundingClientRect();
    return { left: r.left, right: r.right, overflow: el.scrollWidth - el.clientWidth, width: window.innerWidth };
  }, selector);

  // The embedded waiting screen of "Solve with photo", with the widest game.
  await forceGame("rps");
  await startPhotoReading(1);
  await expect(student.locator("#hwStage-loading")).toBeVisible();
  await expect(student.locator("#hwWaitBoard .wg-rps-btn")).toHaveCount(3);
  for (const selector of ["#hwModal .modal-content", "#hwWaitBoard"]) {
    const box = await fits(selector);
    expect(box.left, `${selector} left`).toBeGreaterThanOrEqual(0);
    expect(box.right, `${selector} right`).toBeLessThanOrEqual(box.width);
    expect(box.overflow, `${selector} scrolls sideways`).toBeLessThanOrEqual(0);
  }
  await shot(student, "game-mobile");
  await expect(student.locator("#hwStage-edit")).toBeVisible({ timeout: 30_000 });
  await student.locator("#hwModal .btn-close").click();
  await expect(student.locator("#hwModal")).toBeHidden();

  // The step editor: every field and the symbol bar stay on screen.
  const steps = await openStepEditor(1);
  await steps.first().fill("Stretch by factor 2 parallel to the y-axis, then translate by (4, −9) and check every value");
  for (let i = 0; i < (await steps.count()); i++) {
    const box = (await steps.nth(i).boundingBox())!;
    expect(box.x + box.width, `step field ${i + 1}`).toBeLessThanOrEqual(375);
  }
  const bar = await fits('.work-pane[data-work-pane="type"] .math-toolbar');
  expect(bar.right).toBeLessThanOrEqual(bar.width);
  await shot(student, "steps-mobile");

  // And the check-answer waiting window, with the sequence game.
  await forceGame("sequence");
  await checkButton(1).click();
  await expect(gameModal().locator(".wg-pad")).toHaveCount(4);
  for (const selector of ["#waitingGameModal .modal-content", "#waitingGameModal .wg-board"]) {
    const box = await fits(selector);
    expect(box.right, `${selector} right`).toBeLessThanOrEqual(box.width);
    expect(box.overflow, `${selector} scrolls sideways`).toBeLessThanOrEqual(0);
  }
  expect(await student.evaluate(() => document.documentElement.scrollWidth - window.innerWidth), "the page got wider")
    .toBeLessThanOrEqual(Math.max(0, pageOverflowBefore));
  await expect(gameModal().locator(".wg-reveal-btn")).toBeVisible({ timeout: 30_000 });
  await gameModal().locator(".wg-reveal-btn").click();
});

// ── 4. Typing steps as plain text — last, because the final submission uses up the attempt ─────────────────────────────────────────────
const stepOne = "Translate by (4, −9)";
const stepTwo = "Stretch by factor 2 parallel to the y-axis";
const stepThree = "x = 2.5 or x = 1/2";

test("steps are typed as plain text, survive a reload, and are checked and submitted without $…$", async () => {
  test.setTimeout(180_000);
  await mock("/__reset");
  await mock("/__script", { evalDelayMs: 0 });

  await student.setViewportSize({ width: 1280, height: 800 });
  await openAssignment();
  let steps = await openStepEditor(0);
  await expect(student.locator('.type-steps-list[data-qindex="0"] math-field'), "no formula editor in the steps").toHaveCount(0);

  await steps.nth(0).fill(stepOne);
  await student.locator('.type-step-add[data-qindex="0"]').click();
  await student.locator('.type-steps-list[data-qindex="0"] .type-step-input').nth(1).fill(stepTwo);
  await student.locator('.type-step-add[data-qindex="0"]').click();
  await student.locator('.type-steps-list[data-qindex="0"] .type-step-input').nth(2).fill(stepThree);

  // The symbol bar writes readable symbols at the caret.
  const third = student.locator('.type-steps-list[data-qindex="0"] .type-step-input').nth(2);
  await third.click();
  await third.press("End");
  await student.locator('.work-pane[data-work-pane="type"] .math-toolbar button[title="Degree"]').first().click();
  await expect(third).toHaveValue(`${stepThree}°`);
  await third.fill(stepThree);

  // Reload: the draft comes back, spaces and symbols exactly as typed.
  await student.reload();
  steps = student.locator('.type-steps-list[data-qindex="0"] .type-step-input');
  await expect(steps).toHaveCount(3);
  await expect(steps.nth(0)).toHaveValue(stepOne);
  await expect(steps.nth(1)).toHaveValue(stepTwo);
  await expect(steps.nth(2)).toHaveValue(stepThree);

  // Check my work: the grader receives the plain text, one numbered line per step.
  await checkButton(0).click();
  await expect(gameModal().locator(".wg-reveal-btn")).toBeVisible({ timeout: 30_000 });
  await gameModal().locator(".wg-reveal-btn").click();

  const sent = (await evalRequests()).at(-1)!.user;
  const lines = sent.split("Student's steps:")[1].trim().split("\n");
  expect(lines).toEqual([`1. ${stepOne}`, `2. ${stepTwo}`, `3. ${stepThree}`]);
  expect(sent.split("Student's steps:")[1], "no $…$ wrapping").not.toContain("$");

  const results = student.locator('.type-step-results[data-qindex="0"]');
  await expect(results).toContainText(`Step 2: ${stepTwo}`);
  await expect(results).toContainText(`Step 3: ${stepThree}`);
  expect(await results.innerText()).not.toContain("$");

  // Final submission keeps the working as typed.
  await submitAssignment(student);
  await expect(student).toHaveURL(/\/Student\/AssignmentResult/i);
  const page = await student.locator("body").innerText();
  expect(page).toContain(stepOne);
  expect(page).toContain(stepTwo);
  expect(page).toContain(stepThree);
  expect(page).not.toMatch(/\$[^$\n]*\$/);
});

