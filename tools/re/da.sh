#!/bin/bash
# Дизасемблювання діапазону адрес розпакованого CarTester з підписами API, обробників Delphi,
# рядків і float-констант.
# Використання: tools/re/da.sh <start_hex> <end_hex>   (напр. da.sh 4A785C 4A9600)
# Очікує в $RE_WORK (за замовчуванням ./re-work) файли, створені tools/re/unpack.sh.
DUMPBIN="${DUMPBIN:-$(ls "/c/Program Files/Microsoft Visual Studio/"*/*/VC/Tools/MSVC/*/bin/Hostx64/x86/dumpbin.exe 2>/dev/null | head -1)}"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="${RE_WORK:-./re-work}"
[ -x "$DUMPBIN" ] || { echo "dumpbin.exe не знайдено; задайте DUMPBIN" >&2; exit 1; }
cd "$WORK" || exit 1
"$DUMPBIN" -nologo -disasm -range:0x$1,0x$2 exe_un.exe > _r.asm
python "$HERE/annot.py" _r.asm exe_un.exe.img exe.imp2 _r2.asm >/dev/null
PYTHONIOENCODING=utf-8 python "$HERE/annot2.py" _r2.asm exe_un.exe.img | grep -E "^\s+[0-9A-F]{8}:|^;===" | cut -c1-12,32-
