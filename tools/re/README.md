# Інструменти reverse engineering CarTester

Потрібні Python 3 і `dumpbin.exe` з Visual Studio (C++ tools). Сторонні дизасемблери не потрібні.

```bash
export RE_WORK=/tmp/re-work                 # куди складати розпаковані файли
tools/re/unpack.sh ../CarTester.exe         # розпакувати UPX, відновити імпорт, витягти форми
tools/re/da.sh 4A785C 4A9600                # дизасемблювати діапазон з підписами
```

| Файл | Що робить |
|---|---|
| `unupx.py` | Декомпресор UPX 1.25 (NRV2D), власна реалізація |
| `fix.py` | Скасовує фільтр CALL/JMP 0x26, відновлює таблицю імпорту, будує PE для `dumpbin` |
| `mkpe.py` | Обгортає сирий образ у мінімальний PE з однією секцією коду |
| `dfm.py` | Розбирає бінарні DFM-форми Delphi (`TPF0`) у текст |
| `annot.py` | Підписує виклики API та published-методи Delphi |
| `annot2.py` | Підписує рядки Delphi та float-константи (`fld`/`fmul` …) |
| `unpack.sh`, `da.sh` | Обгортки над усім цим |

Ключові адреси — у `docs/PROTOCOL.md`. `dumpbin` дизасемблює лінійно і може збитися на даних
усередині коду: тоді дизасемблюйте з точної адреси функції через `da.sh`.
