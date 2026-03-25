using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using SquadScout.Functions.Upstream;

namespace SquadScout.Functions;

public sealed class WebPubSubUpstreamFunction
{
    private readonly WebPubSubUpstreamHandler _handler;

    public WebPubSubUpstreamFunction(WebPubSubUpstreamHandler handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    [Function("WebPubSubUpstream")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "options", Route = "webpubsub/upstream")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var result = await _handler.HandleAsync(
                request.Method,
                request.Headers,
                request.Body,
                cancellationToken)
            .ConfigureAwait(false);

        var response = request.CreateResponse(result.StatusCode);
        if (!string.IsNullOrWhiteSpace(result.AllowedOrigin))
        {
            response.Headers.Add(WebPubSubUpstreamHandler.WebHookAllowedOriginHeaderName, result.AllowedOrigin);
        }

        if (!string.IsNullOrWhiteSpace(result.ContentType))
        {
            response.Headers.Add("Content-Type", result.ContentType);
        }

        if (!string.IsNullOrWhiteSpace(result.Body))
        {
            await response.WriteStringAsync(result.Body, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }
}
