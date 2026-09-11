      *> Ground-truth harness for the GAM0VSI row-formatting logic.
      *> Same data division as GAM0BET (DCLEASTINVNTRY) and GAM0VSI working storage,
      *> same MOVE chain as paragraph 1400-GET-INVENTORY-ROW. No SQL, no CICS, so it
      *> compiles under GnuCOBOL and its output is the oracle the model answers are judged by.
       IDENTIFICATION DIVISION.
       PROGRAM-ID. GAMORACLE.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  CONVERT-YEAR        PIC 9(4) USAGE DISPLAY.
       01  CONVERT-PRICE       PIC 9(5) USAGE DISPLAY.

       01  DCLEASTINVNTRY.
           10 VIN.
              49 VIN-LEN           PIC S9(4) USAGE COMP.
              49 VIN-TEXT          PIC X(4).
           10 VIN-RAW REDEFINES VIN PIC X(6).
           10 AUTOYEAR             PIC S9(9) USAGE COMP.
           10 AUTOYEAR-RAW REDEFINES AUTOYEAR PIC X(4).
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
           10 PRICE-RAW REDEFINES PRICE PIC X(4).
      *> COLOR is a reserved word in GnuCOBOL; the layout is unchanged, only the name.
           10 COLOR-V.
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

       01  OUTPUS.
           05  VINO        PIC X(4).
           05  YEARO       PIC X(4).
           05  MODELO      PIC X(20).
           05  PRICEO      PIC X(10).
           05  NEWAUTOO    PIC X.
       01  NEWAUTO-TEXT    PIC X.

       PROCEDURE DIVISION.
       MAIN.
           DISPLAY "LENGTH DCLEASTINVNTRY=" LENGTH OF DCLEASTINVNTRY
           DISPLAY "LENGTH VIN=" LENGTH OF VIN
               " AUTOYEAR=" LENGTH OF AUTOYEAR
               " MAKE=" LENGTH OF MAKE
               " PRICE=" LENGTH OF PRICE
               " DEALERID=" LENGTH OF DEALERID
               " OUTPUS=" LENGTH OF OUTPUS

           PERFORM RUN-CASES.
           STOP RUN.

       RUN-CASES.
      *> Case 1: ordinary row
           MOVE 4 TO VIN-LEN  MOVE "A1B2" TO VIN-TEXT
           MOVE 2024 TO AUTOYEAR
           MOVE 5 TO MODEL-LEN  MOVE "Civic" TO MODEL-TEXT
           MOVE 23499 TO PRICE
           MOVE "Y" TO NEWAUTO-TEXT
           PERFORM BUILD-ROW
           DISPLAY "CASE1|" VINO "|" YEARO "|" MODELO "|" PRICEO "|" NEWAUTOO "|"

      *> Case 2: six-digit price (DECIMAL(6,0) allows it) and a long model name
           MOVE 4 TO VIN-LEN  MOVE "ZZ99" TO VIN-TEXT
           MOVE 2019 TO AUTOYEAR
           MOVE 20 TO MODEL-LEN  MOVE "F-150 Lightning Ext." TO MODEL-TEXT
           MOVE 123456 TO PRICE
           MOVE "N" TO NEWAUTO-TEXT
           PERFORM BUILD-ROW
           DISPLAY "CASE2|" VINO "|" YEARO "|" MODELO "|" PRICEO "|" NEWAUTOO "|"

      *> Case 3: small price, five-digit year (INTEGER allows it)
           MOVE 3 TO VIN-LEN  MOVE "K7 " TO VIN-TEXT
           MOVE 12024 TO AUTOYEAR
           MOVE 3 TO MODEL-LEN  MOVE "Fit" TO MODEL-TEXT
           MOVE 5 TO PRICE
           MOVE "Y" TO NEWAUTO-TEXT
           PERFORM BUILD-ROW
           DISPLAY "CASE3|" VINO "|" YEARO "|" MODELO "|" PRICEO "|" NEWAUTOO "|"

      *> Case 4: negative price (the column is signed DECIMAL; a credit note could produce it)
           MOVE 4 TO VIN-LEN  MOVE "NEG1" TO VIN-TEXT
           MOVE 2020 TO AUTOYEAR
           MOVE 4 TO MODEL-LEN  MOVE "Golf" TO MODEL-TEXT
           MOVE -1500 TO PRICE
           MOVE "N" TO NEWAUTO-TEXT
           PERFORM BUILD-ROW
           DISPLAY "CASE4|" VINO "|" YEARO "|" MODELO "|" PRICEO "|" NEWAUTOO "|"

      *> Case 5: zero price
           MOVE 4 TO VIN-LEN  MOVE "ZERO" TO VIN-TEXT
           MOVE 2024 TO AUTOYEAR
           MOVE 4 TO MODEL-LEN  MOVE "Leaf" TO MODEL-TEXT
           MOVE 0 TO PRICE
           MOVE "Y" TO NEWAUTO-TEXT
           PERFORM BUILD-ROW
           DISPLAY "CASE5|" VINO "|" YEARO "|" MODELO "|" PRICEO "|" NEWAUTOO "|".

       BUILD-ROW.
      *> Verbatim from GAM0VSI 1400-GET-INVENTORY-ROW
           MOVE AUTOYEAR TO CONVERT-YEAR
           MOVE PRICE TO CONVERT-PRICE
           MOVE VIN-TEXT TO VINO
           MOVE CONVERT-YEAR TO YEARO
           MOVE MODEL-TEXT TO MODELO
           MOVE CONVERT-PRICE TO PRICEO
           MOVE NEWAUTO-TEXT TO NEWAUTOO
           DISPLAY "  PRICE packed bytes=" FUNCTION HEX-OF(PRICE-RAW)
               " AUTOYEAR bytes=" FUNCTION HEX-OF(AUTOYEAR-RAW)
               " VIN bytes=" FUNCTION HEX-OF(VIN-RAW).
