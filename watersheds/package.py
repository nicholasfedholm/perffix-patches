# Builds a perffix zip from YOUR copy of the original Watersheds zip (arg 1) and build\Watersheds.dll (output of patcher). Prints a diff summary.
import hashlib, os, re, shutil, sys, zipfile
W = os.path.dirname(os.path.abspath(__file__))  # work dir; expects build\Watersheds.dll here
STOCK = sys.argv[1]  # path to YOUR copy of the original watersheds zip
ZIP = os.path.join(W, 'watersheds_7.0.6-perffix.zip')
MP = os.path.join(W, 'modpath', 'watersheds_706pf')
DESC = 'Revamps the world generation with features derived from a Watershed model.'
NOTE = DESC + ' LOCAL PERF BUILD of 7.0.6 (not official): partially-lit-chunk check skipped for disk-loaded columns.'

dll = open(os.path.join(W, 'build', 'Watersheds.dll'), 'rb').read()
a = zipfile.ZipFile(STOCK)
with zipfile.ZipFile(ZIP, 'w', zipfile.ZIP_DEFLATED) as z:
    for n in a.namelist():
        if n.endswith('/'):
            continue
        data = a.read(n)
        if n == 'Watersheds.dll':
            data = dll
        elif n == 'modinfo.json':
            t = data.decode('utf8')
            assert DESC in t
            data = t.replace(DESC, NOTE).encode('utf8')
        z.writestr(n, data)
        p = os.path.join(MP, n)
        os.makedirs(os.path.dirname(p), exist_ok=True)
        open(p, 'wb').write(data)
b = zipfile.ZipFile(ZIP)
na = [n for n in a.namelist() if not n.endswith('/')]
print('testzip', b.testzip(), 'files', len(b.namelist()))
print('missing', set(na) - set(b.namelist()), 'extra', set(b.namelist()) - set(na))
print('changed', [n for n in na if a.read(n) != b.read(n)])
print('zip sha256', hashlib.sha256(open(ZIP, 'rb').read()).hexdigest())
print('dll sha256', hashlib.sha256(dll).hexdigest())
P = os.path.join(W, 'patched')
os.makedirs(P, exist_ok=True)
for n in b.namelist():
    if n.endswith('.dll'):
        open(os.path.join(P, n), 'wb').write(b.read(n))
