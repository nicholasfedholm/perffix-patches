r"""Build the Farseer perffix: helper DLL -> Cecil patch of stock Farseer.dll -> mod zip in out\<variant>\.
usage: py build.py phase1|phase2
"""
import os, sys, subprocess, zipfile, hashlib, shutil

WS = os.path.dirname(os.path.dirname(os.path.abspath(__file__))) if os.path.basename(os.path.dirname(os.path.abspath(__file__))) == "bench" else os.path.dirname(os.path.abspath(__file__))
STOCK_ZIP = os.path.join(os.environ["APPDATA"], "VintagestoryData", "Mods", "farseer_1.4.0.zip")
variant = sys.argv[1]
assert variant in ("phase1", "phase2")

def run(cmd, **kw):
    r = subprocess.run(cmd, capture_output=True, text=True, **kw)
    lines = [l for l in (r.stdout + r.stderr).splitlines() if " error " in l or l.startswith(("PATCH", "VERIFY")) or "Exception" in l]
    for l in lines: print("  " + l[:300])
    if r.returncode != 0: sys.exit(f"FAILED: {' '.join(cmd)}")

run(["dotnet", "build", os.path.join(WS, "src", "FarseerPerffix"), "-c", "Release", "-v", "q", "-nologo"])
run(["dotnet", "build", os.path.join(WS, "tools", "Patcher"), "-c", "Release", "-v", "q", "-nologo"])
helper = os.path.join(WS, "src", "FarseerPerffix", "bin", "Release", "net10.0", "FarseerPerffix.dll")
outdir = os.path.join(WS, "out", variant)
if os.path.exists(outdir): shutil.rmtree(outdir)
os.makedirs(outdir)
patched = os.path.join(outdir, "Farseer.dll")
args = ["dotnet", os.path.join(WS, "tools", "Patcher", "bin", "Release", "net10.0", "Patcher.dll"),
        os.path.join(WS, "work", "stock", "Farseer.dll"), helper, patched] + (["--phase2"] if variant == "phase2" else [])
run(args)

zpath = os.path.join(outdir, "farseer_1.4.0-perffix.zip")
with zipfile.ZipFile(STOCK_ZIP) as src, zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as dst:
    for info in src.infolist():
        data = open(patched, "rb").read() if info.filename == "Farseer.dll" else src.read(info.filename)
        dst.writestr(info, data)
    dst.write(helper, "FarseerPerffix.dll")
def sha(p): return hashlib.sha256(open(p, "rb").read()).hexdigest()
with zipfile.ZipFile(zpath) as z: names = z.namelist()
print(f"zip={zpath}\n entries={len(names)} {names}\n sha256 zip={sha(zpath)}\n sha256 Farseer.dll={sha(patched)}\n sha256 FarseerPerffix.dll={sha(helper)}")
