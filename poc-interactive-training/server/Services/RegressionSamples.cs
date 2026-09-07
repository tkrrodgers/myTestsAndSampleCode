namespace PocInteractiveTraining.Server.Services;

// Fixture for the regression audit. There are two versions of the same service on purpose: the
// production baseline is the golden reference, and the candidate carries the requested enhancement.
// The suite covers the pre-existing behaviour well and the new behaviour not at all, so both the
// differential comparison and mutation testing have something real to expose.
public static class RegressionSamples
{
    // What runs in production today. This is the golden baseline the candidate is compared against.
    public const string Baseline = """
        public static class ShippingFee
        {
            public static decimal Calculate(decimal orderTotal, bool expedited, int itemCount)
            {
                if (orderTotal <= 0m)
                {
                    return 0m;
                }

                decimal fee = 5.00m;

                if (orderTotal >= 50m)
                {
                    fee = 0m;
                }

                return fee;
            }
        }
        """;

    public const string Source = """
        public static class ShippingFee
        {
            public static decimal Calculate(decimal orderTotal, bool expedited, int itemCount)
            {
                if (orderTotal <= 0m)
                {
                    return 0m;
                }

                decimal fee = 5.00m;

                if (orderTotal >= 50m)
                {
                    fee = 0m;
                }

                if (expedited)
                {
                    fee = fee + 12.50m;
                }

                if (itemCount > 10)
                {
                    fee = fee + 3.00m;
                }

                return fee;
            }
        }
        """;

    public const string Tests = """
        public static class ShippingFeeTests
        {
            public static void Test_standard_small_order()
            {
                Assert.Equal(5.00m, ShippingFee.Calculate(20m, false, 1));
            }

            public static void Test_free_over_threshold()
            {
                Assert.Equal(0m, ShippingFee.Calculate(80m, false, 1));
            }

            public static void Test_zero_total_is_free()
            {
                Assert.Equal(0m, ShippingFee.Calculate(0m, false, 1));
            }
        }
        """;

    public const string Enhancement = """
        FUL-2211: Charge for expedited shipping and bulk handling

        1. Expedited orders add a $12.50 surcharge.
        2. Orders of more than 10 items add $3.00 bulk handling.
        3. Free shipping still applies at $50 and above.
        4. Orders with a zero or negative total are never charged.
        """;

    // QA's condition-permutation data. Columns bind positionally to the entry-point parameters.
    public const string TestVectors = """
        orderTotal,expedited,itemCount
        0,false,1
        -5,false,1
        10,false,1
        20,false,1
        49.99,false,1
        50,false,1
        80,false,1
        20,true,1
        49.99,true,1
        50,true,1
        80,true,1
        20,false,11
        80,false,11
        20,true,11
        80,true,25
        0,true,50
        """;

    // Behaviour changes the business asked for and signed off. A difference matching one of these is
    // intended; a difference matching none is a regression. Both are decisions, and the harness must
    // not silently treat them the same way.
    public static readonly (string Rule, string Description)[] IntendedChanges =
    [
        ("expedited", "FUL-2211 §1 — expedited orders add a $12.50 surcharge"),
        ("itemCount > 10", "FUL-2211 §2 — orders over 10 items add $3.00 bulk handling")
    ];

    // Practice note only; not executed here. Recorded so the tab does not imply that comparing
    // happy-path permutations is the whole of regression assurance.
    public const string FaultInjectionNote = """
        Alongside the differential comparison, the team injects an interceptor at the request and
        response boundary to simulate conditions the permutation data cannot express: malformed and
        out-of-range payloads, truncated and null fields, duplicate and out-of-order messages,
        downstream timeouts, slow responses, and partial failures mid-transaction.

        That is a separate technique from everything measured on this tab. Condition permutations
        establish that the business logic still agrees with production; fault injection establishes
        what happens when the inputs or the dependencies misbehave. A suite can be strong at the
        first and blind to the second.
        """;

    // Minimal assertion helper compiled alongside the fixture so tests can fail by throwing.
    public const string AssertHelper = """
        using System;

        public static class Assert
        {
            public static void Equal(decimal expected, decimal actual)
            {
                if (expected != actual)
                {
                    throw new Exception("Expected " + expected + " but got " + actual + ".");
                }
            }
        }
        """;
}
