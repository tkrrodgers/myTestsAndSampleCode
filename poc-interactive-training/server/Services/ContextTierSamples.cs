namespace PocInteractiveTraining.Server.Services;

// Context Sufficiency: one real ticket against one real repository, presented at three levels of
// documentation quality. The source code is byte-identical in all three tiers — only the surrounding
// knowledge changes — so any difference in the plans is attributable to context and nothing else.
public static class ContextTierSamples
{
    public const string RepoName = "FixedIncomeOptionsEngine";

    // A genuine latent defect in RegTMarginCalculator.OptionMargin: the out-of-the-money amount is
    // computed with the call formula regardless of right, and the alternative floor is 10% of the
    // underlying for both rights. Both are wrong for puts. The ticket deliberately does not name the
    // file, the formula, or the affected stage — finding those is what context is for.
    public const string Ticket = """
        FIO-2291: Short put orders are approved with too little initial margin

        Reported by: Margin Operations
        Severity: High — client-facing risk, potential regulatory finding

        Two short put orders were accepted last week that Margin Ops believe should have been
        rejected for insufficient buying power. Both were deep in the money. Reconciliation
        against the clearing broker's own Reg T calculation shows our requirement was materially
        lower than theirs. Short calls reconcile correctly.

        Acceptance criteria:
        1. Short put initial margin agrees with the clearing broker's Reg T figure.
        2. Short call margin is unchanged.
        3. Long options and defined-risk spreads are unchanged.
        4. Bond margin is unchanged.
        5. Orders that should now be rejected for insufficient buying power are rejected.
        6. The change is covered by tests.

        Do NOT write code. Produce a markdown implementation plan describing the change you would
        make and why.
        """;

    private const string RegTMarginSource = """
        === src/Trading/RegTMarginCalculator.cs ===
        public sealed class RegTMarginCalculator : IOrderStage
        {
            public string Name => "risk";

            public Task<StageResult> ExecuteAsync(OrderContext context, CancellationToken cancellationToken)
            {
                var order = context.Order;
                var assetClass = context.State.GetValueOrDefault("assetClass", order.Attr("assetClass").ToUpperInvariant());
                var buyingPower = order.AttrDecimal("buyingPower", decimal.MaxValue);

                var margin = assetClass == "BOND" ? BondMargin(order) : OptionMargin(order, context);

                context.State["initialMargin"] = margin.ToString("F2");
                if (margin > buyingPower)
                {
                    return Task.FromResult(StageResult.Fail(
                        $"Reg T initial margin {margin:C} exceeds buying power {buyingPower:C}"));
                }

                context.Record("risk", $"Reg T initial margin {margin:C} within buying power");
                return Task.FromResult(StageResult.Ok());
            }

            private static decimal OptionMargin(OrderEvent order, OrderContext context)
            {
                const decimal contractMultiplier = 100m;
                var premium = order.LimitPrice ?? 0m;
                var underlying = order.AttrDecimal("underlyingPrice");
                var strike = order.AttrDecimal("strike1");
                var contracts = order.Quantity;
                var strategy = context.State.GetValueOrDefault("strategy", "SINGLE");

                if (order.Side == OrderSide.Buy)
                {
                    // Long premium is paid in full.
                    return premium * contracts * contractMultiplier;
                }

                if (strategy == "VERTICAL_SPREAD")
                {
                    var width = Math.Abs(order.AttrDecimal("strike1") - order.AttrDecimal("strike2"));
                    var netCredit = premium;
                    var maxLoss = Math.Max(0m, width - netCredit);
                    return maxLoss * contracts * contractMultiplier;
                }

                // Short naked: max(20% underlying - OTM, 10% underlying) + premium.
                var otm = Math.Max(0m, strike - underlying);
                var method1 = underlying * 0.20m - otm + premium;
                var method2 = underlying * 0.10m + premium;
                return Math.Max(method1, method2) * contracts * contractMultiplier;
            }

            private static decimal BondMargin(OrderEvent order)
            {
                var price = order.LimitPrice ?? 100m;
                var face = order.AttrDecimal("faceValue", 1000m);
                var notional = price / 100m * face * order.Quantity;
                var isTreasury = order.Attr("issuerType").Equals("TREASURY", StringComparison.OrdinalIgnoreCase);
                var marginRate = isTreasury ? 0.01m : 0.10m;
                return notional * marginRate;
            }
        }
        """;

    // Tier C. Not absent context — WRONG context. A stale README left over from a service this repo
    // was forked from. Everything in it is plausible and none of it is true here.
    public const string TierCUndocumented = """
        === README.md (last updated 2019) ===
        # Order Execution Service

        Processes retail equity orders end to end. Orders arrive over the message bus, are checked
        for buying power, routed to the exchange, and booked.

        Margin is handled by the account service, not by this repository — see the account team's
        documentation for the buying-power rules. This service only reads the pre-computed
        buyingPower field and compares it against notional value.

        Coding standards: C# 8, one class per file, xUnit for tests.
        Contact: the equities platform team.
        """;

    // Tier B. The code is visible; the rules that govern it are not.
    public const string TierBPartial = """
        === README.md ===
        # FixedIncomeOptionsEngine

        Order pipeline for listed options and OTC fixed income. Stages run in sequence:
        validate, risk, route, execute, settle, persist. Each stage implements IOrderStage.

        Build: dotnet build. Tests: dotnet test.
        Architecture and domain notes live under okf/.

        """ + RegTMarginSource;

    // Tier A. The same code, plus the knowledge required to change it correctly.
    public const string TierADocumented = """
        === okf/index.md ===
        type: Index
        title: FixedIncomeOptionsEngine knowledge base

        Domain: domain/regt-margin.md, domain/options-strategies.md, domain/yield-to-maturity.md
        Architecture: architecture/pipeline.md
        Decisions: decisions/adr-031-cash-secured-puts.md
        Code map: code-map/components.md

        === okf/architecture/pipeline.md ===
        type: Architecture Boundary
        status: stable

        Stages execute in a fixed order and each owns exactly one concern:
        - validate  (OptionsStrategyValidator) — structural legality of the strategy. Sets
                    context.State["assetClass"] and context.State["strategy"].
        - risk      (RegTMarginCalculator) — ALL margin and buying-power decisions live here.
        - execute   (YieldToMaturityCalculator) — pricing only. Never computes margin.
        - settle, persist — post-trade.

        A stage may read context.State keys set by an earlier stage. It must not read state a
        later stage sets, and must not duplicate another stage's responsibility.

        === okf/domain/regt-margin.md ===
        type: Domain
        title: Reg T margin
        resource: /src/Trading/RegTMarginCalculator.cs
        status: stable
        sources:
          - Federal Reserve Regulation T (https://www.federalreserve.gov/supervisionreg/regtcg.htm)

        RegTMarginCalculator is the `risk` stage. It computes the initial margin requirement and
        rejects the order if it exceeds the account buyingPower.

        ## Option margin

        * Long options — paid for in full: premium × contracts × 100. No margin loan is permitted
          on listed long options.

        * Short naked options — the Reg T requirement is the GREATER of two methods, plus premium,
          per contract (×100 multiplier). Both the out-of-the-money amount and the alternative
          minimum depend on the RIGHT of the option:

            Short CALL:  method 1 = 20% × underlying − OTM + premium
                         method 2 = 10% × underlying + premium
                         OTM      = max(0, strike − underlying)

            Short PUT:   method 1 = 20% × underlying − OTM + premium
                         method 2 = 10% × STRIKE + premium
                         OTM      = max(0, underlying − strike)

          Note the two differences for puts: the out-of-the-money amount is measured in the
          opposite direction, and the alternative minimum is a percentage of the STRIKE, not of
          the underlying. A put's exposure is bounded by the strike, not by the current spot.

        * Vertical spreads — defined risk: margin equals the maximum loss,
          max(0, strikeWidth − netCredit) × contracts × 100.

        ## Bond margin

        Notional is price/100 × faceValue × quantity. Treasuries (issuerType = TREASURY) take a
        1% haircut; corporate bonds require 10%.

        === okf/decisions/adr-031-cash-secured-puts.md ===
        type: Decision
        title: ADR-031 — cash-secured puts are margined at full collateral
        status: accepted

        Decision: a short put whose account has posted full cash collateral is NOT margined with
        the naked formula. Its requirement is (strike × contracts × 100) − premium received,
        which is the true maximum loss if assigned.

        Consequence: applying the naked short-put formula to a cash-secured put OVERSTATES the
        requirement and rejects orders that should be accepted. Applying the cash-secured formula
        to an uncollateralised put UNDERSTATES it and is a regulatory finding.

        OPEN: the order attribute that signals posted cash collateral has not been agreed with the
        account service team. It is not in the current OrderEvent.Attributes contract. Do not
        invent an attribute name — raise it with the account service owner.

        === okf/code-map/components.md ===
        type: Code Map

        | Role | Path | Why inspect it |
        | Primary implementation | src/Trading/RegTMarginCalculator.cs | computes initial margin; the risk stage |
        | Upstream state | src/Trading/OptionsStrategyValidator.cs | sets State["strategy"]; only SINGLE, VERTICAL_SPREAD, STRADDLE and STRANGLE are legal |
        | Contract | src/Pipeline/PipelineContracts.cs | OrderEvent.Attr / AttrDecimal; attributes are string-keyed |
        | Verification | tests/RegTMarginCalculatorTests.cs | one test per right, per strategy |

        === src/Pipeline/PipelineContracts.cs (excerpt) ===
        public sealed record OrderEvent(
            string OrderId, string ClientId, string Symbol, OrderSide Side, decimal Quantity,
            decimal? LimitPrice, string OrderType, DateTimeOffset ReceivedAt,
            IReadOnlyDictionary<string, string> Attributes)
        {
            public string Attr(string key, string fallback = "") => ...;
            public decimal AttrDecimal(string key, decimal fallback = 0m) => ...;
        }
        // Option attributes in use: assetClass, strategy, right1, strike1, expiry1,
        // right2, strike2, expiry2, underlyingPrice, buyingPower.

        === src/Trading/OptionsStrategyValidator.cs (excerpt) ===
        // Sets context.State["strategy"] to one of:
        //   SINGLE | VERTICAL_SPREAD | STRADDLE | STRANGLE
        // STRADDLE and STRANGLE each have a call leg AND a put leg (right1 and right2).

        """ + RegTMarginSource;

    public sealed record Tier(int Index, string Id, string Label, string Description, string Context);

    public static IReadOnlyList<Tier> Tiers { get; } =
    [
        new(0, "documented", "A — Documented",
            "Full OKF bundle: stage boundary, the Reg T domain rules including the put-specific formula, ADR-031, code map, contracts and the source file.",
            TierADocumented),
        new(1, "partial", "B — Partial",
            "Current README and the source file. The code is visible; the rules that govern it are not.",
            TierBPartial),
        new(2, "undocumented", "C — Undocumented",
            "A stale README inherited from the service this repo was forked from. Plausible, confident, and wrong — it states margin is not even handled here.",
            TierCUndocumented)
    ];

    // Sealed: never shown to the planning model. UnlockedAtTier is the weakest tier from which the
    // fact is genuinely derivable, which is what makes a miss attributable rather than anecdotal.
    public static IReadOnlyList<CurveCriterion> Criteria { get; } =
    [
        new("Locates the margin code",
            "Names RegTMarginCalculator as the place the change belongs",
            ["regtmargincalculator", "regtmargin"],
            1),
        new("Respects the stage boundary",
            "Keeps margin in the risk stage rather than pricing or validation",
            ["risk stage", "\"risk\"", "iorderstage", "stage boundary", "not the execute", "execute stage"],
            1),
        new("Branches on the option right",
            "Recognises the formula must depend on CALL vs PUT",
            ["right1", "call vs put", "put and call", "depends on the right", "option right", "by right"],
            1),
        new("Corrects the out-of-the-money direction",
            "For a put the OTM amount is underlying − strike, not strike − underlying",
            ["underlying - strike", "underlying − strike", "underlying minus strike", "underlyingprice - strike", "spot - strike", "opposite direction"],
            0),
        new("Corrects the alternative minimum base",
            "The put floor is 10% of the STRIKE, not 10% of the underlying",
            ["10% of the strike", "10% of strike", "10% × strike", "10% x strike", "0.10m * strike", "strike * 0.10", "percentage of the strike", "10 percent of the strike"],
            0),
        new("Handles cash-secured puts",
            "Applies ADR-031 rather than margining a collateralised put as naked",
            ["cash-secured", "cash secured", "adr-031", "adr 031", "posted collateral"],
            0),
        new("Notices the multi-leg fall-through",
            "STRADDLE and STRANGLE reach the naked branch and have a put leg",
            ["straddle", "strangle"],
            0),
        new("Flags the unknown instead of inventing it",
            "Raises the unagreed collateral attribute as an open question",
            ["open question", "not specified", "unspecified", "do not invent", "not agreed", "attribute name", "raise it with", "ask the account"],
            0),
        new("Plans verification",
            "States the test cases that would prove the change",
            ["test", "unit test", "assert"],
            2)
    ];

    public static string BuildRequest(Tier tier) =>
        $"<ticket>\n{Ticket}\n</ticket>\n\n<repository name=\"{RepoName}\" documentation_tier=\"{tier.Id}\">\n{tier.Context}\n</repository>";
}

public sealed record CurveCriterion(
    string Name,
    string Description,
    IReadOnlyList<string> Signals,
    int UnlockedAtTier);
