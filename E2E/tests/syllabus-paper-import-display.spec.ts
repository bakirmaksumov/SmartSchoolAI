import { expect, test, type Browser, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { assignTo, createClass, createDraftAssignment, signIn, signInAs, unique } from "./helpers";

// "Import Cambridge Paper" in the Content Library, end to end through the browser: a teacher reads a Question Paper,
// fixes a topic, leaves and comes back, gets a mismatching mark scheme refused, survives an AI failure, reviews and
// confirms answer keys, saves part of the paper and later the rest without duplicates, and puts saved parts into an
// assignment a student opens. The PDFs are generated TEST DATA shaped like a Cambridge 9709 paper, and the readings
// come from the mock AI (E2E/scripts/mock-ai.mjs), so this runs without paid AI and without Cambridge's documents.
// It runs after syllabus-display.spec.ts (file order): the 9709 2026–2027 syllabus it needs is either the real one that
// spec imported or, when that one is skipped, the generated one imported here.
test.describe.configure({ mode: "serial" });
// Publishing the syllabus needs the seeded admin; the container run registers its accounts through the UI and has
// none, so there this spec is skipped (cambridge-answer-checks-display.spec.ts covers the import without an admin).
test.skip(!process.env.E2E_ADMIN_PASSWORD, "Needs the seeded admin account (E2E_ADMIN_PASSWORD) to publish the syllabus.");

const mockAi = process.env.E2E_MOCK_AI_URL ?? `http://127.0.0.1:${process.env.E2E_MOCK_AI_PORT ?? 5199}`;
const shots = process.env.E2E_SHOT_DIR;
const password = "Paper-Import-123";
const stamp = unique("cip").replace(/\s/g, "");
const className = unique("Pure Maths");

let browserRef: Browser;
let teacher: Page;
let importId = "";

// ── Generated TEST DATA documents ───────────────────────────────────────────────────────────────────────────────
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

const syllabusPdf = () => file("TEST-9709-syllabus.pdf", [
  "TEST DATA - not a real syllabus\nCambridge International\nAS & A Level\nMathematics 9709\nUse this syllabus for exams in 2026 and 2027.\nVersion 5",
  "Contents\n3 Subject content ........ 18",
  "3 Subject content\n1 Pure Mathematics 1 (for Paper 1)\n1.1 Quadratics\nCandidates should be able to:\n1.2 Functions\nCandidates should be able to:\n1.3 Coordinate geometry\nCandidates should be able to:\n1.4 Circular measure\nCandidates should be able to:",
  "1.5 Trigonometry\nCandidates should be able to:\n1.6 Series\nCandidates should be able to:\n1.7 Differentiation\nCandidates should be able to:\n1.8 Integration\nCandidates should be able to:",
  "4 Details of the assessment",
]);
const paperPdf = () => file("TEST-9709_s26_qp_12.pdf", [
  "TEST DATA - not a Cambridge paper\nCambridge International AS & A Level\nMATHEMATICS 9709/12\nPaper 1 Pure Mathematics 1 May/June 2026\n1 hour 50 minutes",
  "2 Functions f and g are defined by f(x) = 3x + 2 and g(x) = 4x - 12.\nSolve the equation f^-1(x) = gf(x). [4]\n9709/12/M/J/26",
  "10 The circle x^2 + y^2 + 4x - 2y - 20 = 0 has centre C.\n(a) State the coordinates of C. [1]\n(b) It is given that the midpoint of AB is D(1.5, 1.5).\n(i) Find the equation of AB. [4]\n(ii) Find the x-coordinates of A and B. [3]",
  "12\n        y\n        |      ___\n        |    /     \\\n   -----O---a-------b----- x\n\nThe diagram shows the curve y = x(x - 2)^2.\n(a) State the value of a. [1]\n(b) Calculate the value of b. [4]\n(c) Find the area of the shaded region. [4]",
]);
const schemePdf = (code: string, name: string) => file(name, [
  `TEST DATA - not a Cambridge mark scheme\nCambridge International AS & A Level\nMATHEMATICS ${code}\nPaper 1 Pure Mathematics 1 May/June 2026\nMARK SCHEME\nMaximum Mark: 75`,
  "Question Answer Marks Partial Guidance\n2 (x-2)/3 = 12x - 4 M1 ...", "10(a) (-2, 1) 1 B1", "12(a) a = 2 1 B1",
]);

const paperReply = {
  questions: [
    { number: 2, stem: null, figure: null, parts: [{ label: "", text: "Functions $f$ and $g$ are defined by $f(x) = 3x + 2$ and $g(x) = 4x - 12$. Solve the equation $f^{-1}(x) = gf(x)$.", marks: 4, page: 2, kind: "ShortAnswer", topic: "1.2", topicConfidence: "high" }] },
    { number: 10, stem: "The circle $x^{2} + y^{2} + 4x - 2y - 20 = 0$ has centre $C$.", figure: null, parts: [
      { label: "(a)", text: "State the coordinates of $C$.", marks: 1, page: 3, topic: "1.3", topicConfidence: "high" },
      { label: "(b)(i)", text: "It is given that the midpoint of $AB$ is $D(1.5, 1.5)$. Find the equation of $AB$.", marks: 4, page: 3, topic: "1.3", topicConfidence: "high" },
      { label: "(b)(ii)", text: "It is given that the midpoint of $AB$ is $D(1.5, 1.5)$. Find the $x$-coordinates of $A$ and $B$.", marks: 3, page: 3, topic: "1.3", topicConfidence: "high" }] },
    { number: 12, stem: "The diagram shows the curve $y = x(x-2)^{2}$.", figure: { page: 4, box: [0.05, 0.03, 0.75, 0.2], description: "The curve with its minimum at a and maximum at b" }, parts: [
      { label: "(a)", text: "State the value of $a$.", marks: 1, page: 4, usesFigure: true, topic: "1.7", topicConfidence: "high" },
      { label: "(b)", text: "Calculate the value of $b$.", marks: 4, page: 4, usesFigure: true, topic: "1.7", topicConfidence: "high" },
      { label: "(c)", text: "Find the area of the shaded region.", marks: 4, page: 4, usesFigure: true, topic: "1.8", topicConfidence: "low" }] },
  ],
};
const schemeReply = {
  rows: [
    { question: "12(a)", appliesTo: ["12(a)"], answer: "$a = 2$", codes: "B1", marks: 1, final: true, page: 4 },
    { question: "10(b)(i)", appliesTo: ["10(b)(i)"], answer: "Gradient $CD = \\frac{1}{7}$", codes: "M1", marks: 1, page: 3 },
    { question: "10(b)(i)", appliesTo: ["10(b)(i)"], answer: "Gradient $AB = -7$", codes: "A1", marks: 1, page: 3 },
    { question: "10(b)(i)", appliesTo: ["10(b)(i)"], answer: "$y - 1.5 = -7(x - 1.5)$", codes: "M1", marks: 1, guidance: "With gradient $-1/$their $m$", page: 4 },
    { question: "10(b)(i)", appliesTo: ["10(b)(i)"], answer: "$y = -7x + 12$", codes: "A1", marks: 1, final: true, page: 4 },
    { question: "10(a)", appliesTo: ["10(a)"], answer: "$(-2, 1)$", codes: "B1", marks: 1, final: true, page: 3 },
    { question: "10(b)(ii)", appliesTo: ["10(b)(ii)"], answer: "$x^{2} + (12 - 7x)^{2} + 4x - 2(12 - 7x) - 20 = 0$", codes: "M1", marks: 1, page: 4 },
    { question: "10(b)(ii)", appliesTo: ["10(b)(ii)"], answer: "$50x^{2} - 150x + 100 = 0$", codes: "A1", marks: 1, page: 4 },
    { question: "10(b)(ii)", appliesTo: ["10(b)(ii)"], answer: "$x = 1, x = 2$", codes: "A1", marks: 1, final: true, page: 4 },
    { question: "10(b)(ii)", appliesTo: ["10(b)(ii)"], answer: "Use $D$ as the midpoint: $x = 1.5 \\pm 0.5$", codes: "M1 A1", marks: 2, alternative: "Alternative method 1", page: 4 },
    { question: "12(b)", appliesTo: ["12(b)"], answer: "$\\frac{dy}{dx} = 3x^{2} - 8x + 4 = 0$", codes: "M1 A1", marks: 2, page: 4 },
    { question: "12(b)", appliesTo: ["12(b)"], answer: "$b = \\frac{2}{3}$", codes: "A1", marks: 1, final: true, page: 4 },
    { question: "2", appliesTo: ["2"], answer: "$\\frac{x-2}{3} = 12x - 4$", codes: "M1", marks: 1, page: 2 },
    { question: "2", appliesTo: ["2"], answer: "$x = \\frac{2}{7}$", codes: "M1 A1 A1", marks: 3, final: true, page: 2 },
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
const part = (page: Page, number: string) => page.locator(`[data-part="${number}"]`);
const row = (page: Page, number: string) => page.locator(`#partList tr[data-row="${number}"]`);

test.beforeAll(async ({ browser }) => {
  browserRef = browser;
  teacher = await (await browser.newContext({ viewport: { width: 1360, height: 900 } })).newPage();
  teacher.on("dialog", d => void d.accept());
});
test.afterAll(async () => { await teacher?.context().close(); await mock("/__reset"); });

test("setup: a published 9709 2026–2027 syllabus, a teacher, scripted readings", async () => {
  test.setTimeout(120_000);
  await mock("/__reset");
  await mock("/__script", { cambridgePaperReply: paperReply, cambridgeSchemeReply: schemeReply });

  const admin = await (await browserRef.newContext()).newPage();
  await signInAs(admin, "admin");
  await admin.goto("/Syllabus");
  await admin.locator("#syllabusFile").setInputFiles(syllabusPdf());
  await admin.locator('#syllabusImportForm button[type="submit"]').click();
  await expect(admin).toHaveURL(/\/Syllabus\/Review\//);
  // Already published (the real syllabus from syllabus-display.spec.ts, or a previous run) — or publish ours now.
  if (await admin.locator("#syllabusPublish").count()) await admin.locator("#syllabusPublish").click();
  await expect(admin.locator("#syllabusStatus")).toContainText("Published");
  await admin.context().close();

  await register(teacher, "Paper Teacher", `${stamp}.teacher@example.test`);
});

test("an ordinary question is still created the usual way, without any paper", async () => {
  await teacher.goto("/ContentLibrary/Create");
  await teacher.locator("#questionEditor").fill("Expand (x + 1)(x + 2)");
  await teacher.locator('input[name="answerKey"]').fill("x^2 + 3x + 2");
  await teacher.locator('input[name="subject"]').fill("Algebra");
  await teacher.getByRole("button", { name: /Save/ }).last().click();
  await expect(teacher).toHaveURL(/\/ContentLibrary\/?(\?.*)?$/);
  await expect(teacher.locator("table tbody tr").filter({ hasText: "Expand (x + 1)(x + 2)" })).toHaveCount(1);
});

test("step 1: the paper is read into nested parts with topics; a topic is fixed; the draft survives leaving", async () => {
  test.setTimeout(120_000);
  await teacher.goto("/ContentLibrary");
  await teacher.locator("#importCambridgeBtn").click();
  await expect(teacher.locator("#importSteps [data-step]")).toHaveCount(4);
  await teacher.locator("#paperFile").setInputFiles(paperPdf());
  await teacher.locator('#paperUploadForm button[type="submit"]').click();
  await expect(teacher).toHaveURL(/\/CambridgeImport\/[0-9a-f-]+\/Paper/);
  importId = teacher.url().match(/CambridgeImport\/([0-9a-f-]+)/)![1];

  // Detected details for the teacher to check.
  await expect(teacher.locator("#dComponent")).toHaveValue("1");
  await expect(teacher.locator("#dVariant")).toHaveValue("2");
  await expect(teacher.locator("#dSession")).toHaveValue("May/June");
  await expect(teacher.locator("#dYear")).toHaveValue("2026");
  await expect(teacher.locator("#importLabel")).toContainText("9709/12 · May/June 2026");
  // Questions shown at once, down to 10(b)(ii), with the suggested topic under each.
  await expect(teacher.locator("[data-part]")).toHaveCount(7, { timeout: 30_000 });
  await expect(part(teacher, "10(b)(ii)")).toContainText("Find the");
  await expect(part(teacher, "10(a)").locator("[data-topic-label]")).toHaveText("9709 · Pure Mathematics 1 · 1.3 Coordinate geometry");
  await expect(part(teacher, "12(c)").locator("[data-topic-label]")).toContainText("No topic yet");
  // The shared figure of question 12 is cut out of the PDF once and then stored.
  const q12 = teacher.locator('[data-question="12"]');
  await expect(q12.locator("[data-figure]")).toContainText("shared by every part");
  await expect.poll(async () => (await teacher.request.get(`/CambridgeImport/${importId}/Figure/q12`)).status(), { timeout: 20_000 }).toBe(200);
  // Formulas are shown as maths, never as raw LaTeX.
  const stem = teacher.locator('[data-question="10"] .ci-stem');
  await expect(stem).toHaveAttribute("data-latex-source", /x\^\{2\}/);
  expect(await stem.innerText()).not.toMatch(/[$\^{}]/);

  // Choose the topic for 12(c) with the keyboard.
  await part(teacher, "12(c)").locator("[data-change-topic]").click();
  const search = part(teacher, "12(c)").locator("[data-sp-search]");
  await search.fill("integ");
  await search.press("Enter");
  await expect(teacher).toHaveURL(/#part-/);
  await expect(part(teacher, "12(c)").locator("[data-topic-label]")).toHaveText("9709 · Pure Mathematics 1 · 1.8 Integration");
  await shot(teacher, "cambridge-step1-question-paper");

  // Save draft & finish later; the Content Library offers to continue.
  await teacher.locator("#saveDraftLater").click();
  await expect(teacher).toHaveURL(/\/ContentLibrary/);
  const draft = teacher.locator(`#cambridgeDrafts [data-draft="${importId}"]`);
  await expect(draft).toContainText("9709/12 · May/June 2026");
  await expect(draft.locator("[data-draft-step]")).toHaveText("Last completed: Question Paper");
  await draft.getByRole("link", { name: "Continue import" }).click();
  await expect(teacher).toHaveURL(new RegExp(`/CambridgeImport/${importId}/MarkScheme`));
  // Back to step 1: the corrected topic and the parts are still there, and nothing was read again.
  await teacher.locator('#importSteps [data-step="1"] a').click();
  await expect(part(teacher, "12(c)").locator("[data-topic-label]")).toHaveText("9709 · Pure Mathematics 1 · 1.8 Integration");
  await expect(teacher.locator("[data-part]")).toHaveCount(7);
  const calls = await (await fetch(`${mockAi}/__requests?kind=cambridge-paper`)).json();
  expect(calls).toHaveLength(1);
});

test("step 2: a mark scheme for another variant is refused; an AI failure can be retried; then parts match", async () => {
  test.setTimeout(120_000);
  await teacher.goto(`/CambridgeImport/${importId}/MarkScheme`);
  await teacher.locator("#schemeFile").setInputFiles(schemePdf("9709/13", "TEST-9709_s26_ms_13.pdf"));
  await teacher.locator("#uploadScheme").click();
  await expect(teacher.locator("#schemeConflict")).toContainText("Variant: paper 2, mark scheme 3");
  await expect(teacher.locator('#detailsCompare tr[data-detail="Variant"]')).toContainText("differs");
  expect(await (await fetch(`${mockAi}/__requests?kind=cambridge-ms`)).json()).toHaveLength(0);   // nothing was read

  // The right mark scheme while the AI is down: the draft stays, "Try again" continues from there.
  await mock("/__script", { cambridgeStatus: 503 });
  await teacher.locator("#schemeFile").setInputFiles(schemePdf("9709/12", "TEST-9709_s26_ms_12.pdf"));
  await teacher.locator("#uploadScheme").click();
  await expect(teacher.locator("#schemeError")).toContainText("temporarily unavailable", { timeout: 30_000 });
  await mock("/__script", { cambridgeStatus: null });
  await teacher.locator("#retryScheme").click();
  await expect(teacher.locator("#schemeSummary")).toBeVisible({ timeout: 30_000 });
  await expect(teacher.locator("#schemeSummary [data-matched]")).toHaveText("5");
  await expect(teacher.locator("#schemeSummary [data-needs-review]")).toHaveText("2");
  await shot(teacher, "cambridge-step2-mark-scheme");
  expect(await (await fetch(`${mockAi}/__requests?kind=cambridge-paper`)).json()).toHaveLength(1);   // the paper was not read again
});

test("step 3: nested part, shared figure, alternative method and a disputed row; edit, confirm, bulk confirm, save some", async () => {
  test.setTimeout(120_000);
  await teacher.locator("#continueToReview").click();
  await expect(teacher.locator("#reviewTabs")).toContainText("Needs review (2)");

  // Nested part with an alternative method kept apart from the main method.
  await row(teacher, "10(b)(ii)").locator("a").click();
  await expect(teacher.locator("#detailStatus")).toHaveText(/Matched/);
  await expect(teacher.locator("#detailStem")).toBeVisible();
  await expect(teacher.locator("#detailCriteria")).toContainText("Alternative method 1 (instead of the method above):");
  expect(await teacher.locator("#detailKey").innerText()).not.toMatch(/[$\^{}]/);

  // Shared figure on another part of question 12.
  await row(teacher, "12(a)").locator("a").click();
  await expect(teacher.locator("#detailFigure")).toBeVisible();

  // The disputed row: marks differ. View source, then fix and confirm it.
  await teacher.locator("#needsReviewTab").click();
  await expect(teacher.locator("#partList tbody tr")).toHaveCount(2);
  await row(teacher, "12(b)").locator("a").focus();
  await teacher.keyboard.press("Enter");                                   // keyboard navigation through the list
  await expect(teacher.locator("#reviewReason")).toContainText("Marks differ: paper [4], mark scheme 3.");
  const source = teacher.locator('#sourceLinks a[href*="/File/markscheme#page=4"]');
  await expect(source).toBeVisible();
  const ms = await teacher.request.get(`/CambridgeImport/${importId}/File/markscheme`);
  expect(ms.headers()["content-type"]).toContain("application/pdf");
  await shot(teacher, "cambridge-step3-review", false);   // the screen as the teacher sees it, action bar at the bottom
  await teacher.locator("#editKey summary").click();
  await teacher.locator("#editCriteria").fill("M1 A1  $\\frac{dy}{dx} = 3x^{2} - 8x + 4 = 0$\nA1  $b = \\frac{2}{3}$\nB1  reject $x = 2$ (the minimum)");
  await teacher.locator("#saveKey").click();
  await expect(teacher.locator("#partDetail")).toContainText("Answer key (edited)");
  await teacher.locator("#confirmPart").click();

  // Bulk: the clear-cut rows, with the count on the button.
  await teacher.goto(`/CambridgeImport/${importId}/Review`);
  await teacher.locator("#selectAllMatched").check();
  await expect(teacher.locator("#confirmSelected")).toContainText("Confirm 5 selected");
  await row(teacher, "10(a)").locator("input[data-bulk]").uncheck();
  await expect(teacher.locator("#confirmSelected")).toContainText("Confirm 4 selected");
  await teacher.locator("#confirmSelected").click();
  await expect(teacher.locator("#saveConfirmed")).toHaveText("Save 5 confirmed parts");

  // Save: step 4 opens by itself with the real numbers.
  await teacher.locator("#saveConfirmed").click();
  await expect(teacher).toHaveURL(new RegExp(`/CambridgeImport/${importId}/Finish`));
  await expect(teacher.locator("#finishCounts")).toHaveText("5 parts saved · 2 waiting for review");
  await shot(teacher, "cambridge-step4-finish");

  // View questions opens exactly what was saved.
  await teacher.locator("#viewQuestions").click();
  await expect(teacher.locator("#importedFilter")).toBeVisible();
  await expect(teacher.locator("table tbody tr")).toHaveCount(5);
  await expect(teacher.locator("table tbody tr [data-source-label]").first()).toContainText("9709/12 · May/June 2026");
});

test("later: the rest is finished without duplicates; a second save and another teacher change nothing", async () => {
  test.setTimeout(120_000);
  await teacher.goto("/ContentLibrary");
  await teacher.locator(`#cambridgeDrafts [data-draft="${importId}"]`).getByRole("link", { name: "Continue import" }).click();
  await expect(teacher).toHaveURL(new RegExp(`/CambridgeImport/${importId}/Review`));
  for (const n of ["10(a)"]) {
    await row(teacher, n).locator("a").click();
    if (await teacher.locator("#confirmPart").isDisabled()) throw new Error("confirm disabled");
    await teacher.locator("#confirmPart").click();
  }
  await row(teacher, "12(c)").locator("a").click();
  await expect(teacher.locator("#reviewReason")).toContainText("No mark scheme row for 12(c).");
  await teacher.locator("#editKey summary").click();
  await teacher.locator("#editAnswerKey").fill("$\\frac{4}{3}$");
  await teacher.locator("#editCriteria").fill("M1 integrate $x^{3} - 4x^{2} + 4x$\nA1 $\\frac{4}{3}$");
  await teacher.locator("#saveKey").click();
  await teacher.locator("#confirmPart").click();
  await teacher.locator("#saveConfirmed").click();
  await expect(teacher.locator("#finishCounts")).toHaveText("7 parts saved · 0 waiting for review");

  // Pressing save again (e.g. a double click that got through) saves nothing new.
  await teacher.goto(`/CambridgeImport/${importId}/Review`);
  const token = await teacher.locator('input[name="__RequestVerificationToken"]').first().inputValue();
  await teacher.request.post(`/CambridgeImport/${importId}/Save`, { form: { __RequestVerificationToken: token } });
  await teacher.goto(`/ContentLibrary?cambridgeImport=${importId}`);
  await expect(teacher.locator("table tbody tr")).toHaveCount(7);
  // Uploading the same paper again opens this import instead of a new one.
  await teacher.goto("/CambridgeImport");
  await teacher.locator("#paperFile").setInputFiles(paperPdf());
  await teacher.locator('#paperUploadForm button[type="submit"]').click();
  await expect(teacher).toHaveURL(new RegExp(`/CambridgeImport/${importId}/`));
  await expect(teacher.locator("#importInfo")).toContainText("already imported this paper");

  // Another teacher: no access by direct link to the draft, its PDFs or figures.
  const other = await (await browserRef.newContext()).newPage();
  await register(other, "Other Teacher", `${stamp}.other@example.test`);
  for (const path of ["Review", "Paper", "File/paper", "File/markscheme", "Figure/q12", "Status"]) {
    const r = await other.request.get(`/CambridgeImport/${importId}/${path}`, { maxRedirects: 0 });
    expect(r.status(), path).toBe(404);
  }
  await other.context().close();
});

test("saved parts in an assignment: the student sees stem, formulas and figure; the teacher sees topic, key and criteria", async () => {
  test.setTimeout(180_000);
  const invite = await createClass(teacher, className);
  const assignmentId = await createDraftAssignment(teacher, `Paper 1 practice ${stamp.slice(-4)}`, className);
  await teacher.goto(`/ContentLibrary?assignmentId=${assignmentId}&cambridgeImport=${importId}`);
  for (const text of ["State the value of", "Calculate the value of"]) {
    await teacher.goto(`/ContentLibrary?assignmentId=${assignmentId}&cambridgeImport=${importId}`);
    await teacher.locator("table tbody tr").filter({ hasText: text }).getByRole("button", { name: "Add" }).click();
    await expect(teacher).toHaveURL(new RegExp(`/Assignments/Edit/${assignmentId}`, "i"));
  }
  // One shared question with the stem and figure, both parts under it.
  await expect(teacher.locator(".question-card")).toHaveCount(3);
  await expect(teacher.locator(".question-card [data-syllabus-label]").first()).toContainText("9709 · P1 · Differentiation");
  const body = await teacher.locator("body").innerText();
  expect(body).toContain("The diagram shows the curve");

  const student = await (await browserRef.newContext({ viewport: { width: 390, height: 844 } })).newPage();
  student.on("dialog", d => void d.accept());
  await register(student, `Lina ${stamp.slice(-4)}`, `${stamp}.student@example.test`, invite);
  await teacher.reload();
  expect(await assignTo(teacher, assignmentId)).toContain("Assigned to 1 student");
  await student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
  await expect(student.locator("body")).toContainText("The diagram shows the curve");
  const figure = student.locator('img[alt="Diagram"]').first();
  await expect(figure).toBeVisible();
  expect(await figure.evaluate((img: HTMLImageElement) => img.naturalWidth)).toBeGreaterThan(0);
  expect(await student.locator("body").innerText()).not.toContain("x(x-2)^{2}");   // maths, not raw LaTeX
  await expect(student.locator("body")).not.toContainText("$b = \\frac");     // no answer key for the student
  await expect(student.locator("body")).not.toContainText("M1 A1");           // nor the marking criteria
  await expect(student.locator("body")).not.toContainText("Differentiation"); // nor the teacher's topic
  await student.context().close();

  // The teacher's view of the part: key and criteria are what the marker uses.
  const json = await (await teacher.request.get(`/Assignments/Edit/${assignmentId}`)).text();
  expect(json).toContain("reject");
});

test("phone width: the review list and a part open one at a time, and the list position comes back", async () => {
  await teacher.setViewportSize({ width: 390, height: 844 });
  await teacher.goto(`/CambridgeImport/${importId}/Review`);
  await expect(teacher.locator("#partList")).toBeVisible();
  await expect(teacher.locator("#partDetail")).toBeHidden();
  await row(teacher, "12(b)").locator("a").click();
  await expect(teacher.locator("#partList")).toBeHidden();
  await expect(teacher.locator("#detailNumber")).toHaveText("12(b)");
  const overflow = await teacher.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(1);
  await teacher.locator(".ci-back-to-list").click();
  await expect(teacher).toHaveURL(/#row-/);
  await expect(row(teacher, "12(b)").locator("a")).toBeFocused();
  await teacher.setViewportSize({ width: 1360, height: 900 });
});

test("topics: a past paper without topics gets them from the nearest edition, without reading the files again", async () => {
  test.setTimeout(120_000);
  // A 2024 paper whose reading named no topics (as when no syllabus was published at the time).
  const pastPaper = file("TEST-9709_s24_qp_11.pdf", [
    `TEST DATA - not a Cambridge paper ${stamp}\nCambridge International AS & A Level\nMATHEMATICS 9709/11\nPaper 1 Pure Mathematics 1 May/June 2024`,
    "1 (a) Express 3y^2 - 12y - 15 in the form 3(y + a)^2 + b. [2]\n(b) Hence find the exact solutions of 3x^4 - 12x^2 - 15 = 0. [3]\n9709/11/M/J/24",
  ]);
  const noTopic = { topic: null, topicConfidence: null };
  await mock("/__script", {
    cambridgePaperReply: { questions: [
      { number: 1, stem: null, figure: null, parts: [
        { label: "(a)", text: "Express $3y^2 - 12y - 15$ in the form $3(y+a)^2 + b$.", marks: 2, page: 2, ...noTopic },
        { label: "(b)", text: "Hence find the exact solutions of the equation $3x^4 - 12x^2 - 15 = 0$.", marks: 3, page: 2, ...noTopic }] },
      { number: 4, stem: "The equation of a curve is $y = f(x)$.", figure: null, parts: [
        { label: "(a)", text: "Find the value of $k$.", marks: 1, page: 2, ...noTopic },
        { label: "(c)", text: "Deduce the value of $f'(2)$ using the values in the table.", marks: 1, page: 2, ...noTopic }] },
    ] },
    cambridgeTopicsReply: { topics: [
      { part: "1(a)", topic: "1.1 Quadratics", topicConfidence: "high" },
      { part: "1(b)", topic: "1.1 Quadratics", topicConfidence: "high" },
      { part: "4(a)", topic: "1.7 Differentiation", topicConfidence: "low" },
      { part: "4(c)", topic: "1.7 Differentiation", topicConfidence: "high" },
    ] },
  });
  await teacher.goto("/CambridgeImport");
  await teacher.locator("#paperFile").setInputFiles(pastPaper);
  await teacher.locator('#paperUploadForm button[type="submit"]').click();
  await expect(teacher.locator("[data-part]")).toHaveCount(4, { timeout: 30_000 });
  const pastId = teacher.url().match(/CambridgeImport\/([0-9a-f-]+)/)![1];

  // The paper keeps 2024; topics come from the nearest published edition, said once on the page.
  await expect(teacher.locator("#topicBasis")).toContainText("Paper 2024 · topics from the 9709 syllabus 2026–2027");
  await expect(teacher.locator("#topicBasisNearest")).toBeVisible();
  await expect(teacher.locator("#topicCount")).toContainText("0 of 4 parts have a topic");
  // The teacher chooses 4(c) before asking: that choice must stay.
  await part(teacher, "4(c)").locator("[data-change-topic]").click();
  const search = part(teacher, "4(c)").locator("[data-sp-search]");
  await search.fill("integ");
  await search.press("Enter");
  await expect(part(teacher, "4(c)").locator("[data-topic-label]")).toContainText("Integration");

  await teacher.locator("#suggestTopics").click();
  await expect(teacher.locator("#topicCount")).toContainText("3 of 4 parts have a topic", { timeout: 30_000 });
  await expect(part(teacher, "1(a)").locator("[data-topic-label]")).toContainText("1.1 Quadratics");
  await expect(part(teacher, "1(b)").locator("[data-topic-label]")).toContainText("1.1 Quadratics");
  await expect(part(teacher, "4(c)").locator("[data-topic-label]")).toContainText("Integration");          // kept
  await expect(part(teacher, "4(a)").locator("[data-topic-note]")).toContainText("Unsure: possibly 1.7 Differentiation");
  await shot(teacher, "cambridge-topics-suggested");
  // Stored text only: the paper was read once and no mark scheme was read; 4(c) was not even asked about.
  expect(await (await fetch(`${mockAi}/__requests?kind=cambridge-paper`)).json()).toHaveLength(2);
  const asked = await (await fetch(`${mockAi}/__requests?kind=cambridge-topics`)).json() as { user: string }[];
  expect(asked).toHaveLength(1);
  expect(asked[0].user).toContain("1.1 Quadratics");
  expect(asked[0].user).not.toContain("\"part\":\"4(c)\"");
  await teacher.request.post(`/CambridgeImport/${pastId}/Discard`, {
    form: { __RequestVerificationToken: await teacher.locator('input[name="__RequestVerificationToken"]').first().inputValue() },
  });
});

test("imported maths is shown as maths in the library list and previewed on the edit page; saving keeps the text", async () => {
  await teacher.goto(`/ContentLibrary?cambridgeImport=${importId}`);
  const titles = teacher.locator("[data-question-title]");
  await expect(titles).toHaveCount(7);
  for (const text of await titles.allInnerTexts()) expect(text, "library list").not.toMatch(/\$|\\frac|\^\{/);
  for (const text of await teacher.locator("[data-answer-key]").allInnerTexts()) expect(text, "answer column").not.toMatch(/\$|\\frac/);
  await expect(titles.filter({ hasText: "State the coordinates of C" })).toHaveAttribute("data-latex-source", /\$C\$/);
  // Where each question came from, readable: the paper, then the question and its marks.
  const source = teacher.locator("table tbody tr").filter({ hasText: "Calculate the value of" }).locator("[data-source-label]");
  await expect(source.locator(".lib-source-paper")).toHaveText("9709/12 · May/June 2026");
  await expect(source.locator(".lib-source-question")).toHaveText("Question 12(b) · 4 marks");
  // Notation from real Cambridge papers reads as maths (degrees, ordinals, an inverse function of a fraction).
  const readable = await teacher.evaluate((cases: string[]) => cases.map(c => (window as any).MathText.inline(c).text), [
    String.raw`rotated through $360^\circ$ about the $x$-axis`,
    String.raw`for $0^\circ \le x \le 360^\circ$`,
    String.raw`$10^{\text{th}}$ term = $25+9d=-49$`,
    String.raw`$\sin^{-1}\frac{10}{15} = 0.7297$`,
  ]);
  expect(readable).toEqual([
    "rotated through 360° about the x-axis",
    "for 0° ≤ x ≤ 360°",
    "10th term = 25 + 9d = −49",
    "sin⁻¹(10/15) = 0.7297",
  ]);
  await shot(teacher, "cambridge-library-maths");

  // The edit page: the source stays editable as written, and the preview shows what students see.
  const row = teacher.locator("table tbody tr").filter({ hasText: "Calculate the value of" });
  await row.locator('a[href*="/ContentLibrary/Edit/"]').first().click();
  await expect(teacher.locator("#questionEditor")).toContainText("$b$");
  await expect(teacher.locator("#titlePreview")).toBeVisible();
  expect(await teacher.locator("#titlePreview [data-preview-body]").innerText()).not.toContain("$");
  await expect(teacher.locator("#stemPreview")).toBeVisible();
  await expect(teacher.locator("#markSchemePreview")).toBeVisible();
  expect(await teacher.locator("#markSchemePreview [data-preview-body]").innerText()).not.toMatch(/\\frac/);
  await expect(teacher.locator("#answerKeyPreview")).toBeVisible();
  // Typing updates the preview.
  await teacher.locator("#answerKeyInput").fill("$b = \\frac{2}{3}$, reject $x < 0$");
  await expect(teacher.locator("#answerKeyPreview [data-preview-body]")).toContainText("reject x < 0");   // updated
  await expect(teacher.locator("#answerKeyPreview [data-preview-body]")).toContainText("b = 2/3");
  expect(await teacher.locator("#answerKeyPreview [data-preview-body]").innerText()).not.toMatch(/\$|\\frac|rac/);
  await shot(teacher, "cambridge-edit-preview");
  await teacher.getByRole("button", { name: /Save/ }).last().click();
  await expect(teacher).toHaveURL(/\/ContentLibrary/);
  // A "<" in a saved answer or title is kept as text, not read as markup, when the question is opened again.
  await teacher.goto(`/ContentLibrary?cambridgeImport=${importId}`);
  await expect(teacher.locator("[data-answer-key]").filter({ hasText: "reject" })).toContainText("x < 0");
});
