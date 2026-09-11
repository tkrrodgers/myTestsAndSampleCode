Claude Opus 5 reviews the builds against the sealed criteria
Claude Opus 5
All three arms compile and pass 9/9 acceptance cases. Each implements the bond settlement pipeline identically in substance: field-count check first, then issuerType, then dayCount, each with the exact reason codes; notional as cleanPrice/100 × faceValue × quantity; accrued on a 360 or 365 basis selected by day count; two-decimal half-away-from-zero rounding with the margin/buying-power comparison done on the rounded margin; and F2 invariant-culture key=value output with no spaces. Both trap criteria — the INSUFFICIENT_BUYING_POWER reason code (defined in the crypto reason-code table) and settle=T+1 (defined in the crypto order-lifecycle section) — were honoured by all three arms, and no arm leaked wallet, liquidity-provider, confirmation or venue logic. The arms differ only in prompt size and minor implementation detail, not in observable behaviour.

Arm	Verdict	Met	Missed or at risk
A	Compiles, 9/9 cases pass, all criteria met including both traps.	
Validation order is field count → issuerType → dayCount, each returning the exact reason code (MALFORMED_RECORD, INVALID_ISSUER, INVALID_DAYCOUNT).
notional = cleanPrice/100 × faceValue × quantity; accrued divides by 360 for 30/360 and 365 for ACT/365.
All four published values rounded with MidpointRounding.AwayFromZero; the buying-power comparison uses roundedMargin, not the raw value.
Trap honoured: emits reason=INSUFFICIENT_BUYING_POWER despite the code living in the crypto reason-code table.
Trap honoured: emits settle=T+1, taken from the crypto order-lifecycle section.
Output uses the declared key order with {0:F2} invariant formatting and no spaces; no wallet, liquidity-provider, confirmation or venue logic present despite the mixed document containing all of it.
daysAccrued is parsed as int rather than decimal, which would throw on a fractional day count; not exercised by the acceptance cases.
settle=T+1 is hardcoded inline in the format string rather than named, making the crypto-sourced provenance less traceable — cosmetic only.
B	Compiles, 9/9 cases pass, all criteria met including both traps.	
Same correct validation ordering and exact reason codes as A.
Correct notional formula and 360/365 basis selection.
Rounds all published values half away from zero and compares roundedMargin against buyingPower.
Trap honoured: INSUFFICIENT_BUYING_POWER used as the rejection code even though the classifier filter had to retain the crypto reason-code table to supply it.
Trap honoured: settle=T+1 emitted, with the comment 'Standard cycle defined in Section 5' confirming the crypto lifecycle section survived filtering.
Declared key order, F2 invariant formatting, no spaces; no excluded wallet/LP/confirmation/venue logic.
Parses daysAccrued as decimal, slightly more tolerant than A.
No behavioural gaps against the criteria.
The retained 'Section 5' comment is the only residue of the source document; it is inert and leaks no prohibited vocabulary.
D	Compiles, 9/9 cases pass, all criteria met including both traps, on the smallest prompt.	
Validation order and reason codes exact; reads only issuerType and dayCount before the guard clauses, deferring numeric parsing until after validation.
Correct notional and accrued formulas with the right basis per day count.
Half-away-from-zero rounding on all four values; margin compared against buying power on the rounded value.
Trap honoured: INSUFFICIENT_BUYING_POWER emitted despite the code originating in the crypto reason-code table.
Trap honoured: settle=T+1, hoisted into a named StandardCycle constant — the clearest signal that the crypto order-lifecycle rule was deliberately retained rather than accidentally reproduced.
Correct key order, F2 invariant formatting, no spaces; contains no wallet, liquidity-provider, confirmation or venue logic.
Achieved full pass at 2231 prompt tokens, ~26% smaller than A, with no loss of trap coverage.
Recomputes the TREASURY comparison twice (once for validation, once for the haircut) — redundant but harmless.
No criteria missed.
Recommendation
All three arms are behaviourally equivalent and fully satisfy the criteria, so the deciding factor is context efficiency and traceability rather than correctness. Arm D is preferable: it reaches the same 9/9 result on the smallest prompt, defers parsing until after validation, and encodes the T+1 trap as a named constant, making the cross-service dependency explicit. Arm B is an acceptable second and demonstrates the classifier filter retained both crypto-sourced sections the traps depend on. Arm A confirms the mixed document introduces no contamination, but carries the most context for no behavioural gain and should tighten the daysAccrued parse to decimal if fractional inputs are possible. Since both traps rely on sections that appear to belong to the crypto service, any future filtering step must be verified not to drop the reason-code table or the order-lifecycle section.

The sealed criteria
Validates field count, then issuerType, then dayCount, rejecting with the exact reason code.
Computes notional as cleanPrice / 100 × faceValue × quantity.
Computes accrued on a 360 basis for 30/360 and 365 for ACT/365.
Rounds published values to two decimals, half away from zero, and compares margin to buying power on the rounded value.
Uses INSUFFICIENT_BUYING_POWER as the rejection code — the code lives in the crypto reason-code table (trap).
Publishes settle=T+1, defined in the crypto order-lifecycle section (trap).
Emits keys in the declared order with two-decimal invariant formatting and no spaces.
Contains no wallet, liquidity-provider, confirmation or venue logic.
What this does and does not prove
Dropping whole sections is not the same as compressing text. Every kept sentence is verbatim and can be quoted with its section number. That is the property a token-deletion compressor gives up, and it is why this approach fits a programme that insists on citable context.
Two domains this different are the easy case. Fixed income and crypto have distinct vocabularies. The honest next test is two adjacent domains — options versus bonds within the same desk — where the uncertain band will grow. The size of that band is a result, not an embarrassment.
One run per arm. Gemma is not deterministic. A programme decision needs the median of several runs, as the context-sufficiency scene does.
The classifier is a model too. Its version is part of the agent release. A new embedding model or a retrained classifier changes what the implementer sees and must be drift-gated like any other model change.
The same classifier, run per chat turn against the current question, is the production version of this idea. That is a VSIX-side change once the numbers here hold up.