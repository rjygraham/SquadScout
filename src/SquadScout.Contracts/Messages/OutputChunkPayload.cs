using SquadScout.Contracts.Security;

namespace SquadScout.Contracts.Messages;

public sealed record OutputChunkPayload
{
    public string Content { get; init; } = string.Empty;

    public bool IsError { get; init; }

    public override string ToString() =>
        $"OutputChunkPayload {{ ContentLength = {Content.Length}, IsError = {IsError}, DiagnosticPreview = {SecretRedactor.RedactedValue} }}";
}
