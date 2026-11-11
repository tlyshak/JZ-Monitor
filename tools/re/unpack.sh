#!/bin/bash
# Розпаковує CarTester.exe (UPX 1.25, NRV2D, фільтр 0x26), відновлює імпорт і готує файли для da.sh.
# Використання: tools/re/unpack.sh <шлях до CarTester.exe>   (результат — у $RE_WORK, за замовч. ./re-work)
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="${RE_WORK:-./re-work}"
mkdir -p "$WORK"
EXE="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
cd "$WORK"
python "$HERE/unupx.py" "$EXE" exe.img
cp "$HERE/mkpe.py" .   # fix.py імпортує mkpe з поточної теки
python "$HERE/fix.py" exe.img 5612 10b000 0 exe_un.exe
# fix.py пише адреси IAT зі зсувом −0x1000; виправлення:
awk '{printf "%08X %s\n", strtonum("0x"$1)+0x1000, $2}' exe_un.exe.imports > exe.imp2
PYTHONIOENCODING=utf-8 python "$HERE/dfm.py" exe.img > forms.txt
echo "Готово: $WORK (exe_un.exe, exe.imp2, forms.txt)"
