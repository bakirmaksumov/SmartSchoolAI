import { expect, test, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { addQuestion, createClass, createDraftAssignment, publishAndAssignAll, signIn, unique } from "./helpers";

// How the AI tutor and "Solve with photo" show text. The model's answers are scripted through the mock AI
// (scripts/mock-ai.mjs) but travel the whole normal path: browser -> controller -> service -> HTTP -> SSE/JSON ->
// browser rendering. Runs on its own accounts, so it also works against a container with no seeded users.
test.describe.configure({ mode: "serial" });

const mockAi = process.env.E2E_MOCK_AI_URL ?? `http://127.0.0.1:${process.env.E2E_MOCK_AI_PORT ?? 5199}`;
const fixtures = resolve(process.cwd(), "fixtures");
const screenshotDir = process.env.E2E_SHOT_DIR;
const password = "Display-Pass-123";

const className = unique("E2E Display Class");
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

async function mockRequests(kind: string): Promise<{ user: string }[]> {
  const response = await fetch(`${mockAi}/__requests?kind=${kind}`);
  return response.json() as Promise<{ user: string }[]>;
}

// Saved only when E2E_SHOT_DIR is set (used for the before/after comparison).
async function shot(page: Page, name: string): Promise<void> {
  if (!screenshotDir) return;
  mkdirSync(screenshotDir, { recursive: true });
  await page.screenshot({ path: resolve(screenshotDir, `${name}.png`) });
}

test.beforeAll(async ({ browser }) => {
  teacher = await (await browser.newContext()).newPage();
  student = await (await browser.newContext({ viewport: { width: 1280, height: 800 } })).newPage();
});

test.afterAll(async () => {
  await teacher.context().close();
  await student.context().close();
});

test("setup: synthetic teacher and student, an assignment with two maths questions", async () => {
  test.setTimeout(240_000);
  const stamp = unique("display").replace(/\s/g, "");
  const teacherEmail = `${stamp}.teacher@example.test`;
  const studentEmail = `${stamp}.student@example.test`;

  await teacher.goto("/Auth/Register");
  await teacher.locator('input[name="fullName"]').fill("Display Teacher");
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
  await student.locator('input[name="fullName"]').fill("Display Student");
  await student.locator('input[name="email"]').fill(studentEmail);
  await student.locator('input[name="password"]').fill(password);
  await student.locator('input[name="confirmPassword"]').fill(password);
  await student.locator('button[type="submit"]').click();
  await expect(student).toHaveURL(/\/Auth\/Login/);
  await signIn(student, studentEmail, password);
  await expect(student).toHaveURL(/\/Student\/?$/);

  assignmentId = await createDraftAssignment(teacher, unique("E2E Display"), className);
  await addQuestion(teacher, { kind: "Text/Numeric", text: "Express $2x^2 - 8x + 14$ in the form $2[(x-a)^2+b]$.", answerKey: "2[(x-2)^2+3]", listText: "in the form" });
  await addQuestion(teacher, {
    kind: "Text/Numeric",
    text: "By using a suitable substitution, solve the equation $(2x-3)^2 - \\frac{4}{(2x-3)^2} - 3 = 0$.",
    answerKey: "x = 5/2 or x = 1/2",
    listText: "By using a suitable substitution"
  });
  await publishAndAssignAll(teacher, assignmentId);
});

// ── 1. Tutor streaming ───────────────────────────────────────────────────────
// Chunks end in spaces ("out the "), start mid-word ("2"), split a formula and a bold marker
// ("**Group" + " the ... square**"), and one carries a paragraph break.
const tutorChunks = [
  "In", " your", " problem", ", this", " means", " factoring out the ", "2", " from the terms with ", "$x$", ". Focus on ",
  "$x^2", " - 4x$", ":\n\n", "1. ", "**Group", " the perfect square**", ": $x^2 - 4x + 4 - 4 + 7$.\n\n",
  "Finally, write it as ", "$2\\left[(x-2)^2 + 3\\right]$", ". Give it a try."
];

test("tutor reply streamed in chunks keeps its spaces and paragraphs and shows readable maths", async () => {
  test.setTimeout(120_000);
  await mock("/__reset");
  await mock("/__script", { tutorChunks, tutorDelayMs: 40 });

  await student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
  await student.locator('[onclick="openTutorial()"]').first().click();

  const bubble = student.locator("#tutorialMessages .msg-bubble.assistant").last();
  await expect(bubble).toContainText("Give it a try.");
  await shot(student, "tutor");

  const text = await bubble.innerText();
  expect(text, "words stay separated").toContain("In your problem, this means factoring out the 2 from the terms with x.");
  expect(text, "punctuation is kept").toContain("Focus on x² − 4x:");
  expect(text, "a formula split across chunks is joined").toContain("x² − 4x + 4 − 4 + 7.");
  expect(text, "the finished formula reads as maths").toContain("2[(x − 2)² + 3]");
  expect(text, "no run-together words").not.toMatch(/Inyour|factoringoutthe/);
  expect(text, "no raw LaTeX or Markdown markers").not.toMatch(/\\|\$|\*\*|\blbrack\b|\bleft\b|\bright\b/);

  // Three blocks were sent: a paragraph, a numbered item and a closing paragraph.
  await expect(bubble.locator("p"), "paragraph breaks survive").toHaveCount(2);
  await expect(bubble.locator("ol > li")).toHaveCount(1);
  expect(text.split("\n").filter(line => line.trim()).length, "the blocks are on separate lines").toBeGreaterThanOrEqual(3);
  await expect(bubble.locator("strong")).toHaveText("Group the perfect square");

  // A follow-up goes through the same path and lands below the first reply.
  await student.locator("#tutorialInput").fill("Thanks");
  await student.locator("#tutorialSendBtn").click();
  await expect(student.locator("#tutorialMessages .msg-bubble.user").last()).toHaveText("Thanks");
  await expect(student.locator("#tutorialMessages .msg-bubble.assistant")).toHaveCount(2);
});

// ── 2. OCR review ────────────────────────────────────────────────────────────
const ocrSteps = [
  { number: 1, text: "$y^4 - 3y^2 - 4 = 0$" },
  { number: 2, text: "$y_{1,2}^2 = 4$" },
  { number: 3, text: "$y_{1,2} = \\pm 2$" },
  { number: 4, text: "$2x - 3 = 2 \\quad 2x - 3 = -2$" },
  { number: 5, text: "$2x = 5 \\quad 2x = 1$" },
  { number: 6, text: "$x_1 = 2.5 \\quad x_2 = \\frac{1}{2}$" }
];

test("Solve with photo shows readable maths, and a student's edit reaches the grader", async () => {
  test.setTimeout(180_000);
  await mock("/__reset");
  await mock("/__script", { ocr: { detectedProblem: "", steps: ocrSteps } });

  await student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
  await student.locator('.nav-btn[data-idx="1"]').click();

  const block = student.locator(".work-block").nth(1);
  await block.locator("#workToggle_1").check();
  await block.locator('.work-tab-btn[data-work-tab="upload"]').click();

  // The dialog resets itself once it has fully opened, so wait for that before choosing the file.
  await student.evaluate(() => {
    const w = window as unknown as { __hwShown: boolean };
    w.__hwShown = false;
    document.getElementById("hwModal")!.addEventListener("shown.bs.modal", () => { w.__hwShown = true; }, { once: true });
  });
  await block.locator('[data-hw-open][data-qindex="1"]').click();
  await student.waitForFunction(() => (window as unknown as { __hwShown: boolean }).__hwShown);

  await student.locator("#hwFileInput").setInputFiles(resolve(fixtures, "solution-correct.jpg"));
  await student.locator("#hwCropUseFullBtn").click();
  await expect(student.locator("#hwAnalyzeBtn")).toBeEnabled();
  await student.locator("#hwAnalyzeBtn").click();
  await expect(student.locator("#hwStage-edit")).toBeVisible({ timeout: 30_000 });
  await shot(student, "ocr");

  // The question reads as maths, not as source.
  const question = await student.locator("#hwQuestionTitle").innerText();
  expect(question).toContain("solve the equation (2x − 3)² − 4/(2x − 3)² − 3 = 0.");
  expect(question).not.toMatch(/[\\$]|\bfrac\b/);

  const inputs = student.locator("#hwStepsList .hw-step-input");
  await expect(inputs).toHaveCount(6);
  const shown = await inputs.evaluateAll(list => list.map(el => (el as HTMLInputElement).value));
  expect(shown[0]).toBe("y⁴ − 3y² − 4 = 0");
  expect(shown[2]).toBe("y₁,₂ = ±2");
  expect(shown[5]).toContain("x₁ = 2.5");
  expect(shown[5]).toContain("x₂ = 1/2");
  for (const step of shown) expect(step, "no LaTeX in the editor").not.toMatch(/[\\$]|\bfrac\b|\bpm\b|\bquad\b/);

  // Edit step 6 as readable text: 2.5 becomes the equivalent 5/2.
  const edited = shown[5].replace("2.5", "5/2");
  await inputs.nth(5).fill(edited);
  await student.locator("#hwSubmitBtn").click();
  await expect(student.locator("#hwStage-results")).toBeVisible({ timeout: 60_000 });

  // What the grader was sent: unchanged steps keep their original maths, the edit arrives as typed.
  const sent = (await mockRequests("eval")).at(-1)!.user;
  const gradedSteps = sent.split("Student's steps:")[1].trim().split("\n");
  expect(gradedSteps).toHaveLength(6);
  expect(gradedSteps[0]).toBe("1. $y^4 - 3y^2 - 4 = 0$");
  expect(gradedSteps[2]).toBe("3. $y_{1,2} = \\pm 2$");
  expect(gradedSteps[3]).toBe("4. $2x - 3 = 2 \\quad 2x - 3 = -2$");
  expect(gradedSteps[5], "the edited step").toBe(`6. ${edited}`);
  expect(sent, "the replaced value is gone").not.toContain("2.5");

  // The results are readable too.
  const results = (await student.locator("#hwResults").innerText()).replace(/[ \t]+/g, " ");
  expect(results).toContain(`Step 6: ${edited.replace(/\s+/g, " ")}`);
  expect(results).toContain("Step 3: y₁,₂ = ±2");
  expect(results).not.toMatch(/[\\$]/);
});

// ── 3. Mobile layout ─────────────────────────────────────────────────────────
test("a long tutor reply stays inside the panel at 375px and the composer stays usable", async () => {
  test.setTimeout(120_000);
  const longWord = "Pneumonoultramicroscopicsilicovolcanoconiosis".repeat(3);
  await mock("/__reset");
  await mock("/__script", {
    tutorDelayMs: 5,
    tutorChunks: [
      `Here is a very long reply that should wrap inside the panel instead of scrolling sideways: ${longWord}.\n\n`,
      "However, the final step is to $expand your answer and see if it matches the original expression then check that every single term on both sides agrees$, which is a long sentence the old renderer set as one unbreakable formula.\n\n",
      "A long formula: $a_1 + a_2 + a_3 + a_4 + a_5 + a_6 + a_7 + a_8 + a_9 + a_{10} + a_{11} + a_{12} + a_{13} + a_{14} = 0$.\n\n",
      "Something too complex to convert: $\\int_0^{\\infty} e^{-x^2}\\,dx = \\frac{\\sqrt{\\pi}}{2} + \\sum_{k=1}^{n} \\frac{1}{k^2}$.\n\n",
      "- A list item with a long path /very/long/path/that/never/ends/and/keeps/going/and/going/forever/and/ever\n\n",
      "END OF REPLY"
    ]
  });

  await student.setViewportSize({ width: 375, height: 740 });
  await student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
  // The assignment page itself may be wider than a phone; what matters is that the tutor adds nothing to it.
  const pageOverflowBefore = await student.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  await student.locator('[onclick="openTutorial()"]').first().click();
  const bubble = student.locator("#tutorialMessages .msg-bubble.assistant").last();
  await expect(bubble).toContainText("END OF REPLY");
  await student.waitForTimeout(300);   // let KaTeX settle on the complex formula
  await shot(student, "tutor-mobile");

  const layout = await student.evaluate(() => {
    const rect = (sel: string) => document.querySelector(sel)!.getBoundingClientRect();
    const messages = document.getElementById("tutorialMessages")!;
    const bubbles = Array.from(messages.querySelectorAll(".msg-bubble.assistant")) as HTMLElement[];
    const panel = rect("#tutorialPanel");
    const send = rect("#tutorialSendBtn");
    const hit = document.elementFromPoint(send.left + send.width / 2, send.top + send.height / 2);
    return {
      pageOverflow: document.documentElement.scrollWidth - window.innerWidth,
      messagesOverflow: messages.scrollWidth - messages.clientWidth,
      bubbleOverflow: Math.max(...bubbles.map(b => b.scrollWidth - b.clientWidth)),
      bubbleRight: Math.max(...bubbles.map(b => b.getBoundingClientRect().right)),
      panelRight: panel.right,
      viewport: { width: window.innerWidth, height: window.innerHeight },
      send: { left: send.left, right: send.right, top: send.top, bottom: send.bottom },
      sendHit: !!hit && !!hit.closest("#tutorialSendBtn")
    };
  });
  expect(layout.pageOverflow, "the tutor widens the page").toBeLessThanOrEqual(Math.max(0, pageOverflowBefore));
  expect(layout.messagesOverflow, "chat scrolls sideways").toBeLessThanOrEqual(0);
  expect(layout.bubbleOverflow, "a reply is wider than its bubble").toBeLessThanOrEqual(1);
  expect(layout.bubbleRight, "a reply sticks out of the panel").toBeLessThanOrEqual(layout.panelRight + 1);
  expect(layout.send.right, "send button is on screen").toBeLessThanOrEqual(layout.viewport.width);
  expect(layout.send.bottom, "send button is on screen").toBeLessThanOrEqual(layout.viewport.height);
  expect(layout.sendHit, "nothing covers the send button").toBe(true);

  // The composer works: a real click on the send button delivers the follow-up.
  await student.locator("#tutorialInput").fill("Thanks");
  await student.locator("#tutorialSendBtn").click();
  await expect(student.locator("#tutorialMessages .msg-bubble.user").last()).toHaveText("Thanks");
  await expect(student.locator("#tutorialMessages .msg-bubble.assistant")).toHaveCount(2);
});
