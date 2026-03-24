using SquadScout.Contracts.Security;

namespace SquadScout.Contracts.Messages;

public sealed record InputChunkPayload
{
    public string Content { get; init; } = string.Empty;

    public override string ToString() =>
        $"InputChunkPayload {{ ContentLength = {Content.Length}, DiagnosticPreview = {SecretRedactor.RedactedValue} }}";
}
