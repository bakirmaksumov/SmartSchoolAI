import { expect, test, type Browser, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { assignTo, createClass, createDraftAssignment, signIn, submitAssignment, unique } from "./helpers";

// Two checks of "Import Cambridge Paper", end to end in the browser:
//  1. an incomplete answer key — a part whose mark scheme requires two separate results (shaped like 1(a) of the
//     2027 specimen Paper 1, where the real reading kept only one) is held for review with what is missing, and the
//     teacher completes the key and confirms it;
//  2. a drawn answer — a part that asks the student to sketch becomes the existing sketch-pad question, an unclear
//     one waits for the teacher to choose the format, and the student's drawing reaches the teacher's marking page.
// The PDFs are generated TEST DATA and the readings come from the mock AI (E2E/scripts/mock-ai.mjs).
test.describe.configure({ mode: "serial" });

const mockAi = process.env.E2E_MOCK_AI_URL ?? `http://127.0.0.1:${process.env.E2E_MOCK_AI_PORT ?? 5199}`;
const shots = process.env.E2E_SHOT_DIR;
const password = "Answer-Checks-123";
const stamp = unique("cac").replace(/\s/g, "");
const className = unique("Drawing class");

let browserRef: Browser;
let teacher: Page;
let importId = "";

function pdf(pages: string[]): Buffer {
  const esc = (t: string) => t.replace(/\\/g, "\\\\").replace(/\(/g, "\\(").replace(/\)/g, "\\)");
  const objects = ["<< /Type /Catalog /Pages 2 0 R >>", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"];
  const kids: number[] = [];
  for (const page of pages) {
    let content = "BT /F1 12 Tf 16 TL 50 790 Td\n";
    for (const line of page.split("\n")) content += `(${esc(line)}) Tj T*\n`;
    content += "ET";
    objects.push(`<< /Length ${content.length} >>\nstream\n${content}\nendstream`);
    objects.push(`<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents ${objects.length} 0 R >>`);
    kids.push(objects.length);
  }
  objects[1] = `<< /Type /Pages /Count ${kids.length} /Kids [${kids.map(k => `${k} 0 R`).join(" ")}] >>`;
  let out = "%PDF-1.4\n";
  const offsets: number[] = [];
  objects.forEach((o, i) => { offsets.push(out.length); out += `${i + 1} 0 obj\n${o}\nendobj\n`; });
  const xref = out.length;
  out += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n` + offsets.map(o => `${String(o).padStart(10, "0")} 00000 n \n`).join("");
  out += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(out, "latin1");
}
const file = (name: string, pages: string[]) => ({ name, mimeType: "application/pdf", buffer: pdf(pages) });

const paperPdf = () => file("TEST-9709_w26_qp_11.pdf", [
  `TEST DATA - not a Cambridge paper ${stamp}\nCambridge International AS & A Level\nMATHEMATICS 9709/11\nPaper 1 Pure Mathematics 1 October/November 2026`,
  "1 The points A(0, 1), B(1, 6), C(1.5, 7.75), D(1.9, 8.79) and E(2, 9) lie on the curve y = f(x).\n(a) Complete the table to show the gradients of CE and DE. [2]\n9709/11/O/N/26",
  "8\n        y\n        |    /\n        |   /\n   -----O--/------ x\n       /|\nThe diagram shows the graph of y = f(x).\n(a) State the range of f. [1]\n(b) On the diagram, sketch the graph of y = f^-1(x). [2]\n(c) Sketch the graph of y = f(x) + 1 on the same diagram. [1]",
]);
const schemePdf = () => file("TEST-9709_w26_ms_11.pdf", [
  "TEST DATA - not a Cambridge mark scheme\nCambridge International AS & A Level\nMATHEMATICS 9709/11\nPaper 1 Pure Mathematics 1 October/November 2026\nMARK SCHEME",
  "1(a) Gradient of CE = 2.5 B1\nGradient of DE = 2.1 B1",
]);

const paperReply = {
  questions: [
    { number: 1, stem: "The points $A(0, 1)$, $B(1, 6)$, $C(1.5, 7.75)$, $D(1.9, 8.79)$ and $E(2, 9)$ lie on the curve $y = f(x)$.", figure: null, parts: [
      { label: "(a)", text: "Complete the table to show the gradients of $CE$ and $DE$.", marks: 2, page: 2, kind: "ShortAnswer", needsDrawing: false }] },
    { number: 8, stem: "The diagram shows the graph of $y = f(x)$.", figure: { page: 3, box: [0.05, 0.03, 0.6, 0.18], description: "The graph of y = f(x)" }, parts: [
      { label: "(a)", text: "State the range of $f$.", marks: 1, page: 3, usesFigure: true, kind: "ShortAnswer", needsDrawing: false },
      { label: "(b)", text: "On the diagram, sketch the graph of $y = f^{-1}(x)$.", marks: 2, page: 3, usesFigure: true, kind: "OpenText", needsDrawing: true },
      { label: "(c)", text: "Sketch the graph of $y = f(x) + 1$ on the same diagram.", marks: 1, page: 3, usesFigure: true, kind: "ShortAnswer", needsDrawing: false }] },
  ],
};
// As the real reading of the specimen did: of the two B1 results only the second is marked final.
const schemeReply = {
  rows: [
    { question: "1(a)", appliesTo: ["1(a)"], answer: "Gradient of $CE = 2.5$", codes: "B1", marks: 1, final: false, page: 2 },
    { question: "1(a)", appliesTo: ["1(a)"], answer: "Gradient of $DE = 2.1$", codes: "B1", marks: 1, final: true, page: 2 },
    { question: "8(a)", appliesTo: ["8(a)"], answer: "$-1 \\le f(x) \\le 3$", codes: "B1", marks: 1, final: true, page: 2 },
    { question: "8(b)", appliesTo: ["8(b)"], answer: "Reflection of the curve in $y = x$", codes: "B1 B1", marks: 2, final: true, guidance: "B1 shape, B1 symmetry in y = x", page: 2 },
    { question: "8(c)", appliesTo: ["8(c)"], answer: "The curve translated up by 1", codes: "B1", marks: 1, final: true, page: 2 },
  ],
};

async function mock(path: string, body?: unknown): Promise<void> {
  const r = await fetch(`${mockAi}${path}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body ?? {}) });
  expect(r.ok, `mock AI ${path}`).toBe(true);
}
async function shot(page: Page, name: string, fullPage = true): Promise<void> {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  await page.waitForTimeout(400);
  await page.screenshot({ path: resolve(shots, `${name}.png`), fullPage });
}
async function register(page: Page, name: string, email: string, invite?: string): Promise<void> {
  await page.goto(invite ? "/Auth/StudentRegister" : "/Auth/Register");
  if (invite) await page.locator('input[name="inviteCode"]').fill(invite);
  await page.locator('input[name="fullName"]').fill(name);
  await page.locator('input[name="email"]').fill(email);
  await page.locator('input[name="password"]').fill(password);
  await page.locator('input[name="confirmPassword"]').fill(password);
  await page.locator('button[type="submit"]').click();
  await signIn(page, email, password);
}
const row = (page: Page, number: string) => page.locator(`#partList tr[data-row="${number}"]`);

test.beforeAll(async ({ browser }) => {
  browserRef = browser;
  teacher = await (await browser.newContext({ viewport: { width: 1360, height: 900 } })).newPage();
  teacher.on("dialog", d => void d.accept());
});
test.afterAll(async () => { await teacher?.context().close(); await mock("/__reset"); });

test("import: the paper and its mark scheme are read", async () => {
  test.setTimeout(120_000);
  await mock("/__reset");
  await mock("/__script", { cambridgePaperReply: paperReply, cambridgeSchemeReply: schemeReply });
  await register(teacher, "Drawing Teacher", `${stamp}.teacher@example.test`);

  await teacher.goto("/CambridgeImport");
  await teacher.locator("#paperFile").setInputFiles(paperPdf());
  await teacher.locator('#paperUploadForm button[type="submit"]').click();
  importId = teacher.url().match(/CambridgeImport\/([0-9a-f-]+)/)![1];
  await expect(teacher.locator("[data-part]")).toHaveCount(4, { timeout: 30_000 });
  // Step 1 already shows which parts are drawn, and that one is unclear.
  await expect(teacher.locator('[data-part="8(b)"] [data-drawn]')).toHaveText(/drawn answer/);
  await expect(teacher.locator('[data-part="8(c)"] [data-drawn]')).toContainText("choose on review");
  await expect(teacher.locator('[data-part="8(a)"] [data-drawn]')).toHaveCount(0);   // uses the figure, answered in writing
  await expect.poll(async () => (await teacher.request.get(`/CambridgeImport/${importId}/Figure/q8`)).status(), { timeout: 20_000 }).toBe(200);

  await teacher.locator("#continueToMarkScheme").click();
  await teacher.locator("#schemeFile").setInputFiles(schemePdf());
  await teacher.locator("#uploadScheme").click();
  await expect(teacher.locator("#schemeSummary")).toBeVisible({ timeout: 30_000 });
  await expect(teacher.locator("#schemeSummary [data-matched]")).toHaveText("2");
  await expect(teacher.locator("#schemeSummary [data-needs-review]")).toHaveText("2");
});

test("an incomplete key: 1(a) needs review, names the missing result, and can be confirmed once completed", async () => {
  await teacher.locator("#continueToReview").click();
  await row(teacher, "1(a)").locator("a").click();
  await expect(teacher.locator("#detailStatus")).toHaveText(/Needs review/);
  const reason = teacher.locator("#reviewReason");
  await expect(reason).toContainText("asks for 2 separate results");
  await expect(reason).toContainText("Missing: Gradient of");
  await expect(reason).toContainText("CE = 2.5");
  expect(await reason.innerText()).not.toContain("$");                     // shown as maths, not raw LaTeX
  await shot(teacher, "cambridge-incomplete-key", false);

  await teacher.locator("#editKey summary").click();
  await teacher.locator("#editAnswerKey").fill("Gradient of $CE = 2.5$; Gradient of $DE = 2.1$");
  await teacher.locator("#saveKey").click();
  await expect(teacher.locator("#confirmPart")).toBeEnabled();
  await teacher.locator("#confirmPart").click();
  await expect(row(teacher, "1(a)")).toHaveAttribute("data-status", "Confirmed");
});

test("a drawn answer: an unclear format is chosen on review; the clear one is already a sketch-pad question", async () => {
  await row(teacher, "8(c)").locator("a").click();
  await expect(teacher.locator("#formatQuestion")).toContainText("was read as a written answer");
  await expect(teacher.locator("#formatDrawn")).toBeChecked();             // drawing proposed
  await expect(teacher.locator("#confirmPart")).toBeDisabled();            // not before the format is chosen
  await shot(teacher, "cambridge-answer-format", false);
  await teacher.locator("#formatDrawn").check();
  await teacher.locator("#saveFormat").click();
  await expect(teacher.locator("#formatQuestion")).toHaveCount(0);
  await expect(teacher.locator("#detailStatus")).toHaveText(/Matched/);
  await expect(teacher.locator("#confirmPart")).toBeEnabled();

  await row(teacher, "8(b)").locator("a").click();
  await expect(teacher.locator("#formatDrawn")).toBeChecked();
  await expect(teacher.locator("#formatQuestion")).toHaveCount(0);
  await row(teacher, "8(a)").locator("a").click();
  await expect(teacher.locator("#formatWritten")).toBeChecked();

  await teacher.goto(`/CambridgeImport/${importId}/Review`);
  await teacher.locator("#selectAllMatched").check();
  await expect(teacher.locator("#confirmSelected")).toContainText("Confirm 3 selected");
  await teacher.locator("#confirmSelected").click();
  await teacher.locator("#saveConfirmed").click();
  await expect(teacher.locator("#finishCounts")).toHaveText("4 parts saved · 0 waiting for review");
});

test("the student draws on the sketch pad over the figure and submits; the teacher sees the drawing", async () => {
  test.setTimeout(180_000);
  // The library question keeps the drawn format, and the teacher can see and change it there.
  await teacher.goto(`/ContentLibrary?cambridgeImport=${importId}`);
  await teacher.locator("table tbody tr").filter({ hasText: "sketch the graph of" }).first().locator('a[href*="/ContentLibrary/Edit/"]').first().click();
  await expect(teacher.locator("#examRequireDrawing")).toBeChecked();

  const invite = await createClass(teacher, className);
  const assignmentId = await createDraftAssignment(teacher, `Drawing practice ${stamp.slice(-4)}`, className);
  for (const text of ["State the range of", "On the diagram, sketch"]) {
    await teacher.goto(`/ContentLibrary?assignmentId=${assignmentId}&cambridgeImport=${importId}`);
    await teacher.locator("table tbody tr").filter({ hasText: text }).getByRole("button", { name: "Add" }).click();
    await expect(teacher).toHaveURL(new RegExp(`/Assignments/Edit/${assignmentId}`, "i"));
  }
  await expect(teacher.locator(".question-card")).toHaveCount(3);          // shared question + two parts
  await expect(teacher.locator('input[id^="requireDrawing_"]:checked')).toHaveCount(1);

  const student = await (await browserRef.newContext({ viewport: { width: 1280, height: 900 } })).newPage();
  student.on("dialog", d => void d.accept());
  await register(student, `Sam ${stamp.slice(-4)}`, `${stamp}.student@example.test`, invite);
  await teacher.reload();
  expect(await assignTo(teacher, assignmentId)).toContain("Assigned to 1 student");

  await mock("/__script", { sketchReply: { correct: true, marks: 2, confidence: 0.9, feedback: "Reflected in y = x." } });
  await student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
  const pad = student.locator(".question-panel .sketch-widget");
  await expect(pad).toHaveCount(1);
  expect(await pad.getAttribute("data-bg")).toMatch(/\S/);                  // drawn over the question's figure
  const written = student.locator(".question-panel:not(:has(.sketch-widget)):not([data-header=\"true\"])");
  await written.locator(".answer-textarea").first().fill("-1 <= f(x) <= 3");
  const canvas = pad.locator("canvas");
  await expect.poll(() => canvas.evaluate(c => (c as HTMLCanvasElement).height)).toBeGreaterThan(0);
  await canvas.scrollIntoViewIfNeeded();
  const box = (await canvas.boundingBox())!;
  await student.mouse.move(box.x + box.width * 0.2, box.y + box.height * 0.8);
  await student.mouse.down();
  await student.mouse.move(box.x + box.width * 0.8, box.y + box.height * 0.2, { steps: 10 });
  await student.mouse.up();
  await expect(student.locator('input[id^="sketchpng_"]')).toHaveValue(/^data:image\/png;base64,/);
  await shot(student, "cambridge-student-sketch");
  await submitAssignment(student);
  await expect(student).toHaveURL(/AssignmentResult/, { timeout: 60_000 });
  await expect(student.locator('img[alt^="Your drawing"]')).toBeVisible();
  await student.context().close();

  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  const href = await teacher.locator('a[href*="/Assignments/SubmissionDetail"]').first().getAttribute("href");
  await teacher.goto(href!);
  const drawing = teacher.locator('img[alt="Student\'s drawing"]');
  await expect(drawing).toBeVisible();
  const png = await teacher.request.get((await drawing.getAttribute("src"))!);
  expect(png.status()).toBe(200);
  expect(png.headers()["content-type"]).toContain("image/png");
  expect(await drawing.evaluate((img: HTMLImageElement) => img.naturalWidth)).toBeGreaterThan(0);
  await shot(teacher, "cambridge-teacher-sees-drawing");
});
