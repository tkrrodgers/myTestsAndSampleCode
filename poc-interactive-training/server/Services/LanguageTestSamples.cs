namespace PocInteractiveTraining.Server.Services;

// Fixtures for the language A/B test. The same business logic is expressed twice: once as ordinary C# where
// the local, on-premise knowledge exists only in the heads of the team, and once as CLARA where that same
// knowledge is carried inline and machine-checked. The JIRA asks for a change whose correctness depends on
// exactly the knowledge C# leaves out.
public static class LanguageTestSamples
{
    public const string CSharpSource = """
        using System;

        namespace Depot.Billing;

        public sealed class LateReturnBilling
        {
            private const decimal DailyRateFactor = 0.045m;
            private const decimal Cap = 0.80m;
            private const decimal Floor = 15.00m;
            private const int Grace = 2;

            public decimal Calculate(
                decimal replacementValue,
                int calendarDaysLate,
                int closureDaysLate,
                bool contractCustomer,
                bool damageWaiver)
            {
                if (calendarDaysLate <= 0)
                {
                    return 0m;
                }

                var billable = calendarDaysLate - closureDaysLate;
                if (contractCustomer)
                {
                    billable -= Grace;
                }

                if (billable < 0)
                {
                    billable = 0;
                }

                var fee = replacementValue * DailyRateFactor * billable;

                if (damageWaiver)
                {
                    fee *= 0.5m;
                }

                var ceiling = replacementValue * Cap;
                if (fee > ceiling)
                {
                    fee = ceiling;
                }

                fee = Math.Round(fee, 2, MidpointRounding.AwayFromZero);

                if (billable > 0 && fee < Floor)
                {
                    fee = Floor;
                }

                return fee;
            }
        }
        """;

    public const string ClaraSource = """
        policy LateReturnBilling
          "Charges a customer for returning rented equipment after its due date. Owned by Depot Billing; see OPS-POL-31."

        inputs:
          replacementValue: money "insured replacement value of the asset, taken from the rental contract"
          calendarDaysLate: integer "whole days between the contracted due date and the return scan"
          closureDaysLate: integer "of those days, the ones the depot was closed and could not accept a return"
          contractCustomer: boolean "true when the customer holds a standing depot contract"
          damageWaiver: boolean "true when the customer bought the damage waiver at checkout"

        constants:
          dailyRate: percent = 4.5% "OPS-POL-31 section 3: daily charge as a share of replacement value"
          insuranceCeiling: percent = 80% "OPS-POL-31 section 9: our insurer will not cover a charge above this share of replacement value"
          adminFee: money = $15.00 "State tariff section 12: minimum administrative charge on any billable late return"
          contractGrace: integer = 2 "OPS-POL-31 section 4: grace days granted to contract customers only"
          waiverShare: percent = 50% "OPS-POL-31 section 7: the damage waiver halves the late charge"

        requires:
          closureDaysLate >= 0 "closure days are a count and are never negative"
          closureDaysLate <= calendarDaysLate "depot closure days are a subset of the days the item was late"

        derive billableDays: integer "days actually charged, after removing depot closure days and any contract grace"
          rule NotLate:
            when calendarDaysLate <= 0
            then billableDays = 0
            because "OPS-POL-31 section 2: an on-time return is never billable"
          rule ContractCustomerGrace:
            when contractCustomer
            then billableDays = max(calendarDaysLate - closureDaysLate - contractGrace, 0)
            because "OPS-POL-31 section 4: grace applies to contract customers only"
          rule WalkUpCustomer:
            otherwise
            then billableDays = max(calendarDaysLate - closureDaysLate, 0)
            because "OPS-POL-31 section 3: the depot cannot charge for days it was closed"

        derive grossFee: money "charge before the insurance ceiling and before the administrative minimum"
          rule NothingBillable:
            when billableDays <= 0
            then grossFee = $0.00
            because "OPS-POL-31 section 2"
          rule WaiverHolder:
            when damageWaiver
            then grossFee = round(replacementValue * dailyRate * billableDays * waiverShare, 2)
            because "OPS-POL-31 section 7"
          rule FullRate:
            otherwise
            then grossFee = round(replacementValue * dailyRate * billableDays, 2)
            because "OPS-POL-31 section 3"

        derive cappedFee: money "charge after the insurance ceiling is applied"
          rule NothingToCap:
            when grossFee <= $0.00
            then cappedFee = $0.00
            because "OPS-POL-31 section 2"
          rule ApplyInsuranceCeiling:
            otherwise
            then cappedFee = min(grossFee, round(replacementValue * insuranceCeiling, 2))
            because "OPS-POL-31 section 9: a charge above the insured ceiling cannot be recovered"

        rules:
          rule NoBillableDays:
            when billableDays <= 0
            then fee = $0.00
            because "OPS-POL-31 section 2: with no billable days there is no charge and no administrative minimum"
          rule AtLeastTheAdministrativeMinimum:
            otherwise
            then fee = max(cappedFee, adminFee)
            because "State tariff section 12: a billable late return always carries at least the administrative charge"

        output:
          fee: money "total late-return charge added to the customer's invoice"

        invariants:
          fee >= $0.00 "a late return never produces a credit"
          fee <= max(round(replacementValue * insuranceCeiling, 2), adminFee) "OPS-POL-31 section 9 caps the charge, except where the statutory minimum in State tariff section 12 is higher"
          fee = $0.00 or fee >= adminFee "State tariff section 12: a billable late return is never charged below the administrative minimum"

        examples:
          example "returned on time":
            replacementValue = $1000.00
            calendarDaysLate = 0
            closureDaysLate = 0
            contractCustomer = false
            damageWaiver = false
            expect fee = $0.00
          example "contract grace absorbs the delay":
            replacementValue = $1000.00
            calendarDaysLate = 2
            closureDaysLate = 0
            contractCustomer = true
            damageWaiver = false
            expect fee = $0.00
          example "depot closure days are not billable":
            replacementValue = $2000.00
            calendarDaysLate = 5
            closureDaysLate = 2
            contractCustomer = false
            damageWaiver = false
            expect fee = $270.00
          example "damage waiver halves the charge":
            replacementValue = $1000.00
            calendarDaysLate = 4
            closureDaysLate = 0
            contractCustomer = false
            damageWaiver = true
            expect fee = $90.00
          example "administrative minimum applies to a tiny charge":
            replacementValue = $100.00
            calendarDaysLate = 1
            closureDaysLate = 0
            contractCustomer = false
            damageWaiver = true
            expect fee = $15.00
          example "insurance ceiling binds a long overrun":
            replacementValue = $100.00
            calendarDaysLate = 60
            closureDaysLate = 0
            contractCustomer = false
            damageWaiver = false
            expect fee = $80.00
          example "the waiver is applied before the ceiling, not after":
            replacementValue = $100.00
            calendarDaysLate = 60
            closureDaysLate = 0
            contractCustomer = false
            damageWaiver = true
            expect fee = $80.00
          example "the statutory minimum outranks a tiny insurance ceiling":
            replacementValue = $10.00
            calendarDaysLate = 1
            closureDaysLate = 0
            contractCustomer = false
            damageWaiver = false
            expect fee = $15.00
        """;

    public const string Jira = """
        RENT-4471: Loyalty discount on late-return charges

        As a depot manager,
        I want repeat customers to receive a loyalty discount on their late-return charge,
        so that we keep our best customers after an honest mistake.

        Acceptance criteria:
        1. Add the customer's number of previously completed rentals as an input.
        2. Customers with 5 or more previously completed rentals receive a 10% loyalty discount
           on the late-return charge.
        3. Customers with fewer than 5 previously completed rentals are unaffected.
        4. All existing behaviour is otherwise unchanged.

        Implement the change. Return the complete updated implementation.
        """;

    // The knowledge that exists on-premise and that the C# arm has no way to know. This is not given to
    // either arm; it is what the CLARA source already carries inline, and what the judge grades against.
    public const string ContextPack = """
        OPS-POL-31 — Depot late-return charging policy (extract)

        Section 2. A return made on or before the contracted due date is never billable. Where no days are
        billable, no charge of any kind is raised, including the administrative charge in State tariff s.12.

        Section 3. The daily late charge is 4.5% of the asset's insured replacement value per billable day.
        Days on which the depot was closed are not billable, because the customer had no opportunity to
        return the asset.

        Section 4. Customers holding a standing depot contract receive two grace days. Walk-up customers do
        not. Grace days are deducted after depot closure days.

        Section 7. Customers who purchased the damage waiver at checkout are charged half the late charge.
        The halving is applied to the gross charge, before the insurance ceiling in section 9.

        Section 9. No late-return charge may exceed 80% of the asset's insured replacement value. Our
        insurer will not reimburse a loss above that share, so a charge above it cannot be recovered and
        must not be raised. This ceiling is applied after the damage-waiver halving.

        State tariff s.12 (statutory, not discretionary)

        Any billable late return carries a minimum administrative charge of $15.00. This is a statutory
        floor and applies after every discretionary reduction. A billable late return may never be invoiced
        at more than $0.00 and less than $15.00. Where nothing is billable, the floor does not apply.

        ADR-031: ordering of adjustments

        Adjustments are applied in a fixed order: billable days, gross charge, discretionary reductions,
        insurance ceiling, statutory floor. Any new adjustment is discretionary unless a statute says
        otherwise, and therefore belongs before the ceiling and the floor. Reordering this sequence
        requires a decision record.
        """;

    // Sealed rubric. Both arms are graded against exactly these, by a judge that never sees which arm is which.
    public static readonly string[] SealedCriteria =
    [
        "The loyalty discount is applied only when the customer has 5 or more previously completed rentals.",
        "The loyalty discount does not turn an unbillable return into a charge: with no billable days the result is still $0.00.",
        "The 10% loyalty discount is applied BEFORE the 80%-of-replacement-value insurance ceiling, because it is a discretionary reduction (OPS-POL-31 s.9, ADR-031).",
        "The $15.00 statutory administrative minimum still holds AFTER the loyalty discount: a billable late return is never invoiced between $0.00 and $15.00.",
        "The result is rounded to 2 decimal places, half-up, after the discount is applied.",
        "Depot closure days and the 2-day contract-customer grace remain excluded from billable days.",
        "The damage-waiver halving is still applied before the insurance ceiling.",
        "The answer treats the $15.00 minimum as a statutory floor rather than an arbitrary constant that may be discounted through, or explicitly preserves it."
    ];
}
