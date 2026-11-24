import { expect, test, type Browser, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { assignTo, createClass, signIn, submitAssignment, unique, expandStudent } from "./helpers";

// Giving work to students in one action, what happens to students who join the class later, batch approval in
// the class, and one student's work. Teacher and students are real accounts made for this run; no AI is needed.
test.describe.configure({ mode: "serial" });

const password = "Assign-Pass-123";
const stamp = unique("assign").replace(/\s/g, "");
const className = unique("E2E Year 12 IT");
const screenshotDir = process.env.E2E_SHOT_DIR;

let browserRef: Browser;
let teacher: Page;
let classId = "";
let inviteCode = "";
const students: Record<string, Page> = {};
const ids: Record<string, string> = {};   // assignment ids by short name

async function shot(page: Page, name: string): Promise<void> {
  if (!screenshotDir) return;
  mkdirSync(screenshotDir, { recursive: true });
  await page.waitForTimeout(600);   // let the page's fade-in and the dialog's animation finish
  await page.screenshot({ path: resolve(screenshotDir, `${name}.png`), fullPage: !(await page.locator(".modal.show").count()) });
}

async function registerStudent(name: string): Promise<Page> {
  const page = await (await browserRef.newContext()).newPage();
  const email = `${stamp}.${name.toLowerCase()}@example.test`;
  await page.goto("/Auth/StudentRegister");
  await page.locator('input[name="inviteCode"]').fill(inviteCode);
  await page.locator('input[name="fullName"]').fill(`${name} ${stamp.slice(-4)}`);
  await page.locator('input[name="email"]').fill(email);
  await page.locator('input[name="password"]').fill(password);
  await page.locator('input[name="confirmPassword"]').fill(password);
  await page.locator('button[type="submit"]').click();
  await expect(page).toHaveURL(/\/Auth\/Login/);
  await signIn(page, email, password);
  await expect(page).toHaveURL(/\/Student\/?$/);
  students[name] = page;
  return page;
}

const fullName = (name: string) => `${name} ${stamp.slice(-4)}`;

/** Makes a draft from the class page (class filled in), with one question, and returns its id. */
async function draft(key: string, options: { homework?: boolean; due?: string; questions?: Record<string, unknown>[] } = {}): Promise<string> {
  await teacher.goto(`/Classes/Details/${classId}?tab=assignments`);
  await teacher.locator("#newAssignmentForClass").click();
  await expect(teacher).toHaveURL(new RegExp(`/Assignments/Create\\?classId=${classId}`, "i"));
  await expect(teacher.locator("#classSelect")).toHaveValue(new RegExp(classId, "i"));
  await teacher.locator('input[name="title"]').fill(`${key} ${stamp}`);
  await teacher.locator("#typeSelect").selectOption(options.homework ? "1" : "0");
  if (options.due) await teacher.locator("#deadlineInput").fill(options.due);
  await teacher.getByRole("button", { name: "Save and add questions" }).click();
  await expect(teacher).toHaveURL(/\/Assignments\/Edit\/[0-9a-f-]+/i);
  const id = teacher.url().match(/\/Assignments\/Edit\/([0-9a-f-]+)/i)![1];

  const token = await teacher.locator('input[name="__RequestVerificationToken"]').first().inputValue();
  for (const q of options.questions ?? [{ title: "What is 2 + 2?", type: 0, answerKey: "4" }]) {
    const response = await teacher.request.post(`/Assignments/AddQuestion/${id}`, {
      form: { questionJson: JSON.stringify({ id: randomUUID(), difficulty: "Easy", ...q }), __RequestVerificationToken: token }
    });
    expect(response.ok(), "AddQuestion").toBe(true);
  }
  await teacher.goto(`/Assignments/Edit/${id}`);
  ids[key] = id;
  return id;
}

const title = (key: string) => `${key} ${stamp}`;

async function sees(student: string, key: string): Promise<boolean> {
  const page = students[student];
  await page.goto(`/Student/Assignments?classId=${classId}`);
  return (await page.getByText(title(key), { exact: true }).count()) > 0;
}

/**
 * Whether the student is let into the assignment by its direct link. The answer itself is read (200 = the page;
 * a redirect away or 403 = refused) without following redirects: in Production the app treats every request as
 * HTTPS (it runs behind a TLS proxy), so a refusal's redirect points at https:// and can't be followed here.
 */
async function canOpen(student: string, key: string): Promise<boolean> {
  const response = await students[student].request.get(`/Student/TakeAssignment?assignmentId=${ids[key]}`, { maxRedirects: 0 });
  return response.status() === 200;
}

const counter = (key: string) => teacher.locator(`[data-counter="${key}"] .counter-value`);
const studentRow = (name: string) => teacher.locator("#recipientTable tbody.recipient", { hasText: name }).first();
const visibleRows = () => teacher.locator("#recipientTable tbody.recipient:not(.d-none)");

/** Close is in the page's "More" menu. */
async function closeFromMenu(): Promise<void> {
  await teacher.locator("#moreMenu").click();
  await teacher.locator('.dropdown-menu.show form[action*="/Close/"] button').click();
}

async function teacherToken(): Promise<string> {
  return teacher.locator('input[name="__RequestVerificationToken"]').first().inputValue();
}

test.beforeAll(async ({ browser }) => {
  browserRef = browser;
  teacher = await (await browser.newContext({ viewport: { width: 1280, height: 900 } })).newPage();
  teacher.on("dialog", d => void d.accept());
});

test.afterAll(async () => {
  await teacher.context().close();
  for (const page of Object.values(students)) await page.context().close();
});

test("setup: a teacher and an empty class", async () => {
  const email = `${stamp}.teacher@example.test`;
  await teacher.goto("/Auth/Register");
  await teacher.locator('input[name="fullName"]').fill("Assign Teacher");
  await teacher.locator('input[name="email"]').fill(email);
  await teacher.locator('input[name="password"]').fill(password);
  await teacher.locator('input[name="confirmPassword"]').fill(password);
  await teacher.locator('button[type="submit"]').click();
  await expect(teacher).toHaveURL(/\/Auth\/Login/);
  await signIn(teacher, email, password);
  inviteCode = await createClass(teacher, className);

  await teacher.goto("/Assignments/Create");
  classId = (await teacher.locator("#classSelect option", { hasText: className }).getAttribute("value"))!;
  expect(classId).toBeTruthy();
});

test("an empty class: the panel offers inviting students and leads back to the draft", async () => {
  await draft("Practice A");
  await teacher.locator("#openAssignBtn").click();
  await expect(teacher.locator("#assignEmptyClass")).toContainText("has no students yet");
  await teacher.locator("#inviteStudentsLink").click();

  await expect(teacher).toHaveURL(new RegExp(`/Classes/Details/${classId}.*tab=students`, "i"));
  await expect(teacher.locator("#inviteCodeValue")).toHaveText(inviteCode);
  await teacher.locator("#backToDraftLink").click();
  await expect(teacher).toHaveURL(new RegExp(`/Assignments/Edit/${ids["Practice A"]}\\?assign=open`, "i"));
  await expect(teacher.locator("#assignModal")).toBeVisible();      // the panel opens again by itself
  await expect(teacher.locator(".badge", { hasText: "Draft" }).first()).toBeVisible();
});

test("three students join; the whole class gets the practice in one click", async () => {
  test.setTimeout(120_000);
  for (const name of ["Amira", "Yusuf", "Lena"]) await registerStudent(name);

  await teacher.goto(`/Assignments/Edit/${ids["Practice A"]}`);
  await teacher.locator("#openAssignBtn").click();
  const modal = teacher.locator("#assignModal");
  await expect(modal.locator("#audEntire")).toBeChecked();
  await expect(modal.locator("#lateAuto")).toBeChecked();            // practice suggests "automatically"
  await expect(modal.locator("#assignSubmit")).toHaveText("Assign to 3 students");
  await expect(modal.locator("#assignSummary")).toContainText(className);
  await shot(teacher, "assign-panel");

  await modal.locator("#assignSubmit").click();
  await expect(teacher.locator(".alert-success")).toContainText(`Assigned to 3 students from ${className}.`);
  await expect(counter("all")).toHaveText("3");
  await expect(counter("submitted")).toHaveText("0");
  await expect(teacher.locator(".badge", { hasText: "Active" }).first()).toBeVisible();

  for (const name of ["Amira", "Yusuf", "Lena"]) expect(await sees(name, "Practice A"), name).toBe(true);
});

test("an old-style draft with three chosen students opens with exactly those three; a failed attempt keeps the choice", async () => {
  test.setTimeout(120_000);
  await registerStudent("Bob");                                      // a fourth student who is not chosen
  const id = await draft("Homework B", { homework: true, due: "2099-01-01T10:00" });

  // The manual way that existed before this panel: three students added one by one to the draft.
  const token = await teacherToken();
  await teacher.goto(`/Assignments/Edit/${id}`);
  for (const name of ["Amira", "Yusuf", "Lena"]) {
    const value = await teacher.locator(".assign-student", { hasText: fullName(name) }).locator("input").getAttribute("value");
    const response = await teacher.request.post("/Assignments/AssignStudent", {
      form: { assignmentId: id, studentId: value!, __RequestVerificationToken: token }
    });
    expect(response.ok()).toBe(true);
  }

  await teacher.goto(`/Assignments/Edit/${id}`);
  await teacher.locator("#openAssignBtn").click();
  const modal = teacher.locator("#assignModal");
  await expect(modal.locator("#audSelected")).toBeChecked();
  for (const name of ["Amira", "Yusuf", "Lena"])
    await expect(modal.locator(".assign-student", { hasText: fullName(name) }).locator("input")).toBeChecked();
  await expect(modal.locator(".assign-student", { hasText: fullName("Bob") }).locator("input")).not.toBeChecked();
  await expect(modal.locator("#assignSelectedCount")).toHaveText("3");
  await expect(modal.locator("#assignSubmit")).toHaveText("Assign to 3 students");
  // The due date set when the draft was made comes back unchanged (no time-zone shift through the form).
  await expect(modal.locator("#assignDue")).toHaveValue("2099-01-01T10:00");

  // Homework without a due date is refused by the server: nothing changes and the choice comes back.
  await modal.locator("#assignDue").evaluate(el => { (el as HTMLInputElement).required = false; (el as HTMLInputElement).value = ""; });
  await modal.locator("#assignSubmit").click();
  await expect(teacher).toHaveURL(/assign=open/);
  await expect(teacher.locator("#assignError")).toContainText("Homework requires a deadline");
  await expect(teacher.locator(".badge", { hasText: "Draft" }).first()).toBeVisible();
  await expect(modal.locator("#audSelected")).toBeChecked();
  await expect(modal.locator("#assignSelectedCount")).toHaveText("3");

  await modal.locator("#assignDue").fill("2099-01-01T10:00");
  await modal.locator("#assignSubmit").click();
  await expect(teacher.locator(".alert-success")).toContainText(`Assigned to 3 students from ${className}.`);
  await teacher.goto(`/Assignments/Edit/${id}?tab=settings`);
  await expect(teacher.locator("#settingsDeadlineInput")).toHaveValue("2099-01-01T10:00");

  // Checked under the students' own accounts: the chosen ones have it, the fourth does not, not even by link.
  for (const name of ["Amira", "Yusuf", "Lena"]) expect(await sees(name, "Homework B"), name).toBe(true);
  expect(await sees("Bob", "Homework B")).toBe(false);
  expect(await canOpen("Bob", "Homework B")).toBe(false);
});

test("students who join later: automatic, with approval, not for hand-picked or closed work", async () => {
  test.setTimeout(180_000);
  // Two homeworks for the whole class with approval, and a practice that is then closed.
  for (const key of ["Homework C", "Homework D"]) {
    await draft(key, { homework: true, due: "2099-02-01T10:00" });
    const message = await assignTo(teacher, ids[key], { lateJoin: "approval" });
    expect(message).toContain("Assigned to 4 students");
  }
  await draft("Practice E");
  await assignTo(teacher, ids["Practice E"], { lateJoin: "auto" });
  await closeFromMenu();
  await expect(teacher.locator(".alert-success")).toContainText("Closed");

  for (const name of ["Omar", "Zara"]) await registerStudent(name);

  for (const name of ["Omar", "Zara"]) {
    expect(await sees(name, "Practice A"), `${name}: automatic`).toBe(true);
    expect(await sees(name, "Homework B"), `${name}: hand-picked`).toBe(false);
    expect(await sees(name, "Homework C"), `${name}: waits for approval`).toBe(false);
    expect(await canOpen(name, "Homework C"), `${name}: no access before approval`).toBe(false);
    expect(await sees(name, "Practice E"), `${name}: closed`).toBe(false);
  }

  // Joining again (a double click, or two tabs at once) changes nothing and never fails.
  const omar = students["Omar"];
  await omar.goto("/Student/Classes");
  const token = await omar.locator('input[name="__RequestVerificationToken"]').first().inputValue();
  const [first, second] = await Promise.all([1, 2].map(() =>
    omar.request.post("/Student/JoinClass", { form: { inviteCode, __RequestVerificationToken: token }, maxRedirects: 0 })));
  for (const r of [first, second]) expect(r.status(), "join again").toBeLessThan(500);

  await teacher.goto(`/Classes/Details/${classId}?tab=students`);
  await expect(teacher.locator("#students tbody tr", { hasText: fullName("Omar") })).toHaveCount(1);
});

test("batch approval in the class: chosen pairs, a personal due date, decide later and decline", async () => {
  test.setTimeout(120_000);
  await teacher.goto(`/Classes/Details/${classId}?tab=students`);
  const block = teacher.locator("#pendingRequests");
  await expect(block).toBeVisible();
  await expect(block.locator(".pending-row")).toHaveCount(4);       // 2 students × 2 homeworks, nothing twice
  await expect(teacher.locator("#pendingSummary")).toHaveText("2 students · 2 assignments · 4 requests");
  await expect(teacher.locator("#pendingApprove")).toHaveText("Approve 4 requests");

  // Only one homework chosen: the button says how many students get it.
  const row = (name: string, key: string) => block.locator(".pending-row", { hasText: fullName(name) }).filter({ hasText: title(key) });
  await row("Zara", "Homework D").locator(".pending-check").uncheck();
  await row("Omar", "Homework D").locator(".pending-check").uncheck();
  await expect(teacher.locator("#pendingApprove")).toHaveText("Assign to 2 students");
  await row("Omar", "Homework D").locator(".pending-check").check();
  await expect(teacher.locator("#pendingSummary")).toHaveText("2 students · 2 assignments · 3 requests");
  await expect(teacher.locator("#pendingApprove")).toHaveText("Approve 3 requests");

  // Omar gets his own due date for Homework C; the others keep the assignment's date.
  await row("Omar", "Homework C").locator(".pending-due-edit").click();
  await row("Omar", "Homework C").locator(".pending-due-input").fill("2099-06-01T09:00");
  await shot(teacher, "pending-approvals");

  await teacher.locator("#pendingApprove").click();
  await expect(teacher.locator(".alert-success")).toContainText("Approved 3 requests: 2 students · 2 assignments.");
  await expect(block.locator(".pending-row")).toHaveCount(1);       // Zara — Homework D still waits

  // "Decide later" sends nothing: the request is still there after a reload.
  await teacher.locator("#pendingLater").click();
  await expect(block).toBeHidden();
  await teacher.reload();
  await expect(teacher.locator("#pendingRequests .pending-row")).toHaveCount(1);

  // Decline closes it without giving the work.
  await teacher.locator("#pendingDecline").click();
  await expect(teacher.locator(".alert-success")).toContainText("1 request declined.");
  await expect(teacher.locator("#pendingRequests")).toHaveCount(0);

  expect(await sees("Omar", "Homework C")).toBe(true);
  expect(await sees("Omar", "Homework D")).toBe(true);
  expect(await sees("Zara", "Homework C")).toBe(true);
  expect(await sees("Zara", "Homework D")).toBe(false);

  await teacher.goto(`/Assignments/Details/${ids["Homework C"]}`);
  await expandStudent(teacher, fullName("Omar"));
  const omarPanel = studentRow(fullName("Omar")).locator(".collapse.show");
  await expect(omarPanel).toContainText("Personal");
  await expect(omarPanel).toContainText("Jun");
});

test("an approval is re-checked when it is made: a closed assignment is not given out", async () => {
  test.setTimeout(120_000);
  await draft("Homework F", { homework: true, due: "2099-03-01T10:00" });
  await assignTo(teacher, ids["Homework F"], { lateJoin: "approval" });
  await registerStudent("Kian");
  await teacher.goto(`/Assignments/Details/${ids["Homework F"]}`);
  await closeFromMenu();

  await teacher.goto(`/Classes/Details/${classId}?tab=students`);
  const kianRow = teacher.locator("#pendingRequests .pending-row", { hasText: fullName("Kian") }).filter({ hasText: title("Homework F") });
  await expect(kianRow).toHaveCount(1);
  await teacher.locator("#pendingAll").uncheck();
  await kianRow.locator(".pending-check").check();
  await teacher.locator("#pendingApprove").click();
  await expect(teacher.locator(".alert-danger")).toContainText("no longer active");
  expect(await canOpen("Kian", "Homework F")).toBe(false);
});

test("a future opening date is shown to the teacher and keeps students out until then", async () => {
  await draft("Practice G");
  const message = await assignTo(teacher, ids["Practice G"], { students: [fullName("Amira")], opens: "2099-01-01T09:00" });
  expect(message).toContain(`Assigned to 1 student from ${className}.`);
  expect(message).toMatch(/Students can start on \d{2} Jan 2099/);

  const amira = students["Amira"];
  await amira.goto(`/Student/TakeAssignment?assignmentId=${ids["Practice G"]}`);
  await expect(amira).not.toHaveURL(/TakeAssignment/i);
  await expect(amira.locator("body")).toContainText("isn't available to you yet");
});

test("submitted X of Y counts students, not attempts; one student's work opens from their name", async () => {
  test.setTimeout(180_000);
  // Unlimited attempts on Practice A, then Amira submits twice and nobody else hands anything in.
  await teacher.goto(`/Assignments/Edit/${ids["Practice A"]}?tab=settings`);
  await teacher.locator('#settingsForm input[name="maxAttempts"]').fill("0");
  await teacher.locator('#settingsForm button[type="submit"]').click();

  const amira = students["Amira"];
  for (const answer of ["5", "4"]) {
    await amira.goto(`/Student/TakeAssignment?assignmentId=${ids["Practice A"]}`);
    await amira.locator(".answer-textarea").first().fill(answer);
    await submitAssignment(amira);
    await expect(amira).toHaveURL(/AssignmentResult/i);
  }

  await teacher.goto(`/Assignments/Details/${ids["Practice A"]}`);
  // Practice A: the first 3 students plus Bob, Omar, Zara and Kian, who joined later and got it automatically.
  await expect(counter("all")).toHaveText("7");
  await expect(counter("submitted")).toHaveText("1");
  await expect(teacher.locator('[data-counter="submitted"] .counter-note')).toContainText("2 submitted attempts");
  await teacher.locator('#liveCounters [data-filter="submitted"]').click();
  await expect(visibleRows()).toHaveCount(1);
  await expect(visibleRows().first()).toContainText("2 attempts");
  await teacher.locator('#liveCounters [data-filter="not-submitted"]').click();
  await expect(visibleRows()).toHaveCount(6);

  // From the class roster: the name opens the student's work, and the attempt opens from there.
  await teacher.goto(`/Classes/Details/${classId}?tab=students`);
  await teacher.locator("#students a.student-link", { hasText: fullName("Amira") }).click();
  await expect(teacher).toHaveURL(/\/Classes\/StudentWork/i);
  const work = teacher.locator("#studentWorkTable");
  for (const key of ["Practice A", "Homework B", "Homework C", "Homework D", "Practice G"])
    await expect(work).toContainText(title(key));
  const practiceRow = work.locator("tr", { hasText: title("Practice A") });
  await expect(practiceRow).toContainText("100%");
  await expect(practiceRow).toContainText(className);
  await expect(work.locator("tr", { hasText: title("Practice E") })).toContainText("Closed");
  await expect(work.locator("tr", { hasText: title("Practice G") })).toContainText("Not open yet");
  await shot(teacher, "student-work");

  await practiceRow.getByRole("link", { name: "Open attempt" }).click();
  await expect(teacher).toHaveURL(/SubmissionDetail/i);
  await teacher.locator("#submissionBack").click();
  await expect(teacher).toHaveURL(/\/Classes\/StudentWork/i);
  await teacher.locator("#studentWorkBack").click();
  await expect(teacher).toHaveURL(new RegExp(`/Classes/Details/${classId}\\?tab=students`, "i"));
});

test("the dashboard's 'needs review' count opens the same list", async () => {
  test.setTimeout(180_000);
  // A drawn answer always waits for the teacher (without an AI it can't even get a first mark).
  await draft("Drawing H", { questions: [{ title: "Sketch y = x", type: 3, answerKey: "", requireDrawing: true }] });
  await assignTo(teacher, ids["Drawing H"], { students: [fullName("Yusuf")] });

  const yusuf = students["Yusuf"];
  await yusuf.goto(`/Student/TakeAssignment?assignmentId=${ids["Drawing H"]}`);
  const canvas = yusuf.locator(".sketch-canvas").first();
  await canvas.scrollIntoViewIfNeeded();
  const box = (await canvas.boundingBox())!;
  await yusuf.mouse.move(box.x + 20, box.y + box.height - 20);
  await yusuf.mouse.down();
  await yusuf.mouse.move(box.x + box.width - 20, box.y + 20, { steps: 6 });
  await yusuf.mouse.up();
  await submitAssignment(yusuf);
  await expect(yusuf).toHaveURL(/AssignmentResult/i, { timeout: 60_000 });

  await teacher.goto("/");
  const count = Number(await teacher.locator("#needsReviewCount").innerText());
  expect(count).toBeGreaterThanOrEqual(1);
  await teacher.locator("#needsReviewLink").click();
  await expect(teacher).toHaveURL(/\/Assignments\/Review/i);
  await expect(teacher.locator("#reviewCount")).toHaveText(String(count));
  await expect(teacher.locator("#reviewTable tbody tr")).toHaveCount(count);
  const mine = teacher.locator("#reviewTable tbody tr", { hasText: title("Drawing H") });
  await expect(mine).toContainText(fullName("Yusuf"));

  await teacher.goto(`/Assignments/Details/${ids["Drawing H"]}?filter=needs-review`);
  await expect(visibleRows()).toHaveCount(1);
  await expect(visibleRows().first()).toContainText("Review work");
});

test("defaults left as they are: practice goes to newcomers automatically, homework only after approval", async () => {
  test.setTimeout(180_000);
  await draft("Practice Def");
  await teacher.locator("#openAssignBtn").click();
  await expect(teacher.locator("#lateAuto")).toBeChecked();
  await expect(teacher.locator("#audEntire")).toBeChecked();
  await teacher.locator("#assignSubmit").click();                    // nothing changed in the panel
  await expect(teacher.locator(".alert-success")).toContainText("Assigned to");
  await expect(teacher.locator("#lateJoinCurrent")).toContainText("automatically");

  await draft("Homework Def", { homework: true, due: "2099-11-27T17:00" });
  await teacher.locator("#openAssignBtn").click();
  await expect(teacher.locator("#lateApproval")).toBeChecked();
  await teacher.locator("#assignSubmit").click();
  await expect(teacher.locator(".alert-success")).toContainText("Assigned to");
  await expect(teacher.locator("#lateJoinCurrent")).toContainText("your approval");

  await registerStudent("Nadia");
  expect(await sees("Nadia", "Practice Def")).toBe(true);
  expect(await canOpen("Nadia", "Practice Def")).toBe(true);
  expect(await sees("Nadia", "Homework Def")).toBe(false);
  expect(await canOpen("Nadia", "Homework Def")).toBe(false);

  await teacher.goto(`/Classes/Details/${classId}?tab=students`);
  const block = teacher.locator("#pendingRequests");
  await teacher.locator("#pendingAll").uncheck();
  await block.locator(".pending-row", { hasText: fullName("Nadia") }).filter({ hasText: title("Homework Def") }).locator(".pending-check").check();
  await expect(teacher.locator("#pendingApprove")).toHaveText("Assign to 1 student");
  await teacher.locator("#pendingApprove").click();
  await expect(teacher.locator(".alert-success")).toContainText("Assigned to 1 student.");
  expect(await sees("Nadia", "Homework Def")).toBe(true);
  expect(await canOpen("Nadia", "Homework Def")).toBe(true);
});

test("17:00 Tashkent stays 17:00: created, saved again, a personal due date, and on the student's screen", async () => {
  test.setTimeout(180_000);
  const zone = "Tashkent time (UTC+05:00)";
  const id = await draft("Homework TZ", { homework: true, due: "2099-11-27T17:00" });

  // Created: the form, the assign panel and the settings show the time exactly as typed.
  await teacher.locator("#openAssignBtn").click();
  await expect(teacher.locator("#assignDue")).toHaveValue("2099-11-27T17:00");
  await expect(teacher.locator("#assignModal")).toContainText(`Times are ${zone}.`);
  await teacher.locator("#assignSubmit").click();
  await expect(teacher.locator(".alert-success")).toContainText("Assigned to");
  await expect(teacher.locator("dl").first()).toContainText("27 Nov 2099 17:00");

  // Saved again (twice) without touching the date: still 17:00.
  for (let i = 0; i < 2; i++) {
    await teacher.goto(`/Assignments/Edit/${id}?tab=settings`);
    await expect(teacher.locator("#settingsDeadlineInput")).toHaveValue("2099-11-27T17:00");
    await teacher.locator('#settingsForm button[type="submit"]').click();
  }
  await teacher.goto(`/Assignments/Edit/${id}?tab=settings`);
  await expect(teacher.locator("#settingsDeadlineInput")).toHaveValue("2099-11-27T17:00");

  // A newcomer approved with a personal due date of 28 Nov 17:00.
  await registerStudent("Timur");
  await teacher.goto(`/Classes/Details/${classId}?tab=students`);
  const row = teacher.locator("#pendingRequests .pending-row", { hasText: fullName("Timur") }).filter({ hasText: title("Homework TZ") });
  await expect(row.locator(".pending-due-text")).toHaveText("27 Nov 17:00");      // the assignment's date, Tashkent time
  await teacher.locator("#pendingAll").uncheck();
  await row.locator(".pending-check").check();
  await row.locator(".pending-due-edit").click();
  await expect(row.locator(".pending-due-input")).toHaveValue("2099-11-27T17:00");
  await row.locator(".pending-due-input").fill("2099-11-28T17:00");
  await teacher.locator("#pendingApprove").click();
  await expect(teacher.locator(".alert-success")).toContainText("Assigned to 1 student.");

  await teacher.goto(`/Assignments/Details/${id}`);
  await expandStudent(teacher, fullName("Timur"));
  const timurPanel = studentRow(fullName("Timur")).locator(".collapse.show");
  await expect(timurPanel).toContainText("28 Nov 2099 17:00");
  await expandStudent(teacher, fullName("Amira"));
  await expect(studentRow(fullName("Amira")).locator(".collapse.show")).toContainText("27 Nov 2099 17:00");
  // The personal-dates form shows the same time, so saving it unchanged keeps it.
  await expect(timurPanel.locator('input[name="dueDate"]')).toHaveValue("2099-11-28T17:00");

  // The student sees their own time, on the same clock.
  const timur = students["Timur"];
  await timur.goto(`/Student/Assignments?classId=${classId}`);
  const card = timur.locator(".card", { hasText: title("Homework TZ") }).last();
  await expect(card.locator(".my-due")).toHaveText(/Due 28 Nov 2099 17:00/);
  await timur.goto(`/Student/TakeAssignment?assignmentId=${id}`);
  await expect(timur.locator("#myDue")).toContainText(`28 Nov 2099 17:00 (${zone})`);
  const amira = students["Amira"];
  await amira.goto(`/Student/TakeAssignment?assignmentId=${id}`);
  await expect(amira.locator("#myDue")).toContainText(`27 Nov 2099 17:00 (${zone})`);
});
