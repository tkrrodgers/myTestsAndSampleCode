using System.Globalization;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// The two designs are authored separately and tagged by domain, so the ground truth for the mixed
// document is known by construction and nobody has to hand-label anything. Two fixed-income sections
// deliberately depend on crypto-labelled sections (the "traps"): a filter that drops what it does not
// recognise will lose them, and the compiled result will show it.
public static class ClassifyContextSamples
{
    public const string FixedIncome = "Fixed income";
    public const string Crypto = "Crypto";
    public const string Shared = "Shared";

    public const string EntryType = "BondSettlementService";
    public const string EntryMethod = "Settle";

    // Fixed seed: the same mixed document every run, so the classifier's confusion matrix is reproducible.
    public const int MixSeed = 20260910;

    public static readonly string[] TrapIds = ["FI-MARGIN", "FI-SETTLE"];

    // Shared sections describe the platform; both domains depend on them. Several have neutral titles on
    // purpose — that is what a real design document looks like, and it is where a binary classifier fails.
    public static readonly DesignSegment[] Segments =
    [
        new("SH-OVERVIEW", Shared, "Purpose of this document",
            """
            This document specifies two new pipeline services for the trading platform: a bond settlement
            amount service for the fixed-income desk and a wallet screening and instant settlement service
            for the spot crypto desk. Both plug into the existing OrderProcessingPipeline as IOrderStage
            implementations and both follow the platform conventions in the shared sections. Each service is
            independently deliverable. An implementer assigned one service does not need to build the other,
            but must honour every shared convention and every section a service section refers to.
            """, []),

        new("SH-ENVELOPE", Shared, "Record envelope and output format",
            """
            Every service exposes a single pure function that accepts one pipe-delimited record as a string
            and returns one string. Fields are separated by the '|' character and are trimmed of surrounding
            whitespace before use. Numbers are parsed and formatted with the invariant culture, never the
            machine locale. Output is a semicolon-separated list of key=value pairs in the order the service
            section declares, with no spaces around '=' or ';'. Monetary values are formatted with exactly two
            decimal places and no thousands separators. A rejected record returns exactly two pairs:
            status=REJECTED;reason=<CODE>, where <CODE> is one of the reason codes in the reason-code table.
            A record with the wrong number of fields is rejected with reason MALFORMED_RECORD.
            """, []),

        new("SH-ROUNDING", Shared, "Rounding policy",
            """
            Compute in full decimal precision and round only when a value is published. Published monetary
            values are rounded to two decimal places using round-half-away-from-zero
            (MidpointRounding.AwayFromZero), not banker's rounding. Any comparison against a limit — buying
            power, a screening threshold, a rate limit — is made on the rounded published value, so that the
            figure a client sees and the figure the decision used are the same figure.
            """, []),

        new("FI-SCOPE", FixedIncome, "Bond settlement amount service — scope",
            """
            The fixed-income desk needs the settlement amount of a cash bond trade at order time, not at
            T+1 when the back office computes it. The service takes a bond order and returns the notional,
            the accrued interest owed to the seller, the total settlement amount and the Regulation T initial
            margin, or rejects the order. It is a pipeline stage named "settlement-preview" and runs after
            the existing "risk" stage. It covers U.S. Treasuries and corporate bonds only; municipal bonds and
            agencies are out of scope for this release.
            """, []),

        new("FI-INPUT", FixedIncome, "Bond settlement amount service — input record",
            """
            The record has exactly eight fields in this order:
            issuerType | cleanPrice | faceValue | quantity | couponRate | dayCount | daysAccrued | buyingPower.
            issuerType is TREASURY or CORPORATE (case-insensitive); any other value is rejected with reason
            INVALID_ISSUER. cleanPrice is the clean price per 100 of face, for example 99.50. faceValue is the
            face value of one bond, for example 1000. quantity is a whole number of bonds. couponRate is the
            annual coupon as a decimal fraction, for example 0.045 for 4.5%. dayCount is the day-count
            convention and must be 30/360 or ACT/365; any other value is rejected with reason INVALID_DAYCOUNT.
            daysAccrued is the whole number of days since the last coupon. buyingPower is the account's
            available buying power in currency. Validation happens before any arithmetic, in this order: the
            field count first (a record without exactly eight fields is MALFORMED_RECORD whatever its contents),
            then issuerType, then dayCount. The first failure wins.
            """, ["SH-ENVELOPE"]),

        new("FI-CALC", FixedIncome, "Bond settlement amount service — calculation",
            """
            notional = cleanPrice / 100 × faceValue × quantity.
            accrued = faceValue × quantity × couponRate × daysAccrued / basis, where basis is 360 for 30/360
            and 365 for ACT/365.
            settlement = notional + accrued.
            These are the amounts the buyer pays: the clean price for the bond plus the coupon interest that
            has built up since the last payment and belongs to the seller. Publish all three per the rounding
            policy. Do not compute yield to maturity in this service; the pricing stage already does.
            """, ["SH-ROUNDING"]),

        new("FI-MARGIN", FixedIncome, "Bond settlement amount service — Regulation T margin",
            """
            margin = notional × haircut, where the haircut is 1% for TREASURY and 10% for CORPORATE, matching
            the existing RegTMarginCalculator. Round margin per the rounding policy, then compare: if the
            rounded margin exceeds buyingPower the order is rejected. The rejection uses the buying-power
            reason code from the reason-code table in the wallet screening section (§CR-CODES) — the two
            desks share one table so that downstream monitoring does not need two vocabularies.
            """, ["SH-ROUNDING", "CR-CODES"]),

        new("FI-SETTLE", FixedIncome, "Bond settlement amount service — settlement date and output",
            """
            Both Treasuries and corporates settle on the desk's standard cycle, which is defined once for the
            whole platform in the order lifecycle section (§CR-LIFECYCLE); publish it verbatim as the value of
            the settle key rather than hard-coding a different cycle here. On success the output is, in this
            order: status=OK;notional=<n>;accrued=<n>;settlement=<n>;margin=<n>;settle=<cycle>.
            Example: TREASURY|99.50|1000|10|0.045|30/360|45|500000 yields
            status=OK;notional=9950.00;accrued=56.25;settlement=10006.25;margin=99.50;settle=T+1.
            """, ["SH-ENVELOPE", "CR-LIFECYCLE"]),

        new("FI-TESTS", FixedIncome, "Bond settlement amount service — acceptance tests",
            """
            The implementation must be exercised on: a Treasury on 30/360; a corporate on ACT/365; a corporate
            whose margin exceeds buying power; an invalid issuer type; an invalid day-count convention; a
            record with too few fields; and a zero-coupon case where daysAccrued is 0. The expected output for
            each is derived from the calculation section by hand and checked against the published example.
            A test that only asserts "no exception" does not count.
            """, ["FI-CALC"]),

        new("CR-SCOPE", Crypto, "Wallet screening and instant settlement service — scope",
            """
            The spot crypto desk settles client trades instantly against the firm's own inventory rather
            than on a clearing cycle, which means a sanctioned or high-risk counterparty wallet is a loss
            event, not a compliance event. The service screens the destination wallet before settlement is
            released, aggregates liquidity-provider quotes to price the fill, and releases settlement only
            once the on-chain confirmation threshold for the asset has been met. It is a pipeline stage
            named "screen-and-settle" and runs after the existing "validate" stage.
            """, []),

        new("CR-INPUT", Crypto, "Wallet screening and instant settlement service — input record",
            """
            The record has seven fields: asset | side | quantity | destinationWallet | walletRiskScore |
            sanctionsHit | confirmations. asset is BTC, ETH or USDC. side is BUY or SELL. quantity is the
            asset quantity to eight decimal places. destinationWallet is the client's on-chain address.
            walletRiskScore is the vendor screening score from 0 to 100. sanctionsHit is Y or N.
            confirmations is the number of block confirmations observed so far for an inbound leg.
            """, ["SH-ENVELOPE"]),

        new("CR-QUOTES", Crypto, "Liquidity provider quote aggregation",
            """
            The desk receives streaming two-way quotes from several liquidity providers. For each fill the
            service takes the best bid or best offer across providers whose quote is no older than 250
            milliseconds; a stale quote is excluded, not repriced. The fill price is the best eligible quote
            plus the desk spread of 15 basis points on the client's side. If fewer than two providers are
            eligible the fill is rejected with reason INSUFFICIENT_LIQUIDITY. The provider that won the fill
            is recorded on the order for best-execution reporting.
            """, ["SH-ROUNDING"]),

        new("CR-SCREEN", Crypto, "Wallet screening rules",
            """
            A destination wallet with sanctionsHit = Y is rejected with reason WALLET_SANCTIONED regardless
            of any other field. A wallet with walletRiskScore of 80 or more is rejected with reason
            WALLET_HIGH_RISK. Scores from 50 to 79 are allowed but flagged for compliance review by writing
            review=Y to the output. Transfers with a notional above 1,000 USD equivalent carry the
            originator and beneficiary information required by the travel rule; the service records that the
            payload was attached but does not build it.
            """, []),

        new("CR-FINALITY", Crypto, "Settlement finality and confirmation thresholds",
            """
            Instant settlement is released only when the inbound on-chain leg has reached the confirmation
            threshold for the asset: 2 confirmations for BTC, 12 for ETH, 12 for USDC on Ethereum. Below the
            threshold the order is held with status=HELD and the number of confirmations still required. The
            thresholds are configuration, not code, because they change when the desk's risk appetite or the
            chain's block time changes.
            """, []),

        new("CR-CODES", Crypto, "Reason codes for screening and settlement",
            """
            All rejections use one of these codes: WALLET_SANCTIONED, WALLET_HIGH_RISK, INSUFFICIENT_LIQUIDITY,
            CHAIN_UNCONFIRMED, INSUFFICIENT_BUYING_POWER, INVALID_ISSUER, INVALID_DAYCOUNT, MALFORMED_RECORD.
            The buying-power code is shared with the fixed-income desk so that the risk dashboard shows one
            series for buying-power rejections across desks. Codes are upper-case with underscores and are
            published exactly as written here.
            """, []),

        new("CR-LIFECYCLE", Crypto, "Order lifecycle and standard settlement cycle",
            """
            Orders progress through capture, pre-trade checks, execution and settlement. The on-chain leg of
            a crypto trade settles when finality is reached; the cash leg, and every other product on the
            platform including cash bonds, settles on the desk's standard cycle of T+1, written exactly as
            T+1. This value is defined here once and referenced by other services rather than repeated.
            """, []),

        new("CR-LIMITS", Crypto, "Venue rate limits and retry",
            """
            The venue enforces a weight-based request budget of 1,200 weight units per minute per API key.
            Order placement costs 1 unit, order-book snapshots 5, and account queries 10. The service tracks
            the budget from the venue's response headers and backs off exponentially from 500 milliseconds
            when a 429 is returned. A timed-out order placement is treated as unknown-outcome and reconciled
            by client order id before any retry, never resubmitted blind.
            """, []),

        new("SH-LOGGING", Shared, "Logging and metrics",
            """
            Every stage records one line per decision through the existing MetricsCollector with the stage
            name, the order id and the outcome. Amounts are logged as published, after rounding. Wallet
            addresses and account identifiers are logged in full because the platform log store is inside the
            firm boundary; they must never be included in a message sent to an external model.
            """, []),

        new("SH-TESTING", Shared, "Testing approach",
            """
            Each service is a pure function of its input record, so it is tested by table: one row per case,
            expected output derived by hand from the specification. Tests assert the exact output string,
            not its parts, so a change to field order or formatting fails the test. No test may depend on
            the machine locale or the current date.
            """, []),

        new("SH-DEPLOY", Shared, "Deployment",
            """
            Both services ship as ordinary stages registered in PipelineConfiguration behind a feature flag
            per desk. The flag defaults to off. Turning it on is a recorded operational change owned by the
            desk's platform lead, and the first week runs in shadow mode, computing and logging without
            affecting the order.
            """, [])
    ];

    // Sealed acceptance criteria — Claude sees these only at review time, after every arm has finished.
    public static readonly string[] SealedCriteria =
    [
        "Validates field count, then issuerType, then dayCount, rejecting with the exact reason code.",
        "Computes notional as cleanPrice / 100 × faceValue × quantity.",
        "Computes accrued on a 360 basis for 30/360 and 365 for ACT/365.",
        "Rounds published values to two decimals, half away from zero, and compares margin to buying power on the rounded value.",
        "Uses INSUFFICIENT_BUYING_POWER as the rejection code — the code lives in the crypto reason-code table (trap).",
        "Publishes settle=T+1, defined in the crypto order-lifecycle section (trap).",
        "Emits keys in the declared order with two-decimal invariant formatting and no spaces.",
        "Contains no wallet, liquidity-provider, confirmation or venue logic."
    ];

    // Vocabulary that has no business in a bond settlement service. Deterministic, sealed, case-insensitive.
    public static readonly string[] CryptoLeakTerms =
    [
        "wallet", "sanction", "liquidity", "confirmation", "venue", "btc", "eth", "usdc", "onchain", "on-chain",
        "travelrule", "travel rule", "riskscore", "rate limit", "429", "spread", "quote"
    ];

    public static readonly string[] TestVectors =
    [
        "TREASURY|99.50|1000|10|0.045|30/360|45|500000",
        "CORPORATE|101.25|1000|25|0.0525|ACT/365|100|1000000",
        "CORPORATE|98.00|1000|500|0.06|30/360|30|40000",
        "MUNICIPAL|99.50|1000|10|0.045|30/360|45|500000",
        "TREASURY|99.50|1000|10|0.045|ACT/360|45|500000",
        "TREASURY|99.50|1000|10|0.045|30/360|45",
        "MUNICIPAL|99.50|1000|10|0.045|ACT/360|45",
        "TREASURY|100.00|1000|10|0.00|30/360|0|500000",
        "corporate|95.125|1000|3|0.0375|ACT/365|1|100000"
    ];

    /// <summary>The reference implementation, executed to produce the expected output for every vector.</summary>
    public static string Oracle(string record)
    {
        var fields = record.Split('|').Select(field => field.Trim()).ToArray();
        var culture = CultureInfo.InvariantCulture;

        // Validation order per FI-INPUT: field count, then issuer, then day count — first failure wins.
        if (fields.Length != 8)
        {
            return "status=REJECTED;reason=MALFORMED_RECORD";
        }

        var issuer = fields[0].ToUpperInvariant();
        if (issuer is not ("TREASURY" or "CORPORATE"))
        {
            return "status=REJECTED;reason=INVALID_ISSUER";
        }

        var dayCount = fields[5].ToUpperInvariant();
        if (dayCount is not ("30/360" or "ACT/365"))
        {
            return "status=REJECTED;reason=INVALID_DAYCOUNT";
        }

        var cleanPrice = decimal.Parse(fields[1], culture);
        var face = decimal.Parse(fields[2], culture);
        var quantity = decimal.Parse(fields[3], culture);
        var coupon = decimal.Parse(fields[4], culture);
        var days = decimal.Parse(fields[6], culture);
        var buyingPower = decimal.Parse(fields[7], culture);
        var basis = dayCount == "30/360" ? 360m : 365m;

        var notional = cleanPrice / 100m * face * quantity;
        var accrued = face * quantity * coupon * days / basis;
        var settlement = notional + accrued;
        var margin = notional * (issuer == "TREASURY" ? 0.01m : 0.10m);

        static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
        static string Fmt(decimal value) => Round(value).ToString("0.00", CultureInfo.InvariantCulture);

        if (Round(margin) > buyingPower)
        {
            return "status=REJECTED;reason=INSUFFICIENT_BUYING_POWER";
        }

        return $"status=OK;notional={Fmt(notional)};accrued={Fmt(accrued)};settlement={Fmt(settlement)};margin={Fmt(margin)};settle=T+1";
    }
}
