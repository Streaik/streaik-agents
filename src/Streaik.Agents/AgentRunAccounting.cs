using System.Globalization;
using System.Text.Json;
using Streaik.Responses;
using Streaik.Vercel.Models;

namespace Streaik.Agents;

// One per endpoint run. Observe gateway completions, not framework usage updates, which may be aggregates.
internal sealed class AgentRunAccounting
{
    private readonly object _gate = new();
    private long? _input, _output, _total, _cached, _cacheWrite, _reasoning;
    private decimal? _cost;
    private bool _costOverflow;

    public void Observe(ResponseResult result)
    {
        var gateway = result.Metadata?.GetValueOrDefault("gateway") is JsonElement value ? value : default;
        lock (_gate)
        {
            AddCost(gateway);
            if (result.NormalizedUsage is not { } usage) return;
            Add(ref _input, usage.InputTokens);
            Add(ref _output, usage.OutputTokens);
            Add(ref _total, usage.TotalTokens);
            Add(ref _cached, usage.InputTokensDetails?.CachedTokens);
            Add(ref _cacheWrite, usage.InputTokensDetails?.CacheWriteTokens);
            Add(ref _reasoning, usage.OutputTokensDetails?.ReasoningTokens);
        }
    }

    public void ObserveDecision(DecisionResponse result)
    {
        lock (_gate) AddCost(result.ProviderMetadata?.GetValueOrDefault("gateway") ?? default);
    }

    public (ResponseUsage? Usage, decimal? Cost) Snapshot()
    {
        lock (_gate)
        {
            var input = Count(_input);
            var output = Count(_output);
            var total = Count(_total);
            var cached = Count(_cached);
            var cacheWrite = Count(_cacheWrite);
            var reasoning = Count(_reasoning);
            var usage = input is null && output is null && total is null && cached is null && cacheWrite is null && reasoning is null
                ? null : new ResponseUsage
                {
                    InputTokens = input, OutputTokens = output, TotalTokens = total,
                    InputTokensDetails = cached is not null || cacheWrite is not null
                        ? new() { CachedTokens = cached, CacheWriteTokens = cacheWrite } : null,
                    OutputTokensDetails = reasoning is not null ? new() { ReasoningTokens = reasoning } : null
                };
            return (usage, _costOverflow ? null : _cost);
        }
    }

    private static void Add(ref long? sum, int? count)
    {
        if (count is >= 0) sum = (sum ?? 0) + count.Value;
    }

    private static int? Count(long? sum) => sum is <= int.MaxValue ? (int?)sum : null;

    private void AddCost(JsonElement gateway)
    {
        if (_costOverflow || gateway.ValueKind != JsonValueKind.Object || !gateway.TryGetProperty("cost", out var value)) return;
        decimal? amount = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
        if (amount is not { } cost || cost < 0) return;
        var prior = _cost ?? 0;
        if (cost > decimal.MaxValue - prior) _costOverflow = true;
        else _cost = prior + cost;
    }
}
