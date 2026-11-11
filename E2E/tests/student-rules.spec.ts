import { expect, test, type Page } from "@playwright/test";
import {
  addQuestion,
  createClass,
  createDraftAssignment,
  joinClass,
  publishAndAssignAll,
  signInAs,
  submitAssignment,
  unique
} from "./helpers";

// Assignment rules that change what a student may do. No AI calls: numeric answers are graded locally.
test.describe.configure({ mode: "serial" });

let teacher: Page;
let student: Page;
const className = unique("E2E Rules Class");

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

async function draftWithQuestion(title: string, settings: { maxAttempts?: string; deadline?: string; allowLate?: boolean }): Promise<string> {
  const id = await createDraftAssignment(teacher, title, className);
  await addQuestion(teacher, { kind: "Text/Numeric", text: `${title}: 2 plus 2`, answerKey: "4" });

  await teacher.goto(`/Assignments/Edit/${id}?tab=settings`);
  if (settings.maxAttempts) await teacher.locator('#settingsForm input[name="maxAttempts"]').fill(settings.maxAttempts);
  if (settings.deadline) {
    // Deadlines apply to Homework only (value 1); the default draft is Classroom Practice.
    await teacher.locator("#settingsTypeSelect").selectOption("1");
    await teacher.locator("#settingsDeadlineInput").fill(settings.deadline);
    // The deadline only blocks students when late submissions are switched off.
    if (settings.allowLate) await teacher.locator("#settingsLateCheck").check();
    else await teacher.locator("#settingsLateCheck").uncheck();
  }
  await teacher.locator('#settingsForm button[type="submit"]').click();
  await expect(teacher).toHaveURL(/\/Assignments\/?(\?.*)?$/);

  if (settings.deadline) {
    await teacher.goto(`/Assignments/Edit/${id}?tab=settings`);
    await expect(teacher.locator("#settingsDeadlineInput"), "saved deadline").toHaveValue(/^2099-01-01T/);
    await expect(teacher.locator("#settingsTypeSelect"), "saved type").toHaveValue("1");
    if (settings.allowLate) await expect(teacher.locator("#settingsLateCheck"), "late submission").toBeChecked();
    else await expect(teacher.locator("#settingsLateCheck"), "late submission").not.toBeChecked();
  }

  await teacher.goto(`/Assignments/Edit/${id}`);
  await publishAndAssignAll(teacher, id);
  return id;
}

// Sets the (only) enrolled student's own start and due dates from the teacher's assignment page.
async function setDates(id: string, startDate: string, dueDate: string): Promise<void> {
  await teacher.goto(`/Assignments/Details/${id}`);
  // The per-student date row is collapsed until its toggle is clicked.
  await teacher.locator('[data-bs-toggle="collapse"][data-bs-target^="#"]').first().click();
  const form = teacher.locator('form[action*="UpdateStudentDates"]').first();
  await expect(form).toBeVisible();
  await form.locator('input[name="startDate"]').fill(startDate);
  await form.locator('input[name="dueDate"]').fill(dueDate);
  await form.locator('button[type="submit"]').first().click();
  await expect(teacher.locator("body")).toContainText(/dates updated/i);
}

// Opens the form and fills in an answer, leaving the tab open without submitting.
async function openAndFill(id: string, value: string): Promise<void> {
  await student.goto(`/Student/TakeAssignment?assignmentId=${id}`);
  await student.locator('.nav-btn[data-idx="0"]').click();
  await student.locator('.answer-textarea[data-qkey="q_0"]').fill(value);
}

async function answer(id: string, value: string): Promise<void> {
  await student.goto(`/Student/TakeAssignment?assignmentId=${id}`);
  await student.locator('.nav-btn[data-idx="0"]').click();
  await student.locator('.answer-textarea[data-qkey="q_0"]').fill(value);
  await submitAssignment(student);
}

test("setup: class with the student in it", async () => {
  const code = await createClass(teacher, className);
  await joinClass(student, code, className);
});

test("attempt limit: two attempts are allowed, the third is refused", async () => {
  test.setTimeout(180_000);
  const id = await draftWithQuestion(unique("E2E Two Attempts"), { maxAttempts: "2" });

  await answer(id, "0");
  await expect(student.getByText("0%", { exact: true }).first()).toBeVisible();

  await answer(id, "4");
  await expect(student.getByText("100%", { exact: true }).first()).toBeVisible();

  await student.goto(`/Student/TakeAssignment?assignmentId=${id}`);
  await expect(student).toHaveURL(/\/Student\/AssignmentResult/i);
  await expect(student.locator("body")).toContainText(/used all your attempts/i);
});

test("a per-student past due date or future start date stops the student from starting", async () => {
  test.setTimeout(180_000);
  // Publishing needs a deadline at least an hour ahead, so the student's own dates are changed afterwards.
  const id = await draftWithQuestion(unique("E2E Student Dates"), { deadline: "2099-01-01T10:00" });

  await setDates(id, "", "2020-01-01T10:00");
  await student.goto(`/Student/TakeAssignment?assignmentId=${id}`);
  await expect(student).not.toHaveURL(/TakeAssignment/i);
  await expect(student.locator("body")).toContainText(/deadline has passed/i);

  await setDates(id, "2099-01-01T10:00", "");
  await student.goto(`/Student/TakeAssignment?assignmentId=${id}`);
  await expect(student).not.toHaveURL(/TakeAssignment/i);
  await expect(student.locator("body")).toContainText(/isn't available to you yet/i);

  await setDates(id, "", "");
  await student.goto(`/Student/TakeAssignment?assignmentId=${id}`);
  await expect(student).toHaveURL(/TakeAssignment/i);
  await expect(student.locator('.nav-btn[data-idx="0"]')).toBeVisible();
});

test("submitting from an already-open form is refused once the student's due date has passed", async () => {
  test.setTimeout(180_000);
  const title = unique("E2E Stale Deadline");
  const id = await draftWithQuestion(title, { maxAttempts: "1", deadline: "2099-01-01T10:00" });

  await openAndFill(id, "4");
  await setDates(id, "", "2020-01-01T10:00"); // the deadline passes while the tab is still open
  await submitAssignment(student);

  await expect(student).toHaveURL(/\/Student\/Assignments/i);
  const alert = student.locator(".alert-danger");
  await expect(alert).toBeVisible();
  await expect(alert).toContainText(`Submission closed. The deadline for ${title} has passed. Your answers were not submitted.`);

  // Nothing was recorded: with a single attempt allowed, a submission would send the student to the result page.
  await setDates(id, "", "");
  await student.goto(`/Student/TakeAssignment?assignmentId=${id}`);
  await expect(student).toHaveURL(/TakeAssignment/i);
  await expect(student.locator('.nav-btn[data-idx="0"]')).toBeVisible();
});

test("submitting from an already-open form is refused before the student's start date", async () => {
  test.setTimeout(180_000);
  const title = unique("E2E Stale Start");
  const id = await draftWithQuestion(title, { maxAttempts: "1", deadline: "2099-01-01T10:00" });

  await openAndFill(id, "4");
  await setDates(id, "2099-01-01T10:00", ""); // the start date moves into the future while the tab is open
  await submitAssignment(student);

  await expect(student).toHaveURL(/\/Student\/Assignments/i);
  const alert = student.locator(".alert-danger");
  await expect(alert).toBeVisible();
  await expect(alert).toContainText(/isn't available to you yet/i);

  await setDates(id, "", "");
  await student.goto(`/Student/TakeAssignment?assignmentId=${id}`);
  await expect(student).toHaveURL(/TakeAssignment/i);
});

test("a late submission still goes through when late submissions are enabled", async () => {
  test.setTimeout(180_000);
  const id = await draftWithQuestion(unique("E2E Late Allowed"), { maxAttempts: "1", deadline: "2099-01-01T10:00", allowLate: true });

  await openAndFill(id, "4");
  await setDates(id, "", "2020-01-01T10:00");
  await submitAssignment(student);

  await expect(student).toHaveURL(/\/Student\/AssignmentResult/i);
  await expect(student.getByText("100%", { exact: true }).first()).toBeVisible();
});
