"""Byte-compare heightmap dumps across bench runs. usage: py compare.py <refRun> <run> [<run> ...]
Compares every p<pass>_<region>.bin present in the reference run against each other run (raw bytes), prints counts only."""
import os, sys, glob, hashlib
R = os.path.join(os.path.dirname(os.path.abspath(__file__)), "results")
ref = sys.argv[1]
refFiles = {os.path.basename(f): f for f in glob.glob(os.path.join(R, ref, "p*_*.bin"))}
ok_all = True
for run in sys.argv[2:]:
    same = diff = missing = 0
    for name, rf in refFiles.items():
        of = os.path.join(R, run, name)
        if not os.path.exists(of): missing += 1; continue
        if open(rf, "rb").read() == open(of, "rb").read(): same += 1
        else: diff += 1; print(f"  DIFF {run}/{name}")
    extra = len(glob.glob(os.path.join(R, run, "p*_*.bin"))) - same - diff
    ok = diff == 0 and missing == 0
    ok_all &= ok
    print(f"{ref} vs {run}: identical={same} different={diff} missing={missing} extra={extra} -> {'BYTE-IDENTICAL' if ok else 'MISMATCH'}")
# also: pass1 vs pass2 within reference (cold vs warm sampler caches)
p1 = sorted(f for f in refFiles if f.startswith("p1_")); p2s = [f.replace("p1_", "p2_", 1) for f in p1]
w = sum(1 for a, b in zip(p1, p2s) if b in refFiles and open(refFiles[a], "rb").read() == open(refFiles[b], "rb").read())
print(f"{ref} pass1 vs pass2 identical={w}/{len(p1)}")
digest = hashlib.sha256(b"".join(open(refFiles[f], "rb").read() for f in p1)).hexdigest()
print(f"{ref} combined pass1 digest={digest}")
sys.exit(0 if ok_all else 1)
