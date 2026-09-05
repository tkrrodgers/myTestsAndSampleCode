# COBOL fixture — `PAYROLL`

Source for the COBOL → Java grounding demo. Only `PAYROLL.cbl` is tracked; everything else here is generated.

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
cobc -C -x PAYROLL.cbl    # emit the intermediate C roadmap (PAYROLL.c, .c.h, .c.l.h)
cobc -x PAYROLL.cbl       # build PAYROLL.exe
.\PAYROLL.exe             # run it -> FINAL PAY: 0950.00
```

`0950.00` is correct: 40 h × $20.00 + 5 h × $20.00 × 1.5 = $950.00.

## What the generated C actually looks like

Worth knowing before building a lesson on it. The output is **libcob runtime IR, not idiomatic C**:

```c
if (((int)cob_cmp_numdisp (b_17 + 5, 2, 40LL, 0) > 0))
  goto l_5;
cob_decimal_set_field (d_0, &f_20);
cob_decimal_mul (d_0, dc_1);
```

- Control flow is `goto` chains with labels (`l_2`, `l_5`), not structured blocks.
- Arithmetic is `cob_decimal_*` runtime calls, not native operators.
- Data is anonymous byte-offset buffers (`b_17 + 5`), not named structs.

It does preserve source traceability comments:
`/* Line: 20 : Paragraph 0002-OVERTIME-CALC : PAYROLL.cbl */`

The durable value of this artifact is as an **executable test oracle** for differential verification,
not as a legibility aid for the model. See
[trainingSampleOverview.md](../../../ai-across-ces/trainingSampleOverview.md).
