namespace PocInteractiveTraining.Server.Services;

// Grounding pack for the Crypto SME demonstration. Every fact in the "exchange mechanics" layer was
// taken from the Binance and CCXT documentation fetched on 2026-09-06 and is cited to its source. The
// firm layer is synthetic and labelled as such.
//
// The point of this scene is that these are the facts a model answering from memory gets subtly wrong:
// header names without their interval suffix, recvWindow without the one-second forward bound, per-IP
// versus per-account limits, and a timeout that means "unknown", not "failed".
public static class CryptoSmeCorpus
{
    public const string Jira = """
        CRY-4417: Add spot crypto execution to the order pipeline

        We are extending the trading estate to place spot crypto orders on a public exchange
        (Binance first, more venues later). Design the execution adapter.

        Requirements:
        1. Place, cancel and query spot orders against the venue's REST API.
        2. Stream order and fill updates rather than polling for them.
        3. Never get the firm's egress IP rate-limited or banned.
        4. An order must never be silently duplicated, including after a network failure.
        5. Order quantities and prices must always be accepted by the venue on first submission.
        6. Support additional venues later without rewriting the adapter.

        Deliverable: the design. Name the concrete mechanisms, headers, parameters and failure
        handling you would implement, and state what you are unsure of.
        """;

    // Layer 1 — the unified abstraction layer.
    private const string LayerCcxt = """
        === LAYER 1: UNIFIED CROSS-EXCHANGE EXECUTION (CCXT) ===
        Source: https://docs.ccxt.com/ and https://docs.ccxt.com/docs (retrieved 2026-09-06)

        CCXT is the de facto standard library for crypto exchange connectivity. It provides ONE unified
        API across 100+ exchanges and prediction markets, so venue-specific code is confined to
        configuration rather than spread through the application.

        - Available for JavaScript, Python, PHP, C#, Go, Java and Rust. A .NET service can use it directly.
        - The unified surface covers markets, tickers, order books, orders and balances. Identical
          response shape from every venue: fetchTicker('BTC/USDT') returns the same structure on Binance,
          Bybit, OKX, KuCoin or Hyperliquid.
        - CCXT Pro is the WebSocket streaming layer: watchTicker, watchOrderBook, watchTrades,
          watchOrders. Use this for order and fill updates instead of polling REST.
        - docs.ccxt.com/docs/base-spec lists every unified method and which exchanges implement it.
          Check this before assuming a method exists on a given venue — coverage is not uniform.
        - Prediction markets (Polymarket, Kalshi, Limitless, Myriad, Hyperliquid) are reachable through
          the same unified API.
        - CCXT now ships an installable skills bundle for AI agents (install-skills.sh).

        Architectural consequence: build the adapter against the CCXT unified interface, not against
        Binance's REST shape. Venue-specific behaviour then lives behind one seam.
        """;

    // Layer 2 — verified exchange mechanics. This is where memory-based answers go wrong.
    private const string LayerExchange = """
        === LAYER 2: EXCHANGE INFRASTRUCTURE MECHANICS (BINANCE SPOT) ===
        Source: https://developers.binance.com/en/docs and the binance-spot-api-docs repository,
        rest-api.md and filters.md (retrieved 2026-09-06)

        --- Rate limiting: weight, not request count ---
        - Every response carries X-MBX-USED-WEIGHT-(intervalNum)(intervalLetter), for example
          X-MBX-USED-WEIGHT-1M. The interval suffix is part of the header name. A client reading a bare
          "X-MBX-USED-WEIGHT" header will find nothing.
          intervalLetter: S = second, M = minute, H = hour, D = day.
        - Each endpoint has its own weight, and the weight often depends on the parameters:
            GET /api/v3/depth        5 (limit 1-100), 25 (101-500), 50 (501-1000), 250 (1001-5000)
            GET /api/v3/ticker/24hr  2 for one symbol, but 80 when the symbol parameter is OMITTED
            GET /api/v3/openOrders   6 for one symbol, but 80 when the symbol parameter is OMITTED
            GET /api/v3/exchangeInfo 20
            GET /api/v3/account      20
            POST /api/v3/order       1
        - HTTP 429 = rate limit exceeded. HTTP 418 = the IP has been AUTO-BANNED for continuing to send
          requests after 429s. Bans scale with repeat offences, from 2 minutes to 3 days.
        - Both 418 and 429 return a Retry-After header giving the number of seconds to wait.
        - RATE LIMITS ARE BASED ON THE IP, NOT THE API KEY. Every service instance behind the same egress
          IP shares one budget. This is the single most commonly mis-designed control.
        - The unfilled order count is different: X-MBX-ORDER-COUNT-(intervalNum)(intervalLetter), and it
          is tracked PER ACCOUNT, not per IP.
        - HTTP 403 means a Web Application Firewall rule was violated. Binance explicitly warns to avoid
          SQL keywords anywhere in a request, because the WAF may block it.

        --- Timing and signatures ---
        - SIGNED requests need a timestamp. recvWindow is optional and DEFAULTS TO 5000 ms. The maximum
          is 60000 ms, and Binance explicitly recommends 5000 or less.
        - recvWindow supports up to three decimal places (e.g. 6000.346) so microseconds can be expressed.
        - The server accepts the request only if BOTH hold:
              timestamp < serverTime + 1 second
              serverTime - timestamp <= recvWindow
          The forward bound is a fixed ONE SECOND and recvWindow does not widen it. A client clock running
          more than a second FAST is rejected no matter how large recvWindow is. Synchronise against
          GET /api/v3/time; do not just enlarge recvWindow.
        - Three key types are supported: HMAC-SHA256, RSA (PKCS#8) and Ed25519. Binance states Ed25519
          gives the best performance and security. HMAC signatures are case-INsensitive; RSA and Ed25519
          signatures are case-SENSITIVE.
        - Timestamps are milliseconds by default. Send X-MBX-TIME-UNIT: MICROSECOND for microseconds.

        --- Order correctness: filters come from the venue, not from config ---
        Filters are published per symbol by GET /api/v3/exchangeInfo and MUST be applied before submitting:
        - LOT_SIZE     — quantity >= minQty, <= maxQty, and quantity % stepSize == 0
        - PRICE_FILTER — price >= minPrice, <= maxPrice, and price % tickSize == 0
        - NOTIONAL     — minNotional <= price * quantity <= maxNotional (supersedes MIN_NOTIONAL)
        - PERCENT_PRICE_BY_SIDE — separate bid and ask multipliers around a reference price
        - MARKET_LOT_SIZE, MAX_NUM_ORDERS, MAX_NUM_ALGO_ORDERS, MAX_POSITION, TRAILING_DELTA
        - MAX_ASSET is an ASSET-level filter that is NOT returned by exchangeInfo. It is only visible via
          GET /api/v3/myFilters, and it caps the quantity or notional of a single order for that asset.
        Modulo checks on decimals are exact-arithmetic operations. Binary floating point will fail them
        intermittently and the venue will reject the order.
        MARKET orders using quoteOrderQty are exempt from LOT_SIZE.

        --- Idempotency and the unknown-outcome case ---
        - The API times out after 10 seconds and returns error -1007 TIMEOUT, whose message states
          "Send status unknown; execution status unknown."
        - Binance is explicit: an HTTP 5XX or a -1007 MUST NOT be treated as a failure. The order may have
          reached the matching engine. Blind retry duplicates the order.
        - The correct recovery is to query order status, ideally by a client-supplied newClientOrderId.
          An order with a reused newClientOrderId is accepted only after the previous one is filled,
          which makes it usable as an idempotency key.
        - Endpoints declare a data source: Matching Engine, Memory or Database, ordered by increasing
          staleness. After an unknown outcome, prefer the freshest source.
        - cancelReplace returns HTTP 409 when the cancel fails but the new order succeeds — a partial
          success that a naive client will read as an error.
        """;

    // Layer 3 — firm rules. Synthetic; this is the layer that would carry real internal policy.
    private const string LayerFirm = """
        === LAYER 3: FIRM-SPECIFIC RULES (SYNTHETIC — stands in for internal policy) ===

        - All monetary and quantity arithmetic uses decimal. Binary floating point is prohibited in any
          code path that touches a price, a quantity or a balance.
        - The digital-asset desk sits in a separate regulatory perimeter (APM-1187, MSB) from the
          broker-dealer estate. Controls are not shared across that boundary.
        - Every venue credential is issued per environment and per service. Two services never share an
          API key, because the account-level order-count budget cannot then be attributed.
        - Outbound venue traffic egresses through a dedicated NAT address per environment, since venue
          rate limits are per IP.
        - Every order carries a firm-generated client order id derived from the internal order id, so an
          unknown outcome is always recoverable by query rather than by retry.
        """;

    public static string GroundingPack() => string.Join("\n\n", LayerCcxt, LayerExchange, LayerFirm);

    public static readonly (string Label, string Url)[] Sources =
    [
        ("CCXT — unified API manual", "https://docs.ccxt.com/docs/manual"),
        ("CCXT — unified method spec", "https://docs.ccxt.com/docs/base-spec"),
        ("Binance — Spot REST API", "https://developers.binance.com/en/docs"),
        ("Binance — rest-api.md (limits, timing, security)", "https://github.com/binance/binance-spot-api-docs/blob/master/rest-api.md"),
        ("Binance — filters.md (LOT_SIZE, PRICE_FILTER, NOTIONAL, MAX_ASSET)", "https://github.com/binance/binance-spot-api-docs/blob/master/filters.md"),
        ("CoinMarketCap — developer guide", "https://coinmarketcap.com/academy/article/best-cryptocurrency-apis-in-2026-ultimate-developer-guide")
    ];

    // Sealed and deterministic. Each fact was verified against the source above, and each is the kind of
    // detail a model answering from memory states approximately rather than exactly.
    //
    // Signals are deliberately specific. Earlier versions matched bare "418", "unknown", "decimal" and
    // "5000", which any competent design prose contains incidentally — that inflated both scores and made
    // the comparison meaningless.
    public sealed record SmeFact(string Name, string Truth, IReadOnlyList<string> Signals, string Trap);

    public static IReadOnlyList<SmeFact> Facts { get; } =
    [
        new("Weight header carries the interval",
            "The header is X-MBX-USED-WEIGHT-(intervalNum)(intervalLetter), e.g. X-MBX-USED-WEIGHT-1M.",
            ["used-weight-1m", "used-weight-(interval", "intervalnum)(intervalletter", "x-mbx-used-weight-1"],
            "Reading a bare X-MBX-USED-WEIGHT header finds nothing."),
        new("418 is a ban, not a rate limit",
            "429 is the rate limit. 418 means the IP is already auto-banned, for 2 minutes up to 3 days.",
            ["418"],
            "Treating 418 as just another 429 keeps the client hammering a banned address."),
        new("Limits are per IP, not per API key",
            "Binance rate limits are applied to the IP. Every instance behind one egress address shares the budget.",
            ["based on the ip", "not the api key", "not per api key", "keyed on ip", "keyed on the ip", "per ip, not", "per-ip, not", "egress ip"],
            "Sizing the budget per key silently overruns it as instances scale out."),
        new("Order count is per account",
            "X-MBX-ORDER-COUNT-(intervalNum)(intervalLetter) is tracked per account, unlike the IP weight limit.",
            ["order-count", "order count"],
            "Conflating the two budgets hides one of them."),
        new("recvWindow default and bound",
            "recvWindow defaults to 5000 ms and cannot exceed 60000 ms; Binance recommends 5000 or less.",
            ["recvwindow"],
            "Assuming there is no default, or that a large window is safer."),
        new("The forward clock bound is one second",
            "Acceptance requires timestamp < serverTime + 1s AND serverTime - timestamp <= recvWindow. recvWindow does not widen the forward bound.",
            ["servertime + 1", "server time + 1", "clock ahead", "running fast", "forward bound", "one second ahead", "1 second ahead", "clock skew", "clock drift"],
            "A fast clock is rejected however large recvWindow is — enlarging it does not help."),
        new("Filters are fetched from the venue",
            "LOT_SIZE stepSize, PRICE_FILTER tickSize and NOTIONAL come from GET /api/v3/exchangeInfo per symbol.",
            ["exchangeinfo", "lot_size", "ticksize", "tick size", "stepsize", "step size"],
            "Hard-coding precision per symbol; it changes without notice."),
        new("MAX_ASSET is not in exchangeInfo",
            "The MAX_ASSET filter is only visible via GET /api/v3/myFilters, not exchangeInfo.",
            ["max_asset", "myfilters"],
            "An order can pass every exchangeInfo filter and still be rejected."),
        new("Timeout means unknown, not failed",
            "-1007 TIMEOUT and HTTP 5XX mean the execution status is UNKNOWN. The order may have reached the matching engine.",
            ["-1007", "status unknown", "execution status unknown", "outcome is unknown", "unknown outcome", "ambiguous outcome"],
            "Retrying on timeout duplicates a live order."),
        new("Idempotency via newClientOrderId",
            "A firm-generated newClientOrderId makes an unknown outcome recoverable by query rather than retry.",
            ["newclientorderid", "origclientorderid", "idempotenc"],
            "Without it there is no way to ask the venue what happened."),
        new("Decimal, never float",
            "Filter modulo checks are exact-arithmetic. Binary floating point fails them intermittently.",
            ["decimal arithmetic", "decimal type", "bignumber", "big decimal", "floating point", "never float", "not float", "binary float"],
            "Intermittent rejections that reproduce only at certain sizes."),
        new("CCXT as the venue seam",
            "CCXT gives one unified API over 100+ venues; CCXT Pro supplies WebSocket watchOrders streaming.",
            ["ccxt"],
            "Hand-rolling per-venue clients makes venue two a rewrite.")
    ];

    public static (int Score, List<string> Hit, List<string> Missed) ScoreFacts(string? text)
    {
        var lowered = (text ?? string.Empty).ToLowerInvariant();
        var hit = new List<string>();
        var missed = new List<string>();
        foreach (var fact in Facts)
        {
            if (Present(fact, lowered))
            {
                hit.Add(fact.Name);
            }
            else
            {
                missed.Add(fact.Name);
            }
        }

        return ((int)Math.Round(100.0 * hit.Count / Facts.Count), hit, missed);
    }

    public static bool Present(SmeFact fact, string loweredText) =>
        fact.Signals.Any(signal => loweredText.Contains(signal, StringComparison.Ordinal));

    // Follows each fact through the three stages so a flat score can be explained. A fact absent from the
    // final design because nobody asked about it is a different failure from one the SME got wrong.
    public static List<Models.SmeFactTrace> Trace(string? unaided, string? questions, string? smeAnswers, string? design)
    {
        var a = (unaided ?? string.Empty).ToLowerInvariant();
        var q = (questions ?? string.Empty).ToLowerInvariant();
        var s = (smeAnswers ?? string.Empty).ToLowerInvariant();
        var d = (design ?? string.Empty).ToLowerInvariant();

        return Facts.Select(fact =>
        {
            var inUnaided = Present(fact, a);
            var asked = Present(fact, q);
            var answered = Present(fact, s);
            var inDesign = Present(fact, d);
            var verdict = (inDesign, answered, asked, inUnaided) switch
            {
                (true, _, _, _) => "carried into the design",
                (false, true, _, _) => "the SME supplied it and the design dropped it",
                (false, false, true, _) => "asked about, but the SME did not supply it",
                (false, false, false, true) => "known unaided, then lost",
                _ => "never asked, never supplied, never used"
            };

            return new Models.SmeFactTrace(fact.Name, inUnaided, asked, answered, inDesign, verdict);
        }).ToList();
    }
}
