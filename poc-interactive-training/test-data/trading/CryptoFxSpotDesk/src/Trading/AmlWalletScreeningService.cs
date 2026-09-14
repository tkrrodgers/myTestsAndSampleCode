using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CryptoFxSpotDesk.Pipeline;

namespace CryptoFxSpotDesk.Trading;

/// <summary>
/// Global anti-money-laundering wallet screening. Every crypto order's
/// counterparty wallet is screened against OFAC/sanctions address lists and
/// scored for on-chain risk (mixer/tumbler exposure, darknet-market proximity,
/// and hop distance to a flagged cluster). Orders touching a sanctioned address
/// are blocked outright; orders above the risk threshold are escalated for
/// enhanced due diligence. Fiat FX legs are screened by counterparty
/// jurisdiction. This runs on every order because the desk settles instantly and
/// there is no post-trade window to claw a transfer back.
/// </summary>
public sealed class AmlWalletScreeningService : IOrderStage
{
    private static readonly HashSet<string> SanctionedAddresses = new(StringComparer.OrdinalIgnoreCase)
    {
        "1BadActorSanctionedWalletAddrXXXXXXX",
        "0xdeadbeefsanctionedwalletaddress0001",
    };

    private static readonly HashSet<string> HighRiskJurisdictions = new(StringComparer.OrdinalIgnoreCase)
    {
        "KP", "IR", "SY", "CU",
    };

    private const double EscalationThreshold = 0.75;

    public string Name => "risk";

    public Task<StageResult> ExecuteAsync(OrderContext context, CancellationToken cancellationToken)
    {
        var order = context.Order;
        var isCrypto = order.Attr("assetClass").Equals("CRYPTO", StringComparison.OrdinalIgnoreCase);

        if (isCrypto)
        {
            var wallet = order.Attr("counterpartyWallet", order.Attr("destinationWallet"));
            if (!string.IsNullOrEmpty(wallet) && SanctionedAddresses.Contains(wallet))
            {
                return Task.FromResult(StageResult.Fail($"wallet {Mask(wallet)} is on the sanctions list — blocked"));
            }

            var riskScore = ScoreWallet(wallet, order);
            context.State["amlRiskScore"] = riskScore.ToString("F3", CultureInfo.InvariantCulture);
            if (riskScore >= EscalationThreshold)
            {
                return Task.FromResult(StageResult.Fail(
                    $"wallet {Mask(wallet)} risk score {riskScore:F2} exceeds EDD threshold"));
            }

            context.Record("risk", $"AML screen clear for {Mask(wallet)} (risk {riskScore:F2})");
            return Task.FromResult(StageResult.Ok());
        }

        var jurisdiction = order.Attr("counterpartyJurisdiction", "US").ToUpperInvariant();
        if (HighRiskJurisdictions.Contains(jurisdiction))
        {
            return Task.FromResult(StageResult.Fail($"counterparty jurisdiction {jurisdiction} is sanctioned"));
        }

        context.Record("risk", $"AML screen clear (FX jurisdiction {jurisdiction})");
        return Task.FromResult(StageResult.Ok());
    }

    private static double ScoreWallet(string wallet, OrderEvent order)
    {
        if (string.IsNullOrEmpty(wallet))
        {
            return 0d;
        }

        // Deterministic pseudo on-chain heuristic: hash-derived base risk, then
        // amplified by declared mixer exposure and hop distance to a flagged cluster.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(wallet));
        var baseRisk = hash[0] / 255d * 0.4d;
        var mixerExposure = (double)order.AttrDecimal("mixerExposure");         // 0..1
        var hops = (double)order.AttrDecimal("hopsToFlaggedCluster", 6m);
        var proximity = hops <= 0 ? 1d : Math.Min(1d, 1d / hops);
        return Math.Clamp(baseRisk + 0.4d * mixerExposure + 0.4d * proximity, 0d, 1d);
    }

    private static string Mask(string wallet)
        => string.IsNullOrEmpty(wallet) || wallet.Length <= 8
            ? "****"
            : $"{wallet[..4]}…{wallet[^4..]}";
}
