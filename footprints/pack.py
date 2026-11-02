import zipfile, hashlib, os
base = os.path.dirname(os.path.abspath(__file__))
src = os.path.join(os.environ["APPDATA"], "VintagestoryData", "Mods", "Footprints-v1.2.14.zip")
out = os.path.join(base, 'Footprints-v1.2.14-perffix.zip')
zi = zipfile.ZipFile(src)
n = 0
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as zo:
    for i in zi.infolist():
        if i.filename == 'Footprints.pdb':
            continue  # stale after IL patch
        d = open(os.path.join(base, 'out', 'Footprints.dll'), 'rb').read() if i.filename == 'Footprints.dll' else zi.read(i.filename)
        zo.writestr(i.filename, d)
        n += 1
z = zipfile.ZipFile(out)
h = hashlib.sha256(open(out, 'rb').read()).hexdigest()
open(out + '.sha256', 'w').write(h + '  Footprints-v1.2.14-perffix.zip\n')
print('files', n, 'testzip', z.testzip() or 'OK', h)
