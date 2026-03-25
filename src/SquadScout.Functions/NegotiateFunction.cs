using System.Net;
using System.Text.Json;
using Azure;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using SquadScout.Contracts.Realtime;
using SquadScout.Functions.Negotiation;

namespace SquadScout.Functions;

public sealed class NegotiateFunction
{
    private readonly NegotiationIdentityResolver _identityResolver;
    private readonly WebPubSubNegotiationService _negotiationService;
    private readonly ILogger<NegotiateFunction> _logger;

    public NegotiateFunction(
        NegotiationIdentityResolver identityResolver,
        WebPubSubNegotiationService negotiationService,
        ILogger<NegotiateFunction> logger)
    {
        _identityResolver = identityResolver;
        _negotiationService = negotiationService;
        _logger = logger;
    }

    [Function("Negotiate")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "negotiate")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        PubSubNegotiateRequest? negotiateRequest;

        try
        {
            negotiateRequest = await request.ReadFromJsonAsync<PubSubNegotiateRequest>(cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return await WriteErrorAsync(request, HttpStatusCode.BadRequest, "The negotiate request body must be valid JSON.", cancellationToken);
        }

        if (negotiateRequest is null)
        {
            return await WriteErrorAsync(request, HttpStatusCode.BadRequest, "The negotiate request body is required.", cancellationToken);
        }

        if (!PubSubNegotiateRequestValidator.TryValidate(negotiateRequest, out var validationError))
        {
            return await WriteErrorAsync(request, HttpStatusCode.BadRequest, validationError, cancellationToken);
        }

        if (!_identityResolver.TryResolve(request.Url, request.Headers, out var identity, out var authFailureStatus, out var authFailureMessage))
        {
            _logger.LogWarning(
                "Rejected negotiate request with status {StatusCode}: {FailureMessage}",
                authFailureStatus,
                authFailureMessage);
            return await WriteErrorAsync(request, authFailureStatus, authFailureMessage, cancellationToken);
        }

        try
        {
            var response = await _negotiationService.NegotiateAsync(negotiateRequest, identity, cancellationToken);
            _logger.LogInformation(
                "Issued Web PubSub access for {ParticipantKind} on {ProjectId}/{SessionId} with group {SessionGroup} for {UserId}. Development identity: {IsDevelopmentIdentity}.",
                response.ParticipantKind,
                response.ProjectId,
                response.SessionId,
                response.SessionGroup,
                response.UserId,
                identity.IsDevelopment);

            var httpResponse = request.CreateResponse(HttpStatusCode.OK);
            await httpResponse.WriteAsJsonAsync(response, cancellationToken);
            return httpResponse;
        }
        catch (AuthenticationFailedException exception)
        {
            _logger.LogError(exception, "Azure credential resolution failed while issuing a Web PubSub access URI.");
            return await WriteErrorAsync(
                request,
                HttpStatusCode.ServiceUnavailable,
                "Azure Web PubSub token issuance is unavailable. Check managed identity or local Azure credentials.",
                cancellationToken);
        }
        catch (RequestFailedException exception)
        {
            _logger.LogError(exception, "Azure Web PubSub rejected negotiate token issuance.");
            return await WriteErrorAsync(
                request,
                HttpStatusCode.BadGateway,
                "Azure Web PubSub token issuance failed. Check service configuration and permissions.",
                cancellationToken);
        }
    }

    private static async Task<HttpResponseData> WriteErrorAsync(
        HttpRequestData request,
        HttpStatusCode statusCode,
        string error,
        CancellationToken cancellationToken)
    {
        var response = request.CreateResponse(statusCode);
        await response.WriteAsJsonAsync(new { error }, cancellationToken);
        return response;
    }
}
