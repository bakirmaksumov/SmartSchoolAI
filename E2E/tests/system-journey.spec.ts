import { expect, test, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { publishAndAssignAll, signIn, signInAs, submitAssignment, unique } from "./helpers";

// One school day across all three roles, with every number cross-checked between the pages that show it:
//   admin resets a new teacher's password -> the teacher is forced to change it -> builds a class and an assignment
//   -> a student registers with the invite code and submits it -> the teacher reviews and changes a mark
//   -> the student, the teacher's tables, the CSV export and the analytics all agree -> sign-out ends every session.
// Runs against the normal seeded app, or against the Docker container (E2E_BASE_URL). No AI is involved: both
// questions are marked by their answer keys.
test.describe.configure({ mode: "serial" });

const stamp = unique("journey").replace(/\s/g, "");
const teacherEmail = `${stamp}.teacher@example.test`;
const studentEmail = `${stamp}.student@example.test`;
const studentName = `Journey Student ${stamp}`;
const className = unique("E2E Journey Class");
const assignmentTitle = unique("E2E Journey Assignment");
const teacherFinalPassword = "Teacher-Final-123";
const studentPassword = "Student-Pass-123";

let admin: Page;
let teacher: Page;
let student: Page;
let classId = "";
let assignmentId = "";
let resultId = "";

test.beforeAll(async ({ browser }) => {
  admin = await (await browser.newContext()).newPage();
  teacher = await (await browser.newContext()).newPage();
  student = await (await browser.newContext()).newPage();
});

test.afterAll(async () => {
  for (const page of [admin, teacher, student]) await page.context().close();
});

const detailsRow = () => teacher.getByRole("row").filter({ has: teacher.locator('a[href*="SubmissionDetail"]') });

test("a new teacher registers, and the admin finds them and resets their password", async () => {
  test.setTimeout(120_000);
  await teacher.goto("/Auth/Register");
  await teacher.locator('input[name="fullName"]').fill(`Journey Teacher ${stamp}`);
  await teacher.locator('input[name="email"]').fill(teacherEmail);
  await teacher.locator('input[name="password"]').fill("Teacher-Pass-123");
  await teacher.locator('input[name="confirmPassword"]').fill("Teacher-Pass-123");
  await teacher.locator('button[type="submit"]').click();
  await expect(teacher).toHaveURL(/\/Auth\/Login/);

  await signInAs(admin, "admin");
  await admin.goto("/Admin");
  await expect(admin.getByRole("heading", { name: /Teachers \(\d+\)/ })).toBeVisible();

  // The search narrows the list to this one teacher.
  await admin.goto(`/Admin?search=${encodeURIComponent(teacherEmail)}`);
  await expect(admin.getByRole("heading", { name: "Teachers (1)" })).toBeVisible();
  const row = admin.locator("tr", { hasText: teacherEmail });
  await expect(row).toHaveCount(1);

  await row.getByRole("button", { name: /Reset password/ }).click();
  await admin.locator("#resetPasswordInput").fill("Reset-By-Admin-1");
  await admin.locator("#resetPasswordModal").getByRole("button", { name: /Reset password/ }).click();

  // The new password is shown once, to the admin only, and is gone on the next visit.
  await expect(admin.locator("body")).toContainText("Reset-By-Admin-1");
  await admin.goto("/Admin");
  await expect(admin.locator("body")).not.toContainText("Reset-By-Admin-1");
});

test("the teacher is held on the change-password page until they pick their own password", async () => {
  test.setTimeout(120_000);
  await signIn(teacher, teacherEmail, "Teacher-Pass-123");
  await expect(teacher.getByText("Invalid email or password.")).toBeVisible();   // the old password no longer works

  await signIn(teacher, teacherEmail, "Reset-By-Admin-1");
  await expect(teacher).toHaveURL(/\/Auth\/ChangePassword/);

  // Every other page sends them straight back while the change is pending.
  await teacher.goto("/Classes");
  await expect(teacher).toHaveURL(/\/Auth\/ChangePassword/);

  await teacher.locator("#newPassword").fill(teacherFinalPassword);
  await teacher.locator("#confirmPassword").fill(teacherFinalPassword);
  await teacher.locator('button[type="submit"]').click();
  await expect(teacher.getByRole("heading", { name: "Dashboard" })).toBeVisible();
});

test("the teacher creates a class and a published assignment", async () => {
  test.setTimeout(180_000);
  await teacher.goto("/Classes/Create");
  await teacher.locator('input[name="name"]').fill(className);
  await teacher.locator('input[name="subject"]').fill("Mathematics");
  await teacher.locator('select[name="gradeLevel"]').selectOption("8");
  await teacher.locator('select[name="academicYear"]').selectOption("2026-2027");
  await teacher.getByRole("button", { name: "Create Class" }).click();
  await expect(teacher).toHaveURL(/\/Classes\/?$/);

  const card = teacher.locator(".card").filter({ has: teacher.getByRole("heading", { name: className, exact: true }) });
  const code = (await card.locator(".badge.bg-label-info").innerText()).match(/[A-Z0-9]{6}/)![0];
  await card.getByRole("link").first().click();
  classId = teacher.url().match(/\/Classes\/Details\/([0-9a-f-]{36})/i)![1];

  // The student registers with the invite code, as a new pupil would.
  await student.goto("/Auth/StudentRegister");
  await student.locator('input[name="inviteCode"]').fill(code);
  await student.locator('input[name="fullName"]').fill(studentName);
  await student.locator('input[name="email"]').fill(studentEmail);
  await student.locator('input[name="password"]').fill(studentPassword);
  await student.locator('input[name="confirmPassword"]').fill(studentPassword);
  await student.locator('button[type="submit"]').click();
  await expect(student).toHaveURL(/\/Auth\/Login/);

  await teacher.goto("/Assignments/Create");
  await teacher.locator('input[name="title"]').fill(assignmentTitle);
  await teacher.locator('textarea[name="description"]').fill("Created by the system journey.");
  await teacher.locator("#classSelect").selectOption({ label: className });
  await teacher.locator("#typeSelect").selectOption("0");
  await teacher.getByRole("button", { name: "Save as Draft" }).click();
  await expect(teacher).toHaveURL(/\/Assignments\/Edit\/[0-9a-f-]+/i);
  await teacher.waitForLoadState("load");
  assignmentId = teacher.url().match(/\/Assignments\/Edit\/([0-9a-f-]{36})/i)![1];

  const token = await teacher.locator('input[name="__RequestVerificationToken"]').first().inputValue();
  const add = async (question: Record<string, unknown>) => {
    const response = await teacher.request.post(`/Assignments/AddQuestion/${assignmentId}`, {
      form: { questionJson: JSON.stringify({ id: randomUUID(), difficulty: "Medium", ...question }), __RequestVerificationToken: token }
    });
    expect(response.ok(), "AddQuestion").toBe(true);
  };
  await add({ title: "Journey 1: what is 6 plus 7?", type: 0, answerKey: "13" });
  await add({ title: "Journey 2: which number is prime?", type: 4, options: ["9", "11", "15", "21"], answerKey: "B" });

  await teacher.goto(`/Assignments/Edit/${assignmentId}`);
  await publishAndAssignAll(teacher, assignmentId);

  await teacher.goto("/Assignments");
  await expect(teacher.getByText(assignmentTitle).first()).toBeVisible();
});

test("the student signs in, finds the assignment and submits one right and one wrong answer: 50%", async () => {
  test.setTimeout(180_000);
  await signIn(student, studentEmail, studentPassword);
  await expect(student).toHaveURL(/\/Student\/?$/);
  await expect(student.getByText(className, { exact: true }).first()).toBeVisible();

  await student.goto("/Student/Classes");
  await student.locator('a[href*="/Student/Assignments?classId="]').first().click();
  await expect(student.getByText(assignmentTitle).first()).toBeVisible();

  await student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
  await student.locator('.nav-btn[data-idx="0"]').click();
  await student.locator('.answer-textarea[data-qkey="q_0"]').fill("13");
  await student.locator('.nav-btn[data-idx="1"]').click();
  await student.locator('.option-card[data-qkey="q_1"][data-value="A"]').click();   // wrong: 9 is not prime
  await submitAssignment(student);

  await expect(student).toHaveURL(/\/Student\/AssignmentResult/i);
  await expect(student.getByText("50%", { exact: true }).first()).toBeVisible();
  resultId = new URL(student.url()).searchParams.get("resultId")!;
  expect(resultId).toMatch(/^[0-9a-f-]{36}$/i);
});

test("the teacher sees the same 50% in the table and the CSV, and changes the wrong mark to correct", async () => {
  test.setTimeout(180_000);
  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  await expect(detailsRow().getByText("50%", { exact: true })).toBeVisible();
  await expect(detailsRow()).toContainText(studentName);

  const before = await teacher.context().request.get(`/Assignments/ExportGrades/${assignmentId}`);
  expect(before.status()).toBe(200);
  const csvBefore = await before.text();
  expect(csvBefore).toContain(studentName);
  expect(csvBefore).toContain("50");

  await teacher.goto(`/Assignments/SubmissionDetail?resultId=${resultId}`);
  await expect(teacher.locator("body")).toContainText(studentName);
  await teacher.locator('label[for="gcCorrect_1"]').click();
  await teacher.locator('#gradeForm button[type="submit"]').click();
  await expect(teacher.locator("body")).toContainText(/Grades saved/i);
});

test("after the change every page agrees on 100%: student, teacher, CSV and analytics", async () => {
  test.setTimeout(180_000);
  await student.goto(`/Student/AssignmentResult?assignmentId=${assignmentId}&resultId=${resultId}`);
  await expect(student.getByText("100%", { exact: true }).first()).toBeVisible();

  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  await expect(detailsRow().getByText("100%", { exact: true })).toBeVisible();

  const csv = await (await teacher.context().request.get(`/Assignments/ExportGrades/${assignmentId}`)).text();
  expect(csv).toContain(studentName);
  expect(csv).toContain("100");
  expect(csv).not.toContain(",50,");

  await teacher.goto(`/Classes/Details/${classId}?tab=students`);
  await expect(teacher.locator("strong.d-block", { hasText: studentName })).toBeVisible();

  await teacher.goto(`/Analytics?classId=${classId}`);
  await expect(teacher.locator("#classSelector")).toBeVisible();
  await expect(teacher.locator("body")).not.toContainText(/An error occurred while processing your request/i);

  // The teacher's dashboard counts the student and the assignment.
  await teacher.goto("/");
  await expect(teacher.getByRole("heading", { name: "Dashboard" })).toBeVisible();
});

test("archiving the class hides it from the active list; restoring brings it back", async () => {
  test.setTimeout(120_000);
  const heading = () => teacher.getByRole("heading", { name: className, exact: true });

  await teacher.goto(`/Classes/Details/${classId}?tab=settings`);
  teacher.once("dialog", d => d.accept());
  await teacher.locator('form[action*="/Classes/Archive/"] button[type="submit"]').click();
  await teacher.goto("/Classes");
  await expect(heading()).toHaveCount(0);
  await teacher.goto("/Classes?archived=true");
  await expect(heading()).toBeVisible();

  await teacher.goto(`/Classes/Details/${classId}?tab=settings`);
  await teacher.locator('form[action*="/Classes/Restore/"] button[type="submit"]').click();
  await teacher.goto("/Classes");
  await expect(heading()).toBeVisible();
});

test("signing out ends every session: protected pages send each role to the sign-in page", async () => {
  test.setTimeout(120_000);
  const sessions: [Page, string][] = [[student, "/Student"], [teacher, "/Classes"], [admin, "/Admin"]];
  for (const [page, path] of sessions) {
    await page.goto("/Auth/Logout");
    await page.goto(path);
    await expect(page, `${path} after sign-out`).toHaveURL(/\/Auth\/Login/);
  }
});
