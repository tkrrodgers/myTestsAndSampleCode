namespace PocInteractiveTraining.Server.Services;

// Fixture for the COBOL migration A/B/C test.
//
// This is real IBM Enterprise COBOL, not a synthetic toy. The field definitions and the interest
// computation are taken verbatim from AWS CardDemo (Apache-2.0), a reference mainframe credit-card
// application: CBACT04C paragraph 1300-COMPUTE-INTEREST, with the record layouts from copybooks
// CVTRA01Y (TRAN-CAT-BAL) and CVTRA02Y (DIS-INT-RATE).
//
// A driver was added so the program reads one record from stdin and can be executed over a case set;
// the arithmetic and the PICTURE clauses are unchanged.
//
// Source: https://github.com/aws-samples/aws-mainframe-modernization-carddemo
public static class MigrationSamples
{
    public const string EntryType = "InterestCalculator";
    public const string EntryMethod = "Calculate";

    public const string CobolSource = """
        IDENTIFICATION DIVISION.
        PROGRAM-ID. CBINTCAL.
        DATA DIVISION.
        WORKING-STORAGE SECTION.
        01  WS-IN                    PIC X(18).
        01  WS-PARSE REDEFINES WS-IN.
            05  WS-SIGN              PIC X(01).
            05  WS-BAL-DIGITS        PIC 9(09)V99.
            05  WS-RATE-DIGITS       PIC 9(04)V99.
        01  TRAN-CAT-BAL             PIC S9(09)V99.
        01  DIS-INT-RATE             PIC S9(04)V99.
        01  WS-MONTHLY-INT           PIC S9(09)V99.
        PROCEDURE DIVISION.
            ACCEPT WS-IN
            MOVE WS-RATE-DIGITS TO DIS-INT-RATE
            IF WS-SIGN = "-"
                COMPUTE TRAN-CAT-BAL = 0 - WS-BAL-DIGITS
            ELSE
                MOVE WS-BAL-DIGITS TO TRAN-CAT-BAL
            END-IF
            COMPUTE WS-MONTHLY-INT = (TRAN-CAT-BAL * DIS-INT-RATE) / 1200
            DISPLAY WS-MONTHLY-INT
            STOP RUN.
        """;

    // 18-byte record: sign X(1) | balance 9(09)V99 | rate 9(04)V99. Both V points are implied.
    // Expected values come from the compiled COBOL, not from arithmetic done by hand.
    public static readonly string[] Cases =
    [
        "+00000100000001899",  // 1000.00 @ 18.99% -> 15.825 exact, truncates to 15.82
        "+00000250000002499",  // 2500.00 @ 24.99% -> 52.0625 exact, truncates to 52.06
        "+00000010000001200",  //  100.00 @ 12.00% -> exactly 1.00
        "+00000033333001500",  //  333.33 @ 15.00% -> 4.166625 exact, truncates to 4.16
        "-00000050000001899",  // -500.00 @ 18.99% -> -7.9125, truncates toward zero to -7.91
        "+00000000000001899"   //    0.00 @ 18.99% -> 0.00
    ];

    // The AST / DDG / PDG artifacts a static-analysis pass would produce. Arm B receives these.
    public const string StructuralArtifacts = """
        --- AST EXTRACT ---
        {
          "Program": "CBINTCAL",
          "WorkingStorage": [
            { "Name": "WS-IN", "Pic": "X(18)" },
            { "Name": "WS-PARSE", "Redefines": "WS-IN", "Children": [
              { "Name": "WS-SIGN",        "Pic": "X(01)" },
              { "Name": "WS-BAL-DIGITS",  "Pic": "9(09)V99" },
              { "Name": "WS-RATE-DIGITS", "Pic": "9(04)V99" }
            ]},
            { "Name": "TRAN-CAT-BAL",   "Pic": "S9(09)V99" },
            { "Name": "DIS-INT-RATE",   "Pic": "S9(04)V99" },
            { "Name": "WS-MONTHLY-INT", "Pic": "S9(09)V99" }
          ]
        }

        --- DATA DEPENDENCIES (DDG) ---
        TRAN-CAT-BAL   depends on WS-SIGN, WS-BAL-DIGITS
        DIS-INT-RATE   depends on WS-RATE-DIGITS
        WS-MONTHLY-INT depends on TRAN-CAT-BAL, DIS-INT-RATE

        --- CONTROL DEPENDENCIES (PDG) ---
        The sign of TRAN-CAT-BAL is conditional on WS-SIGN = "-"
        The interest COMPUTE is unconditional and executes once per record
        """;

    public const string Contract = """
        Return ONE C# file, no prose and no Markdown fences, with this exact shape:

        public static class InterestCalculator
        {
            public static decimal Calculate(string record) { ... }
        }

        'record' is the 18-byte input record as a string, one char per byte.
        Return the computed monthly interest as a decimal.
        Use only pure computation - no I/O, no networking, no reflection.
        """;
}
