# JZ Monitor

WPF replacement for **CarTester** — a Delphi program that reads the live data stream of
Toyota OBD-1 ECUs (JZ family) through a COM port. The protocol, formulas and the `.ctr`
file format were reverse engineered from `../CarTester.exe`; the findings are the
project's spec: **`docs/PROTOCOL.md`**.

Reference vehicle: Toyota Chaser 1997, 2JZ-GTE non-VVTi, A340H. Real recordings from it
live in `../Chaser logs/**/*.ctr` (outside the repo, private user data — read, never
modify, **never commit**; `*.ctr` is git-ignored).

## Layout

```
JzMonitor.slnx
src/JzMonitor/                 WPF app (.NET 10, net10.0-windows), no NuGet packages
  Protocol/                    signal → frames → values (no WPF types here)
    ComLineReader.cs           Win32 P/Invoke: WaitCommEvent(EV_DSR) + GetCommModemStatus, Stopwatch timing
    FrameAssembler.cs          level runs → bits → frames; frame end = idle gap, any length
    ParameterDecoder.cs        byte → physical value; formulas from CarTester's MemoDraw
    DecoderSettings.cs         sensor modes (CarTester.ini [Param]) + line/protocol options
    SimulatedSource.cs         demo signal generator
  Recording/RecordingIO.cs     .ctr read/write (CarTester 1.0.0.3), CSV export, raw edge log
  ViewModels/, Controls/       MVVM (hand-written ObservableObject/RelayCommand), chart control
tests/JzMonitor.Tests/         xUnit (synthetic signals and a synthetic CarTester-style .ctr)
docs/PROTOCOL.md               protocol + .ctr spec, verified points, what the original hid
docs/plans/<slug>/             plans written by /architect
tools/re/                      UPX unpacker, DFM parser, dumpbin annotation (see its README)
```

## Hard rules

1. **Protocol fidelity.** Every formula, constant, bit meaning or frame-layout rule must cite
   evidence: a disassembly address in `CarTester.exe` (re-check with `tools/re/da.sh`), a
   CarTester screenshot, or a pattern observed in the reference logs. Never "improve" a
   formula from memory or a forum post without marking it as unverified. Update
   `docs/PROTOCOL.md` in the same change.
2. **`.ctr` stays the native format and stays byte-compatible** with CarTester 1.0.0.3:
   cp1251, CRLF, 40 values + time per frame, the footer layout in `docs/PROTOCOL.md`.
   Load → save of an original file must be byte-identical (test
   `SaveCtr_CarTesterStyleFile_IsByteIdenticalAfterLoad`, plus a manual round trip of
   `../Chaser logs`). Data that does not fit
   (bytes 13+, extra metadata) goes to CSV export or a separate file, never into `.ctr`.
3. **Do not drop data the ECU sends.** The original discarded bytes after the 12th and hid
   some bits; JZ Monitor shows everything and marks what the original hid (`HiddenInOriginal`).
4. **No NuGet packages in `src/`** — COM access is P/Invoke, charts are hand-drawn. Tests use
   xUnit only (versions present in the local NuGet cache). A new package needs the user's OK.
5. **Threading.** The COM reader runs on a dedicated high-priority thread: no UI access, no
   blocking I/O, no heavy allocation there beyond decoding. Results reach the UI only via
   `Dispatcher.BeginInvoke`. `FrameAssembler` is not thread-safe — one producer, under lock.
6. **Win32 handles** are `SafeFileHandle`; stopping the reader calls `SetCommMask(h, 0)` to
   release `WaitCommEvent` before disposing. Never leave a port open after Stop/close.
7. **Never modify** `../CarTester.exe`, `../Viewer.dll`, `../CarTester.ini` or `../Chaser logs/`.
8. **Language.** UI text, code comments and `docs/` are Ukrainian. Identifiers are English.
   Keep comment density as in the surrounding code.

## Conventions

- Nullable on; no `!` to silence real nulls. File-scoped namespaces, `sealed` by default.
- Pure logic (decoding, file format) lives in `Protocol/` and `Recording/` and is unit-tested;
  keep WPF types out of those folders.
- Parameter keys: `inj ign iac rpm air ect tps spd af9 af10`, flags `f<byte>.<bit>`,
  extra bytes `b<n>`, fuel `lph l100km`. Code filters by these prefixes — a new key must
  not start with `f` or `b` unless it is a flag or an extra byte.
- Settings persist to `JzMonitor.settings.json` next to the exe.

## Verify

Match verification to the change:

- Any code change: `dotnet build JzMonitor.slnx` (0 warnings expected) and `dotnet test JzMonitor.slnx`.
- Protocol/decoder change: tests in `tests/JzMonitor.Tests/Protocol` cover it, and the claim is
  re-checked against the disassembly or the reference logs.
- `.ctr` change: the byte-identical round-trip test stays green; ideally also round-trip every
  file in `../Chaser logs`.
- UI change: start the app (`dotnet run --project src/JzMonitor`) and use **Демо**.
- Real hardware (COM port + car) cannot be tested by an agent — say so instead of claiming it works.
- Docs-only change: run nothing.
