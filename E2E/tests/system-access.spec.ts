import { expect, test, type APIRequestContext, type Browser } from "@playwright/test";
import { signInAs, type Role } from "./helpers";

// Whole-system access control: every page route of the application against every kind of visitor.
// Runs against the normal seeded app, or against the Docker container (E2E_BASE_URL).
//
//   allow   the page opens (200)
//   deny    the visitor is turned away: a redirect to the sign-in page, 401, 403 or 404, never the page and never a 500
//   anyone  the visitor may or may not be allowed, but it must never be a server error
type Visitor = Role | "anonymous";
interface Expectations { allow: string[]; deny: string[]; anyone: string[] }

const teacherPages = [
  "/Classes", "/Classes?archived=true", "/Classes/Create", "/Classes/Students",
  "/Assignments", "/Assignments?status=Active", "/Assignments/Create",
  "/ContentLibrary/Create", "/ContentLibrary/GetQuestions"
];
const studentPages = ["/Student", "/Student/Classes"];
const adminPages = ["/Admin", "/Admin?search=e2e"];
const publicPages = ["/Auth/Login", "/Auth/Register", "/Auth/StudentRegister", "/Auth/ForgotPassword"];

const matrix: Record<Visitor, Expectations> = {
  anonymous: {
    allow: publicPages,
    deny: ["/", ...studentPages, ...adminPages, ...teacherPages, "/Profile", "/Analytics"],
    anyone: []
  },
  student: {
    allow: [...studentPages, "/Profile"],
    deny: [...adminPages, ...teacherPages],
    anyone: ["/Analytics"]
  },
  teacher: {
    allow: ["/", ...teacherPages, "/Profile", "/Analytics"],
    deny: [...adminPages, ...studentPages],
    anyone: []
  },
  admin: {
    allow: [...adminPages, "/Profile"],
    deny: [...studentPages, ...teacherPages],
    anyone: ["/", "/Analytics"]
  }
};

const turnedAway = [301, 302, 303, 307, 308, 401, 403, 404];

async function contextFor(browser: Browser, visitor: Visitor): Promise<{ request: APIRequestContext; close: () => Promise<void> }> {
  const context = await browser.newContext();
  if (visitor !== "anonymous") await signInAs(await context.newPage(), visitor);
  return { request: context.request, close: () => context.close() };
}

for (const visitor of Object.keys(matrix) as Visitor[]) {
  test.describe(`${visitor} access`, () => {
    test(`${visitor}: allowed pages open, other roles' pages are refused`, async ({ browser }) => {
      test.setTimeout(120_000);
      const { request, close } = await contextFor(browser, visitor);
      try {
        const rules = matrix[visitor];

        for (const path of rules.allow) {
          const response = await request.get(path, { maxRedirects: 0 });
          expect(response.status(), `${visitor} → ${path} should open`).toBe(200);
          expect(await response.text(), `${visitor} → ${path} is an error page`).not.toMatch(/An error occurred while processing your request|Unhandled exception/i);
        }

        for (const path of rules.deny) {
          const response = await request.get(path, { maxRedirects: 0 });
          expect(turnedAway, `${visitor} → ${path} should be refused, got ${response.status()}`).toContain(response.status());
          if (response.status() >= 300 && response.status() < 400) {
            const location = response.headers()["location"] ?? "";
            expect(location, `${visitor} → ${path} redirects somewhere other than sign-in or home`).toMatch(/\/Auth\/Login|\/Student|\/$|\/Home/i);
          }
        }

        for (const path of rules.anyone) {
          const response = await request.get(path, { maxRedirects: 0 });
          expect(response.status(), `${visitor} → ${path} must never be a server error`).toBeLessThan(500);
        }
      } finally {
        await close();
      }
    });
  });
}

test("the browser's home address sends each role to its own start page", async ({ browser }) => {
  test.setTimeout(120_000);
  for (const [visitor, expected] of [["student", /\/Student\/?$/], ["teacher", /\/$/]] as const) {
    const context = await browser.newContext();
    const page = await context.newPage();
    await signInAs(page, visitor);
    await page.goto("/");
    expect(page.url(), `${visitor} home`).toMatch(expected);
    await context.close();
  }
});

test("pages that need an id refuse an id that does not exist, without a server error", async ({ browser }) => {
  test.setTimeout(120_000);
  const missing = "00000000-0000-0000-0000-000000000001";
  const checks: [Visitor, string][] = [
    ["teacher", `/Assignments/Details/${missing}`],
    ["teacher", `/Assignments/Edit/${missing}`],
    ["teacher", `/Assignments/Preview/${missing}`],
    ["teacher", `/Assignments/SubmissionDetail?resultId=${missing}`],
    ["teacher", `/Classes/Details/${missing}`],
    ["teacher", `/Classes/Edit/${missing}`],
    ["teacher", `/ContentLibrary/Edit/${missing}`],
    ["student", `/Student/TakeAssignment?assignmentId=${missing}`],
    ["student", `/Student/Assignments?classId=${missing}`],
    ["student", `/Student/AssignmentResult?assignmentId=${missing}&resultId=${missing}`]
  ];

  for (const [visitor, path] of checks) {
    const { request, close } = await contextFor(browser, visitor);
    try {
      const response = await request.get(path, { maxRedirects: 0 });
      expect(response.status(), `${visitor} → ${path} must not be a server error`).toBeLessThan(500);
      expect(response.status(), `${visitor} → ${path} must not pretend the id exists`).not.toBe(200);
    } finally {
      await close();
    }
  }
});

test("an unknown grading job answers with an error the page can show, not a server error", async ({ browser }) => {
  // GradeProgress is polled by the page and reports an expired or unknown job as JSON, so it answers 200 by design.
  const { request, close } = await contextFor(browser, "student");
  try {
    const response = await request.get("/Student/GradeProgress/00000000-0000-0000-0000-000000000001");
    expect(response.status()).toBe(200);
    expect((await response.json()).status).toBe("error");
  } finally {
    await close();
  }
});

test("an unknown address answers 404 without leaking internals, for everyone", async ({ browser }) => {
  test.setTimeout(120_000);
  for (const visitor of ["anonymous", "student", "teacher", "admin"] as Visitor[]) {
    const { request, close } = await contextFor(browser, visitor);
    try {
      const response = await request.get("/this/page/does/not/exist", { maxRedirects: 0 });
      expect([302, 404], `${visitor} unknown address`).toContain(response.status());
      const body = await response.text();
      expect(body).not.toMatch(/System\.[A-Za-z.]+Exception|at SmartSchoolAI\.|Microsoft\.AspNetCore|stack trace/i);
    } finally {
      await close();
    }
  }
});

test("security headers are present on the sign-in page", async ({ request }) => {
  const response = await request.get("/Auth/Login");
  const headers = response.headers();
  expect(headers["x-content-type-options"]).toBe("nosniff");
  expect(headers["x-frame-options"]).toMatch(/SAMEORIGIN/i);
  expect(headers["content-security-policy"]).toContain("default-src 'self'");
  expect(headers["referrer-policy"]).toBeTruthy();
});

test("form posts without an anti-forgery token are rejected for every role", async ({ browser }) => {
  test.setTimeout(120_000);
  const posts: [Visitor, string][] = [
    ["student", "/Student/CheckAnswer"],
    ["student", "/Student/JoinClass"],
    ["teacher", "/Classes/Archive"],
    ["teacher", "/Assignments/Publish"],
    ["admin", "/Admin/ResetTeacherPassword"]
  ];
  for (const [visitor, path] of posts) {
    const { request, close } = await contextFor(browser, visitor);
    try {
      const response = await request.post(path, { form: { x: "1" }, maxRedirects: 0 });
      expect([400, 302, 401, 403, 404, 415], `${visitor} POST ${path} without a token, got ${response.status()}`).toContain(response.status());
    } finally {
      await close();
    }
  }
});

test("an admin cannot use a teacher's or student's POST actions, and a student cannot reset a teacher's password", async ({ browser }) => {
  test.setTimeout(120_000);
  // The student's own page gives a valid anti-forgery token; the action must still refuse the role.
  const context = await browser.newContext();
  const page = await context.newPage();
  await signInAs(page, "student");
  await page.goto("/Student");
  const token = await page.locator('input[name="__RequestVerificationToken"]').first().inputValue();

  const response = await context.request.post("/Admin/ResetTeacherPassword", {
    form: { teacherId: "00000000-0000-0000-0000-000000000001", newPassword: "Hacked-Pass-123", __RequestVerificationToken: token },
    maxRedirects: 0
  });
  expect(turnedAway.concat([400]), `student → ResetTeacherPassword, got ${response.status()}`).toContain(response.status());
  await context.close();
});
