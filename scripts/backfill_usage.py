#!/usr/bin/env python3
"""One-off backfill of session_usage for historical Claude transcripts.

Santa's (size, mtime) cursor skips unchanged files, so sessions indexed before
the usage feature would never get usage rows from the normal refresh. This
script parses every transcript directly (recursively — subagent sidechain files
included, which santa's top-level ingest does not see) and writes the same
rollups the C# UsageAccumulator produces: dedup by message id keeping the
max-output observation, grouped by model. Idempotent; safe to re-run.

Pricing weights are duplicated with Santa.Core/Usage/ModelUsage.cs and
~/tools/burn-sentinel — keep in sync.
"""
import glob
import json
import os
import sqlite3
from datetime import datetime, timezone

HOME = os.path.expanduser("~")
DB = os.path.join(HOME, ".local/share/santa/index.db")
PROJECTS = os.path.join(HOME, ".claude/projects")

PRICES = [("fable", 10, 50), ("mythos", 10, 50), ("opus", 5, 25),
          ("sonnet-4-6", 3, 15), ("sonnet", 2, 10), ("haiku", 1, 5)]


def price(model):
    for key, i, o in PRICES:
        if key in model:
            return i, o
    return 5, 25


def rollup_file(path):
    by_msg = {}
    sess = os.path.basename(path)[:-6]
    with open(path, "rb") as fh:
        for line in fh:
            if b'"assistant"' not in line:
                continue
            try:
                obj = json.loads(line)
            except Exception:
                continue
            if obj.get("type") != "assistant":
                continue
            msg = obj.get("message") or {}
            u = msg.get("usage")
            model = msg.get("model", "")
            if not u or not model or "synthetic" in model:
                continue
            key = msg.get("id") or obj.get("uuid") or ""
            prev = by_msg.get(key)
            out = u.get("output_tokens", 0)
            if prev and prev[2] >= out:
                continue
            by_msg[key] = (model, u.get("input_tokens", 0), out,
                           u.get("cache_read_input_tokens", 0),
                           u.get("cache_creation_input_tokens", 0))
    models = {}
    for model, i, o, cr, cw in by_msg.values():
        m = models.setdefault(model, [0, 0, 0, 0, 0])
        m[0] += 1
        m[1] += i
        m[2] += o
        m[3] += cr
        m[4] += cw
    return sess, models


def main():
    db = sqlite3.connect(DB, timeout=30)
    now = datetime.now(timezone.utc).isoformat()
    files = glob.glob(PROJECTS + "/**/*.jsonl", recursive=True)
    done = sessions_written = rows = 0
    for path in files:
        sess, models = rollup_file(path)
        done += 1
        if not models:
            continue
        db.execute("DELETE FROM session_usage WHERE session_id = ?", (sess,))
        for model, (turns, i, o, cr, cw) in models.items():
            pi, po = price(model)
            cost = round((i * pi + o * po + cr * pi * 0.1 + cw * pi * 1.25) / 1e6, 4)
            db.execute(
                "INSERT INTO session_usage (session_id, model, turns, input_tokens, output_tokens,"
                " cache_read_tokens, cache_write_tokens, cost_usd, updated_at)"
                " VALUES (?,?,?,?,?,?,?,?,?)",
                (sess, model, turns, i, o, cr, cw, cost, now))
            rows += 1
        db.execute(
            "UPDATE sessions SET cost_usd ="
            " (SELECT COALESCE(SUM(cost_usd),0) FROM session_usage WHERE session_id = ?)"
            " WHERE id = ?", (sess, sess))
        sessions_written += 1
        if done % 500 == 0:
            db.commit()
            print(f"  {done}/{len(files)} files…", flush=True)
    db.commit()
    total = db.execute("SELECT COUNT(*), ROUND(SUM(cost_usd),2) FROM session_usage").fetchone()
    print(f"done: {done} files scanned, {sessions_written} sessions written, {rows} rows")
    print(f"session_usage now holds {total[0]} rows, ${total[1]} total")
    db.close()


if __name__ == "__main__":
    main()
