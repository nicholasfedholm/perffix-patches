"""Bench runner: builds an isolated VS data path (never the live one), copies the terrain mod stack + chosen Farseer build,
runs a headless server with the FarseerBench harness, and prints a short summary.
usage: py run_bench.py <runName> <farseerZip> [--mode plain|prio] [--passes N] [--serialref] [--env K=V ...]
"""
import os, sys, shutil, subprocess, zipfile, json, time, re, argparse

GAME = os.path.join(os.environ["APPDATA"], "Vintagestory")
LIVE = os.path.join(os.environ["APPDATA"], "VintagestoryData")
WS = os.path.dirname(os.path.dirname(os.path.abspath(__file__))) if os.path.basename(os.path.dirname(os.path.abspath(__file__))) == "bench" else os.path.dirname(os.path.abspath(__file__))
STACK = ["algernonsterrainsampler_1.3.0.zip", "watersheds_7.0.4-perffix.zip", "TerraPrety_7.11.2-pre.1.zip",
         "terrapretydefault.zip", "P5WorldgenEngine-0.1.0.zip", "rivers_5.0.3.zip"]
CONFIGS = ["P5WorldgenEngine.json", "TerraPrety.json", "Watersheds", "farseer-server.json", "rivers.json"]
SEED = "424242"

ap = argparse.ArgumentParser()
ap.add_argument("run"); ap.add_argument("farseer")
ap.add_argument("--mode", default="plain"); ap.add_argument("--passes", default="2")
ap.add_argument("--serialref", action="store_true"); ap.add_argument("--env", nargs="*", default=[])
a = ap.parse_args()

data = os.path.join(WS, "bench", "data", a.run)
out = os.path.join(WS, "bench", "results", a.run)
assert os.path.abspath(data).startswith(WS) and os.path.abspath(out).startswith(WS)
for d in (data, out):
    if os.path.exists(d): shutil.rmtree(d)
os.makedirs(os.path.join(data, "Mods")); os.makedirs(os.path.join(data, "ModConfig")); os.makedirs(out)

# mods: read-only copies from live Mods + bench harness + chosen farseer build
for z in STACK: shutil.copy2(os.path.join(LIVE, "Mods", z), os.path.join(data, "Mods", z))
shutil.copy2(a.farseer, os.path.join(data, "Mods", os.path.basename(a.farseer)))
hz = os.path.join(data, "Mods", "farseerbench.zip")
with zipfile.ZipFile(hz, "w") as zf:
    hb = os.path.join(WS, "bench", "harness", "bin", "Release", "net10.0")
    zf.write(os.path.join(hb, "FarseerBench.dll"), "FarseerBench.dll")
    zf.write(os.path.join(WS, "bench", "harness", "modinfo.json"), "modinfo.json")
# configs: read-only copies of live ModConfig so terrain matches the live settings
for c in CONFIGS:
    s = os.path.join(LIVE, "ModConfig", c)
    if os.path.isdir(s): shutil.copytree(s, os.path.join(data, "ModConfig", c))
    elif os.path.exists(s): shutil.copy2(s, os.path.join(data, "ModConfig", c))

server = os.path.join(GAME, "VintagestoryServer.exe")
subprocess.run([server, "--dataPath", data, "--genconfig"], cwd=GAME, capture_output=True, timeout=300)
cfgp = os.path.join(data, "serverconfig.json")
cfg = json.load(open(cfgp, encoding="utf-8-sig"))
cfg["Port"] = 42499; cfg["MaxClients"] = 1; cfg["AdvertiseServer"] = False
cfg.setdefault("WorldConfig", {})["Seed"] = SEED
cfg["WorldConfig"]["SaveFileLocation"] = os.path.join(data, "Saves", "bench.vcdbs")
cfg["WorldConfig"]["WorldName"] = "bench"
json.dump(cfg, open(cfgp, "w", encoding="utf-8"), indent=1)

env = dict(os.environ, FARSEER_BENCH_OUT=out, FARSEER_BENCH_PASSES=a.passes, FARSEER_BENCH_MODE=a.mode,
           FARSEER_BENCH_SERIALREF="1" if a.serialref else "0")
for kv in a.env:
    k, v = kv.split("=", 1); env[k] = v
t0 = time.time()
with open(os.path.join(out, "server-stdout.log"), "w", encoding="utf-8", errors="replace") as so:
    p = subprocess.Popen([server, "--dataPath", data, "--port", "42499"], cwd=GAME, env=env, stdout=so, stderr=subprocess.STDOUT, stdin=subprocess.PIPE)
    try: p.wait(timeout=3600)
    except subprocess.TimeoutExpired: p.kill(); print("SERVER TIMEOUT (killed)")
print(f"run={a.run} exit={p.returncode} elapsed_s={time.time()-t0:.0f}")

logs = os.path.join(data, "Logs")
def grep(fn, pat):
    f = os.path.join(logs, fn)
    if not os.path.exists(f): return []
    return [l.rstrip() for l in open(f, encoding="utf-8", errors="replace") if re.search(pat, l)]
rep = os.path.join(out, "report.txt")
print(open(rep, encoding="utf-8").read() if os.path.exists(rep) else "NO REPORT")
ov = grep("server-main.log", r"[Oo]verloaded")
print(f"server-main 'overloaded' lines: {len(ov)}")
errs = grep("server-main.log", r"\[(Error|Fatal)\]") + grep("server-main.log", r"Watersheds height sample failed|FarseerPerffix")
print(f"error/fatal/perffix lines: {len(errs)}")
for l in errs[:12]: print("  " + l[:220])
