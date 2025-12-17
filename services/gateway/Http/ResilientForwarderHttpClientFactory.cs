using System.Collections.Concurrent;
using System.Net;
using Polly;
using Polly.Timeout;
using WastingNoTime.HireFlow.Gateway.Http;
using Yarp.ReverseProxy.Forwarder;

public sealed class ResilientForwarderHttpClientFactory : IForwarderHttpClientFactory
{
    private readonly ConcurrentDictionary<string, HttpMessageInvoker> _cache = new();

    public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context)
    {
        // clusterId is the right cache key for gateway policies
        var key = context.ClusterId ?? "default";

        return _cache.GetOrAdd(key, _ =>
        {
            var sockets = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(2) };
            var breaker = BuildBreaker(key);
            var retryGets = BuildRetryForGets();
            var timeoutGet =
                Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(3), TimeoutStrategy.Pessimistic);
            var timeoutPatch =
                Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(5), TimeoutStrategy.Pessimistic);

            // IMPORTANT: breaker should be OUTER so it short-circuits before retry/timeout when open
            var getPolicy = Policy.WrapAsync(breaker, retryGets, timeoutGet);
            var writePolicy = Policy.WrapAsync(breaker, timeoutPatch);

            var pipeline = new GatewayResilienceHandler(getPolicy, writePolicy) { InnerHandler = sockets };

            return new HttpMessageInvoker(pipeline, disposeHandler: false);
        });
    }

    private static IAsyncPolicy<HttpResponseMessage> BuildRetryForGets() =>
        Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .OrResult(r => r.StatusCode is HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout
                or HttpStatusCode.RequestTimeout)
            .WaitAndRetryAsync(2, attempt =>
                TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt)) +
                TimeSpan.FromMilliseconds(Random.Shared.Next(0, 150)));

    private static IAsyncPolicy<HttpResponseMessage> BuildBreaker(string cluster) =>
        Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .OrResult(r => r.StatusCode is HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout
                or HttpStatusCode.RequestTimeout)
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 10,
                durationOfBreak: TimeSpan.FromSeconds(20)
            );
}