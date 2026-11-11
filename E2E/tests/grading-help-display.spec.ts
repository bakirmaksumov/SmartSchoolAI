import { expect, test, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { createClass, createDraftAssignment, publishAndAssignAll, signIn, unique } from "./helpers";

// Step feedback, choice-question checks and tutor help. The model's answers are scripted through the mock AI
// (scripts/mock-ai.mjs); the application around it is real: the controllers, the grading job and its second-opinion
// pass, the answer-key logic, the browser code. What the app SENDS to the model is read back from the mock and
// asserted, which is how these tests pin the behaviour that depends on the live model. They do not measure how
// accurate a live model is: that needs a live key and is reported separately.
test.describe.configure({ mode: "serial" });

const mockAi = process.env.E2E_MOCK_AI_URL ?? `http://127.0.0.1:${process.env.E2E_MOCK_AI_PORT ?? 5199}`;
const screenshotDir = process.env.E2E_SHOT_DIR;
const password = "Grading-Pass-123";
const className = unique("E2E Grading Class");

// The regression fixture from a student's screenshots: f(x) = x² onto g(x) = 2x² − 8x + 14 = 2(x − 2)² + 6.
const transformTitle =
  "The functions f and g are defined by f(x) = x² and g(x) = 2x² − 8x + 14. Describe fully a sequence of " +
  "transformations that maps the graph of y = f(x) onto the graph of y = g(x), making clear the order in which " +
  "the transformations are applied.";
const transformKey = "2(x − 2)² + 6, for example translate by (2, 3) then stretch vertically by a factor of 2";

const choiceTitle = "The line y = 2cx + 3 and the curve y = cx² + 3x − c are given, where c is a constant. Which statement is true?";
const choiceOptions = [
  "The line and curve intersect only when c > 0",
  "The line and curve intersect for all values of c",
  "The line and curve never intersect",
  "The line and curve intersect only when c = 0"
];
const optionB = choiceOptions[1];

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

// Prompts wrap across lines in the source; compare them as running text.
const flat = (s: string) => s.replace(/\s+/g, " ");

const gameModal = () => student.locator("#waitingGameModal");
const openAssignment = () => student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);

// Fills a question's "Type solution" steps, one per row.
async function typeSteps(questionIndex: number, steps: string[]): Promise<void> {
  await student.locator(`.nav-btn[data-idx="${questionIndex}"]`).click();
  const toggle = student.locator(`#workToggle_${questionIndex}`);
  if (!(await toggle.isChecked())) await toggle.check();
  await student.locator(".work-block").nth(questionIndex).locator('.work-tab-btn[data-work-tab="type"]').click();

  const list = student.locator(`.type-steps-list[data-qindex="${questionIndex}"] .type-step-input`);
  for (let i = 0; i < steps.length; i++) {
    if ((await list.count()) <= i) await student.locator(`.type-step-add[data-qindex="${questionIndex}"]`).click();
    await list.nth(i).fill(steps[i]);
  }
}

// "Check my work", then open the result once the verdict is in.
async function checkWork(questionIndex: number): Promise<void> {
  await student.locator(`.type-step-check[data-qindex="${questionIndex}"]`).click();
  await expect(gameModal().locator(".wg-reveal-btn")).toBeVisible({ timeout: 30_000 });
  await gameModal().locator(".wg-reveal-btn").click();
  await expect(gameModal()).toBeHidden();
  await expect(student.locator(`.type-step-results[data-qindex="${questionIndex}"] .hw-result-step`).first()).toBeVisible();
}

const results = (questionIndex: number) => student.locator(`.type-step-results[data-qindex="${questionIndex}"]`);
const feedback = (questionIndex: number) => student.locator(".question-panel").nth(questionIndex).locator(".check-feedback");

async function chooseAndCheck(questionIndex: number, letter: string): Promise<void> {
  await student.locator(`.nav-btn[data-idx="${questionIndex}"]`).click();
  await student.locator(`.option-card[data-qkey="q_${questionIndex}"][data-value="${letter}"]`).click();
  await student.locator(`.check-answer-btn[data-qindex="${questionIndex}"]`).click();
}

test.beforeAll(async ({ browser }) => {
  teacher = await (await browser.newContext()).newPage();
  student = await (await browser.newContext({ viewport: { width: 1280, height: 800 } })).newPage();
});

test.afterAll(async () => {
  await teacher.context().close();
  await student.context().close();
});

test("setup: synthetic accounts and an assignment with a transformation question and four choice variants", async () => {
  test.setTimeout(240_000);
  const stamp = unique("grading").replace(/\s/g, "");
  const teacherEmail = `${stamp}.teacher@example.test`;
  const studentEmail = `${stamp}.student@example.test`;

  await teacher.goto("/Auth/Register");
  await teacher.locator('input[name="fullName"]').fill("Grading Teacher");
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
  await student.locator('input[name="fullName"]').fill("Grading Student");
  await student.locator('input[name="email"]').fill(studentEmail);
  await student.locator('input[name="password"]').fill(password);
  await student.locator('input[name="confirmPassword"]').fill(password);
  await student.locator('button[type="submit"]').click();
  await expect(student).toHaveURL(/\/Auth\/Login/);
  await signIn(student, studentEmail, password);
  await expect(student).toHaveURL(/\/Student\/?$/);

  assignmentId = await createDraftAssignment(teacher, unique("E2E Grading"), className);

  // Questions go in through the same endpoint the editor and the AI import use, so a choice question can be
  // saved with the key stored three different ways.
  const token = await teacher.locator('input[name="__RequestVerificationToken"]').first().inputValue();
  const add = async (question: Record<string, unknown>) => {
    const response = await teacher.request.post(`/Assignments/AddQuestion/${assignmentId}`, {
      form: { questionJson: JSON.stringify({ id: randomUUID(), difficulty: "Medium", ...question }), __RequestVerificationToken: token }
    });
    expect(response.ok(), "AddQuestion").toBe(true);
  };
  await add({ title: transformTitle, type: 0, answerKey: transformKey });
  await add({ title: choiceTitle, type: 4, options: choiceOptions, answerKey: "B", requireWork: true });
  await add({ title: choiceTitle, type: 4, options: choiceOptions, answerKey: optionB, requireWork: false });
  await add({ title: choiceTitle, type: 4, options: choiceOptions, answerKey: "", requireWork: true });
  await add({ title: choiceTitle, type: 4, options: choiceOptions, answerKey: `B) ${optionB}`, requireWork: false });

  await teacher.goto(`/Assignments/Edit/${assignmentId}`);
  await publishAndAssignAll(teacher, assignmentId);
});

// ── 1. Step feedback: the false green ───────────────────────────────────────
test("a step the first marker wrongly passed is corrected by the second check, with its reason under the step", async () => {
  test.setTimeout(120_000);
  const wrongFirst = "2((x − 4)² − 9)";
  const second = "Translate by (4, −9), then stretch vertically by a factor of 2";

  await mock("/__reset");
  await mock("/__script", {
    // The grader marks step 1 correct (the reported false green), rewords it, and still says "incorrect" overall.
    evalReply: {
      steps: [
        { number: 1, text: "2((x-4)^2-9)", status: "correct", explanation: "" },
        { number: 2, text: second, status: "incorrect", explanation: "The translation is wrong." }
      ],
      overall: "incorrect", expectedAnswer: "2(x − 2)² + 6", summary: "The working does not give g(x)."
    },
    // The second check agrees with the overall verdict but marks every step itself, and finds step 1 wrong.
    verifyReply: {
      overall: "incorrect", agrees: true, confidence: 0.9, notes: "Overall verdict stands.",
      steps: [
        { number: 1, status: "incorrect", explanation: "Expanding gives 2x² − 16x + 14, not 2x² − 8x + 14." },
        { number: 2, status: "incorrect", explanation: "The translation is wrong." }
      ]
    }
  });

  await openAssignment();
  await typeSteps(0, [wrongFirst, second]);
  await checkWork(0);
  await results(0).scrollIntoViewIfNeeded();
  await shot(student, "feedback-step");

  const cards = results(0).locator(".hw-result-step");
  await expect(cards).toHaveCount(2);

  // Step 1 is red, shows the student's own text (not the model's rewording), and the reason is right below it.
  await expect(cards.nth(0)).toHaveClass(/incorrect/);
  await expect(cards.nth(0).locator(".hw-step-text")).toContainText(`Step 1: ${wrongFirst}`);
  await expect(cards.nth(0).locator(".hw-step-text")).not.toContainText("2((x-4)^2-9)");
  const reason = cards.nth(0).locator(".hw-step-text + .hw-step-reason");
  await expect(reason).toBeVisible();
  await expect(reason).toContainText("Expanding gives 2x² − 16x + 14");
  await expect(cards.nth(0).locator(".hw-first-mistake")).toBeVisible();
  await expect(cards.nth(1).locator(".hw-first-mistake")).toHaveCount(0);

  // The overall verdict is untouched.
  await expect(results(0).locator(".alert").first()).toContainText("Not correct");

  // What the app asked the models: every step by number and in order, the rule for transformations in the order
  // given, and the verifier shown the first marker's marks and told to mark every step itself.
  const evalRequest = (await seen("eval")).at(-1)!;
  expect(evalRequest.user).toContain(`1. ${wrongFirst}\n2. ${second}`);
  expect(evalRequest.user).toContain(`Expected answer: ${transformKey}`);
  expect(flat(evalRequest.system)).toContain("IN THE ORDER GIVEN");
  expect(flat(evalRequest.system)).toContain("Check EVERY step on its own");
  const verifyRequest = (await seen("verify")).at(-1)!;
  expect(verifyRequest.user).toContain("1. correct");
  expect(flat(verifyRequest.system)).toContain("give your own mark for EVERY step");

  // Phone width: the reason is ordinary text that fits the screen, not a hover tooltip.
  await student.setViewportSize({ width: 375, height: 740 });
  const box = (await reason.boundingBox())!;
  expect(box.x, "reason starts on screen").toBeGreaterThanOrEqual(0);
  expect(box.x + box.width, "reason fits a phone").toBeLessThanOrEqual(375);
  await results(0).scrollIntoViewIfNeeded();
  await shot(student, "feedback-step-mobile");
  await student.setViewportSize({ width: 1280, height: 800 });
});

test("both valid transformation sequences are sent in order with the rule and the target, and pass", async () => {
  test.setTimeout(180_000);
  const sequences = [
    ["Translate by (2, 3)", "Stretch vertically by a factor of 2"],
    ["Stretch vertically by a factor of 2", "Translate by (2, 6)"]
  ];

  for (const steps of sequences) {
    await mock("/__reset");
    await mock("/__script", {
      evalReply: {
        steps: steps.map((text, i) => ({ number: i + 1, text, status: "correct", explanation: "" })),
        overall: "correct", expectedAnswer: "2(x − 2)² + 6", summary: "Both transformations give g(x)."
      },
      verifyReply: {
        overall: "correct", agrees: true, confidence: 0.95, notes: "Agreed.",
        steps: steps.map((_, i) => ({ number: i + 1, status: "correct", explanation: "" }))
      }
    });

    await openAssignment();
    await typeSteps(0, steps);
    await checkWork(0);

    const cards = results(0).locator(".hw-result-step");
    await expect(cards).toHaveCount(2);
    await expect(results(0).locator(".hw-result-step.incorrect")).toHaveCount(0);
    await expect(results(0).locator(".alert").first()).toContainText("Correct");

    // Each sequence reaches the grader exactly as written and in the order given, next to the target and
    // the rule that the operations are applied in that order. (How a live model applies it is not tested here.)
    const request = (await seen("eval")).at(-1)!;
    expect(request.user).toContain(`Student's steps:\n1. ${steps[0]}\n2. ${steps[1]}`);
    expect(request.user).toContain(transformKey);
    expect(flat(request.system)).toContain("Translation by (a, b): y = f(x − a) + b");
    expect(flat(request.system)).toContain("Accept every sequence whose result equals the target");
  }
});

// ── 2. Choice questions ──────────────────────────────────────────────────────
test("choice with a teacher key: the option is judged by the key, and required working gets its own message", async () => {
  test.setTimeout(120_000);
  await mock("/__reset");

  await openAssignment();
  await chooseAndCheck(1, "B");
  await expect(feedback(1)).toContainText("Your choice is correct. Add your working to complete this question.");
  await expect(feedback(1)).not.toContainText("Your answer is correct");
  await shot(student, "choice-required-work");

  // With working added, the same choice reads as a normal pass.
  await typeSteps(1, ["For c ≠ 0 the discriminant is 8c² + 9 > 0, and for c = 0 the equation is 3x = 0."]);
  await student.locator('.nav-btn[data-idx="1"]').click();
  await student.locator(`.check-answer-btn[data-qindex="1"]`).click();
  await expect(feedback(1)).toContainText("Your answer is correct");

  expect((await seen("answer")).length, "a key decides the verdict without asking the AI").toBe(0);
});

test("choice whose key is saved as the option's full text is judged without the AI", async () => {
  test.setTimeout(60_000);
  await mock("/__reset");
  await openAssignment();

  await chooseAndCheck(2, "B");
  await expect(feedback(2)).toContainText("Your answer is correct");
  await expect(feedback(2)).not.toContainText("Add your working");
  expect((await seen("answer")).length).toBe(0);
});

test("choice without a key falls back to the AI with every choice and the selected text, never just a letter", async () => {
  test.setTimeout(120_000);
  await mock("/__reset");
  await mock("/__script", { evalDelayMs: 0, answerReply: { correct: true, feedback: "That statement is true for every value of c." } });

  await openAssignment();
  await student.locator('.nav-btn[data-idx="3"]').click();
  await student.locator('.option-card[data-qkey="q_3"][data-value="B"]').click();
  await student.locator('.check-answer-btn[data-qindex="3"]').click();
  await expect(gameModal().locator(".wg-reveal-btn")).toBeVisible({ timeout: 30_000 });
  await gameModal().locator(".wg-reveal-btn").click();

  // The choice is judged on its own, and the required working is mentioned separately.
  await expect(feedback(3)).toContainText("Your choice is correct. Add your working to complete this question.");

  const request = (await seen("answer")).at(-1)!;
  expect(request.user).toContain("Choices:");
  for (const [i, text] of choiceOptions.entries()) expect(request.user).toContain(`${String.fromCharCode(65 + i)}. ${text}`);
  expect(request.user).toContain(`Selected option(s): B. ${optionB}`);
  expect(request.user, "the letter alone is not the answer").not.toMatch(/Student's answer: B\s*$/m);
  expect(flat(request.system)).toContain("Test special cases");
  expect(flat(request.system)).toContain("Grade only whether the selected statement(s) are true");
});

test("choice whose key is written as \"B) text\" is judged by the key", async () => {
  test.setTimeout(60_000);
  await mock("/__reset");
  await openAssignment();

  await chooseAndCheck(4, "B");
  await expect(feedback(4)).toContainText("Your answer is correct");
  await chooseAndCheck(4, "A");
  await expect(feedback(4)).toContainText("not correct");
  expect((await seen("answer")).length).toBe(0);
});

// ── 3. Hint, Explain and Get help ───────────────────────────────────────────
test("Hint, Explain and a step's Get help receive the current question and working", async () => {
  test.setTimeout(180_000);
  const step1 = "2((x − 4)² − 9)";
  const step2 = "Translate by (4, −9), then stretch vertically by a factor of 2";

  await mock("/__reset");
  await mock("/__script", {
    tutorChunks: ["Expand the bracket", " and compare the coefficient of x.\n\n", "Then check the constant term."],
    tutorDelayMs: 5,
    evalReply: {
      steps: [
        { number: 1, text: step1, status: "incorrect", explanation: "Expanding gives 2x² − 16x + 14, not 2x² − 8x + 14." },
        { number: 2, text: step2, status: "incorrect", explanation: "The translation is wrong." }
      ],
      overall: "incorrect", expectedAnswer: "2(x − 2)² + 6", summary: "The working does not give g(x)."
    },
    verifyReply: { overall: "incorrect", agrees: true, confidence: 0.9, notes: "", steps: [] }
  });

  await openAssignment();
  await typeSteps(0, [step1, step2]);

  // Hint: the tutor is told which question, what the student has written, and the teacher's reveal setting.
  await student.locator('[onclick="openTutorial()"]').first().click();
  const bubble = student.locator("#tutorialMessages .msg-bubble.assistant").last();
  await expect(bubble).toContainText("compare the coefficient of x.");
  await expect(bubble).toContainText("Expand the bracket and compare");           // spaces between chunks kept
  await expect(bubble.locator("p")).toHaveCount(2);                              // the line break kept
  const hint = (await seen("tutor")).at(-1)!;
  expect(flat(hint.system)).toContain("ONE actionable next move");
  expect(flat(hint.system)).toContain(`The student is working on Q1: ${transformTitle}`);
  expect(flat(hint.system)).toContain("Student's working:");
  expect(flat(hint.system)).toContain(`1. ${step1}`);
  expect(flat(hint.system)).toContain(`2. ${step2}`);
  expect(flat(hint.system)).toContain("Solution reveal: NOT allowed");
  expect(flat(hint.system)).toContain("Teacher's reference answer (private");

  // Explain: about the errors in THEIR working, with the same current context.
  await student.locator('.mode-btn[data-mode="concept"]').click();
  await expect(student.locator("#tutorialMessages .msg-bubble.assistant")).toHaveCount(2);
  await expect(student.locator("#tutorialMessages .msg-bubble.assistant").last()).toContainText("constant term");
  const explain = (await seen("tutor")).at(-1)!;
  expect(flat(explain.system)).toContain("Describe the error in THEIR work");
  expect(flat(explain.system)).toContain(`1. ${step1}`);

  // The latest working is what a later turn sees, not the working from when the panel was opened.
  await student.locator("#tutorialPanel .close-btn").click();
  const edited = "2((x − 2)² + 3)";
  await student.locator('.type-steps-list[data-qindex="0"] .type-step-input').first().fill(edited);
  await student.locator('[onclick="openTutorial()"]').first().click();
  await student.locator("#tutorialInput").fill("Is my first step better now?");
  await student.locator("#tutorialSendBtn").click();
  await expect(student.locator("#tutorialMessages .msg-bubble.assistant")).toHaveCount(3);
  await expect(student.locator("#tutorialMessages .msg-bubble.assistant").last()).toContainText("constant term");
  const followUp = (await seen("tutor")).at(-1)!;
  expect(flat(followUp.system)).toContain(`1. ${edited}`);
  expect(followUp.system).not.toContain(`1. ${step1}`);
  await student.locator("#tutorialPanel .close-btn").click();

  // Get help on one step: the tutor is told which step and what the checker said about it.
  await student.locator('.type-steps-list[data-qindex="0"] .type-step-input').first().fill(step1);
  await checkWork(0);
  await results(0).locator(".type-step-help").first().click();
  await expect(student.locator("#tutorialPanel")).toHaveClass(/open/);
  await expect(student.locator("#tutorialMessages .msg-bubble.assistant")).toHaveCount(1);
  await expect(student.locator("#tutorialMessages .msg-bubble.assistant").last()).toContainText("constant term");
  const help = (await seen("tutor")).at(-1)!;
  expect(flat(help.system)).toContain(`The student asked for help with step 1: "${step1}"`);
  expect(flat(help.system)).toContain("The checker said about that step: Expanding gives 2x² − 16x + 14");
  expect(help.user).toContain("I need help with step 1");
  await shot(student, "tutor-step-help");
});
