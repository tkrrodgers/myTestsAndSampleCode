namespace PocInteractiveTraining.Server.Services;

// Fixture for regression-coverage adequacy. The suite deliberately covers the pre-existing behaviour
// well and the enhanced behaviour (expedited surcharge, bulk handling) not at all — so mutation testing
// has something real to expose rather than a manufactured result.
public static class RegressionSamples
{
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
