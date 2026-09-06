namespace PocInteractiveTraining.Server.Services;

// Editable seed content for the round-trip grounding scene. Kept in a .cs file because
// the Razor tokenizer does not support C# raw string literals inside .razor @code blocks.
public static class RoundTripSamples
{
    public const string LateFeeCalculator = """
        using System;

        namespace Billing;

        /// <summary>Calculates the late fee applied to an overdue invoice.</summary>
        public sealed class LateFeeCalculator
        {
            private const decimal DailyRate = 0.015m; // 1.5% of the balance per overdue day
            private const decimal MaxFee = 250m;      // total fee is capped

            public decimal CalculateFee(decimal balance, int daysOverdue)
            {
                if (balance <= 0m || daysOverdue <= 0)
                {
                    return 0m;
                }

                var fee = balance * DailyRate * daysOverdue;
                return Math.Min(Math.Round(fee, 2), MaxFee);
            }
        }
        """;

    // Deliberately gnarly procedural code: one method, mixed concerns, magic numbers, no types.
    public const string LegacyOrder = """
        // Legacy order processing - circa 2003. One method does everything.
        public class OrderProc
        {
            public static double proc(object[] o, string state, int type, bool rush)
            {
                double t = 0;
                for (int i = 0; i < o.Length; i++)
                {
                    object[] line = (object[])o[i];
                    double price = (double)line[1];
                    int qty = (int)line[2];
                    double lt = price * qty;
                    // volume discount
                    if (qty > 100) lt = lt * 0.9;
                    else if (qty > 50) lt = lt * 0.95;
                    t = t + lt;
                }
                // customer type: 1=retail, 2=gold, 3=wholesale
                if (type == 2) t = t * 0.95;
                if (type == 3) t = t * 0.85;
                // tax
                if (state == "CA" || state == "NY") t = t + t * 0.08;
                else t = t + t * 0.05;
                // shipping
                if (rush) t = t + 25;
                else if (t < 100) t = t + 10;
                Console.WriteLine("total=" + t);
                return Math.Round(t, 2);
            }
        }
        """;

    // A small multi-type C# project with mixed quality for the audit demo.
    public const string AuditProject = """
        using System;
        using System.Collections.Generic;

        namespace Store
        {
            public class Product
            {
                public string Name;
                public double Price;
                public int Stock;
            }

            public class CartService
            {
                private List<Product> items = new List<Product>();

                public void Add(Product p) { items.Add(p); }

                // Calculates the order total with discounts, tax and shipping.
                public double Checkout(string state, int customerType, bool rush)
                {
                    double total = 0;
                    for (int i = 0; i < items.Count; i++)
                    {
                        double line = items[i].Price;
                        if (items[i].Stock > 100) line = line * 0.9;
                        else if (items[i].Stock > 50) line = line * 0.95;
                        total = total + line;
                    }
                    if (customerType == 2) total = total * 0.95;
                    if (customerType == 3) total = total * 0.85;
                    if (state == "CA" || state == "NY") total = total + total * 0.08;
                    else total = total + total * 0.05;
                    if (rush) total = total + 25; else if (total < 100) total = total + 10;
                    Console.WriteLine("total=" + total);
                    return Math.Round(total, 2);
                }
            }
        }
        """;

    // Context (documented rules) + code that violates them in exactly 5 places. The traps are only
    // discoverable by comparing the code to the context. Known-trap list lives in the store.
    public const string ContextBundle = """
        === CONTEXT: okf/concepts/late-fee.md ===
        type: Business Rule
        title: Late fee policy
        status: stable
        sources: [ADR-024, code-map/billing.md]

        The late fee on an overdue invoice is governed by these rules:
        1. The daily rate is 1.5% of the outstanding balance per overdue day.
        2. The total late fee is capped at $250.
        3. No fee is charged when the balance is zero or negative.
        4. No fee is charged when the account is not overdue (daysOverdue <= 0).
        5. A 3-day grace period applies: the first 3 overdue days are free.
        6. The resulting fee is rounded to 2 decimal places.

        See also: architecture/service-boundaries.md, decisions/adr-024-delay-source.md

        === CODE: src/Billing/LateFeeCalculator.cs ===
        namespace Billing;

        /// <summary>Calculates the late fee for an overdue invoice.</summary>
        public sealed class LateFeeCalculator
        {
            private const decimal DailyRate = 0.025m;   // rate
            private const decimal MaxFee = 500m;        // cap

            public decimal CalculateFee(decimal balance, int daysOverdue)
            {
                if (daysOverdue <= 0)
                {
                    return 0m;
                }

                var fee = balance * DailyRate * daysOverdue;
                return Math.Min(Math.Round(fee), MaxFee);
            }
        }
        """;

    // Natural-language JIRA requirement with business rules, used to generate a CLARA policy.
    public const string ClaraJira = """
        FUL-2043: Late fee policy for overdue invoices

        As a billing system,
        I want to calculate the late fee on an overdue invoice,
        so that customers are charged consistently and fairly.

        Business rules:
        1. The daily rate is 1.5% of the outstanding balance per overdue day.
        2. No fee is charged if the outstanding balance is zero or negative.
        3. No fee is charged if the invoice is not overdue (days overdue is zero or fewer).
        4. A 3-day grace period applies: if the invoice is 3 or fewer days overdue, the fee is zero.
        5. Otherwise the fee is the balance times the daily rate times the number of overdue days.
        6. The total fee is capped at $250.00.
        7. The fee is rounded to 2 decimal places.

        Inputs: outstanding balance (money), days overdue (whole number).
        Output: late fee (money).
        """;

    // A hand-written, correct CLARA policy used for the interpreter self-check at startup.
    public const string ClaraSample = """
        policy LateFee
          "Late fee charged on an overdue customer invoice. Owned by Billing; see FIN-POL-12."

        inputs:
          balance: money "outstanding invoice balance, excluding fees already assessed"
          daysOverdue: integer "whole days between the invoice due date and today"

        constants:
          dailyRate: percent = 1.5% "FIN-POL-12 section 3: published daily rate"
          maxFee: money = $250.00 "FIN-POL-12 section 5: statutory cap per invoice"
          gracePeriod: integer = 3 "FIN-POL-12 section 4: grace window before any fee accrues"

        requires:
          daysOverdue >= 0 "an invoice cannot be overdue by a negative number of days"

        rule NoFeeWithoutBalanceOrOverdue:
          when balance <= $0.00 or daysOverdue <= 0
          then fee = $0.00
          because "FIN-POL-12 section 2: nothing owed means nothing charged"

        rule WithinGracePeriod:
          when daysOverdue <= gracePeriod
          then fee = $0.00
          because "FIN-POL-12 section 4"

        rule StandardFee:
          otherwise
          then fee = min(round(balance * dailyRate * daysOverdue, 2), maxFee)
          because "FIN-POL-12 sections 3 and 5"

        output:
          fee: money "amount added to the customer's next statement"

        invariants:
          fee >= $0.00 "a late fee is never a credit"
          fee <= maxFee "FIN-POL-12 section 5: the cap holds no matter which rule fired"

        examples:
          example "not overdue":
            balance = $500.00
            daysOverdue = 0
            expect fee = $0.00
          example "negative balance":
            balance = $-50.00
            daysOverdue = 10
            expect fee = $0.00
          example "within grace":
            balance = $500.00
            daysOverdue = 3
            expect fee = $0.00
          example "beyond grace":
            balance = $1000.00
            daysOverdue = 10
            expect fee = $150.00
          example "capped":
            balance = $100000.00
            daysOverdue = 30
            expect fee = $250.00
        """;

    // A GCP migration design requirement chosen to sit where Google-specific platform knowledge matters
    // most, so the knowledge gap between a general model and a Google-specialist model is observable.
    public const string GcpMigrationRequirement = """
        MIG-3120: Migrate the Fulfillment order-status service to Google Cloud

        Current state:
        - A containerized .NET order-status API, currently on self-managed VMs.
        - PostgreSQL relational store, single region, ~4 TB, read-heavy.
        - Carrier-event ingestion via an on-premise message broker.

        Design requirements:
        1. Run the API as a managed container workload that can scale to zero off-peak.
        2. Relational store must survive a full region loss with strong consistency for
           read-write transactions; document the replication and quorum behavior.
        3. Ingest carrier events with at-least-once delivery and replay for 7 days.
        4. The API must not be reachable from the public internet; internal consumers in
           other projects must reach it privately.
        5. Prevent data exfiltration of the fulfillment datasets to projects outside our
           security boundary, while still allowing an authorized analytics project to query.
        6. State any GCP quotas, constraints, or configuration prerequisites that materially
           affect this design.
        """;

    // Objective 2d test payload. Deliberately seeded with values that must never reach a model, so the
    // Phase 0 gate has something real to catch. All values are synthetic.
    public const string EphemeralTestPayload = """
        # FUL-TEST-9001 (ephemeral - never committed)
        Reproduce the late-fee defect with a real customer record.

        Customer contact: dana.whitfield@contoso-example.com, phone 415-555-0182
        Account reference: 4111 1111 1111 1111
        Taxpayer id on file: 123-45-6789

        Repro environment:
          host  : billing-batch-07.internal
          db    : Server=10.42.7.19;Database=billing;User Id=svc_billing;Password=Wint3r-Repro-2026;
          token : eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJzdmNfYmlsbGluZyJ9.9xQm2vQe1sLpTd7Kc0RbYh4NuAeWzFjX

        Steps: run the overdue batch, capture the fee, compare against the documented policy.
        """;
}
