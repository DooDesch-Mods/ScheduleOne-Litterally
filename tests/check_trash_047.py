"""Litterally on game 0.4.7: trash is placed again without the per-frame MissingMethodException.

Reads the newest MelonLoader log of a running test slot (through Workspace/tools/bridge/s1.py), waits until
Litterally has written enough telemetry lines, and reports PASS or FAIL per check.

    python tests/check_trash_047.py --slot mptest6               # fixed build: every check must pass
    python tests/check_trash_047.py --slot mptest6 --expect-bug  # old build: the bug must show

Start the game first, for example:
    pwsh -File Workspace/tools/session/session.ps1 -Slot mptest6 -Build ..\\_wt-Trashville-createtrash -Config Release
"""
import argparse
import pathlib
import re
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[2]
S1 = ROOT / "Workspace" / "tools" / "bridge" / "s1.py"

MISSING = re.compile(r"MissingMethodException.*TrashManager\.CreateTrashItem")
PATCH_FAIL = re.compile(r"\[Litterally\].*Harmony patch failed")
TELEMETRY = re.compile(r"\[Litterally\].*\[telemetry\].*field-live=(\d+)\s+real-player=(\d+).*real-cleaner=(\d+)\s+mgr=(-?\d+)\s+absorbed=(\d+)")
VIRT_ERR = re.compile(r"\[Litterally\].*\[virt\] materialize error")
ACTIVE = re.compile(r"\[Litterally\].*Performance layer ACTIVE")


def read_log(slot):
    out = subprocess.run([sys.executable, "-I", str(S1), "--slot", slot, "log", "-n", "200000"],
                         capture_output=True, text=True, encoding="utf-8", errors="replace")
    if out.returncode != 0:
        print(f"ERROR s1.py log exit {out.returncode}: {out.stderr.strip()[:300]}")
        sys.exit(2)
    return out.stdout.splitlines()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", required=True)
    ap.add_argument("--expect-bug", action="store_true", help="old build: pass when the bug is present")
    ap.add_argument("--telemetry", type=int, default=3, help="telemetry lines to wait for (15 s each)")
    ap.add_argument("--timeout", type=int, default=180)
    a = ap.parse_args()

    deadline = time.time() + a.timeout
    lines = read_log(a.slot)
    while sum(1 for l in lines if TELEMETRY.search(l)) < a.telemetry and time.time() < deadline:
        if a.expect_bug and sum(1 for l in lines if MISSING.search(l)) >= 100:
            break
        time.sleep(5)
        lines = read_log(a.slot)

    missing = [l for l in lines if MISSING.search(l)]
    patch_fail = [l for l in lines if PATCH_FAIL.search(l)]
    virt_err = [l for l in lines if VIRT_ERR.search(l)]
    tele = [TELEMETRY.search(l) for l in lines if TELEMETRY.search(l)]
    active = any(ACTIVE.search(l) for l in lines)
    real_max = max((int(m.group(2)) for m in tele), default=0)
    field_max = max((int(m.group(1)) for m in tele), default=0)

    results = []

    def check(name, ok, detail):
        results.append(ok)
        print(f"{'PASS' if ok else 'FAIL'}  {name}: {detail}")

    if a.expect_bug:
        check("bug present: CreateTrashItem MissingMethodException", len(missing) > 0,
              f"{len(missing)} lines" + (f", first: {missing[0].strip()[:200]}" if missing else ""))
    else:
        check("performance layer active", active, "found" if active else "no 'Performance layer ACTIVE' line")
        check("no CreateTrashItem MissingMethodException", not missing,
              f"{len(missing)} lines" + (f", first: {missing[0].strip()[:200]}" if missing else ""))
        check("all Harmony patches applied", not patch_fail,
              patch_fail[0].strip()[:200] if patch_fail else "no 'Harmony patch failed' line")
        check("no materialize error", not virt_err,
              virt_err[0].strip()[:200] if virt_err else "none")
        check("telemetry written", len(tele) >= a.telemetry, f"{len(tele)} lines")
        check("trash field holds trash", field_max > 0, f"max field-live={field_max}")
        check("real trash placed near the player", real_max > 0, f"max real-player={real_max}")
        if tele:
            print("      last telemetry:", tele[-1].group(0)[:220])

    print(f"{sum(results)}/{len(results)} checks passed")
    sys.exit(0 if all(results) else 1)


if __name__ == "__main__":
    main()
