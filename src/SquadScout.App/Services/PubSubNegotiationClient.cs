using System.Net.Http.Json;
using System.Text.Json;
using SquadScout.App.Configuration;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Realtime;
using SquadScout.Contracts.Sessions;

namespace SquadScout.App.Services;

public interface IPubSubNegotiationClient
{
    Task<PubSubNegotiateResponse> NegotiateAsync(SessionDescriptor session, CancellationToken cancellationToken = default);
}

public sealed class PubSubNegotiationClient : IPubSubNegotiationClient
{
    private const string DevelopmentUserHeaderName = "x-squadscout-dev-user";
    private const string DevelopmentDisplayNameHeaderName = "x-squadscout-dev-name";

    private readonly IAuthenticationService _authenticationService;
    private readonly HttpClient _httpClient;
    private readonly MessagingOptions _messagingOptions;

    public PubSubNegotiationClient(
        HttpClient httpClient,
        MessagingOptions messagingOptions,
        IAuthenticationService authenticationService)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _messagingOptions = messagingOptions ?? throw new ArgumentNullException(nameof(messagingOptions));
        _authenticationService = authenticationService ?? throw new ArgumentNullException(nameof(authenticationService));
    }

    public async Task<PubSubNegotiateResponse> NegotiateAsync(SessionDescriptor session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!Uri.TryCreate(_messagingOptions.NegotiateUrl, UriKind.Absolute, out var negotiateUri))
        {
            throw new InvalidOperationException(
                $"Messaging:NegotiateUrl '{_messagingOptions.NegotiateUrl}' is not a valid absolute URI.");
        }

        var identity = await _authenticationService.GetCurrentIdentityAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, negotiateUri)
        {
            Content = JsonContent.Create(
                new PubSubNegotiateRequest
                {
                    ProjectId = session.ProjectId,
                    SessionId = session.SessionId,
                    ParticipantKind = PubSubParticipantKind.Client
                },
                options: SessionMessageSerializer.DefaultOptions)
        };

        if (negotiateUri.IsLoopback &&
            string.Equals(identity.Mode, "LocalDevelopment", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation(DevelopmentUserHeaderName, identity.RequestedBy);
            request.Headers.TryAddWithoutValidation(
                DevelopmentDisplayNameHeaderName,
                string.IsNullOrWhiteSpace(identity.DisplayName) ? identity.RequestedBy : identity.DisplayName);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorDetail = await TryReadErrorDetailAsync(response, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Negotiate failed with {(int)response.StatusCode} ({response.ReasonPhrase}). {errorDetail}".Trim());
        }

        var negotiateResponse = await response.Content
            .ReadFromJsonAsync<PubSubNegotiateResponse>(SessionMessageSerializer.DefaultOptions, cancellationToken)
            .ConfigureAwait(false);

        if (negotiateResponse is null ||
            string.IsNullOrWhiteSpace(negotiateResponse.Url) ||
            string.IsNullOrWhiteSpace(negotiateResponse.Hub) ||
            string.IsNullOrWhiteSpace(negotiateResponse.SessionGroup))
        {
            throw new InvalidOperationException("Negotiate completed, but the response did not contain a usable Web PubSub connection payload.");
        }

        return negotiateResponse;
    }

    private static async Task<string> TryReadErrorDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return "The negotiate endpoint did not return an error body.";
        }

        try
        {
            var error = JsonSerializer.Deserialize<NegotiateErrorResponse>(body, SessionMessageSerializer.DefaultOptions);
            if (!string.IsNullOrWhiteSpace(error?.Error))
            {
                return error.Error.Trim();
            }
        }
        catch (JsonException)
        {
        }

        return body.Trim();
    }

    private sealed record NegotiateErrorResponse
    {
        public string? Error { get; init; }
    }
}
