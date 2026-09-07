namespace PocInteractiveTraining.Server.Services;

// The shared prompt library is only useful if a contribution can be executed, not just described. This
// gives the pre-filled pattern a real target: a class whose comment documents a $200 cap that the code
// does not implement. A prompt worth sharing makes the model surface that contradiction rather than
// quietly building on top of it.
public static class PatternLibrarySamples
{
    public const string SourceClass = """
        namespace Billing.Pricing;

        /// <summary>
        /// Applies the membership discount described in policy DISC-14.
        /// Standard members receive no discount, Silver 5%, Gold 10%.
        /// The discount never applies to shipping and never exceeds $200 on a single order.
        /// </summary>
        public sealed class MembershipDiscountCalculator
        {
            public decimal Apply(decimal orderTotal, string tier)
            {
                var rate = tier switch
                {
                    "Gold" => 0.10m,
                    "Silver" => 0.05m,
                    _ => 0m
                };

                var discount = orderTotal * rate;
                return decimal.Round(orderTotal - discount, 2);
            }
        }
        """;

    public const string ChangeRequest = """
        DISC-27: add the Platinum membership tier

        Platinum members receive a 15% discount. Everything else in policy DISC-14 is unchanged.
        """;

    // The prompt itself is the artefact being shared. It is written to be reusable across any
    // rule-bearing class, not tailored to this one.
    public const string PatternPrompt = """
        You are changing one class to satisfy a change request.

        1. Read the class and its documentation comment first. Restate, in one line, the rules the class
           claims to implement.
        2. Compare those claims against the code. If the documentation asserts a rule the code does not
           implement, or the code does something the documentation does not mention, STOP and report the
           contradiction before making any change. Do not silently fix it and do not silently keep it.
        3. Make the smallest change that satisfies the request. Do not restructure, rename, or
           "improve" anything the request did not ask for.
        4. State explicitly what behaviour you preserved and why.

        Return the complete modified class, then the contradiction report and the preserved-behaviour list.
        """;

    public static PocInteractiveTraining.Server.Models.PatternSubmission PreFilled() => new()
    {
        Title = "Reconcile the doc comment before you change the code",
        Problem = "Agents implement the request against the code as written and inherit whatever the documentation already contradicts, so a latent defect survives the change and now looks reviewed.",
        Approach = PatternPrompt,
        Evidence = "Applied to 9 rule-bearing classes: the model surfaced a doc-vs-code contradiction in 4 of them, all 4 confirmed as real defects by the owning team.",
        ContextNeeded = "Only works on classes that actually carry a documentation comment or a linked policy. On undocumented code it degrades to an ordinary change request and finds nothing.",
        FailureModes = "Produces false contradictions when the comment is deliberately aspirational, and it will stop and report rather than deliver a change — which is wrong for a trivial edit under time pressure."
    };
}
