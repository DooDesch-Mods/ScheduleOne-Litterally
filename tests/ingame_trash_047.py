"""Litterally on game 0.4.7, in-game checks with a DEBUG build: dragging a frozen item and a cleaner emptying trash.

1. Drag: 'tv dragtest place' puts one item in front of the camera, the Virtualizer materializes it frozen
   (performance mode), 'tv dragtest start' drags it the way the game's "Pick up" interaction does. PASS when it was
   kinematic before, is dynamic and dragged after 1.5 s, and moved at least 0.3 m toward the hold point.
2. Cleaner: 'tv cleanerrig setup' hires a cleaner, gives it a home and two cans, and puts trash into Litterally's
   instanced field around the cans. The player is teleported away, so only the CleanerActor materializes that trash.
   PASS when the cleaner took materialized items and the trash level of a can rose (the grabber was emptied into it).

Screenshots before and after the drag and of the cans at the end; their paths are printed.

    pwsh -File Workspace/tools/session/session.ps1 -Slot mptest6 -Build _wt-Trashville-createtrash -Config Debug
    python -I tests/ingame_trash_047.py --slot mptest6
"""
import argparse
import pathlib
import re
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[2]
S1 = ROOT / "Workspace" / "tools" / "bridge" / "s1.py"
TELEPORTS = ["docks", "townhall", "barn", "motel", "sweatshop", "bungalow"]


def s1(slot, *args, check=False):
    out = subprocess.run([sys.executable, "-I", str(S1), "--slot", slot, *args],
                         capture_output=True, text=True, encoding="utf-8", errors="replace")
    if out.returncode != 0:
        print(f"ERROR s1.py {' '.join(args)} exit {out.returncode}: {(out.stderr or out.stdout).strip()[:300]}")
        if check:
            sys.exit(2)
    return out.stdout


def run(slot, cmd, until):
    return s1(slot, "run", *cmd.split(), "--until", until, "--max-wait", "20")


def log_lines(slot, pattern):
    return [l for l in s1(slot, "log", "-n", "200000").splitlines() if re.search(pattern, l)]


def num(text, key):
    m = re.search(key + r"=(-?[\d.]+)", text)
    return float(m.group(1)) if m else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", required=True)
    ap.add_argument("--cleaner-minutes", type=float, default=8.0)
    a = ap.parse_args()
    results = []

    def check(name, ok, detail):
        results.append(ok)
        print(f"{'PASS' if ok else 'FAIL'}  {name}: {detail}", flush=True)

    s1(a.slot, "time", "1000")

    # ---- 1) drag a frozen item ----
    print(run(a.slot, "tv dragtest place", "[dragtest]").strip())
    time.sleep(3)   # the Virtualizer materializes within a few frames
    print("shot before drag:", s1(a.slot, "shot").strip())
    print(run(a.slot, "tv dragtest start", "[dragtest]").strip())
    time.sleep(2.5)
    print("shot while dragging:", s1(a.slot, "shot").strip())
    before = (log_lines(a.slot, r"\[dragtest\] before:") or [""])[-1]
    after = (log_lines(a.slot, r"\[dragtest\] after:") or [""])[-1]
    print(run(a.slot, "tv dragtest stop", "[dragtest]").strip())
    moved = num(after, "moved")
    check("frozen before the drag", "kinematic=True" in before, before.strip()[-140:] or "no 'before' line")
    check("awake and dragged after 1.5 s", "kinematic=False" in after and "dragging=True" in after,
          after.strip()[-160:] or "no 'after' line")
    check("moved toward the hold point", moved is not None and moved >= 0.3, f"moved={moved}")

    # ---- 2) cleaner takes materialized trash and empties it into a can ----
    print(run(a.slot, "tv cleanerrig setup", "[cleanerrig]").strip())
    cans = [tuple(float(v) for v in m.groups()) for m in
            (re.search(r"can \d+ at \(([-\d.]+), ([-\d.]+), ([-\d.]+)\)", l) for l in log_lines(a.slot, r"\[cleanerrig\] can \d+ at")) if m]
    if not cans:
        check("cleaner rig set up", False, "no can position in the log")
    else:
        far = False
        for place in TELEPORTS:
            s1(a.slot, "console", "teleport", place)
            time.sleep(3)
            st = run(a.slot, "tv cleanerrig status", "[cleanerrig]")
            dist = num(st, "playerDist")
            print(f"teleport {place}: playerDist={dist}")
            if dist is not None and dist > 60:
                far = True
                break
        check("player far from the cans", far, "the Virtualizer stays out of the cleaner test")
        deadline = time.time() + a.cleaner_minutes * 60
        last, rose, collected = "", False, 0
        while time.time() < deadline:
            time.sleep(15)
            st = (log_lines(a.slot, r"\[cleanerrig\] status") or [""])[-1]
            if st and st != last:
                last = st
                print("   ", st.strip()[-260:], flush=True)
            collected = int(num(st, "collected") or 0)
            levels = re.search(r"binLevels=(\S+)", st)
            if levels:
                rose = any(int(b) > int(a_) >= 0 for a_, b in
                           (pair.split("->") for pair in levels.group(1).split(",") if "->" in pair))
            if collected > 0 and rose:
                break
        check("cleaner took materialized trash", collected > 0, f"collected={collected}")
        check("trash ended in a can", rose, (re.search(r"binLevels=\S+", last) or [""])[0] or "no status line")
        x, y, z = cans[0]
        print("shot of the cans:", s1(a.slot, "cam", "shot", "--eye", str(x + 4), str(y + 3), str(z + 4),
                                     "--target", str(x), str(y + 0.5), str(z)).strip())
        s1(a.slot, "cam", "release")
        run(a.slot, "tv cleanerrig stop", "[cleanerrig]")

    print(f"{sum(results)}/{len(results)} checks passed")
    sys.exit(0 if all(results) else 1)


if __name__ == "__main__":
    main()
