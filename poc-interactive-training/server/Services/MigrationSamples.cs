namespace PocInteractiveTraining.Server.Services;

// Fixture for the COBOL migration A/B test. The program reads a fixed-layout record from stdin so the
// oracle can execute it over a case set; every case exercises a construct where COBOL's meaning is not
// recoverable from the source text alone.
public static class MigrationSamples
{
    public const string EntryType = "LateReturnBilling";
    public const string EntryMethod = "Calculate";

    public const string CobolSource = """
           IDENTIFICATION DIVISION.
           PROGRAM-ID. HARDPAY.
           DATA DIVISION.
           WORKING-STORAGE SECTION.
           01  WS-RAW-RECORD           PIC X(20).
           01  WS-EMPLOYEE REDEFINES WS-RAW-RECORD.
               05  WS-EMP-ID           PIC X(5).
               05  WS-GRADE            PIC 9.
                   88  WS-SENIOR       VALUE 7 THRU 9.
               05  WS-HOURS            PIC 9(3)V9.
               05  WS-RATE             PIC 9(4)V99.
               05  FILLER              PIC X(4).
           01  WS-CALC.
               05  WS-GROSS            PIC S9(6)V99 COMP-3 VALUE 0.
               05  WS-BONUS            PIC S9(5)V99 COMP-3 VALUE 0.
               05  WS-NET              PIC S9(6)V99 VALUE 0.

           PROCEDURE DIVISION.
           0001-MAIN.
               ACCEPT WS-RAW-RECORD.
               PERFORM 0100-GROSS THRU 0300-NET.
               DISPLAY WS-NET.
               STOP RUN.

           0100-GROSS.
               COMPUTE WS-GROSS ROUNDED = WS-HOURS * WS-RATE.

           0200-BONUS.
               IF WS-SENIOR
                   COMPUTE WS-BONUS ROUNDED = WS-GROSS * 0.075
               ELSE
                   MOVE ZERO TO WS-BONUS
               END-IF.

           0300-NET.
               COMPUTE WS-NET = WS-GROSS + WS-BONUS.
        """;

    // 20-byte fixed layout: id X(5) | grade 9 | hours 9(3)V9 | rate 9(4)V99 | filler X(4).
    // The V is implied - there is no decimal point in the data, which is the first thing a naive
    // translation gets wrong. Expected values are produced by the compiled COBOL, not by hand.
    public static readonly string[] Cases =
    [
        "E101720450002000    ",  // 45.0h @ 20.00, grade 2 -> 900.00, no bonus
        "E202880400002500    ",  // 40.0h @ 25.00, grade 8 -> 1075.00 with bonus
        "E303190375001999    ",  // 37.5h @ 19.99 -> 805.85, exercises ROUNDED at both COMPUTEs
        "E600760400002000    ",  // grade 6 -> 800.00, just below the 88-level boundary
        "E700770400002000    ",  // grade 7 -> 860.00, first grade inside VALUE 7 THRU 9
        "E800790007000333    ",  // 0.7h @ 3.33 -> 2.50, rounding at small scale
        "E500100000000000    "   // all zeros -> 0.00
    ];

    // The AST / DDG / PDG artifacts an analysis pass would produce. Arm B receives these.
    public const string StructuralArtifacts = """
        --- AST EXTRACT ---
        {
          "Program": "HARDPAY",
          "WorkingStorage": [
            { "Name": "WS-RAW-RECORD", "Pic": "X(20)" },
            { "Name": "WS-EMPLOYEE", "Redefines": "WS-RAW-RECORD", "Children": [
              { "Name": "WS-EMP-ID",       "Pic": "X(5)" },
              { "Name": "WS-GRADE",        "Pic": "9", "Condition88": { "WS-SENIOR": "7 THRU 9" } },
              { "Name": "WS-HOURS",        "Pic": "9(3)V9" },
              { "Name": "WS-RATE",         "Pic": "9(4)V99" },
              { "Name": "FILLER",          "Pic": "X(4)" }
            ]}
          ],
          "Paragraphs": ["0001-MAIN", "0100-GROSS", "0200-BONUS", "0300-NET"]
        }

        --- DATA DEPENDENCIES (DDG) ---
        WS-GROSS depends on WS-HOURS, WS-RATE             via 0100-GROSS
        WS-BONUS depends on WS-GROSS, WS-GRADE            via 0200-BONUS
        WS-NET   depends on WS-GROSS, WS-BONUS            via 0300-NET

        --- CONTROL DEPENDENCIES (PDG) ---
        0100-GROSS, 0200-BONUS, 0300-NET execute as one PERFORM range from 0001-MAIN
        0200-BONUS bonus branch is conditional on WS-SENIOR (WS-GRADE in 7..9)
        """;

    // What every arm is told about the interface it must implement.
    public const string Contract = """
        Return ONE C# file, no prose and no Markdown fences, with this exact shape:

        public static class LateReturnBilling
        {
            public static decimal Calculate(string record) { ... }
        }

        'record' is the 20-byte input record as a string, one char per byte.
        Return the computed net value as a decimal, rounded to 2 decimal places.
        Use only pure computation - no I/O, no networking, no reflection.
        """;
}
