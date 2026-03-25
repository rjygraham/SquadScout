using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SquadScout.Contracts.Messages;

namespace SquadScout.Functions.Upstream;

public sealed class BrokerInputForwarder
{
    private readonly HttpClient _httpClient;

    public BrokerInputForwarder(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<BrokerForwardResult> ForwardAsync(
        MessageEnvelope<InputChunkPayload> envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                    $"/api/sessions/{Uri.EscapeDataString(envelope.SessionId)}/input",
                    envelope,
                    SessionMessageSerializer.DefaultOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            var body = response.Content is null
                ? null
                : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return new BrokerForwardResult(
                response.StatusCode,
                string.IsNullOrWhiteSpace(body) ? null : body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return Failure(
                HttpStatusCode.GatewayTimeout,
                "The broker input endpoint did not respond before the upstream request timed out.");
        }
        catch (HttpRequestException)
        {
            return Failure(
                HttpStatusCode.ServiceUnavailable,
                "The broker input endpoint is unavailable.");
        }
    }

    private static BrokerForwardResult Failure(HttpStatusCode statusCode, string error) =>
        new(
            statusCode,
            JsonSerializer.Serialize(new { error }, SessionMessageSerializer.DefaultOptions));
}

public sealed record BrokerForwardResult(HttpStatusCode StatusCode, string? Body)
{
    public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and < 300;
}
