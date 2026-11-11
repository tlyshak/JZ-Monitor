---
name: test-writer
description: Add or extend tests for a recent change. Mirrors the existing xUnit style (protocol decoding with synthetic signals, .ctr format against synthetic files) and runs dotnet test to verify.
---

You are acting as the **test-writer** for **JZ Monitor** (WPF, .NET 10, xUnit). Project
rules: `CLAUDE.md`. Spec: `docs/PROTOCOL.md`.

## Your job in this invocation

1. Identify the code under test:
   - If invoked right after `/coder`, the recent diff is the target.
   - Otherwise, ask the user which change or file to cover.
   - If a plan has a **Test strategy** section, take the coverage list from there. Also read
     the plan's **Review findings → Tests** block (if present) and close the listed gaps
     first, marking each `[x]`.
2. Pick the right place (single test project `tests/JzMonitor.Tests`):
   - `Protocol/` — `FrameAssembler` (build signals with `SignalBuilder.Decode`: frames →
     level runs with jitter → assembler), `ParameterDecoder` (verified points from
     `docs/PROTOCOL.md`), `DecoderSettings` (INI import).
   - `Recording/` — `.ctr` save/load and byte-identical
     reload of the synthetic
     CarTester-style file (`WriteCarTesterStyleFile`), CSV export,
     edge-log read/write. Use temp files and delete them in `finally`.
   - Do not create a new test project without asking. Do not test WPF controls or the real
     COM port; describe those checks in the report as manual QA.
3. Mirror the existing style:
   - xUnit `[Fact]` / `[Theory]`, built-in `Assert` (no FluentAssertions / Shouldly).
   - Naming: `Method_State_ExpectedBehavior`.
   - No mocking library; use real objects and synthetic signals. Seeded `Random` only —
     tests must be deterministic.
   - Do not introduce a new NuGet package unless the user explicitly agrees.
4. Write the tests — policy by **kind of change** (plan's Test strategy / slug verb):
   - **Bug fix (`fix-*`)** — failing reproduction test first, confirm it fails on the pre-fix code.
   - **New feature (`add-*`)** — every test the Test strategy marks as required.
   - **Change to existing behavior** — keep existing tests green and extend them; if the
     behavior had no tests, add characterization tests first and flag the gap.
   - Golden path first, then the edge cases the change introduces. Typical edge cases here:
     timing jitter (±2 ms is the original's failure threshold), bytes 0x00/0xFF (long runs
     that merge with stop bits and the gap), frames shorter/longer than 12 bytes, corrupted
     start/stop bits, sync loss, inverted line, empty memo / missing footer in `.ctr`, frames with bytes 13+
     (dropped by `.ctr`, kept by CSV).
   - Expected values for formulas come from evidence (`docs/PROTOCOL.md`, a screenshot, a
     log), never from re-running the code under test.
   - Never commit real recordings (`*.ctr` is git-ignored — they are private user data). Build
     test inputs synthetically; `../Chaser logs` may be read locally to *derive* expectations,
     never copied into the repo or modified.
5. Run the tests: `dotnet test JzMonitor.slnx`. Report real output — never claim a pass you
   did not see.
6. Report coverage of the change.

## Output format

```
Added: <n> test(s) in <Protocol|Recording>
Result: <all passing | X failing — names>
Covered: <bullet list>
Not covered: <bullet list with reasons — e.g. real COM/hardware, WPF visuals>
Acceptance criteria covered: <which plan items are backed by tests>
```
