import { expect, test, type Page } from "@playwright/test";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import {
  addQuestion,
  aiEnabled,
  createClass,
  createDraftAssignment,
  joinClass,
  publishAndAssignAll,
  signIn,
  signInAs,
  unique
} from "./helpers";

// A student photographs a handwritten solution to "Evaluate 2^-3": the photo is read (OCR), the
// steps are graded, and teacher and student can open the result. Uses the real AI (4 calls in total),
// so it only runs when run-e2e.mjs found a local OpenRouter key.
test.describe.configure({ mode: "serial" });
test.skip(!aiEnabled, "needs an AI key (user-secrets OpenRouter:ApiKey)");

const fixtures = resolve(process.cwd(), "fixtures");
const className = unique("E2E Handwritten Class");
const photos = [
  { name: "solution-correct.jpg", question: 0, correct: true, answerPattern: /1\s*\/\s*8|frac\s*\{?\s*1\s*\}?\s*\{?\s*8|0\.125/i },
  { name: "solution-wrong.jpg", question: 1, correct: false, answerPattern: /9/ }
];

let teacher: Page;
let student: Page;
let assignmentId = "";
const uploaded: Record<string, string[]> = {};
const resultIds: string[] = [];

test.beforeAll(async ({ browser }) => {
  teacher = await (await browser.newContext()).newPage();
  student = await (await browser.newContext()).newPage();
  await signInAs(teacher, "teacher");
  await signInAs(student, "student");
});

test.afterAll(async () => {
  await teacher.context().close();
  await student.context().close();
});

async function studentToken(): Promise<string> {
  await student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
  return student.locator('input[name="__RequestVerificationToken"]').first().inputValue();
}

test("setup: assignment with the question the photos answer", async () => {
  test.setTimeout(180_000);
  const code = await createClass(teacher, className);
  await joinClass(student, code, className);

  assignmentId = await createDraftAssignment(teacher, unique("E2E Handwritten"), className);
  for (const label of ["first", "second", "third"]) {
    await addQuestion(teacher, { kind: "Text/Numeric", text: `Evaluate 2^-3 (${label} copy)`, answerKey: "1/8" });
  }
  await publishAndAssignAll(teacher, assignmentId);
});

for (const photo of photos) {
  test(`student uploads ${photo.name}: the AI reads it and grades it ${photo.correct ? "correct" : "incorrect"}`, async () => {
    test.setTimeout(240_000);
    const token = await studentToken();

    const analysed = await student.request.post("/Student/AnalyzeHandwritten", {
      multipart: {
        assignmentId,
        questionIndex: String(photo.question),
        __RequestVerificationToken: token,
        images: { name: photo.name, mimeType: "image/jpeg", buffer: readFileSync(resolve(fixtures, photo.name)) }
      },
      timeout: 120_000
    });
    const ocr = await analysed.json() as {
      success: boolean; error?: string; imagePaths: string[]; detectedProblem?: string; steps: { number: number; text: string }[];
    };
    expect(ocr.success, `OCR failed: ${ocr.error}`).toBe(true);
    expect(ocr.steps.length, "the photo should be read into steps").toBeGreaterThan(0);
    expect(ocr.steps.map(s => s.text).join(" "), "the written answer should be recognised").toMatch(photo.answerPattern);
    uploaded[photo.name] = ocr.imagePaths;

    const submitted = await student.request.post("/Student/SubmitHandwritten", {
      form: {
        assignmentId,
        questionIndex: String(photo.question),
        imagePathsJson: JSON.stringify(ocr.imagePaths),
        stepsJson: JSON.stringify(ocr.steps),
        __RequestVerificationToken: token
      }
    });
    const job = await submitted.json() as { success: boolean; jobId?: string; error?: string };
    expect(job.success, `submit failed: ${job.error}`).toBe(true);

    let outcome: { status: string; error?: string; result?: { overall?: string; resultId?: string; summary?: string } } = { status: "running" };
    for (let i = 0; i < 160 && outcome.status === "running"; i++) {
      await student.waitForTimeout(1_000);
      outcome = await (await student.request.get(`/Student/GradeProgress/${job.jobId}`)).json();
    }
    expect(outcome.status, `grading reported: ${outcome.error}`).toBe("done");

    const overall = outcome.result?.overall ?? "";
    if (photo.correct) {
      expect(overall, `summary: ${outcome.result?.summary}`).toMatch(/^correct$/i);
    } else {
      expect(overall, `a solution ending in 9 must not be correct (summary: ${outcome.result?.summary})`).not.toMatch(/^correct$/i);
    }
    resultIds.push(outcome.result!.resultId!);
  });
}

test("student, teacher and outsiders: who can open the uploaded photos", async ({ browser }) => {
  const path = uploaded["solution-correct.jpg"]?.[0];
  expect(path, "the first test should have stored a photo").toBeTruthy();

  const own = await student.request.get(path);
  expect(own.status(), "the student's own photo").toBe(200);
  expect(own.headers()["content-type"]).toContain("image/");

  expect((await teacher.request.get(path)).status(), "the teacher of the class").toBe(200);

  const outsider = await (await browser.newContext()).newPage();
  const anonymous = await browser.newContext();
  try {
    const email = `${unique("photo.outsider").replace(/\s/g, "")}@example.test`;
    await outsider.goto("/Auth/Register");
    await outsider.locator('input[name="fullName"]').fill("Photo Outsider");
    await outsider.locator('input[name="email"]').fill(email);
    await outsider.locator('input[name="password"]').fill("Teacher-Pass-123");
    await outsider.locator('input[name="confirmPassword"]').fill("Teacher-Pass-123");
    await outsider.locator('button[type="submit"]').click();
    await signIn(outsider, email, "Teacher-Pass-123");
    expect((await outsider.request.get(path)).status(), "a teacher from another class").toBe(404);

    const anon = await anonymous.request.get(path, { maxRedirects: 0 });
    expect(anon.headers()["content-type"] ?? "", `anonymous visitor got ${anon.status()}`).not.toContain("image/");
  } finally {
    await outsider.context().close();
    await anonymous.close();
  }
});

test("teacher opens both graded solutions and sees the photo and the verdict", async () => {
  expect(resultIds.length).toBe(2);
  for (const [i, resultId] of resultIds.entries()) {
    const response = await teacher.goto(`/Assignments/SubmissionDetail?resultId=${resultId}`);
    expect(response?.status(), `result ${i + 1}`).toBeLessThan(400);
    await expect(teacher.locator("body")).toContainText(`Evaluate 2^-3`);
    await expect(teacher.locator(`img[src*="/uploads/submissions/"]`).first(), "the student's photo").toBeVisible();
  }
});

test("student uses the upload dialog on the page: photo, crop, check the steps, submit, get the verdict", async () => {
  test.setTimeout(300_000);
  await student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
  await student.locator('.nav-btn[data-idx="2"]').click();

  const block = student.locator(".work-block").nth(2);
  await block.locator("#workToggle_2").check();
  await block.locator('.work-tab-btn[data-work-tab="upload"]').click();

  // The dialog resets itself once it has fully opened, so wait for that before choosing the file.
  await student.evaluate(() => {
    const w = window as unknown as { __hwShown: boolean };
    w.__hwShown = false;
    document.getElementById("hwModal")!.addEventListener("shown.bs.modal", () => { w.__hwShown = true; }, { once: true });
  });
  await block.locator('[data-hw-open][data-qindex="2"]').click();
  await student.waitForFunction(() => (window as unknown as { __hwShown: boolean }).__hwShown);

  await student.locator("#hwFileInput").setInputFiles(resolve(fixtures, "solution-correct.jpg"));
  await student.locator("#hwCropUseFullBtn").click();
  await expect(student.locator("#hwAnalyzeBtn")).toBeEnabled();

  const read = student.waitForResponse(r => r.url().includes("/Student/AnalyzeHandwritten"), { timeout: 120_000 });
  await student.locator("#hwAnalyzeBtn").click();
  const ocr = await (await read).json() as { success: boolean; error?: string; steps: unknown[] };
  expect(ocr.success, `OCR reported: ${ocr.error}`).toBe(true);
  expect(ocr.steps.length).toBeGreaterThan(0);

  const graded = student.waitForResponse(async r => {
    if (!r.url().includes("/Student/GradeProgress/")) return false;
    return (await r.json().catch(() => ({ status: "running" }))).status !== "running";
  }, { timeout: 180_000 });
  await expect(student.locator("#hwSubmitBtn")).toBeVisible({ timeout: 30_000 });
  await student.locator("#hwSubmitBtn").click();

  const outcome = await (await graded).json() as { status: string; error?: string; result?: { overall?: string } };
  expect(outcome.status, `grading reported: ${outcome.error}`).toBe("done");
  expect(outcome.result?.overall ?? "").toMatch(/^correct$/i);
});
