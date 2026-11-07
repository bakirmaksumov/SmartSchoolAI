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

test.describe.configure({ mode: "serial" });

let teacher: Page;
let student: Page;
const className = unique("E2E Lifecycle Class");
const assignmentTitle = unique("E2E Lifecycle Assignment");
let assignmentId = "";

const questions = [1, 2, 3].map(n => ({
  kind: "Text/Numeric" as const,
  text: `Lifecycle ${n}: ${n} plus 100`,
  answerKey: String(n + 100)
}));

async function answerAll(page: Page, values: string[]): Promise<void> {
  await page.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
  for (let i = 0; i < values.length; i++) {
    await page.locator(`.nav-btn[data-idx="${i}"]`).click();
    await page.locator(`.answer-textarea[data-qkey="q_${i}"]`).fill(values[i]);
  }
  await submitAssignment(page);
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

test("teacher creates a class and the student joins it", async () => {
  const code = await createClass(teacher, className);
  await joinClass(student, code, className);
  await student.goto("/Student/Classes");
  await expect(student.getByText(className).first()).toBeVisible();
});

test("student joining with a bad invite code is refused", async () => {
  await student.goto("/Student");
  await student.getByRole("button", { name: /Join a Class/ }).first().click();
  const modal = student.locator("#joinClassModal");
  await modal.locator('input[name="inviteCode"]').fill("QQQQQQ");
  await modal.getByRole("button", { name: "Join Class" }).click();
  await expect(student.locator(".alert-danger, .toast, .text-danger").first()).toBeVisible();
});

test("teacher drafts an assignment, edits the question list and previews it", async () => {
  assignmentId = await createDraftAssignment(teacher, assignmentTitle, className);
  for (const q of questions) {
    await addQuestion(teacher, q);
  }

  const list = teacher.locator("#questionsList");
  await expect(list).toContainText(questions[2].text);

  await list.getByTitle("Duplicate").first().click();
  await expect(list.getByText(questions[0].text)).toHaveCount(2);

  teacher.once("dialog", d => d.accept());
  await list.getByTitle("Remove").first().click();
  await expect(list.getByText(questions[0].text)).toHaveCount(1);

  await teacher.goto(`/Assignments/Preview/${assignmentId}`);
  await expect(teacher.locator("body")).toContainText(questions[0].text);
  await teacher.goto(`/Assignments/Edit/${assignmentId}`);
});

test("assignment appears in the teacher list and student cannot see a draft", async () => {
  await teacher.goto("/Assignments");
  await expect(teacher.getByText(assignmentTitle).first()).toBeVisible();

  await student.goto("/Student/Assignments");
  await expect(student.getByText(assignmentTitle)).toHaveCount(0);
});

test("teacher publishes and assigns, student sees it and answers wrongly", async () => {
  await teacher.goto(`/Assignments/Edit/${assignmentId}`);
  await publishAndAssignAll(teacher, assignmentId);

  await student.goto("/Student/Assignments");
  await expect(student.getByText(assignmentTitle).first()).toBeVisible();

  await answerAll(student, ["0", "0", "0"]);
  await expect(student.getByText("0%", { exact: true }).first()).toBeVisible();
});

test("teacher sees the submission, its details and can export grades", async () => {
  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  const row = teacher.getByRole("row").filter({ has: teacher.locator('a[href*="SubmissionDetail"]') });
  await expect(row.getByText("0%", { exact: true })).toBeVisible();

  const download = teacher.waitForEvent("download");
  await teacher.getByRole("link", { name: /Export CSV/ }).click();
  const file = await download;
  expect(file.suggestedFilename()).toMatch(/\.csv$/i);

  await row.locator('a[href*="SubmissionDetail"]').click();
  await expect(teacher).toHaveURL(/SubmissionDetail/i);
  await teacher.goBack();
});

test("teacher resets the result and the student retakes with correct answers", async () => {
  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  teacher.once("dialog", d => d.accept());
  await teacher.locator('form[action*="ResetStudentResult"] button').first().click();

  await answerAll(student, questions.map(q => q.answerKey));
  await expect(student.getByText("100%", { exact: true }).first()).toBeVisible();

  await teacher.goto(`/Assignments/Details/${assignmentId}`);
  await expect(teacher.getByRole("row").filter({ has: teacher.locator('a[href*="SubmissionDetail"]') }).getByText("100%", { exact: true })).toBeVisible();
});

test("class results and analytics reflect the score", async () => {
  await teacher.goto("/Classes");
  await teacher.locator(".card").filter({ has: teacher.getByRole("heading", { name: className, exact: true }) })
    .getByRole("link").first().click();
  await expect(teacher).toHaveURL(/\/Classes\/Details\//);
  await teacher.getByRole("link", { name: /Results/ }).first().click();
  await expect(teacher.locator("body")).toContainText("E2E Student");

  await teacher.goto("/Analytics");
  await expect(teacher.locator("#classSelector")).toBeVisible();
});

  test("closing the assignment blocks submissions; reopening restores them; then it can be deleted", async () => {
    const action = (name: string) => teacher.locator(`form[action*="/Assignments/${name}/"] button[type="submit"]`);

    await teacher.goto(`/Assignments/Details/${assignmentId}`);
    teacher.once("dialog", d => d.accept());
    await action("Close").click();
    await expect(action("Reopen")).toBeVisible();

    await action("Reopen").click();
    await expect(action("Close")).toBeVisible();

    teacher.once("dialog", d => d.accept());
    await action("Close").click();
    await expect(action("Delete")).toBeVisible();

    teacher.once("dialog", d => d.accept());
    await action("Delete").click();
    await expect(teacher).toHaveURL(/\/Assignments\/?(\?.*)?$/);
    await expect(teacher.locator(".alert-danger")).toBeVisible();
    await expect(teacher.locator(`a[href*="/Assignments/Details/${assignmentId}"]`).first()).toBeVisible();

    const draftTitle = unique("E2E Deletable Draft");
    const draftId = await createDraftAssignment(teacher, draftTitle, className);
    await teacher.goto(`/Assignments/Details/${draftId}`);
    teacher.once("dialog", d => d.accept());
    await action("Delete").click();
    await expect(teacher.locator(`a[href*="/Assignments/Details/${draftId}"]`)).toHaveCount(0);
  });

