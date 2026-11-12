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
    DecoderSettings.cs         sensor modes (CarTester.ini [Param]) + connection/protocol options
  Recording/RecordingIO.cs     CSV export, raw edge log, vehicle info
  Recording/CtrFormat.cs       .ctr — the only recording format (open/save), CarTester 1.0.0.3
  Recording/FrameRange.cs      view range + parser (frame numbers / time of day), Timeline (align by seconds)
  CompareWindow + ViewModels/CompareViewModel.cs   two recordings side by side (A solid, B dashed)
  ViewModels/, Controls/       MVVM (hand-written ObservableObject/RelayCommand), chart control
  Themes/Light.xaml            the only theme: palette, control styles (all colors live here)
tests/JzMonitor.Tests/         xUnit (synthetic signals, a synthetic CarTester-style .ctr)
docs/PROTOCOL.md               protocol, .ctr spec, verified points, what the original hid
docs/plans/<slug>/             plans written by /architect
tools/re/                      UPX unpacker, DFM parser, dumpbin annotation (see its README)
```

## Hard rules

1. **Protocol fidelity.** Every formula, constant, bit meaning or frame-layout rule must cite
   evidence: a disassembly address in `CarTester.exe` (re-check with `tools/re/da.sh`), a
   CarTester screenshot, or a pattern observed in the reference logs. Never "improve" a
   formula from memory or a forum post without marking it as unverified. Update
   `docs/PROTOCOL.md` in the same change.
2. **`.ctr` is the only recording format** (open/save) and stays byte-compatible with
   CarTester 1.0.0.3: cp1251, CRLF, 40 values + time per frame, the footer layout in
   `docs/PROTOCOL.md`. Load → save of an original file must be byte-identical (test
   `SaveCtr_CarTesterStyleFile_IsByteIdenticalAfterLoad`, plus a manual round trip of
   `../Chaser logs`). What `.ctr` cannot hold (bytes 13+, milliseconds) goes to CSV export;
   no second file format.
3. **Do not drop data the ECU sends.** The original discarded bytes after the 12th and hid
   some bits; JZ Monitor shows everything and marks what the original hid (`HiddenInOriginal`).
4. **No NuGet packages in `src/`** — COM access is P/Invoke, charts are hand-drawn. Tests use
   xUnit only (versions present in the local NuGet cache). A new package needs the user's OK.
5. **Threading.** The COM reader runs on a dedicated high-priority thread: no UI access, no
   blocking I/O, no heavy allocation there beyond decoding. Results reach the UI only via
   `Dispatcher.BeginInvoke`. `FrameAssembler` is not thread-safe — one producer, under lock.
6. **Win32 handles** are `SafeFileHandle`. The port is opened **overlapped**
   (`FILE_FLAG_OVERLAPPED`, as CarTester did): `WaitCommEvent` waits on an event together with
   a stop event, Stop sets the stop event, the reader cancels the pending wait (`CancelIoEx` +
   `GetOverlappedResult(wait)`) and only then frees its buffers. Never call into a synchronous
   comm handle from another thread to "wake" a blocked wait — Windows serializes it and the UI
   hangs (that was a real bug). Never leave a port open after Stop/close.
7. **Scope of the hardware.** The signal is read from **DSR only** — no CTS or other line
   options. The target engine is an inline six: V6 right-bank data (byte 10, bit 12.5) is not
   decoded or shown (the raw byte still appears in the frame hex dump).
8. **Never modify** `../CarTester.exe`, `../Viewer.dll`, `../CarTester.ini` or `../Chaser logs/`.
9. **Language.** UI text is **English** — short, plain labels (engine/automotive terms as on
   a scan tool). Code comments and `docs/` are Ukrainian. Identifiers are English.
   Keep comment density as in the surrounding code.
10. **Design.** Light theme only, defined in `Themes/Light.xaml` — use its brushes and styles
   (`Card`, `AccentButton`, `GhostButton`, `Caption`, `SectionTitle`, …), no hard-coded colors
   in views except the status tints already used in `MainWindow.xaml`.

## Conventions

- Nullable on; no `!` to silence real nulls. File-scoped namespaces, `sealed` by default.
- Pure logic (decoding, file format) lives in `Protocol/` and `Recording/` and is unit-tested;
  keep WPF types out of those folders.
- Parameter keys: `inj ign iac rpm air ect tps spd af9`, flags `f<byte>.<bit>`,
  extra bytes `b<n>`, fuel `lph l100km`, trip totals `trip_*` (cumulative, from `TripMeter`). Code filters by these prefixes — a new key must
  not start with `f` or `b` unless it is a flag or an extra byte.
- Settings persist to `JzMonitor.settings.json` next to the exe.

## Verify

Match verification to the change:

- Any code change: `dotnet build JzMonitor.slnx` (0 warnings expected) and `dotnet test JzMonitor.slnx`.
- Protocol/decoder change: tests in `tests/JzMonitor.Tests/Protocol` cover it, and the claim is
  re-checked against the disassembly or the reference logs.
- `.ctr` change: the byte-identical round-trip test stays green; ideally also round-trip every
  file in `../Chaser logs`.
- UI change: start the app (`dotnet run --project src/JzMonitor`) with a recording —
  `JzMonitor.exe "<file>.ctr"` opens it directly; check the Parameters / Frame / Setup tabs.
  There is no simulated signal: live reading can only be checked on the car.
- Real hardware (COM port + car) cannot be tested by an agent — say so instead of claiming it works.
- Docs-only change: run nothing.
