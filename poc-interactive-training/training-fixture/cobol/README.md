# COBOL fixtures

Source for the COBOL → Java grounding demo. Only the `.cbl` files are tracked; everything else here is generated.

| Fixture | Purpose |
| --- | --- |
| `PAYROLL.cbl` | Minimal happy-path program. Good for smoke-testing the toolchain, useless as a lesson — it has no ambiguity for grounding to resolve. |
| `HARDPAY.cbl` | The real fixture. Contains `REDEFINES`, `COMP-3` packed decimal, `PERFORM THRU` fall-through, a level-88 condition, `ROUNDED` and `ON SIZE ERROR`. Correct answer is `NET: +010696.81`, which cannot be derived by reading the source. |

## Toolchain

Prebuilt GnuCOBOL 3.2rc1 (MinGW x64) from
[mridoni/gnucobol-binaries](https://github.com/mridoni/gnucobol-binaries/releases), extracted to
`C:\Users\tkrro\tools\gnucobol-3.2rc1\gnucobol-3.2rc1-windows-mingw-x64`.

The config paths are **not** where the package layout suggests — `cobc` fails with
`config/default.conf: No such file or directory` unless these are set:

```powershell
$gc = "C:\Users\tkrro\tools\gnucobol-3.2rc1\gnucobol-3.2rc1-windows-mingw-x64"
$env:PATH = "$gc\bin;$env:PATH"
$env:COB_CONFIG_DIR = "$gc\share\gnucobol\config"
$env:COB_COPY_DIR   = "$gc\share\gnucobol\copy"
```

## Commands

```powershell
cobc -C -x HARDPAY.cbl    # emit the intermediate C (HARDPAY.c, .c.h, .c.l.h)
cobc -x HARDPAY.cbl       # build HARDPAY.exe
.\HARDPAY.exe             # run it -> NET: +010696.81  OVERFLOW: N
```

`PAYROLL.exe` prints `FINAL PAY: 0950.00` — correct: 40 h × $20.00 + 5 h × $20.00 × 1.5 = $950.00.

## What the generated C is good for

The output is **libcob runtime IR, not idiomatic C** — `goto` chains, `cob_decimal_*` calls, anonymous
byte buffers. Do not present it as "more readable C." Its value is that it **resolves semantics the
COBOL source leaves ambiguous**, which is what stalls a migration.

From `HARDPAY.c.l.h` — a complete `REDEFINES` overlay resolution:

```c
static cob_u8_t b_17[21];                     /* WS-RAW-RECORD */
static cob_field f_22 = {3, b_17 + 6, &a_5};  /* WS-HOURS-PACKED */
static cob_field f_23 = {4, b_17 + 9, &a_6};  /* WS-RATE */

a_5 = {0x12, 4, 1, 0x0001}   /* packed decimal, 4 digits, scale 1, signed */
a_6 = {0x12, 6, 2, 0x0001}   /* packed decimal, 6 digits, scale 2, signed */
```

Name, exact byte offset, length, type, digits, scale, sign. And control flow:

```c
/* PERFORM 0100-GROSS THRU 0300-NET */
frame_ptr->perform_through = 7;
/* Line: 27 : Paragraph 0100-GROSS : HARDPAY.cbl */
/* Implicit PERFORM return */
```

The `THRU` range is resolved and every construct carries a line-and-paragraph back-reference.

**Extract these facts into the prompt; do not paste the whole file.** Most of it is `cob_decimal`
plumbing that wastes context. See
[trainingSampleOverview.md](../../../ai-across-ces/trainingSampleOverview.md) tab 16 and
[sampleApproaches.md §2.2.1](../../../ai-across-ces/sampleApproaches.md).
