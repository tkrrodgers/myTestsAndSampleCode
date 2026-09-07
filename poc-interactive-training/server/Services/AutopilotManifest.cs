using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

public enum AutoKind { Observe, Click, Select, Fill, Scroll }

public enum AutoWait { Settle, Element, State }

// Closed set. Every step must choose one; there is deliberately no default.
public enum AutoFail { NarrateAndContinue, RetryOnceThenContinue, SkipScene, AbortRun }

public enum AutoProfile { Deterministic, Rehearsed, Full }

public sealed record AutoStep(
    string StepId,
    string SceneId,
    int SceneNumber,
    AutoKind Kind,
    string? Selector,
    string? Value,
    bool BridgeRequired,
    AutoWait WaitType,
    string? WaitKey,
    int TimeoutSeconds,
    AutoFail OnTimeout,
    AutoFail OnFailure,
    bool ResultNarration,
    IReadOnlyList<string> Facts,
    IReadOnlyList<string> MustNotClaim,
    int MaxWords,
    bool RequiresInProcessExecution = false);

/// <summary>
/// The authored walkthrough. Sequence lives here as data so no model ever infers what a control does.
/// </summary>
public static class AutopilotManifest
{
    public const int Version = 1;

    private static AutoStep S(
        string id, string scene, int number, AutoKind kind, string[] facts,
        string? selector = null, string? value = null, bool bridge = false,
        AutoWait wait = AutoWait.Settle, string? waitKey = null, int timeout = 8,
        AutoFail onTimeout = AutoFail.NarrateAndContinue, AutoFail onFailure = AutoFail.NarrateAndContinue,
        bool result = false, string[]? mustNot = null, int maxWords = 70, bool needsExec = false)
        => new(id, scene, number, kind, selector, value, bridge, wait, waitKey, timeout,
            onTimeout, onFailure, result, facts, mustNot ?? [], maxWords, needsExec);

    public static readonly AutoStep[] Steps =
    [
        // ---- 1 introduction -------------------------------------------------
        S("s01.01", "introduction", 1, AutoKind.Observe,
            ["Six Director of AI priorities are mapped onto thirty-four working demonstrations.",
             "Every scene ends in a number, and each scene states whether a tool or a model produced it."]),
        S("s01.02", "introduction", 1, AutoKind.Scroll, selector: "[data-auto='s01.gaps']",
            facts: ["Eight capabilities the role calls for are not yet covered by the lab.",
                    "Two of the eight have no scene at all: runtime incident response, and the operating model."],
            mustNot: ["that the programme is complete"]),
        S("s01.03", "introduction", 1, AutoKind.Scroll, selector: "[data-auto='s01.caveats']",
            facts: ["Model-scored numbers are judgement, not measurement.",
                    "Most results here are single runs, and these models are not deterministic."]),

        // ---- 2 welcome ------------------------------------------------------
        S("s02.01", "welcome", 2, AutoKind.Click, selector: "[data-auto='s02.start']", bridge: true,
            wait: AutoWait.State, waitKey: "Coach", timeout: 120,
            facts: ["This queues grounded narration authored by Gemma 4 from the OKF fixture.",
                    "The fixture is synthetic; no real repository or ticket is involved."]),
        S("s02.02", "welcome", 2, AutoKind.Observe, result: true,
            facts: ["The banner names the model that actually authored the narration.",
                    "If Gemma was unavailable a fallback model is disclosed, never substituted silently."]),

        // ---- 3 why-okf ------------------------------------------------------
        S("s03.01", "why-okf", 3, AutoKind.Observe,
            ["Ungrounded path: JIRA keywords, broad repository search, first plausible match.",
             "Grounded path: index, concept, architecture boundary, decision record, code map."]),
        S("s03.02", "why-okf", 3, AutoKind.Observe,
            ["The token-reduction figure is the OKF authors' own measurement.",
             "CES has not re-measured it on a CES repository yet."],
            mustNot: ["that we have measured a token saving ourselves"]),

        // ---- 4 artifact-tree ------------------------------------------------
        S("s04.01", "artifact-tree", 4, AutoKind.Observe,
            ["One file holds one concept, and ordinary Markdown links form the graph.",
             "An agent follows only the relevant links, reading about three small files."]),
        S("s04.02", "artifact-tree", 4, AutoKind.Observe,
            ["Structure does not guarantee that the content is true.",
             "Reading these files does not change the model's weights; it is context, not training."],
            mustNot: ["that supplying artifacts trains the model"]),

        // ---- 5 jira-trace ---------------------------------------------------
        S("s05.01", "jira-trace", 5, AutoKind.Click, selector: "[data-auto='s05.step-1']",
            facts: ["Step one opens okf/index.md, the governed entry point.",
                    "The instruction is explicit: do not search the whole repository yet."]),
        S("s05.02", "jira-trace", 5, AutoKind.Click, selector: "[data-auto='s05.step-2']",
            facts: ["The delivery-estimate concept is distinct from order-status formatting.",
                    "That distinction is what excludes superficially similar UI matches."]),
        S("s05.03", "jira-trace", 5, AutoKind.Click, selector: "[data-auto='s05.step-3']",
            facts: ["Fulfillment owns the customer-facing estimate; Shipping supplies carrier events.",
                    "The documented boundary identifies the owning service before any code is read."]),
        S("s05.04", "jira-trace", 5, AutoKind.Click, selector: "[data-auto='s05.step-4']",
            facts: ["ADR-024 states that only a confirmed carrier-delay event may revise an estimate.",
                    "That policy becomes a constraint the code has to enforce."]),
        S("s05.05", "jira-trace", 5, AutoKind.Click, selector: "[data-auto='s05.step-5']",
            facts: ["The code map names DeliveryEstimateService.cs as primary and the handler as a caller.",
                    "This locates the implementation and the contract impact."]),
        S("s05.06", "jira-trace", 5, AutoKind.Click, selector: "[data-auto='s05.step-6']",
            facts: ["The existing tests already expose the delayed and unchanged paths.",
                    "Proof is planned before a change is proposed."]),
        S("s05.07", "jira-trace", 5, AutoKind.Scroll, selector: "[data-auto='s05.context']",
            facts: ["The agent is given exactly eight artifacts: five OKF documents and three source files.",
                    "Nothing else is available to it."]),
        S("s05.08", "jira-trace", 5, AutoKind.Observe,
            ["FUL-1842 never names the API field for the estimate source.",
             "That omission is deliberate; it is the trap this scene exists to spring."]),
        S("s05.09", "jira-trace", 5, AutoKind.Click, selector: "[data-auto='s05.run']", bridge: true,
            wait: AutoWait.State, waitKey: "GroundedChange", timeout: 300,
            facts: ["Gemma 4 now implements the ticket from those artifacts and nothing else.",
                    "Claude Opus 5 then traces every change back to the sentence that justifies it."]),
        S("s05.10", "jira-trace", 5, AutoKind.Observe, result: true,
            facts: ["The implementation is shown with the files it changed.",
                    "Each change carries an inline citation naming the OKF document behind it."]),
        S("s05.11", "jira-trace", 5, AutoKind.Observe, result: true,
            facts: ["The audit reports whether the missing API field was flagged as an open question or invented.",
                    "Naming a gap in prose is not the same as leaving it open in the code."],
            mustNot: ["that the implementation is correct", "that the audit proves correctness"]),

        // ---- 6 animation-demo -----------------------------------------------
        S("s06.01", "animation-demo", 6, AutoKind.Click, selector: "[data-auto='s06.replay']", timeout: 10,
            facts: ["The motion shows the investigation narrowing from a broad story to one code path."]),
        S("s06.02", "animation-demo", 6, AutoKind.Observe,
            ["This is labelled an illustration of an example flow.",
             "Under reduced-motion settings every step appears immediately with no travelling marker."],
            mustNot: ["that this shows the model reasoning", "that this is live model thinking"]),

        // ---- 7 image-demo ---------------------------------------------------
        S("s07.01", "image-demo", 7, AutoKind.Observe,
            ["The bitmap carries an asset record: rendered source, format, data classification, and review status."]),
        S("s07.02", "image-demo", 7, AutoKind.Observe,
            ["Structured HTML remains the authoritative content.",
             "Images alone are not searchable, localisable, or sufficient for accessibility."]),

        // ---- 8 knowledge-check ----------------------------------------------
        S("s08.01", "knowledge-check", 8, AutoKind.Click, selector: "[data-auto='s08.q0-o1']",
            facts: ["The index routes the investigation to governed concepts and authoritative artifacts.",
                    "Current code still verifies whatever the artifact claims."]),
        S("s08.02", "knowledge-check", 8, AutoKind.Click, selector: "[data-auto='s08.q1-o2']",
            facts: ["A verified ownership boundary is stronger evidence than naming proximity."]),
        S("s08.03", "knowledge-check", 8, AutoKind.Click, selector: "[data-auto='s08.q2-o2']",
            facts: ["Good grounding reveals missing knowledge instead of hiding it behind a plausible guess."]),
        S("s08.04", "knowledge-check", 8, AutoKind.Observe,
            ["These checks practise the mental model.",
             "They are not a workplace competency grade."],
            mustNot: ["that this certifies anyone"]),

        // ---- 9 prompt-challenge ---------------------------------------------
        S("s09.01", "prompt-challenge", 9, AutoKind.Observe,
            ["The rubric has six areas and is visible before the review runs."]),
        S("s09.02", "prompt-challenge", 9, AutoKind.Observe,
            ["The prompt in the box is a pre-filled worked example.",
             "It was not typed live during this run."],
            mustNot: ["that a person just wrote this prompt"]),
        S("s09.03", "prompt-challenge", 9, AutoKind.Click, selector: "[data-auto='s09.submit']", bridge: true,
            wait: AutoWait.State, waitKey: "Review", timeout: 240,
            facts: ["Only the synthetic story, the fixture, the rubric and the prompt are sent.",
                    "Claude streams each rubric decision as it is made."]),
        S("s09.04", "prompt-challenge", 9, AutoKind.Observe, result: true,
            facts: ["The trace lists each rubric check with the evidence it rests on.",
                    "This is the reviewer narrating its own steps."],
            mustNot: ["that this is private chain-of-thought", "that the trace proves the review is correct"]),
        S("s09.05", "prompt-challenge", 9, AutoKind.Observe, result: true,
            facts: ["The result is coaching commentary against a visible rubric.",
                    "It is not a validated grade."],
            mustNot: ["that the score is validated"]),

        // ---- 10 model-comparison --------------------------------------------
        S("s10.01", "model-comparison", 10, AutoKind.Select, selector: "[data-auto='s10.topic']", value: "gke-autoscaling",
            facts: ["The same question is put to three models."]),
        S("s10.02", "model-comparison", 10, AutoKind.Click, selector: "[data-auto='s10.run']", bridge: true,
            wait: AutoWait.State, waitKey: "Comparison", timeout: 420,
            facts: ["Answers are collected first, then the official documentation is fetched live.",
                    "A judge model then scores the anonymised answers against that documentation."]),
        S("s10.03", "model-comparison", 10, AutoKind.Observe, result: true,
            facts: ["The verdict shows per-model scores with quoted evidence.",
                    "Any model slot that failed is displayed with its reason rather than dropped."]),
        S("s10.04", "model-comparison", 10, AutoKind.Observe,
            ["This is an illustrative harness on a small number of questions.",
             "It is not a validated benchmark."],
            mustNot: ["that this ranks the models generally"]),

        // ---- 11 llm-support --------------------------------------------------
        S("s11.01", "llm-support", 11, AutoKind.Observe,
            ["The requirement is a Google Cloud migration brief."]),
        S("s11.02", "llm-support", 11, AutoKind.Click, selector: "[data-auto='s11.run']", bridge: true,
            wait: AutoWait.State, waitKey: "LlmSupport", timeout: 360,
            facts: ["A specialist model is used as a retrieval instrument, not as the designer.",
                    "Claude then states what was already known, what was learned, and what is still unverified."]),
        S("s11.03", "llm-support", 11, AutoKind.Observe, result: true,
            facts: ["Each design step is tagged with the source it came from.",
                    "Anything still resting on the specialist's claim is marked as needing primary documentation."]),

        // ---- 12 round-trip ---------------------------------------------------
        S("s12.01", "round-trip", 12, AutoKind.Observe,
            ["The original class is the input to Gemma 4."]),
        S("s12.02", "round-trip", 12, AutoKind.Click, selector: "[data-auto='s12.run']", bridge: true,
            wait: AutoWait.State, waitKey: "RoundTrip", timeout: 420,
            facts: ["Gemma turns the class into a JIRA story, then rebuilds the class from that story alone.",
                    "Claude then reviews how much behaviour survived the round trip."]),
        S("s12.03", "round-trip", 12, AutoKind.Observe, result: true,
            facts: ["The review reports which behaviours survived and which were lost.",
                    "Loss here is a property of the story, not only of the model."]),

        // ---- 13 modernize ----------------------------------------------------
        S("s13.01", "modernize", 13, AutoKind.Click, selector: "[data-auto='s13.run']", bridge: true,
            wait: AutoWait.State, waitKey: "Modernize", timeout: 300,
            facts: ["Legacy comprehension is a token cost paid on every future task.",
                    "Modernising once exposes the business rules and the seams for later services."]),
        S("s13.02", "modernize", 13, AutoKind.Observe, result: true,
            facts: ["The rewrite is intended to preserve behaviour and to make the rules readable.",
                    "Behaviour preservation is asserted by the model here, not proven by tests."],
            mustNot: ["that behaviour is proven identical"]),

        // ---- 14 audit-code ---------------------------------------------------
        S("s14.01", "audit-code", 14, AutoKind.Click, selector: "[data-auto='s14.run']", bridge: true,
            wait: AutoWait.State, waitKey: "Audit", timeout: 300,
            facts: ["Roslyn analyses the project first: deterministic, reproducible, and the code is never executed.",
                    "Gemma then grounds its recommendations on that rating."]),
        S("s14.02", "audit-code", 14, AutoKind.Observe, result: true,
            facts: ["The numeric rating came from static analysis.",
                    "The model supplied the judgement, not the numbers."]),

        // ---- 15 audit-context ------------------------------------------------
        S("s15.01", "audit-context", 15, AutoKind.Click, selector: "[data-auto='s15.run']", bridge: true,
            wait: AutoWait.State, waitKey: "ContextAudit", timeout: 360,
            facts: ["A deterministic scan plus an ML.NET classifier decide cheaply whether the context is usable.",
                    "Only if it passes does Gemma hunt the planted traps using that context."]),
        S("s15.02", "audit-context", 15, AutoKind.Observe, result: true,
            facts: ["Claude grades how many planted traps the context actually caught.",
                    "The planted list is sealed and is not shown to the hunting model."]),

        // ---- 16 llm-language -------------------------------------------------
        S("s16.01", "llm-language", 16, AutoKind.Observe,
            ["The pipeline runs source, parse, AST, intermediate form, code generation, then execution.",
             "Every stage is deterministic and inspectable."]),
        S("s16.02", "llm-language", 16, AutoKind.Click, selector: "[data-auto='s16.run']", bridge: true,
            wait: AutoWait.State, waitKey: "Clara", timeout: 300,
            facts: ["A model writes the policy from the requirement, it compiles and runs, and a second model reviews it.",
                    "The compiler, not a model, decides whether it is valid."]),
        S("s16.03", "llm-language", 16, AutoKind.Observe, result: true,
            facts: ["The compiled output is a deterministic execution result."]),
        S("s16.04", "llm-language", 16, AutoKind.Click, selector: "[data-auto='s16.benchmark']",
            wait: AutoWait.Element, waitKey: "[data-auto='s16.benchmark-result']", timeout: 90,
            result: true,
            facts: ["Conformance and cost are measured against hand-written C#.",
                    "No model is involved in this measurement."]),

        // ---- 17 language-test ------------------------------------------------
        S("s17.01", "language-test", 17, AutoKind.Observe,
            ["Both arms get the same ticket, the same model, and no extra context.",
             "In one arm the local policy knowledge lives only in people's heads."]),
        S("s17.02", "language-test", 17, AutoKind.Click, selector: "[data-auto='s17.run']", bridge: true,
            wait: AutoWait.State, waitKey: "LanguageTest", timeout: 420,
            facts: ["Both arms run, then Claude grades them against sealed criteria neither arm ever saw."]),
        S("s17.03", "language-test", 17, AutoKind.Observe, result: true,
            facts: ["The verdict shows a score per arm and the prompt-token difference between them.",
                    "This is a single paired run, not a benchmark."],
            mustNot: ["that this settles the question generally"]),

        // ---- 18 cobol-migration ----------------------------------------------
        S("s18.01", "cobol-migration", 18, AutoKind.Observe,
            ["The oracle is the real COBOL program, compiled and executed.",
             "Its captured output is the ground truth for this scene."]),
        S("s18.02", "cobol-migration", 18, AutoKind.Click, selector: "[data-auto='s18.run']", bridge: true,
            wait: AutoWait.State, waitKey: "Migration", timeout: 600,
            facts: ["Three arms migrate the same program with increasing grounding: none, static analysis, and compiler-resolved facts.",
                    "Every answer is compiled and executed against the captured legacy output."]),
        S("s18.03", "cobol-migration", 18, AutoKind.Observe, result: true,
            facts: ["Nothing in this scene is graded by a model.",
                    "The verdict is decided by execution against the oracle."]),

        // ---- 19 ticket-gate --------------------------------------------------
        S("s19.01", "ticket-gate", 19, AutoKind.Observe,
            ["The pre-check scores the ticket deterministically and recomputes as the text changes.",
             "No model and no bridge are involved in that score."]),
        S("s19.02", "ticket-gate", 19, AutoKind.Click, selector: "[data-auto='s19.run']", bridge: true,
            wait: AutoWait.State, waitKey: "TicketGate", timeout: 240,
            facts: ["Claude grades the ticket against the rubric and rewrites it so it would pass."]),
        S("s19.03", "ticket-gate", 19, AutoKind.Observe, result: true,
            facts: ["Most agent failures are underspecified tickets rather than weak models.",
                    "The rewrite shows what the ticket was missing."]),

        // ---- 20 trap-hunt ----------------------------------------------------
        S("s20.01", "trap-hunt", 20, AutoKind.Click, selector: "[data-auto='s20.run']", bridge: true,
            wait: AutoWait.State, waitKey: "TrapHunt", timeout: 360,
            facts: ["Gemma audits a proposed cloud design against the five-pillar checklist.",
                    "Claude then grades that audit against the sealed list of what was actually planted."]),
        S("s20.02", "trap-hunt", 20, AutoKind.Observe, result: true,
            facts: ["Recall counts planted defects found; precision penalises invented ones.",
                    "Both matter — a report that flags everything is not an audit."]),

        // ---- 21 drift-scorecard ----------------------------------------------
        S("s21.01", "drift-scorecard", 21, AutoKind.Observe,
            ["The golden case is a frozen commit, its ticket, and the patch a human actually wrote."]),
        S("s21.02", "drift-scorecard", 21, AutoKind.Click, selector: "[data-auto='s21.agent']", bridge: true,
            wait: AutoWait.State, waitKey: "Drift", timeout: 300,
            facts: ["The candidate patch is scored on three layers: functional, structural, and semantic.",
                    "The first two are deterministic; only the semantic layer asks a model."]),
        S("s21.03", "drift-scorecard", 21, AutoKind.Observe, result: true,
            facts: ["The scorecard separates what was executed from what was judged."],
            mustNot: ["that one golden case generalises"]),

        // ---- 22 phase-zero ---------------------------------------------------
        S("s22.01", "phase-zero", 22, AutoKind.Select, selector: "[data-auto='s22.blast']", value: "Customer-facing",
            facts: ["Risk tier is derived from blast radius, autonomy, and data tier together."]),
        S("s22.02", "phase-zero", 22, AutoKind.Select, selector: "[data-auto='s22.autonomy']", value: "Acts-autonomously",
            facts: ["Autonomy is the second axis: suggests, acts with approval, or acts autonomously."]),
        S("s22.03", "phase-zero", 22, AutoKind.Select, selector: "[data-auto='s22.datatier']", value: "Restricted",
            facts: ["Restricted data raises the tier on its own, regardless of the other axes."]),
        S("s22.04", "phase-zero", 22, AutoKind.Click, selector: "[data-auto='s22.tier']",
            wait: AutoWait.Element, waitKey: "[data-auto='s22.tier-result']", timeout: 15, result: true,
            facts: ["The tier and any missing mandatory registration fields are computed with no model at all."]),
        S("s22.05", "phase-zero", 22, AutoKind.Click, selector: "[data-auto='s22.classify']",
            wait: AutoWait.Element, waitKey: "[data-auto='s22.tier-decision']", timeout: 15, result: true,
            facts: ["The payload is classified before anything could be sent to a model.",
                    "The decision, the finding types, and the incident severity are all deterministic."]),
        S("s22.06", "phase-zero", 22, AutoKind.Observe,
            ["Both of these controls run with no model.",
             "A prerequisite that can hallucinate is not a prerequisite."]),

        // ---- 23 context-curve ------------------------------------------------
        S("s23.01", "context-curve", 23, AutoKind.Observe,
            ["Three variants of the same repository hold identical source and different knowledge.",
             "They are documented, partially documented, and undocumented."]),
        S("s23.02", "context-curve", 23, AutoKind.Click, selector: "[data-auto='s23.run']", bridge: true,
            wait: AutoWait.State, waitKey: "ContextCurve", timeout: 900,
            facts: ["The same task and model run against each tier, repeated, and the median is taken.",
                    "Scoring uses sealed criteria the model never sees."]),
        S("s23.03", "context-curve", 23, AutoKind.Observe, result: true,
            facts: ["The degradation figures are deterministic; no model produced them.",
                    "The plateau is the token budget, measured rather than argued."]),

        // ---- 24 action-authority ---------------------------------------------
        S("s24.01", "action-authority", 24, AutoKind.Select, selector: "[data-auto='s24.action']", value: "rerun-tests",
            facts: ["Authority is decided per exact action against the agent's registered envelope."]),
        S("s24.02", "action-authority", 24, AutoKind.Click, selector: "[data-auto='s24.decide']",
            wait: AutoWait.Element, waitKey: "[data-auto='s24.decision']", timeout: 15, result: true,
            facts: ["A reversible action inside the registered envelope may proceed."]),
        S("s24.03", "action-authority", 24, AutoKind.Select, selector: "[data-auto='s24.action']", value: "deploy-prod",
            facts: ["The same agent now proposes a production deployment."]),
        S("s24.04", "action-authority", 24, AutoKind.Click, selector: "[data-auto='s24.decide']",
            wait: AutoWait.Element, waitKey: "[data-auto='s24.decision']", timeout: 15, result: true,
            facts: ["This action stops for a verified human decision.",
                    "Approving the word deploy is not the same as approving this deployment."]),

        // ---- 25 framing-lab ---------------------------------------------------
        S("s25.01", "framing-lab", 25, AutoKind.Observe,
            ["One question is asked three ways of one model: neutral, leading, and authority-framed."]),
        S("s25.02", "framing-lab", 25, AutoKind.Click, selector: "[data-auto='s25.run']", bridge: true,
            wait: AutoWait.State, waitKey: "Framing", timeout: 420,
            facts: ["Divergence from the neutral answer is measured before anyone offers an opinion about it."]),
        S("s25.03", "framing-lab", 25, AutoKind.Observe, result: true,
            facts: ["The most expensive bias is usually the one the asker introduced.",
                    "If the model never disagrees with you, it is an echo rather than analysis."]),

        // ---- 26 regression-adequacy -------------------------------------------
        S("s26.01", "regression-adequacy", 26, AutoKind.Observe,
            ["Roslyn is a static analyser: it reads the code but never runs it.",
             "It can name every branch predicate, but not which ones the test data reaches, and not whether the candidate still agrees with production."],
            mustNot: ["that static analysis alone establishes regression safety"]),
        S("s26.02", "regression-adequacy", 26, AutoKind.Observe,
            ["The production version is the golden baseline; the candidate is the QA build.",
             "Both are executed against the same condition data and their responses compared.",
             "A difference matching a signed-off rule is intended; a difference matching none is a regression."]),
        S("s26.03", "regression-adequacy", 26, AutoKind.Scroll, selector: "[data-auto='s26.vectors']",
            facts: ["This is the permutation data QA maintains, one row per combination of conditions.",
                    "Coverage of the logic space is a property of this file, not of the code."]),
        S("s26.04", "regression-adequacy", 26, AutoKind.Click, selector: "[data-auto='s26.mutate']",
            wait: AutoWait.Element, waitKey: "[data-auto='s26.audit-result']", timeout: 240, result: true,
            needsExec: true,
            facts: ["Both versions are compiled and executed over every row, then one deliberate defect at a time is injected into the candidate."]),
        S("s26.05", "regression-adequacy", 26, AutoKind.Observe, result: true,
            facts: ["The comparison classifies every row against production.",
                    "Rows needing adjudication are ones where a signed-off rule applies but behaviour did not change."],
            mustNot: ["that an unexplained difference is definitely a bug"]),
        S("s26.06", "regression-adequacy", 26, AutoKind.Observe, result: true,
            facts: ["Condition coverage shows which predicates the data drove both true and false.",
                    "A predicate driven only one way is a gap in the data, not in the assertions."]),
        S("s26.07", "regression-adequacy", 26, AutoKind.Observe, result: true,
            facts: ["The mutation score is a separate signal: would a test have noticed if a line were wrong?",
                    "Survivors are defects the suite would have shipped."],
            mustNot: ["that the mutation score proves the code is correct"]),
        S("s26.08", "regression-adequacy", 26, AutoKind.Click, selector: "[data-auto='s26.guidance']", bridge: true,
            wait: AutoWait.State, waitKey: "RegressionGuidance", timeout: 300,
            facts: ["Gemma proposes tests that close the named survivors, not general extra coverage.",
                    "Claude then checks whether each proposed test would actually kill what it claims."],
            mustNot: ["that the proposed tests have been run"]),

        // ---- 27 ephemeral-test -------------------------------------------------
        S("s27.01", "ephemeral-test", 27, AutoKind.Observe,
            ["The ticket carries contact details, an account reference, a taxpayer id, a connection string, and a token.",
             "It is realistic on purpose, because that is what makes it both useful and dangerous."]),
        S("s27.02", "ephemeral-test", 27, AutoKind.Click, selector: "[data-auto='s27.run']", bridge: true,
            wait: AutoWait.State, waitKey: "Ephemeral", timeout: 240,
            facts: ["The deterministic gate runs before anything leaves the boundary.",
                    "The model receives only the scrubbed copy."]),
        S("s27.03", "ephemeral-test", 27, AutoKind.Observe, result: true,
            facts: ["Not checked in is not the same as not sent.",
                    "The audit record keeps references rather than payloads."]),

        // ---- 28 portfolio-tiers -------------------------------------------------
        S("s28.01", "portfolio-tiers", 28, AutoKind.Click, selector: "[data-auto='s28.load']",
            wait: AutoWait.Element, waitKey: "[data-auto='s28.estate']", timeout: 20, result: true,
            facts: ["Tier one is the Cortex view: the services in the estate and what each is for."]),
        S("s28.02", "portfolio-tiers", 28, AutoKind.Click, selector: "[data-auto='s28.amp-APM-1042']",
            facts: ["Tier two is the APM record, which bounds what a team actually owns."]),
        S("s28.03", "portfolio-tiers", 28, AutoKind.Click, selector: "[data-auto='s28.amp-APM-1187']",
            facts: ["The digital-assets record carries the money-services perimeter.",
                    "That perimeter is the documented reason crypto is out of scope for this change."]),
        S("s28.04", "portfolio-tiers", 28, AutoKind.Click, selector: "[data-auto='s28.tiers']", bridge: true,
            wait: AutoWait.State, waitKey: "PortfolioTiers", timeout: 540,
            facts: ["Tier one finds the candidate services, tier two settles scope, and only tier three can name a file."]),
        S("s28.05", "portfolio-tiers", 28, AutoKind.Observe, result: true,
            facts: ["All three order-handling services depend on one shared pipeline library.",
                    "Loading the tiers in the wrong order either misses a service or buries the model in context."]),

        // ---- 29 common-code ------------------------------------------------------
        S("s29.01", "common-code", 29, AutoKind.Click, selector: "[data-auto='s29.analyse']",
            wait: AutoWait.Element, waitKey: "[data-auto='s29.clusters']", timeout: 260, result: true,
            facts: ["Embedding similarity is run over three real trading repositories.",
                    "It finds duplication by meaning rather than by name."]),
        S("s29.02", "common-code", 29, AutoKind.Observe,
            ["The similarity threshold was set by calibration against measured distributions, not guessed.",
             "The margin between duplicates and unrelated code is narrow and specific to this corpus."],
            mustNot: ["that this threshold transfers to other codebases"]),
        S("s29.03", "common-code", 29, AutoKind.Click, selector: "[data-auto='s29.consolidate']", bridge: true,
            wait: AutoWait.State, waitKey: "Consolidation", timeout: 900,
            facts: ["Claude designs the consolidation and Gemma writes each module from that design.",
                    "The work is split into one task per module because a single large structured response exceeded the output cap."]),
        S("s29.04", "common-code", 29, AutoKind.Observe, result: true,
            facts: ["Per-module results are shown, including any module that failed.",
                    "The expensive model was billed for judgement rather than for typing."]),

        // ---- 30 token-economics ---------------------------------------------------
        S("s30.01", "token-economics", 30, AutoKind.Click, selector: "[data-auto='s30.run']",
            wait: AutoWait.Element, waitKey: "[data-auto='s30.result']", timeout: 20, result: true,
            facts: ["The model compares cost per attempt against cost per successful outcome."]),
        S("s30.02", "token-economics", 30, AutoKind.Observe,
            ["The rates used here are illustrative, not a CES rate card.",
             "A model that fails a third of the time is billed for every failure, including the retries."],
            mustNot: ["that these are real CES prices"]),

        // ---- 31 pattern-library ----------------------------------------------------
        S("s31.01", "pattern-library", 31, AutoKind.Observe,
            ["The submission in the box is a pre-filled worked example."]),
        S("s31.02", "pattern-library", 31, AutoKind.Click, selector: "[data-auto='s31.submit']", bridge: true,
            wait: AutoWait.State, waitKey: "PatternRun", timeout: 300,
            facts: ["A deterministic admission check runs first, looking for evidence, preconditions, and known failure modes.",
                    "Gemma applies the change only if the submission is admitted, and Claude then reviews it."]),
        S("s31.03", "pattern-library", 31, AutoKind.Observe, result: true,
            facts: ["A shared library scales whatever is put into it, including the mistakes.",
                    "That is why admission rests on evidence rather than enthusiasm."]),

        // ---- 32 plan-first ------------------------------------------------------------
        S("s32.01", "plan-first", 32, AutoKind.Click, selector: "[data-auto='s32.review']",
            wait: AutoWait.Element, waitKey: "[data-auto='s32.result']", timeout: 20, result: true,
            facts: ["The plan is scored deterministically for an executive summary, a diagram, risks, and a verification path."]),
        S("s32.02", "plan-first", 32, AutoKind.Observe,
            ["Jumping straight to an implementation is the habit this gate exists to break."]),

        // ---- 33 leadership-map ---------------------------------------------------------
        S("s33.01", "leadership-map", 33, AutoKind.Observe,
            ["Six acts, each answering a question leadership actually asks, and each ending in a number.",
             "This is the order to walk the programme in, not the order it was built in."]),
        S("s33.02", "leadership-map", 33, AutoKind.Observe,
            ["The auto-run does not follow these links, because every scene has already been run."]),

        // ---- 34 crypto-sme --------------------------------------------------------------
        S("s34.01", "crypto-sme", 34, AutoKind.Observe,
            ["The ticket is a spot crypto execution adapter against a specific venue."]),
        S("s34.02", "crypto-sme", 34, AutoKind.Click, selector: "[data-auto='s34.run']", bridge: true,
            wait: AutoWait.State, waitKey: "CryptoSme", timeout: 600,
            facts: ["Claude answers unaided first, then consults a Gemma grounded on vendor documentation, then designs.",
                    "The improvement is measured against verified facts rather than asserted."]),
        S("s34.03", "crypto-sme", 34, AutoKind.Observe, result: true,
            facts: ["The unaided arm is scored on its attempt only.",
                    "Crediting the uncertainty list would reward naming a fact the model has just said it cannot recall."]),
        S("s34.04", "crypto-sme", 34, AutoKind.Observe, result: true,
            facts: ["The trace follows each fact through unaided, asked, supplied, and used.",
                    "Delegation only retrieves what the delegator thought to ask.",
                    "Keyword recall measures vocabulary, not correctness."],
            mustNot: ["that a higher recall score means the design is correct"])
    ];

    public static IEnumerable<AutoStep> ForProfile(AutoProfile profile, bool allowInProcessExecution) =>
        Steps.Where(step =>
            (profile == AutoProfile.Full || !step.BridgeRequired) &&
            (allowInProcessExecution || !step.RequiresInProcessExecution));

    public static IReadOnlyList<string> Selectors() =>
        Steps.Where(step => step.Selector is not null)
             .Select(step => step.Selector!)
             .Concat(Steps.Where(step => step.WaitType == AutoWait.Element && step.WaitKey is not null)
                          .Select(step => step.WaitKey!))
             .Distinct()
             .ToList();

    /// <summary>Extracts the data-auto token from a selector of the form [data-auto='x'].</summary>
    public static string? TokenOf(string selector)
    {
        var start = selector.IndexOf('\'');
        var end = selector.LastIndexOf('\'');
        return start >= 0 && end > start ? selector[(start + 1)..end] : null;
    }
}
