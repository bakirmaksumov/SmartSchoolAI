import { expect, test, type APIResponse, type Page } from "@playwright/test";
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

test.describe.configure({ mode: "serial" });

// Top-level `const` of the assignment editor page; reachable by name inside page.evaluate.
declare const QUESTIONS_DATA: { id: string }[];

const fixtures = resolve(process.cwd(), "fixtures");
const pdfFiles = ["maths-practice.pdf", "mock-pure-math-2025.pdf"];

// A valid 1x1 PNG, so the server's magic-byte check accepts it.
const png = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==",
  "base64"
);
const pdfHeader = Buffer.from("%PDF-1.4\n");

const className = unique("E2E Upload Class");
let teacher: Page;
let student: Page;
let assignmentId = "";
let questionId = "";
let diagramPath = "";
let answerKeyPath = "";

async function token(page: Page): Promise<string> {
  return page.locator('input[name="__RequestVerificationToken"]').first().inputValue();
}

async function postFile(page: Page, url: string, field: string, file: { name: string; mimeType: string; buffer: Buffer } | null,
  extra: Record<string, string> = {}): Promise<APIResponse> {
  const multipart: Record<string, string | { name: string; mimeType: string; buffer: Buffer }> = {
    __RequestVerificationToken: await token(page),
    ...extra
  };
  if (file) multipart[field] = file;
  return page.request.post(url, { multipart });
}

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

test("setup: class, draft assignment with one question", async () => {
  test.setTimeout(180_000);
  const code = await createClass(teacher, className);
  await joinClass(student, code, className);

  assignmentId = await createDraftAssignment(teacher, unique("E2E Upload Assignment"), className);
  await addQuestion(teacher, { kind: "Text/Numeric", text: "Upload question: what is 6 times 7?", answerKey: "42" });

  questionId = await teacher.evaluate(() => QUESTIONS_DATA[0].id);
  expect(questionId).toMatch(/^[0-9a-f-]{36}$/i);
});

test("PDF and image importers reject wrong, fake and oversized files", async () => {
  await teacher.goto(`/Assignments/Edit/${assignmentId}`);
  const pdfUrl = `/Assignments/ImportFromPdfStart/${assignmentId}`;
  const imageUrl = `/Assignments/ImportFromImageStart/${assignmentId}`;

  const cases: [string, Promise<APIResponse>, RegExp][] = [
    ["no file", postFile(teacher, pdfUrl, "pdf", null), /No file uploaded/i],
    ["text file", postFile(teacher, pdfUrl, "pdf", { name: "notes.txt", mimeType: "text/plain", buffer: Buffer.from("hello") }), /Only PDF/i],
    ["fake pdf", postFile(teacher, pdfUrl, "pdf", { name: "fake.pdf", mimeType: "application/pdf", buffer: Buffer.from("this is not a pdf") }), /not a valid PDF/i],
    ["huge pdf", postFile(teacher, pdfUrl, "pdf", { name: "huge.pdf", mimeType: "application/pdf", buffer: Buffer.concat([pdfHeader, Buffer.alloc(21 * 1024 * 1024)]) }), /too large/i],
    ["pdf as image", postFile(teacher, imageUrl, "image", { name: "scan.pdf", mimeType: "application/pdf", buffer: pdfHeader }), /Only image files/i],
    ["fake png", postFile(teacher, imageUrl, "image", { name: "fake.png", mimeType: "image/png", buffer: Buffer.from("not an image") }), /not a valid image/i]
  ];

  for (const [label, request, expected] of cases) {
    const response = await request;
    expect(response.status(), label).toBe(200);
    expect(JSON.stringify(await response.json()), label).toMatch(expected);
  }
});

test("teacher attaches a diagram and an answer-key image and rejects bad ones", async () => {
  const diagram = await postFile(teacher, `/Assignments/UploadQuestionImage/${assignmentId}`, "file",
    { name: "diagram.png", mimeType: "image/png", buffer: png }, { questionId });
  const diagramBody = await diagram.json();
  expect(diagramBody.error, JSON.stringify(diagramBody)).toBeUndefined();
  diagramPath = diagramBody.path;
  expect(diagramPath).toContain(assignmentId);

  const key = await postFile(teacher, `/Assignments/UploadAnswerKeyImage/${assignmentId}`, "file",
    { name: "key.png", mimeType: "image/png", buffer: png }, { questionId });
  const keyBody = await key.json();
  expect(keyBody.error, JSON.stringify(keyBody)).toBeUndefined();
  answerKeyPath = keyBody.path;

  const bad = async (file: { name: string; mimeType: string; buffer: Buffer }, extra: Record<string, string>) =>
    JSON.stringify(await (await postFile(teacher, `/Assignments/UploadQuestionImage/${assignmentId}`, "file", file, extra)).json());

  expect(await bad({ name: "x.png", mimeType: "image/png", buffer: Buffer.from("text pretending to be png") }, { questionId }))
    .toMatch(/not a valid image/i);
  expect(await bad({ name: "x.txt", mimeType: "text/plain", buffer: Buffer.from("hello") }, { questionId }))
    .toMatch(/Only image files/i);
  expect(await bad({ name: "x.png", mimeType: "image/png", buffer: png }, { questionId: "not-a-guid" }))
    .toMatch(/Invalid question/i);
});

test("teacher publishes and the assigned student can open the attached images", async () => {
  await teacher.goto(`/Assignments/Edit/${assignmentId}`);
  await publishAndAssignAll(teacher, assignmentId);

  for (const path of [diagramPath, answerKeyPath]) {
    const asTeacher = await teacher.request.get(path);
    expect(asTeacher.status(), `teacher ${path}`).toBe(200);
    expect(asTeacher.headers()["content-type"]).toContain("image/png");

    const asStudent = await student.request.get(path);
    expect(asStudent.status(), `assigned student ${path}`).toBe(200);
    expect(asStudent.headers()["content-type"]).toContain("image/png");
    expect(Buffer.compare(await asStudent.body(), png)).toBe(0);
  }
});

test("a student who is not assigned, an anonymous visitor and path tricks cannot read the images", async ({ browser }) => {
  const outsider = await (await browser.newContext()).newPage();
  const anonymous = await browser.newContext();
  try {
    const email = `${unique("outsider").replace(/\s/g, "")}@example.test`;
    await outsider.goto("/Auth/Register");
    await outsider.locator('input[name="fullName"]').fill("Outsider Teacher");
    await outsider.locator('input[name="email"]').fill(email);
    await outsider.locator('input[name="password"]').fill("Teacher-Pass-123");
    await outsider.locator('input[name="confirmPassword"]').fill("Teacher-Pass-123");
    await outsider.locator('button[type="submit"]').click();
    await signIn(outsider, email, "Teacher-Pass-123");

    expect((await outsider.request.get(diagramPath)).status(), "other teacher").toBe(404);

    const anon = await anonymous.request.get(diagramPath, { maxRedirects: 0 });
    expect([301, 302, 401, 403, 404], `anonymous got ${anon.status()}`).toContain(anon.status());
    expect(anon.headers()["content-type"] ?? "").not.toContain("image/png");

    const parts = diagramPath.split("/");
    const traversal = [...parts.slice(0, -1), "..%2F..%2F..%2Fsmartschool.db"].join("/");
    const attempt = await student.request.get(traversal);
    expect([400, 404]).toContain(attempt.status());
  } finally {
    await outsider.context().close();
    await anonymous.close();
  }
});

test("profile photo: teacher uploads a PNG, student can load it, bad files are refused", async () => {
  await teacher.goto("/Profile");
  await teacher.locator("#photoInput").setInputFiles({ name: "me.png", mimeType: "image/png", buffer: png });
  await expect(teacher.locator("body")).toContainText("Profile photo updated");

  const src = await teacher.locator("#avatarPreview").getAttribute("src");
  expect(src).toContain("/uploads/avatars/");

  const asStudent = await student.request.get(src!);
  expect(asStudent.status()).toBe(200);
  expect(asStudent.headers()["content-type"]).toContain("image/png");

  const refusals: [string, { name: string; mimeType: string; buffer: Buffer }, RegExp][] = [
    ["text file", { name: "me.txt", mimeType: "text/plain", buffer: Buffer.from("hi") }, /Only JPG, PNG, GIF or WEBP/i],
    ["fake png", { name: "me.png", mimeType: "image/png", buffer: Buffer.from("not an image") }, /not a valid image/i],
    ["over 2 MB", { name: "big.png", mimeType: "image/png", buffer: Buffer.concat([png, Buffer.alloc(2 * 1024 * 1024 + 1)]) }, /smaller than 2 MB/i]
  ];
  for (const [label, file, message] of refusals) {
    await teacher.goto("/Profile");
    await teacher.locator("#photoInput").setInputFiles(file);
    await expect(teacher.locator("body"), label).toContainText(message);
  }

  const still = await teacher.locator("#avatarPreview").getAttribute("src");
  expect((await student.request.get(still!)).status(), "previous photo should still load").toBe(200);
});

test("student profile photo upload works and is visible to the teacher", async () => {
  await student.goto("/Profile");
  await student.locator("#photoInput").setInputFiles({ name: "kid.png", mimeType: "image/png", buffer: png });
  await expect(student.locator("body")).toContainText("Profile photo updated");

  const src = await student.locator("#avatarPreview").getAttribute("src");
  const asTeacher = await teacher.request.get(src!);
  expect(asTeacher.status()).toBe(200);
});

// PDF import tests come last and each use their own draft assignment, so a failure here cannot
// hide the file-access checks above (a failed serial test skips everything after it).
for (const file of pdfFiles) {
  test(`teacher imports ${file} through the PDF importer and the student sees the questions`, async () => {
    test.setTimeout(420_000);
    const draftId = await createDraftAssignment(teacher, unique(`E2E PDF ${file}`), className);
    await teacher.getByRole("button", { name: "Add questions" }).first().click();

    // The page calls pdfReset() once the modal has fully opened (shown.bs.modal), which clears any
    // file chosen earlier, so wait for that event before selecting the file.
    await teacher.evaluate(() => {
      const w = window as unknown as { __pdfShown: boolean };
      w.__pdfShown = false;
      document.getElementById("pdfImportModal")!.addEventListener("shown.bs.modal", () => { w.__pdfShown = true; }, { once: true });
    });
    await teacher.locator("button[onclick=\"openImportModal('pdf')\"]").click();
    await teacher.waitForFunction(() => (window as unknown as { __pdfShown: boolean }).__pdfShown);

    await teacher.locator("#pdfFileInput").setInputFiles(resolve(fixtures, file));
    await expect(teacher.locator("#pdfFileName")).toContainText(file);

    const finished = teacher.waitForResponse(async response => {
      if (!response.url().includes("/Assignments/GenerationProgress/")) return false;
      const body = await response.json().catch(() => ({ status: "running" }));
      return body.status !== "running";
    }, { timeout: 240_000 });
    await teacher.locator("#pdfAnalyseBtn").click();
    const job = await (await finished).json() as { status: string; error?: string; questions?: unknown[] };

    if (!aiEnabled) {
      expect(job.status).toBe("error");
      expect(job.error).toMatch(/API key|Import failed|try again/i);
      return;
    }

    expect(job.status, `AI import of ${file} reported: ${job.error ?? ""}`).toBe("done");
    const found = job.questions?.length ?? 0;
    expect(found, `AI import of ${file} finished but produced 0 questions`).toBeGreaterThan(0);

    await expect(teacher.locator("#pdfQuestionList .list-group-item")).toHaveCount(found);
    await teacher.locator("#pdfAddBtn").click();
    await teacher.waitForLoadState("load");
    // The page adds the questions one by one and then reloads, so the evaluate can hit a page that is
    // mid-navigation; treat that as "not there yet".
    await expect.poll(
      () => teacher.evaluate(() => (typeof QUESTIONS_DATA === "undefined" ? -1 : QUESTIONS_DATA.length)).catch(() => -1),
      { timeout: 120_000 }
    ).toBe(found);

    await publishAndAssignAll(teacher, draftId);
    await student.goto(`/Student/TakeAssignment?assignmentId=${draftId}`);
    await expect(student.locator(".nav-btn[data-idx]")).toHaveCount(found);
  });
}
