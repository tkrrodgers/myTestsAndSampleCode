using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// One IBM Enterprise COBOL design question, asked identically of several models. Everything a compiler
// can check is checked by GnuCOBOL; everything an SME would look for is a sealed keyword check; only
// design judgement is left to a blinded judge model.
public static class LlmShootoutSamples
{
    public static readonly string[] Contestants = ["Claude Opus 5", "Claude Fable 5.1", "GPT-6 Astra", "Gemini 3.8 Flash"];

    // Elementary items of DCLEASTINVNTRY in declaration order with their byte lengths. Offsets are
    // arithmetic over this list; the total is cross-checked against LENGTH OF from the compiler.
    public static readonly (string Name, int Length)[] Layout =
    [
        ("VIN-LEN", 2), ("VIN-TEXT", 4), ("AUTOYEAR", 4),
        ("MAKE-LEN", 2), ("MAKE-TEXT", 20), ("MODEL-LEN", 2), ("MODEL-TEXT", 20),
        ("AUTOTRIM-LEN", 2), ("AUTOTRIM-TEXT", 32), ("BODY-LEN", 2), ("BODY-TEXT", 35),
        ("PRICE", 4),
        ("COLOR-LEN", 2), ("COLOR-TEXT", 32), ("TRANS-LEN", 2), ("TRANS-TEXT", 10),
        ("CYLIND-LEN", 2), ("CYLIND-TEXT", 5), ("MILES-LEN", 2), ("MILES-TEXT", 10),
        ("DEALERID", 4)
    ];

    public static readonly string[] HexKeys = ["PRICE_123456", "PRICE_NEG1500", "AUTOYEAR_2024", "VINLEN_4"];

    // Sealed SME checks. Keyword detection over the design prose: a floor, not a grade. Every group
    // must have at least one hit for the check to pass.
    public static readonly (string Name, string Why, string[][] Groups)[] Insights =
    [
        ("FETCH INTO :VIN-TEXT bypasses the -LEN fields", "The program fetches into the 49-level text items, so every -LEN holds stale storage. A mapper that trusts -LEN reads garbage.",
            [["-len"], ["not set", "not populated", "never set", "stale", "garbage", "bypass", "not refreshed", "ignored", "not consulted", "never referenced", "not updated", "whatever was"]]),
        ("Only AUTOTRIM and MILES are nullable", "Everything else is NOT NULL in the DDL; the fetched columns need no indicators.",
            [["autotrim"], ["miles"], ["nullable", "null"]]),
        ("Preserve the legacy truncation for cut-over, fix afterwards", "Mixing a fix into a migration makes the regression diff unusable.",
            [["preserve", "bit-for-bit", "byte-for-byte", "compat", "reproduce the legacy", "replicate"]]),
        ("Names the sign loss", "PIC 9(5) is unsigned; -1500 becomes 01500 with no error.",
            [["sign"], ["drop", "lost", "discard", "strip", "absolute"]]),
        ("Names the left-justified, space-padded PRICEO", "A numeric DISPLAY item moved to PIC X(10) is an alphanumeric move, not an edit.",
            [["left-justif", "left justif", "left-align", "trailing space", "space-pad", "padded with spaces", "space-fill", "space fill"]]),
        ("Names a specific CCSID and where conversion belongs", "IBM037 and IBM1047 differ at several code points; the boundary must be one place.",
            [["037", "1047", "ccsid"]]),
        ("Spots the loop quirks", "Counters still advance on the SQLCODE 100 pass and unused OUTPUS slots are not cleared — a byte-for-byte test trips on both.",
            [["not cleared", "still increment", "increments even", "increment on", "after end-of-data", "sqlcode 100 iteration", "counters"]]),
        ("Knows MOVE has no ON SIZE ERROR", "Truncation on MOVE is silent by the standard; only arithmetic statements can raise a size error.",
            [["size error"]]),
        ("Proposes an executable oracle, not a reading", "Equivalence is proven by compiling and running the COBOL and diffing bytes.",
            [["cobc", "gnucobol", "compile"], ["oracle", "golden", "diff", "byte"]])
    ];

    public const string Brief = """
        IBM's Global Auto Mart sample. A CICS/DB2 program GAM0VSI reads the EASTINVNTRY table through a
        DCLGEN copybook (GAM0BET) and formats up to 10 rows into a fixed-layout output area later shown on a
        BMS screen. Teams converting this to Java routinely get the copybook byte layout and the numeric field
        semantics wrong. Design the conversion.

        ### DB2 table (from the JCL that creates it)
        CREATE TABLE &SCHEMA.EASTINVNTRY(VIN VARCHAR(4) NOT NULL, AUTOYEAR INTEGER NOT NULL,
          MAKE VARCHAR(20) NOT NULL, MODEL VARCHAR(20) NOT NULL, AUTOTRIM VARCHAR(32),
          BODY VARCHAR(35) NOT NULL, PRICE DECIMAL(6,0) NOT NULL, COLOR VARCHAR(32) NOT NULL,
          TRANS VARCHAR(10) NOT NULL, CYLIND VARCHAR(5) NOT NULL, MILES VARCHAR(10),
          DEALERID INTEGER NOT NULL, NEWAUTO VARCHAR(1) NOT NULL, DATEADDED DATE NOT NULL,
          CONSTRAINT C9331477 PRIMARY KEY(VIN));

        ### DCLGEN copybook GAM0BET (COBOL host structure, INDVAR(YES) was specified)
               01  DCLEASTINVNTRY.
                   10 VIN.
                      49 VIN-LEN           PIC S9(4) USAGE COMP.
                      49 VIN-TEXT          PIC X(4).
                   10 AUTOYEAR             PIC S9(9) USAGE COMP.
                   10 MAKE.
                      49 MAKE-LEN          PIC S9(4) USAGE COMP.
                      49 MAKE-TEXT         PIC X(20).
                   10 MODEL.
                      49 MODEL-LEN         PIC S9(4) USAGE COMP.
                      49 MODEL-TEXT        PIC X(20).
                   10 AUTOTRIM.
                      49 AUTOTRIM-LEN      PIC S9(4) USAGE COMP.
                      49 AUTOTRIM-TEXT     PIC X(32).
                   10 BODY.
                      49 BODY-LEN          PIC S9(4) USAGE COMP.
                      49 BODY-TEXT         PIC X(35).
                   10 PRICE                PIC S9(6)V USAGE COMP-3.
                   10 COLOR.
                      49 COLOR-LEN         PIC S9(4) USAGE COMP.
                      49 COLOR-TEXT        PIC X(32).
                   10 TRANS.
                      49 TRANS-LEN         PIC S9(4) USAGE COMP.
                      49 TRANS-TEXT        PIC X(10).
                   10 CYLIND.
                      49 CYLIND-LEN        PIC S9(4) USAGE COMP.
                      49 CYLIND-TEXT       PIC X(5).
                   10 MILES.
                      49 MILES-LEN         PIC S9(4) USAGE COMP.
                      49 MILES-TEXT        PIC X(10).
                   10 DEALERID             PIC S9(9) USAGE COMP.
        (NEWAUTO and DATEADDED host variables follow in the real copybook; treat NEWAUTO-TEXT as PIC X(1).)

        ### GAM0VSI working storage and the row-formatting paragraph (verbatim)
               01  CONVERT-YEAR        PIC 9(4) USAGE DISPLAY.
               01  CONVERT-PRICE       PIC 9(5) USAGE DISPLAY.
                   02 OUTPUS OCCURS 10 TIMES.
                       05  VINO        PIC X(4).
                       05  YEARO       PIC X(4).
                       05  MODELO      PIC X(20).
                       05  PRICEO      PIC X(10).
                       05  NEWAUTOO    PIC X.

               1400-GET-INVENTORY-ROW.
                   PERFORM UNTIL POS-INDEX > 10 OR SQLCODE = 100
                       EXEC SQL FETCH ICURSOR INTO :VIN-TEXT, :AUTOYEAR, :MODEL-TEXT, :PRICE, :NEWAUTO-TEXT END-EXEC
                       IF SQLCODE NOT = 100
                           MOVE AUTOYEAR TO CONVERT-YEAR
                           MOVE PRICE TO CONVERT-PRICE
                           MOVE VIN-TEXT TO VINO (POS-INDEX)
                           MOVE CONVERT-YEAR TO YEARO (POS-INDEX)
                           MOVE MODEL-TEXT TO MODELO (POS-INDEX)
                           MOVE CONVERT-PRICE TO PRICEO (POS-INDEX)
                           MOVE NEWAUTO-TEXT TO NEWAUTOO (POS-INDEX)
                       END-IF
                       COMPUTE CURSOR-POSITION = CURSOR-POSITION + 1
                       COMPUTE POS-INDEX = POS-INDEX + 1
                   END-PERFORM.
        Compiled as IBM Enterprise COBOL for z/OS with default options (TRUNC(STD), NUMPROC(NOPFD), ARITH(COMPAT)).

        ### The five fetched rows to predict
        CASE1: VIN-TEXT "A1B2", AUTOYEAR 2024, MODEL-TEXT "Civic", PRICE 23499, NEWAUTO-TEXT "Y"
        CASE2: VIN-TEXT "ZZ99", AUTOYEAR 2019, MODEL-TEXT "F-150 Lightning Ext.", PRICE 123456, NEWAUTO-TEXT "N"
        CASE3: VIN-TEXT "K7 " (VIN-LEN 3), AUTOYEAR 12024, MODEL-TEXT "Fit", PRICE 5, NEWAUTO-TEXT "Y"
        CASE4: VIN-TEXT "NEG1", AUTOYEAR 2020, MODEL-TEXT "Golf", PRICE -1500, NEWAUTO-TEXT "N"
        CASE5: VIN-TEXT "ZERO", AUTOYEAR 2024, MODEL-TEXT "Leaf", PRICE 0, NEWAUTO-TEXT "Y"
        """;

    public const string AnswerContract = """
        Return JSON only, no Markdown fences, exactly this shape:
        {
          "recordLength": <int, bytes in DCLEASTINVNTRY as declared, without NEWAUTO/DATEADDED>,
          "outpusLength": <int, bytes in one OUTPUS occurrence>,
          "offsets": {"VIN-LEN":0, "VIN-TEXT":2, ... one entry per elementary item of DCLEASTINVNTRY, byte offset from the start of the 01},
          "cases": ["CASE1|VINO|YEARO|MODELO|PRICEO|NEWAUTOO|", ... five strings, every field at its full declared width with space padding, exactly as the bytes would appear],
          "hex": {"PRICE_123456":"...", "PRICE_NEG1500":"...", "AUTOYEAR_2024":"...", "VINLEN_4":"..."},
          "design": {
            "numericBehaviour": "what the MOVE chain does to AUTOYEAR and PRICE, and whether to preserve or fix it for cut-over",
            "priceType": "BigDecimal vs long/int for PRICE, with reasons",
            "varchar": "how -LEN/-TEXT pairs map to Java and what to do with trailing spaces",
            "encoding": "EBCDIC vs ASCII and where the conversion belongs",
            "nulls": "how INDVAR(YES) indicators map to Java and which columns are nullable",
            "equivalence": "how to prove behavioural equivalence before cut-over — be concrete about the oracle"
          },
          "observations": ["anything else about the program a migration team must know — quirks, defects, risks"],
          "javaConfidence": "high|medium|low",
          "java": "complete Java source for a record mapper (byte[] -> POJO) and the row formatter reproducing the MOVE chain, plus a JUnit test for the five cases — ONLY if you are confident it is correct; otherwise an empty string",
          "notWritten": "what you deliberately did not write and why"
        }
        Hex values are upper-case with no spaces. Be exact: the compiler will mark approximate answers wrong.
        """;
}
