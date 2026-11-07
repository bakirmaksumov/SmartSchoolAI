import { expect, test, type Page } from "@playwright/test";
import {
  accounts,
  addQuestion,
  aiEnabled,
  createClass,
  createDraftAssignment,
  joinClass,
  publishAndAssignAll,
  signIn,
  signInAs,
  submitAssignment,
  unique
} from "./helpers";

test.describe.configure({ mode: "serial" });

const missingId = "00000000-0000-4000-8000-000000000001";
const className = unique("E2E Coverage Class");
const assignmentTitle = unique("E2E Coverage Assignment");
const questions = [1, 2, 3].map(n => ({
  kind: "Text/Numeric" as const,
  text: `Coverage ${n}: ${n} plus 200`,
  answerKey: String(n + 200)
}));

let teacher: Page;
let student: Page;
let classId = "";
let assignmentId = "";
let resultId = "";

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

test("setup: class, published assignment, one correct submission", async () => {
  test.setTimeout(240_000);

  const code = await createClass(teacher, className);
  await joinClass(student, code, className);
  const card = teacher.locator(".card").filter({ has: teacher.getByRole("heading", { name: className, exact: true }) });
  await card.getByRole("link").first().click();
  classId = teacher.url().match(/\/Classes\/Details\/([0-9a-f-]+)/i)![1];

  assignmentId = await createDraftAssignment(teacher, assignmentTitle, className);
  for (const q of questions) {
    await addQuestion(teacher, q);
  }
  await publishAndAssignAll(teacher, assignmentId);

  await student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
  for (let i = 0; i < questions.length; i++) {
    await student.locator(`.nav-btn[data-idx="${i}"]`).click();
    await student.locator(`.answer-textarea[data-qkey="q_${i}"]`).fill(questions[i].answerKey);
  }
  await submitAssignment(student);
  await expect(student.getByText("100%", { exact: true }).first()).toBeVisible();
  resultId = student.url().match(/resultId=([0-9a-f-]+)/i)![1];
});

test("student result page shows the score and is reachable from the assignment list", async () => {
  await student.goto(`/Student/Assignments?classId=${classId}`);
  await student.getByRole("link", { name: /View My Result/ }).first().click();
  await expect(student).toHaveURL(/\/Student\/AssignmentResult/i);
  await expect(student.getByText("100%", { exact: true }).first()).toBeVisible();
});

test("student AI tutor chat opens in each mode and keeps the message even when the AI is offline", async () => {
  for (const mode of ["hint", "explain"]) {
    const response = await student.goto(`/Student/Chat?resultId=${resultId}&mode=${mode}`);
    expect(response?.status(), mode).toBeLessThan(400);
    await expect(student.getByRole("heading", { name: /AI Academic Assistant/ })).toBeVisible();
  }

  await student.goto(`/Student/Chat?resultId=${resultId}&mode=hint`);
  const message = unique("Help me with question one");
  await student.locator("#userMessageInput").fill(message);
  await student.locator('form[action*="SendChatMessage"] button[type="submit"]').click();
  await expect(student).toHaveURL(/\/Student\/Chat/i);
  await expect(student.locator("#chatBox")).toContainText(message);
  if (aiEnabled) {
    // A real tutor reply, not the "couldn't reach the tutor" fallback.
    await expect(student.locator("#chatBox")).not.toContainText("couldn't reach the tutor");
  }
});

test("student cannot read a result or chat that does not belong to them", async () => {
  for (const path of [
    `/Student/AssignmentResult?assignmentId=${assignmentId}&resultId=${missingId}`,
    `/Student/Chat?resultId=${missingId}`,
    `/Student/TakeAssignment?assignmentId=${missingId}`
  ]) {
    const response = await student.request.get(path);
    expect(response.status(), path).toBe(404);
  }
});

test("a second student who was not assigned cannot open the assignment or the first student's result", async ({ browser }) => {
  const code = (await teacher.goto("/Classes").then(() =>
    teacher.locator(".card").filter({ has: teacher.getByRole("heading", { name: className, exact: true }) })
      .locator(".badge.bg-label-info").first().innerText())).match(/[A-Z0-9]{6}/)![0];

  const other = await (await browser.newContext()).newPage();
  try {
    const email = `${unique("late.student").replace(/\s/g, "")}@example.test`;
    await other.goto("/Auth/StudentRegister");
    await other.locator('input[name="inviteCode"]').fill(code);
    await other.locator('input[name="fullName"]').fill("Late Student");
    await other.locator('input[name="email"]').fill(email);
    await other.locator('input[name="password"]').fill("Student-Pass-123");
    await other.locator('input[name="confirmPassword"]').fill("Student-Pass-123");
    await other.locator('button[type="submit"]').click();
    await signIn(other, email, "Student-Pass-123");
    await expect(other).toHaveURL(/\/Student\/?$/);

    const take = await other.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
    expect(take?.status() === 403 || !other.url().includes("TakeAssignment"), "unassigned student must be refused").toBeTruthy();

    const result = await other.request.get(`/Student/AssignmentResult?assignmentId=${assignmentId}&resultId=${resultId}`);
    expect(result.status()).toBe(404);
    const chat = await other.request.get(`/Student/Chat?resultId=${resultId}`);
    expect(chat.status()).toBe(404);
  } finally {
    await other.context().close();
  }
});

test("teacher sees one student's history and the submission detail", async () => {
  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  await teacher.locator('a[href*="/Assignments/StudentStats"]').first().click();
  await expect(teacher).toHaveURL(/\/Assignments\/StudentStats/i);
  await expect(teacher.locator("body")).toContainText("E2E Student");
  await expect(teacher.locator("body")).toContainText("100");

  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  await teacher.locator('a[href*="SubmissionDetail"]').first().click();
  await expect(teacher).toHaveURL(/SubmissionDetail/i);
  await expect(teacher.locator("body")).toContainText("E2E Student");
  await expect(teacher.locator("body")).toContainText(questions[0].text);

  const unknown = await teacher.request.get(`/Assignments/StudentStats?assignmentId=${assignmentId}&studentId=${missingId}`);
  expect(unknown.status()).toBe(404);
});

test("live status endpoint reports progress and rejects unknown assignments", async () => {
  const ok = await teacher.request.get(`/Assignments/LiveStatus/${assignmentId}`);
  expect(ok.status()).toBe(200);
  expect(typeof (await ok.json())).toBe("object");

  const unknown = await teacher.request.get(`/Assignments/LiveStatus/${missingId}`);
  expect(unknown.status()).toBe(404);
});

test("CSV export contains the student and their score", async () => {
  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  const download = teacher.waitForEvent("download");
  await teacher.getByRole("link", { name: /Export CSV/ }).click();
  const file = await download;
  const { readFile } = await import("node:fs/promises");
  const csv = await readFile((await file.path())!, "utf8");
  expect(csv).toContain("E2E Student");
  expect(csv).toContain("100");
});

test("assignment list filters by class and status", async () => {
  await teacher.goto(`/Assignments?classId=${classId}`);
  await expect(teacher.getByText(assignmentTitle).first()).toBeVisible();

  await teacher.goto(`/Assignments?classId=${classId}&status=Active`);
  await expect(teacher.getByText(assignmentTitle).first()).toBeVisible();

  await teacher.goto(`/Assignments?classId=${classId}&status=Draft`);
  await expect(teacher.getByText(assignmentTitle)).toHaveCount(0);

  await teacher.goto(`/Assignments?classId=${missingId}`);
  await expect(teacher.getByText(assignmentTitle)).toHaveCount(0);
});

test("question editor: move, require-work toggle, subpoint, duplicate, remove", async () => {
  test.setTimeout(240_000);

  const draftId = await createDraftAssignment(teacher, unique("E2E Editor Ops"), className);
  const first = { kind: "Text/Numeric" as const, text: "Editor first question", answerKey: "1" };
  const second = { kind: "Text/Numeric" as const, text: "Editor second question", answerKey: "2" };
  await addQuestion(teacher, first);
  await addQuestion(teacher, second);

  const order = async () => {
    const text = await teacher.locator("#questionsList").innerText();
    return [text.indexOf(first.text), text.indexOf(second.text)];
  };

  let [a, b] = await order();
  expect(a).toBeGreaterThanOrEqual(0);
  expect(a).toBeLessThan(b);

  await teacher.locator('#questionsList button[title="Move down"]:not([disabled])').first().click();
  await expect(teacher).toHaveURL(new RegExp(`/Assignments/Edit/${draftId}`, "i"));
  [a, b] = await order();
  expect(b).toBeLessThan(a);

  await teacher.locator("#requireWork_0").check();
  await teacher.waitForLoadState("load");
  await expect(teacher.locator("#requireWork_0")).toBeChecked();

  await teacher.getByRole("button", { name: /Add subpoint/ }).first().click();
  await teacher.waitForLoadState("load");
  await expect(teacher.locator("#questionsList")).toContainText(/Q\d+[a-z.]|\(a\)|subpoint/i);

  await teacher.locator('#questionsList button[title="Duplicate"]').first().click();
  await teacher.waitForLoadState("load");
  const countAfterDuplicate = await teacher.locator('#questionsList button[title="Remove"]').count();
  expect(countAfterDuplicate).toBeGreaterThanOrEqual(4);

  // A parent question is removed together with its subpoints, so remove the last item instead.
  teacher.once("dialog", d => d.accept());
  await teacher.locator('#questionsList button[title="Remove"]').last().click();
  await teacher.waitForLoadState("load");
  await expect(async () => {
    expect(await teacher.locator('#questionsList button[title="Remove"]').count()).toBeLessThan(countAfterDuplicate);
  }).toPass();
});

test("analytics dashboard loads for the class on both tabs", async () => {
  for (const tab of ["practice", "assessment"]) {
    const response = await teacher.goto(`/Analytics?classId=${classId}&tab=${tab}`);
    expect(response?.status(), tab).toBeLessThan(400);
    await expect(teacher.getByRole("heading", { name: /Analytics Dashboard/ })).toBeVisible();
    await expect(teacher.locator("#classSelector")).toHaveValue(classId);
    await expect(teacher.locator("#analyticsTabs .nav-link.active")).toHaveText(new RegExp(tab, "i"));
  }

  await teacher.locator("#classSelector").selectOption(classId);
  await expect(teacher).toHaveURL(new RegExp(`classId=${classId}`));
});

test("admin can search teachers and the reset-password server check rejects short passwords", async ({ browser }) => {
  const admin = await (await browser.newContext()).newPage();
  try {
    await signInAs(admin, "admin");

    await admin.goto("/Admin");
    await admin.locator('input[name="search"]').fill(accounts.teacher.name);
    await admin.locator('form[method="get"] button[type="submit"]').click();
    await expect(admin.locator("tr", { hasText: accounts.teacher.email })).toBeVisible();

    await admin.locator('input[name="search"]').fill("zzz-nobody-matches-this");
    await admin.locator('form[method="get"] button[type="submit"]').click();
    await expect(admin.getByText("No teachers found.")).toBeVisible();

    await admin.goto(`/Admin?search=${encodeURIComponent(accounts.teacher.email)}`);
    await admin.locator("tr", { hasText: accounts.teacher.email }).getByRole("button", { name: /Reset password/ }).click();
    await admin.locator("#resetPasswordInput").evaluate((el: HTMLInputElement) => {
      el.removeAttribute("minlength");
      el.value = "short";
    });
    await admin.locator("#resetPasswordModal").getByRole("button", { name: /Reset password/ }).click();
    await expect(admin.locator("body")).toContainText("at least 8 characters");

    // The seeded teacher must still be able to sign in with the real password.
    const check = await (await browser.newContext()).newPage();
    try {
      await signIn(check, accounts.teacher.email, accounts.teacher.password);
      await expect(check).not.toHaveURL(/\/Auth\/Login|ChangePassword/);
    } finally {
      await check.context().close();
    }
  } finally {
    await admin.context().close();
  }
});

test("admin reset reveals the new password once", async ({ browser }) => {
  const admin = await (await browser.newContext()).newPage();
  const teacherPage = await (await browser.newContext()).newPage();
  const email = `${unique("reveal.teacher").replace(/\s/g, "")}@example.test`;
  const newPassword = "Reveal-Once-123";
  try {
    await teacherPage.goto("/Auth/Register");
    await teacherPage.locator('input[name="fullName"]').fill("Reveal Teacher");
    await teacherPage.locator('input[name="email"]').fill(email);
    await teacherPage.locator('input[name="password"]').fill("Teacher-Pass-123");
    await teacherPage.locator('input[name="confirmPassword"]').fill("Teacher-Pass-123");
    await teacherPage.locator('button[type="submit"]').click();

    await signInAs(admin, "admin");
    await admin.goto(`/Admin?search=${encodeURIComponent(email)}`);
    await admin.locator("tr", { hasText: email }).getByRole("button", { name: /Reset password/ }).click();
    await admin.locator("#resetPasswordInput").fill(newPassword);
    await admin.locator("#resetPasswordModal").getByRole("button", { name: /Reset password/ }).click();
    await expect(admin.locator("body")).toContainText(newPassword);

    await admin.reload();
    await expect(admin.locator("body")).not.toContainText(newPassword);

    await signIn(teacherPage, email, "Teacher-Pass-123");
    await expect(teacherPage.getByText("Invalid email or password.")).toBeVisible();
  } finally {
    await Promise.all([admin.context().close(), teacherPage.context().close()]);
  }
});

test("another teacher cannot open this assignment", async ({ browser }) => {
  const other = await (await browser.newContext()).newPage();
  try {
    const email = `${unique("intruder.teacher").replace(/\s/g, "")}@example.test`;
    await other.goto("/Auth/Register");
    await other.locator('input[name="fullName"]').fill("Intruder Teacher");
    await other.locator('input[name="email"]').fill(email);
    await other.locator('input[name="password"]').fill("Teacher-Pass-123");
    await other.locator('input[name="confirmPassword"]').fill("Teacher-Pass-123");
    await other.locator('button[type="submit"]').click();
    await signIn(other, email, "Teacher-Pass-123");

    for (const path of [
      `/Assignments/Details/${assignmentId}`,
      `/Assignments/Edit/${assignmentId}`,
      `/Assignments/Preview/${assignmentId}`
    ]) {
      const response = await other.request.get(path);
      expect(response.status(), path).toBe(404);
    }
    expect((await other.request.get(`/Assignments/LiveStatus/${assignmentId}`)).status()).toBe(404);
  } finally {
    await other.context().close();
  }
});

// Kept last: this one currently fails because of a real access-control bug (see the report).
test("students are kept out of every teacher page", async () => {
  const reachable: string[] = [];
  for (const path of ["/Assignments", "/Assignments/Create", "/Classes", "/Classes/Create", "/Classes/Students",
    "/ContentLibrary", "/ContentLibrary/Create", "/Admin"]) {
    const response = await student.goto(path);
    const finalPath = new URL(student.url()).pathname.replace(/\/$/, "");
    if (response?.status() === 200 && finalPath === path) {
      reachable.push(path);
    }
  }
  expect(reachable, "teacher pages a student could open").toEqual([]);
});
