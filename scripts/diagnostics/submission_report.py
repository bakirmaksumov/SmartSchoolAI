"""Read-only report on one student's hand-in of one assignment.

    python submission_report.py --db <smartschool.db> --assignment <id> --student <id> [--app-data <App_Data dir>]

Prints the assignment, whether the student is a recipient and in the class, any late-join request, every attempt
(submitted or in progress) with its times, submission key, AI-marking state and teacher review, the answers of each
attempt, and the activity-log lines about this student's submissions (SubmitStarted / SubmitSaved /
SubmitAlreadySaved / SubmitRejected / SubmitFailed / SubmitGradingFailed — written from this version on).

It never writes: the database is opened with mode=ro, the log is only read. All times are UTC as stored.

How to read the outcome:
  * an attempt with IsSubmitted = 1 is handed in, whatever its score or review;
  * IsSubmitted = 0 is a draft — the student opened or saved work but the final Submit was not saved;
  * SubmitStarted without a SubmitSaved for the same key means that request did not finish saving;
  * no SubmitStarted at all (from this version on) means the request never reached the application.
Older versions wrote no submit lines, so for work before the update only the attempts themselves can tell.
"""
import argparse, os, sqlite3, sys

def rows(con, sql, *args):
    cur = con.execute(sql, args)
    cols = [d[0] for d in cur.description]
    return [dict(zip(cols, r)) for r in cur.fetchall()]

def has_column(con, table, column):
    return any(r[1] == column for r in con.execute(f'PRAGMA table_info("{table}")'))

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--db", required=True)
    ap.add_argument("--assignment", required=True)
    ap.add_argument("--student", required=True)
    ap.add_argument("--app-data", help="App_Data folder (for logs/activity.log); default: next to the database")
    a = ap.parse_args()
    aid, sid = a.assignment.upper(), a.student.upper()

    con = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
    con.row_factory = None

    asg = rows(con, "SELECT Id, Title, Status, Type, ClassId, Deadline, AvailableFrom, MaxAttempts, AllowLateSubmission, DeletedAt FROM Assignments WHERE upper(Id)=?", aid)
    if not asg:
        print("Assignment not found."); return 1
    asg = asg[0]
    stu = rows(con, "SELECT Id, FullName, Email, DeletedAt FROM Users WHERE upper(Id)=?", sid)
    print("== Assignment"); print(f"  {asg['Title']}  [{asg['Status']}, {asg['Type']}]  due {asg['Deadline']}  opens {asg['AvailableFrom']}  max attempts {asg['MaxAttempts'] or 'unlimited'}  late allowed {bool(asg['AllowLateSubmission'])}")
    print("== Student"); print(f"  {stu[0]['FullName']} <{stu[0]['Email']}>" if stu else "  not found")

    rec = rows(con, "SELECT AssignedAt, StartDateOverride, DueDateOverride FROM AssignmentStudents WHERE upper(AssignmentId)=? AND upper(StudentId)=?", aid, sid)
    member = rows(con, "SELECT JoinedAt FROM ClassStudents WHERE upper(ClassId)=? AND upper(StudentId)=?", asg["ClassId"].upper(), sid)
    print("== Access")
    print(f"  recipient: {'yes, since ' + rec[0]['AssignedAt'] if rec else 'NO (not given this assignment)'}"
          + (f"; personal start {rec[0]['StartDateOverride']}" if rec and rec[0]['StartDateOverride'] else "")
          + (f"; personal due {rec[0]['DueDateOverride']}" if rec and rec[0]['DueDateOverride'] else ""))
    print(f"  in the class: {'yes, joined ' + member[0]['JoinedAt'] if member else 'NO'}")
    if has_column(con, "Assignments", "Audience"):
        req = rows(con, "SELECT Status, RequestedAt, DecidedAt FROM LateJoinRequests WHERE upper(AssignmentId)=? AND upper(StudentId)=?", aid, sid)
        for r in req: print(f"  late-join request: {r['Status']} (asked {r['RequestedAt']}, decided {r['DecidedAt']})")

    extra = ", SubmissionKey, GradingPending" if has_column(con, "StudentResults", "SubmissionKey") else ""
    attempts = rows(con, f"""SELECT Id, AttemptNumber, IsSubmitted, CreatedAt, SubmittedAt, UpdatedAt, Score, OverallStatus,
                             TeacherReviewedAt, DeletedAt{extra} FROM StudentResults
                             WHERE upper(AssignmentId)=? AND upper(StudentId)=? ORDER BY AttemptNumber""", aid, sid)
    print(f"== Attempts ({len(attempts)})")
    if not attempts:
        print("  none — the student never saved anything for this assignment (no draft, no submission).")
    for t in attempts:
        state = "SUBMITTED" if t["IsSubmitted"] else "DRAFT (not submitted)"
        when = f"submitted {t['SubmittedAt']}" if t["IsSubmitted"] else f"started {t['CreatedAt']}, last saved {t['UpdatedAt'] or '-'} (its SubmittedAt {t['SubmittedAt']} is only the creation time)"
        print(f"  #{t['AttemptNumber']} {state}  {when}")
        print(f"      score {t['Score']}% {t['OverallStatus']}  teacher reviewed {t['TeacherReviewedAt'] or 'no'}"
              + (f"  key {t.get('SubmissionKey')}  AI still marking {bool(t.get('GradingPending'))}" if extra else "")
              + ("  DELETED " + t["DeletedAt"] if t["DeletedAt"] else ""))
        sketch = ", SketchPath" if has_column(con, "StudentAnswers", "SketchPath") else ""
        for ans in rows(con, f"SELECT QuestionIndex, AnswerValue, IsCorrect, PointsAwarded, TeacherOverridden, NeedsReview, ImagePath{sketch} FROM StudentAnswers WHERE upper(StudentResultId)=? AND DeletedAt IS NULL ORDER BY QuestionIndex", t["Id"].upper()):
            media = " photo" if ans["ImagePath"] else ""
            media += " drawing" if ans.get("SketchPath") else ""
            verdict = {1: "correct", 0: "wrong", None: "not marked"}[ans["IsCorrect"]]
            print(f"      Q{ans['QuestionIndex'] + 1}: {verdict}, {ans['PointsAwarded']} pts{media}"
                  + (" (teacher's mark)" if ans["TeacherOverridden"] else "") + (" NEEDS REVIEW" if ans["NeedsReview"] else "")
                  + f"  answer: {str(ans['AnswerValue'])[:40]!r}")

    app_data = a.app_data or os.path.join(os.path.dirname(os.path.abspath(a.db)), "App_Data")
    print("== Submit requests in the activity log")
    found = 0
    for name in ("activity.log.1", "activity.log"):
        path = os.path.join(app_data, "logs", name)
        if not os.path.exists(path): continue
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                if line.split("\t")[1:2] and line.split("\t")[1].startswith("Submit") and sid in line.upper() and aid in line.upper():
                    print("  " + line.rstrip()); found += 1
    if not found:
        print("  none found (older versions did not record submissions; or the log was rotated, or --app-data points elsewhere)")
    return 0

if __name__ == "__main__":
    sys.exit(main())
