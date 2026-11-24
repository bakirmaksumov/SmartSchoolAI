"""Snapshot a SmartSchoolAI SQLite database (and its uploaded files) and compare two snapshots.

Used to prove an update keeps existing data: take a snapshot of a copy of the database before the update, start the
new version on that copy (it migrates on start), take another snapshot, then compare.

    python dbcheck.py snapshot <db file> <out.json> [--files <App_Data dir>]
    python dbcheck.py compare  <before.json> <after.json>

The comparison is row by row, over every column that existed before, keyed by the full row — not a count of records.
It also compares what each student can open (active assignments they are a recipient of, in a class they are in),
and the uploaded files by size and SHA-256. Exit code 0 = no difference, 1 = differences (listed).
Read-only: the database is opened with mode=ro.
"""
import hashlib, json, os, sqlite3, sys

def snapshot(db_path, out_path, files_dir=None):
    con = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    tables = [r[0] for r in con.execute(
        "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name")]
    data = {"tables": {}, "access": [], "files": {}}
    for t in tables:
        cols = [r[1] for r in con.execute(f'PRAGMA table_info("{t}")')]
        rows = [dict(zip(cols, ["NULL" if v is None else str(v) for v in row]))
                for row in con.execute(f'SELECT * FROM "{t}"')]
        data["tables"][t] = {"columns": cols, "rows": rows}
    data["access"] = sorted(r[0] for r in con.execute("""
        SELECT x.StudentId || ' > ' || a.Id FROM AssignmentStudents x
        JOIN Assignments a ON a.Id = x.AssignmentId
        JOIN ClassStudents cs ON cs.ClassId = a.ClassId AND cs.StudentId = x.StudentId
        WHERE a.Status = 'Active' AND a.DeletedAt IS NULL"""))
    if files_dir and os.path.isdir(files_dir):
        for root, _, names in os.walk(files_dir):
            for n in names:
                p = os.path.join(root, n)
                rel = os.path.relpath(p, files_dir).replace("\\", "/")
                if rel.startswith("backups/"):
                    continue
                with open(p, "rb") as f:
                    data["files"][rel] = {"size": os.path.getsize(p), "sha256": hashlib.sha256(f.read()).hexdigest()}
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1, sort_keys=True)
    print(f"snapshot: {len(tables)} tables, {sum(len(v['rows']) for v in data['tables'].values())} rows, "
          f"{len(data['access'])} student-assignment access pairs, {len(data['files'])} files -> {out_path}")

def key(row, cols):
    return "|".join(f"{c}={row.get(c, '<missing>')}" for c in cols)

def compare(before_path, after_path):
    b = json.load(open(before_path, encoding="utf-8"))
    a = json.load(open(after_path, encoding="utf-8"))
    problems, report = [], []
    for t, tb in sorted(b["tables"].items()):
        if t == "__EFMigrationsHistory":
            continue
        ta = a["tables"].get(t)
        if ta is None:
            problems.append(f"table {t} is gone"); continue
        missing_cols = [c for c in tb["columns"] if c not in ta["columns"]]
        if missing_cols:
            problems.append(f"table {t}: columns gone: {missing_cols}")
        cols = [c for c in tb["columns"] if c in ta["columns"]]
        rb = sorted(key(r, cols) for r in tb["rows"])
        ra = sorted(key(r, cols) for r in ta["rows"])
        same = rb == ra
        new_cols = [c for c in ta["columns"] if c not in tb["columns"]]
        report.append(f"  {t:<22} rows {len(rb):>4} -> {len(ra):>4}  identical over {len(cols)} old columns: {'yes' if same else 'NO'}"
                      + (f"  (new columns: {', '.join(new_cols)})" if new_cols else ""))
        if not same:
            gone = [x for x in rb if x not in ra][:3]; added = [x for x in ra if x not in rb][:3]
            problems.append(f"table {t} differs; e.g. before-only {gone} after-only {added}")
    for t in sorted(set(a["tables"]) - set(b["tables"])):
        n = len(a["tables"][t]["rows"])
        report.append(f"  {t:<22} NEW table, rows: {n}")
        if t != "__EFMigrationsHistory" and n:
            problems.append(f"new table {t} is not empty ({n} rows)")
    if "Assignments" in a["tables"]:
        vals = sorted({(r.get("Audience"), r.get("LateJoinPolicy")) for r in a["tables"]["Assignments"]["rows"]})
        report.append(f"  Assignments (Audience, LateJoinPolicy) after: {vals}")
        if any(v != ("SelectedStudents", "Manual") for v in vals):
            problems.append(f"an existing assignment did not keep the manual rule: {vals}")
    report.append(f"  student access pairs  {len(b['access'])} -> {len(a['access'])}  identical: {'yes' if b['access'] == a['access'] else 'NO'}")
    if b["access"] != a["access"]:
        problems.append("what students can open changed")
    report.append(f"  uploaded files        {len(b['files'])} -> {len(a['files'])}  identical: {'yes' if b['files'] == a['files'] else 'NO'}")
    if b["files"] != a["files"]:
        problems.append("uploaded files changed")
    print("\n".join(report))
    if problems:
        print("\nDIFFERENCES:\n  " + "\n  ".join(problems)); return 1
    print("\nNo differences: every old row, link, value, access pair and file is unchanged; new tables are empty.")
    return 0

if __name__ == "__main__":
    if len(sys.argv) >= 4 and sys.argv[1] == "snapshot":
        files = sys.argv[sys.argv.index("--files") + 1] if "--files" in sys.argv else None
        snapshot(sys.argv[2], sys.argv[3], files)
    elif len(sys.argv) == 4 and sys.argv[1] == "compare":
        sys.exit(compare(sys.argv[2], sys.argv[3]))
    else:
        print(__doc__); sys.exit(2)
