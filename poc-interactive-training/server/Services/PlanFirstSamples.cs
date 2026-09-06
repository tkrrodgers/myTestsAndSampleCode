namespace PocInteractiveTraining.Server.Services;

// Kept in a .cs file because the Razor tokenizer does not support C# raw string literals inside @code.
public static class PlanFirstSamples
{
    public const string GoodPlan = """
        ## Executive summary
        Orders that hit a confirmed carrier delay currently show a stale delivery date, so customers call
        support. We will revise the estimate in the service that already owns it, leave every other order
        untouched, and expose where the estimate came from. No customer-facing wording changes.

        ## Approach
        ```mermaid
        flowchart LR
            E[Carrier event] --> C{Confirmed?}
            C -->|yes| R[Revise estimate]
            C -->|no| K[Keep original]
        ```

        ## Risks
        - Provisional delays must not revise the estimate; ADR-024 is explicit and the current code is not.
        - The response field naming the estimate source is unspecified; inventing it would break the contract.

        ## Verification
        Extend the existing tests to cover the confirmed and unchanged paths, and verify the order-status
        response shape is unchanged for orders with no delay.
        """;
}
