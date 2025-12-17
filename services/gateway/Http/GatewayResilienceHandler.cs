using Polly;

namespace WastingNoTime.HireFlow.Gateway.Http;

public sealed class GatewayResilienceHandler : DelegatingHandler
{
    private readonly IAsyncPolicy<HttpResponseMessage> _getPolicy;
    private readonly IAsyncPolicy<HttpResponseMessage> _writePolicy;

    public GatewayResilienceHandler(
        IAsyncPolicy<HttpResponseMessage> getPolicy,
        IAsyncPolicy<HttpResponseMessage> writePolicy)
    {
        _getPolicy = getPolicy;
        _writePolicy = writePolicy;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.Method;
        var isGetLike =
            method == HttpMethod.Get ||
            method == HttpMethod.Head ||
            method == HttpMethod.Options;

        var policy = isGetLike ? _getPolicy : _writePolicy;

        // Execute policy around the actual HTTP call
        request.Headers.TryAddWithoutValidation("x-hireflow-resilience", "1");

        return policy.ExecuteAsync(
            (ct) => base.SendAsync(request, ct),
            cancellationToken);
    }
}
