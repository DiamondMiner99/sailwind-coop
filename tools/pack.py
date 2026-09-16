"""Build the co-op release zip.

Takes the previous release zip as the base (it carries BepInEx core, doorstop and the Steam
libraries, which do not change), swaps in the freshly built SailwindCoop.dll, adds the Sailwind
Player Model DLL that co-op now depends on, and writes a fresh INSTALL.txt.

Usage: python tools/pack.py <version> [<base zip>]
"""
import hashlib, os, sys, zipfile

if len(sys.argv) < 2:
    sys.exit("usage: pack.py <version> [<base zip>]")
ver = sys.argv[1]
here = os.path.dirname(os.path.abspath(__file__))
repo = os.path.dirname(here)
downloads = os.path.join(os.path.expanduser("~"), "Downloads")
base = sys.argv[2] if len(sys.argv) > 2 else os.path.join(downloads, "SailwindCoop-v0.3.2.zip")
out = os.path.join(downloads, "SailwindCoop-v%s.zip" % ver)

coop_dll = os.path.join(repo, "src", "SailwindCoop", "bin", "Release", "net472", "SailwindCoop.dll")
pm_dll = os.path.normpath(os.path.join(repo, "..", "sailwind-playermodel", "src", "SailwindPlayerModel",
                                       "bin", "Release", "net472", "SailwindPlayerModel.dll"))
install = os.path.join(here, "INSTALL.txt")
for p in (base, coop_dll, pm_dll, install):
    assert os.path.exists(p), p

REPLACED = {"BepInEx/plugins/SailwindCoop/SailwindCoop.dll", "INSTALL.txt",
            "BepInEx/plugins/SailwindPlayerModel/SailwindPlayerModel.dll"}

with zipfile.ZipFile(base) as src, zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as dst:
    for info in src.infolist():
        if info.filename in REPLACED:
            continue
        dst.writestr(info.filename, src.read(info.filename))
    dst.write(coop_dll, "BepInEx/plugins/SailwindCoop/SailwindCoop.dll")
    dst.write(pm_dll, "BepInEx/plugins/SailwindPlayerModel/SailwindPlayerModel.dll")
    dst.write(install, "INSTALL.txt")

BACKSLASH = chr(92)
with zipfile.ZipFile(out) as z:
    for i in z.infolist():
        assert BACKSLASH not in i.filename, "backslash in path: " + i.filename
        # bit 3 = data descriptor; older Windows Explorer refuses those
        assert not (i.flag_bits & 0x08), "data descriptor set on " + i.filename
    assert z.testzip() is None
    names = z.namelist()
    for n in REPLACED:
        assert n in names, "missing " + n
    print("%s: %d entries, %d bytes" % (out, len(names), os.path.getsize(out)))
    for n in sorted(REPLACED):
        print("  %-62s %8d  md5 %s" % (n, z.getinfo(n).file_size, hashlib.md5(z.read(n)).hexdigest()))
