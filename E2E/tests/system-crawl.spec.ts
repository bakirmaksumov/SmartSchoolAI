import { expect, test, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { createClass, createDraftAssignment, joinClass, publishAndAssignAll, signInAs, submitAssignment, unique, type Role } from "./helpers";

// Whole-system page crawl. After real data exists (a class, a published assignment, a submitted attempt), each role
// walks every page it can reach by following the links on the pages themselves, as a person would, and every page
// must open cleanly:
//   - the response is a success, never a server error or an error page;
//   - no script throws an uncaught error;
//   - no request to this application (stylesheet, script, image, file) fails.
// Runs against the normal seeded app, or against the Docker container (E2E_BASE_URL).
test.describe.configure({ mode: "serial" });

const className = unique("E2E System Class");
const assignmentTitle = unique("E2E System Assignment");
let assignmentId = "";
let classId = "";
let resultId = "";

const pages: Record<Role, Page> = {} as Record<Role, Page>;

test.beforeAll(async ({ browser }) => {
  for (const role of ["teacher", "student", "admin"] as Role[]) {
    pages[role] = await (await browser.newContext()).newPage();
    await signInAs(pages[role], role);
  }
});

test.afterAll(async () => {
  for (const page of Object.values(pages)) await page.context().close();
});

test("setup: a class, a published assignment and a submitted attempt, so every kind of page exists", async () => {
  test.setTimeout(240_000);
  const { teacher, student } = pages;

  const code = await createClass(teacher, className);
  await joinClass(student, code, className);

  await teacher.goto("/Classes");
  const classLink = teacher.locator(".card").filter({ has: teacher.getByRole("heading", { name: className, exact: true }) }).locator('a[href*="/Classes/Details/"]').first();
  classId = (await classLink.getAttribute("href"))!.match(/\/Classes\/Details\/([0-9a-f-]{36})/i)![1];

  assignmentId = await createDraftAssignment(teacher, assignmentTitle, className);
  const token = await teacher.locator('input[name="__RequestVerificationToken"]').first().inputValue();
  const add = async (question: Record<string, unknown>) => {
    const response = await teacher.request.post(`/Assignments/AddQuestion/${assignmentId}`, {
      form: { questionJson: JSON.stringify({ id: randomUUID(), difficulty: "Medium", ...question }), __RequestVerificationToken: token }
    });
    expect(response.ok(), "AddQuestion").toBe(true);
  };
  await add({ title: "System check: 2 plus 2", type: 0, answerKey: "4" });
  await add({ title: "System check: pick the even number", type: 4, options: ["3", "4", "5", "7"], answerKey: "B" });

  await teacher.goto(`/Assignments/Edit/${assignmentId}`);
  await publishAndAssignAll(teacher, assignmentId);

  await student.goto(`/Student/TakeAssignment?assignmentId=${assignmentId}`);
  await student.locator('.nav-btn[data-idx="0"]').click();
  await student.locator('.answer-textarea[data-qkey="q_0"]').fill("4");
  await student.locator('.nav-btn[data-idx="1"]').click();
  await student.locator('.option-card[data-qkey="q_1"][data-value="B"]').click();
  await submitAssignment(student);
  await expect(student).toHaveURL(/\/Student\/AssignmentResult/i);
  resultId = new URL(student.url()).searchParams.get("resultId")!;
  expect(resultId).toMatch(/^[0-9a-f-]{36}$/i);
});

interface Crawl { visited: string[]; problems: string[] }

const unwanted = /An error occurred while processing your request|Unhandled exception|System\.[A-Za-z.]+Exception|Microsoft\.EntityFrameworkCore\.[A-Za-z.]+Exception/i;
const neverFollow = /\/Auth\/Logout/i;   // signing out would end the crawl

async function crawl(page: Page, starts: string[], limit: number): Promise<Crawl> {
  const origin = new URL(page.url() === "about:blank" ? (test.info().project.use.baseURL as string) : page.url()).origin;
  const queue = [...starts];
  const seen = new Set<string>();
  const visited: string[] = [];
  const problems: string[] = [];

  let current = "";
  const onPageError = (error: Error) => problems.push(`${current}: uncaught script error: ${error.message}`);
  const onResponse = (response: { url(): string; status(): number }) => {
    const url = response.url();
    if (!url.startsWith(origin) || url.endsWith("/favicon.ico")) return;
    // The page's own navigation is judged below; this catches failed assets and background requests.
    if (response.status() >= 400 && url !== new URL(current, origin).href) problems.push(`${current}: ${response.status()} for ${url.replace(origin, "")}`);
  };
  page.on("pageerror", onPageError);
  page.on("response", onResponse);

  const key = (href: string) => {
    const u = new URL(href, origin);
    u.hash = "";
    return u.pathname + u.search;
  };

  while (queue.length > 0 && visited.length < limit) {
    const next = queue.shift()!;
    const id = key(next);
    if (seen.has(id) || neverFollow.test(id)) continue;
    seen.add(id);
    current = id;

    let status = 0;
    try {
      const response = await page.goto(id, { waitUntil: "load" });
      status = response?.status() ?? 0;
    } catch (error) {
      // A link to a file (a CSV export, a photo) starts a download instead of showing a page.
      if (!/Download is starting|ERR_ABORTED/i.test(String(error))) {
        problems.push(`${id}: could not open (${String(error).split("\n")[0]})`);
        continue;
      }
      const file = await page.context().request.get(id);
      if (file.status() >= 400) problems.push(`${id}: file link answered ${file.status()}`);
      visited.push(id);
      continue;
    }

    visited.push(id);
    if (status >= 400 || status === 0) {
      problems.push(`${id}: answered ${status}`);
      continue;
    }

    const text = await page.locator("body").innerText().catch(() => "");
    if (unwanted.test(text)) problems.push(`${id}: shows an error page`);
    if (!(await page.title()).trim()) problems.push(`${id}: has no title`);

    // Give scripts that run on load a moment to fail if they are going to.
    await page.waitForTimeout(150);

    const hrefs = await page.locator("a[href]").evaluateAll(anchors =>
      anchors.map(a => a.getAttribute("href") ?? "").filter(h => h.startsWith("/") && !h.startsWith("//")));
    for (const href of hrefs) if (!seen.has(key(href)) && !neverFollow.test(href)) queue.push(href);
  }

  page.off("pageerror", onPageError);
  page.off("response", onResponse);
  return { visited, problems };
}

test("teacher: every reachable page opens cleanly", async () => {
  test.setTimeout(420_000);
  const result = await crawl(pages.teacher, [`/Assignments/Details/${assignmentId}`, `/Classes/Details/${classId}`, "/", "/Classes", "/Assignments", "/Analytics", "/Profile", "/Classes/Students"], 90);
  console.log(`teacher crawled ${result.visited.length} pages`);
  expect(result.problems, `teacher problems:\n${result.problems.join("\n")}`).toEqual([]);

  const all = result.visited.join(" ");
  expect(result.visited.length, "the crawl should reach a good part of the app").toBeGreaterThanOrEqual(15);
  for (const area of [/\/Classes\/Details\//, /\/Assignments\/Details\//, /\/Assignments\/Edit\//, /SubmissionDetail/, /\/Analytics/]) {
    expect(all, `teacher never reached ${area}`).toMatch(area);
  }
});

test("student: every reachable page opens cleanly", async () => {
  test.setTimeout(300_000);
  const result = await crawl(pages.student, [`/Student/AssignmentResult?assignmentId=${assignmentId}&resultId=${resultId}`, "/Student", "/Student/Classes", "/Student/Assignments", "/Profile"], 60);
  console.log(`student crawled ${result.visited.length} pages`);
  expect(result.problems, `student problems:\n${result.problems.join("\n")}`).toEqual([]);

  const all = result.visited.join(" ");
  expect(result.visited.length).toBeGreaterThanOrEqual(5);
  expect(all, "student never reached the result page").toMatch(/AssignmentResult/);
});

test("admin: every reachable page opens cleanly", async () => {
  test.setTimeout(120_000);
  const result = await crawl(pages.admin, ["/Admin", "/Profile"], 20);
  console.log(`admin crawled ${result.visited.length} pages`);
  expect(result.problems, `admin problems:\n${result.problems.join("\n")}`).toEqual([]);
  expect(result.visited.length).toBeGreaterThanOrEqual(2);
});

test("the deep pages the crawl may miss also open: result, submission, assignment tools", async () => {
  test.setTimeout(180_000);
  const { teacher, student } = pages;
  const deep: [Page, string][] = [
    [student, `/Student/Assignments?classId=${await classIdFor(student)}`],
    [student, `/Student/AssignmentResult?assignmentId=${assignmentId}&resultId=${resultId}`],
    [student, `/Student/Chat?resultId=${resultId}`],
    [teacher, `/Assignments/Details/${assignmentId}`],
    [teacher, `/Assignments/Preview/${assignmentId}`],
    [teacher, `/Assignments/SubmissionDetail?resultId=${resultId}`]
  ];
  for (const [page, path] of deep) {
    const response = await page.goto(path);
    expect(response?.status(), path).toBe(200);
    await expect(page.locator("body")).not.toContainText(unwanted);
  }

  // The grade export is a file, not a page.
  const csv = await teacher.context().request.get(`/Assignments/ExportGrades/${assignmentId}`);
  expect(csv.status()).toBe(200);
  expect(await csv.text()).toContain("E2E Student");
});

async function classIdFor(student: Page): Promise<string> {
  await student.goto("/Student/Classes");
  const href = await student.locator('a[href*="/Student/Assignments?classId="]').first().getAttribute("href");
  return new URL(href!, "http://x").searchParams.get("classId")!;
}
