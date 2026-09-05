       IDENTIFICATION DIVISION.
       PROGRAM-ID. HARDPAY.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-RAW-RECORD           PIC X(20).
       01  WS-EMPLOYEE REDEFINES WS-RAW-RECORD.
           05  WS-EMP-ID           PIC X(5).
           05  WS-GRADE            PIC 9.
               88  WS-SENIOR       VALUE 7 THRU 9.
           05  WS-HOURS-PACKED     PIC S9(3)V9 COMP-3.
           05  WS-RATE             PIC S9(4)V99 COMP-3.
           05  FILLER              PIC X(8).
       01  WS-CALC.
           05  WS-GROSS            PIC S9(6)V99 COMP-3 VALUE 0.
           05  WS-BONUS            PIC S9(5)V99 VALUE 0.
           05  WS-NET              PIC S9(6)V99 VALUE 0.
       01  WS-FLAGS.
           05  WS-OVERFLOW-FLAG    PIC X VALUE "N".

       PROCEDURE DIVISION.
       0001-MAIN.
           MOVE "E101720000500002000" TO WS-RAW-RECORD.
           PERFORM 0100-GROSS THRU 0300-NET.
           DISPLAY "NET: " WS-NET " OVERFLOW: " WS-OVERFLOW-FLAG.
           STOP RUN.

       0100-GROSS.
           COMPUTE WS-GROSS ROUNDED = WS-HOURS-PACKED * WS-RATE
               ON SIZE ERROR MOVE "Y" TO WS-OVERFLOW-FLAG
           END-COMPUTE.

       0200-BONUS.
           IF WS-SENIOR
               COMPUTE WS-BONUS ROUNDED = WS-GROSS * 0.075
           ELSE
               MOVE ZERO TO WS-BONUS
           END-IF.

       0300-NET.
           COMPUTE WS-NET = WS-GROSS + WS-BONUS.
