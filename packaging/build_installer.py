"""Stage a private CPython runtime and compile the Windows installer."""

import argparse
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys


ROOT = Path(__file__).resolve().parent.parent


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--iscc", type=Path, default=Path(
        r"C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    ))
    arguments = parser.parse_args()
    if not re.fullmatch(r"0\.1\.(0|[1-9][0-9]*)", arguments.version):
        parser.error("Version must be 0.1.N, with a nonnegative patch number.")
    if os.name != "nt" or struct.calcsize("P") != 8 or sys.version_info[:2] != (3, 14):
        parser.error("Build with 64-bit CPython 3.14 on Windows.")
    if not arguments.iscc.is_file():
        parser.error(f"Inno Setup 6 compiler not found: {arguments.iscc}")
    source = Path(sys.base_prefix)
    output = ROOT / "dist"
    stage = output / "stage"
    if stage.exists():
        shutil.rmtree(stage)
    runtime = stage / "runtime"
    runtime.mkdir(parents=True)
    ignored = shutil.ignore_patterns(
        "__pycache__", "*.pyc", "site-packages", "test", "tests",
        "idlelib", "ensurepip", "turtledemo",
    )
    for name in ("Lib", "DLLs", "tcl"):
        shutil.copytree(source / name, runtime / name, ignore=ignored)
    for name in ("python.exe", "pythonw.exe", "LICENSE.txt"):
        shutil.copy2(source / name, runtime / name)
    for library in source.glob("*.dll"):
        shutil.copy2(library, runtime / library.name)
    # The DLL-named path file isolates imports from system/user Python and PYTHONPATH.
    (runtime / "python314._pth").write_text(
        ".\nLib\nDLLs\n..\\app\n", encoding="ascii"
    )
    app = stage / "app"
    app.mkdir()
    for name in ("api_probe.py", "obk_core.py", "obk_app.py"):
        shutil.copy2(ROOT / "tools" / name, app / name)
    shutil.copy2(ROOT / "README.md", stage / "README.md")
    subprocess.run(
        [str(runtime / "python.exe"), "-I", str(app / "obk_app.py"), "--smoke-test"],
        check=True,
    )
    subprocess.run(
        [str(arguments.iscc), "/Q", f"/DAppVersion={arguments.version}",
         str(ROOT / "packaging" / "installer.iss")],
        check=True,
    )
    installer = output / f"OBK-{arguments.version}-windows-x64-setup.exe"
    if not installer.is_file():
        raise FileNotFoundError(f"Compiler did not produce {installer}")
    print(installer)


if __name__ == "__main__":
    main()
