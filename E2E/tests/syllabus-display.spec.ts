import { expect, test, type Browser, type Page } from "@playwright/test";
import { existsSync, mkdirSync, readFileSync } from "node:fs";
import { basename, resolve } from "node:path";
import { createClass, createDraftAssignment, signInAs, unique, assignTo } from "./helpers";

// Cambridge 9709 syllabus topics, end to end through the UI: an administrator imports the official syllabus PDF,
// checks the preview and publishes it; a teacher writes an ordinary question with their own topic and an original
// question aligned to "Quadratics" (found by typing "quad"), reopens it, finds it with the Content Library filters,
// puts both in one assignment, and a student opens it. The PDF is Cambridge's document and is not stored in the
// repository: set E2E_SYLLABUS_PDF to its path (e.g. the 2026–2027 syllabus). Without it this spec is skipped.
test.describe.configure({ mode: "serial" });

const pdf = process.env.E2E_SYLLABUS_PDF;
const shots = process.env.E2E_SHOT_DIR;
const password = "Syllabus-Pass-123";
const stamp = unique("syl").replace(/\s/g, "");
const className = unique("Further Maths");

let browserRef: Browser;
let admin: Page;
let teacher: Page;
let ordinaryId = "";
let cambridgeId = "";
let assignmentId = "";
let invite = "";

async function shot(page: Page, file: string): Promise<void> {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  await page.waitForTimeout(300);
  await page.screenshot({ path: resolve(shots, `${file}.png`), fullPage: true });
}
async function register(page: Page, name: string, email: string, inviteCode?: string): Promise<void> {
  await page.goto(inviteCode ? "/Auth/StudentRegister" : "/Auth/Register");
  if (inviteCode) await page.locator('input[name="inviteCode"]').fill(inviteCode);
  await page.locator('input[name="fullName"]').fill(name);
  await page.locator('input[name="email"]').fill(email);
  await page.locator('input[name="password"]').fill(password);
  await page.locator('input[name="confirmPassword"]').fill(password);
  await page.locator('button[type="submit"]').click();
  await page.goto("/Auth/Login");
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page).not.toHaveURL(/\/Auth\/Login/);
}
async function createLibraryQuestion(page: Page, text: string, answer: string, subject: string): Promise<void> {
  await page.locator("#questionEditor").fill(text);
  await page.locator('input[name="answerKey"]').fill(answer);
  await page.locator('input[name="subject"]').fill(subject);
}
const picker = (page: Page) => page.locator("#syllabusPicker");
const libraryRows = (page: Page) => page.locator("table tbody tr");

test.beforeAll(async ({ browser }) => {
  test.skip(!pdf || !existsSync(pdf), "Set E2E_SYLLABUS_PDF to the Cambridge 9709 syllabus PDF to run this spec.");
  browserRef = browser;
  admin = await (await browser.newContext({ viewport: { width: 1360, height: 900 } })).newPage();
  admin.on("dialog", d => void d.accept());
  teacher = await (await browser.newContext({ viewport: { width: 1360, height: 900 } })).newPage();
  teacher.on("dialog", d => void d.accept());
});
test.afterAll(async () => { await admin?.context().close(); await teacher?.context().close(); });

test("admin: import shows a preview by component with PDF pages, publish, and importing again adds nothing", async () => {
  test.setTimeout(120_000);
  await signInAs(admin, "admin");
  await admin.goto("/Syllabus");
  await admin.locator("#syllabusFile").setInputFiles(pdf!);
  await admin.locator("#syllabusSource").fill("https://www.cambridgeinternational.org/Images/697427-2026-2027-syllabus.pdf");
  await admin.locator('#syllabusImportForm button[type="submit"]').click();

  await expect(admin).toHaveURL(/\/Syllabus\/Review\//);
  await expect(admin.locator("#syllabusStatus")).toContainText("Draft");
  await expect(admin.locator("h4")).toContainText("9709 · 2026–2027");
  const components = admin.locator("[data-component]");
  await expect(components).toHaveCount(6);
  await expect(components.locator("h5")).toHaveText([
    "1 · Pure Mathematics 1", "2 · Pure Mathematics 2", "3 · Pure Mathematics 3",
    "4 · Mechanics", "5 · Probability & Statistics 1", "6 · Probability & Statistics 2"]);
  const counts = await components.evaluateAll(cs => cs.map(c => c.querySelectorAll("tr[data-topic]").length));
  expect(counts).toEqual([8, 6, 9, 5, 5, 5]);
  const quad = admin.locator('[data-component="1"] tr[data-topic="Quadratics"]');
  await expect(quad.locator('input[name^="number_"]')).toHaveValue("1.1");
  await expect(quad.locator('input[name^="page_"]')).toHaveValue("19");
  await shot(admin, "syllabus-import-preview");

  await admin.locator("#syllabusPublish").click();
  await expect(admin.locator("#syllabusStatus")).toContainText("Published");

  // The same file again: nothing new, the existing version opens.
  await admin.goto("/Syllabus");
  await admin.locator("#syllabusFile").setInputFiles(pdf!);
  await admin.locator('#syllabusImportForm button[type="submit"]').click();
  await expect(admin.locator("#syllabusInfo")).toContainText("already in the library");
  await expect(admin.locator("#syllabusDifferentFile")).toHaveCount(0);

  // A different file for the same exam years (here: the same PDF with a trailing comment, so its bytes differ):
  // the admin is told the files differ and which edition is kept; nothing is replaced.
  const altered = Buffer.concat([readFileSync(pdf!), Buffer.from("\n% different copy for the e2e check\n")]);
  await admin.goto("/Syllabus");
  await admin.locator("#syllabusFile").setInputFiles({ name: "9709-other-copy.pdf", mimeType: "application/pdf", buffer: altered });
  await admin.locator('#syllabusImportForm button[type="submit"]').click();
  const notice = admin.locator("#syllabusDifferentFile");
  await expect(notice).toContainText("different from the 2026–2027 syllabus already in the library");
  await expect(notice).toContainText(`Kept: ${basename(pdf!)}`);
  await expect(notice).toContainText("published");
  await expect(notice).toContainText("Uploaded: 9709-other-copy.pdf");
  await expect(notice).toContainText("question links are unchanged");
  await expect(admin.locator("#syllabusStatus")).toContainText("Published");
  await expect(admin.locator("[data-component] tr[data-topic]")).toHaveCount(38);

  await admin.goto("/Syllabus");
  await expect(admin.locator("#syllabusVersions tbody tr")).toHaveCount(1);
  await expect(admin.locator("#syllabusVersions tbody tr")).toContainText(basename(pdf!));
});

test("teacher: an ordinary question needs no extra step; a Cambridge one is linked by typing \"quad\"", async () => {
  test.setTimeout(120_000);
  await register(teacher, "Syllabus Teacher", `${stamp}.teacher@example.test`);

  // Ordinary question with the teacher's own topic: the usual path, nothing else to fill in.
  await teacher.goto("/ContentLibrary/Create");
  await createLibraryQuestion(teacher, `${stamp} Simplify 3/6`, "1/2", "Fractions");
  await expect(picker(teacher).locator("[data-sp-open]")).toHaveText(/Link to Cambridge 9709/);
  await teacher.getByRole("button", { name: "Save Question" }).click();
  await expect(teacher).toHaveURL(/\/ContentLibrary\/?(\?.*)?$/);

  // Original question aligned to the syllabus.
  await teacher.goto("/ContentLibrary/Create");
  await createLibraryQuestion(teacher, `${stamp} Solve x^2 - 5x + 6 = 0`, "2, 3", "My quadratics unit");
  await picker(teacher).locator("[data-sp-open]").click();
  await expect(picker(teacher).locator("[data-sp-version-wrap]")).toBeHidden();      // one published version: chosen for you
  const search = picker(teacher).locator("[data-sp-search]");
  await expect(search).toBeFocused();
  await search.fill("quad");
  const options = picker(teacher).locator('[role="option"]');
  await expect(options).toHaveCount(1);
  await expect(options.first()).toHaveText(/Quadratics\s*· Pure Mathematics 1/);

  // Same-named topics in different components are separate choices.
  await search.fill("trig");
  await expect(options).toHaveCount(3);
  await expect(options).toHaveText([/Trigonometry\s*· Pure Mathematics 1/, /Trigonometry\s*· Pure Mathematics 2/, /Trigonometry\s*· Pure Mathematics 3/]);
  // Narrowing by component, and an empty result offers a way out.
  await picker(teacher).locator("[data-sp-component]").selectOption({ label: "4 · Mechanics" });
  await expect(options).toHaveCount(0);
  await expect(picker(teacher).locator("[data-sp-empty]")).toContainText("Change the search or choose All components");
  await picker(teacher).locator("[data-sp-component]").selectOption({ label: "All components" });

  await search.fill("quad");
  await shot(teacher, "syllabus-topic-picker");
  await search.press("Enter");                                                        // keyboard choice; does not submit the form
  await expect(teacher).toHaveURL(/\/ContentLibrary\/Create/);
  await expect(picker(teacher).locator("[data-sp-label]")).toHaveText("9709 · 2026–2027 · Pure Mathematics 1 · Quadratics");
  await expect(picker(teacher).locator("[data-sp-aligned]")).toHaveText("Aligned to Cambridge 9709");
  await shot(teacher, "syllabus-topic-chosen");
  await teacher.getByRole("button", { name: "Save Question" }).click();
  await expect(teacher).toHaveURL(/\/ContentLibrary\/?(\?.*)?$/);

  const row = libraryRows(teacher).filter({ hasText: "Solve x^2" });
  await expect(row.locator("[data-syllabus-label]")).toHaveText("9709 · P1 · Quadratics");
  cambridgeId = (await row.locator('a[href^="/ContentLibrary/Edit/"]').getAttribute("href"))!.split("/").pop()!;
  ordinaryId = (await libraryRows(teacher).filter({ hasText: "Simplify 3/6" }).locator('a[href^="/ContentLibrary/Edit/"]').getAttribute("href"))!.split("/").pop()!;
  await expect(libraryRows(teacher).filter({ hasText: "Simplify 3/6" }).locator("[data-syllabus-label]")).toHaveCount(0);

  // Reopened: the exact choice is restored, next to the teacher's own topic.
  await teacher.goto(`/ContentLibrary/Edit/${cambridgeId}`);
  await expect(picker(teacher).locator("[data-sp-label]")).toHaveText("9709 · 2026–2027 · Pure Mathematics 1 · Quadratics");
  await expect(teacher.locator('input[name="subject"]')).toHaveValue("My quadratics unit");
});

test("teacher: Content Library filters by syllabus, component and topic, together with search", async () => {
  await teacher.goto("/ContentLibrary");
  await expect(libraryRows(teacher)).toHaveCount(2);
  await teacher.locator("#filterSyllabusComponent").selectOption({ label: "1 · Pure Mathematics 1" });
  await expect(teacher).toHaveURL(/syllabusComponent=/);
  await teacher.locator("#filterSyllabusTopic").selectOption({ label: "Quadratics" });
  await expect(teacher).toHaveURL(/syllabusTopic=/);
  await expect(libraryRows(teacher)).toHaveCount(1);
  await expect(libraryRows(teacher).first()).toContainText("Solve x^2");
  await expect(teacher.getByText("1 question found")).toBeVisible();
  await shot(teacher, "syllabus-filtered-bank");

  // With search: no match, and a clear way back.
  await teacher.locator('input[name="search"]').fill("Simplify");
  await teacher.locator('#libraryFilters button[type="submit"]').click();
  await expect(teacher.locator("#libraryNoMatch")).toBeVisible();
  await teacher.locator("#libraryNoMatch a").click();
  await expect(libraryRows(teacher)).toHaveCount(2);
});

test("both questions in one assignment; the student sees two ordinary questions", async () => {
  test.setTimeout(180_000);
  invite = await createClass(teacher, className);
  assignmentId = await createDraftAssignment(teacher, `Mixed practice ${stamp.slice(-4)}`, className);
  await teacher.goto(`/ContentLibrary?assignmentId=${assignmentId}`);
  for (const text of ["Simplify 3/6", "Solve x^2"]) {
    await libraryRows(teacher).filter({ hasText: text }).getByRole("button", { name: "Add" }).click();
    await expect(teacher).toHaveURL(new RegExp(`/Assignments/Edit/${assignmentId}`, "i"));
    await teacher.goto(`/ContentLibrary?assignmentId=${assignmentId}`);
  }
  await teacher.goto(`/Assignments/Edit/${assignmentId}`);
  await expect(teacher.locator(".question-card")).toHaveCount(2);
  await expect(teacher.locator(".question-card [data-syllabus-label]")).toHaveText(["9709 · P1 · Quadratics"]);

  const student = await (await browserRef.newContext({ viewport: { width: 390, height: 844 } })).newPage();
  student.on("dialog", d => void d.accept());
  await register(student, `Sami ${stamp.slice(-4)}`, `${stamp}.student@example.test`, invite);
  await teacher.reload();                                    // the assign panel lists the class as it was when loaded
  expect(await assignTo(teacher, assignmentId)).toContain("Assigned to 1 student");
  await student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
  await expect(student.locator(".question-panel")).toHaveCount(2);
  await expect(student.locator("body")).not.toContainText("Cambridge");
  await expect(student.locator("body")).not.toContainText("Quadratics");
  await student.context().close();
});

test("removing the link keeps the question, its answer and the teacher's own topic; phone width works", async () => {
  await teacher.setViewportSize({ width: 390, height: 844 });
  await teacher.goto(`/ContentLibrary/Edit/${cambridgeId}`);
  const overflow = await teacher.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(1);
  await picker(teacher).getByRole("button", { name: "Remove link" }).click();
  await expect(picker(teacher).locator("[data-sp-open]")).toBeVisible();
  await teacher.getByRole("button", { name: /Save|Update/ }).last().click();
  await expect(teacher).toHaveURL(/\/ContentLibrary\/?(\?.*)?$/);
  await teacher.setViewportSize({ width: 1360, height: 900 });

  await teacher.goto(`/ContentLibrary/Edit/${cambridgeId}`);
  await expect(picker(teacher).locator("[data-sp-open]")).toBeVisible();
  await expect(teacher.locator('input[name="subject"]')).toHaveValue("My quadratics unit");
  await expect(teacher.locator('input[name="answerKey"]')).toHaveValue("2, 3");
  await teacher.goto(`/ContentLibrary/Edit/${ordinaryId}`);
  await expect(teacher.locator('input[name="subject"]')).toHaveValue("Fractions");
});
