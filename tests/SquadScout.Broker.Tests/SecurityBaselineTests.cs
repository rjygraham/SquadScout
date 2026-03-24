using SquadScout.Broker.Pty;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Security;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests;

public sealed class SecurityBaselineTests
{
    [Fact]
    public void PtyInputSanitizerNormalizesLineEndingsAndAllowsTerminalControls()
    {
        var sanitized = PtyInputSanitizer.Sanitize("status\r\n\u001B[A\t\b");

        Assert.Equal("status\n\u001B[A\t\b", sanitized);
    }

    [Fact]
    public void PtyInputSanitizerRejectsUnsupportedControls()
    {
        var exception = Assert.Throws<ArgumentException>(() => PtyInputSanitizer.Sanitize("hello\0world"));

        Assert.Contains("U+0000", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PtyInputSanitizerRejectsOversizedWrites()
    {
        var oversized = new string('a', PtyInputSanitizer.DefaultMaxInputCharactersPerWrite + 1);

        var exception = Assert.Throws<ArgumentException>(() => PtyInputSanitizer.Sanitize(oversized));

        Assert.Contains("safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MockPtySessionStoresSanitizedInput()
    {
        var session = new MockPtySession(new PtySessionStartRequest
        {
            ProjectId = "broker",
            SessionId = "session-123"
        });

        _ = await session.ReadEventAsync();
        await session.WriteAsync("status\r\n");

        Assert.Equal(["status\n"], session.WrittenInputs);
    }

    [Fact]
    public void SecretRedactorRedactsSecretsAndConnectionDetails()
    {
        const string sample = "password=swordfish Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.signature https://user:pass@example.com/path?sig=abc&token=def AccessKey=top-secret ghp_1234567890abcdef";

        var redacted = SecretRedactor.Redact(sample);

        Assert.DoesNotContain("swordfish", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.signature", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("def", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_1234567890abcdef", redacted, StringComparison.Ordinal);
        Assert.Contains("password=[REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("Bearer [REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("https://[REDACTED]@example.com/path?sig=[REDACTED]&token=[REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("AccessKey=[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretRedactorLeavesOrdinaryTextAlone()
    {
        const string sample = "dotnet test --filter Replay";

        Assert.Equal(sample, SecretRedactor.Redact(sample));
    }

    [Fact]
    public void SensitivePayloadsAndEnvelopesDoNotExposeContentInToString()
    {
        var payload = new InputChunkPayload
        {
            Content = "password=swordfish"
        };

        var outputPayload = new OutputChunkPayload
        {
            Content = "ghp_1234567890abcdef",
            IsError = true
        };

        var envelope = new MessageEnvelope<InputChunkPayload>
        {
            ProjectId = "broker",
            SessionId = "session-123",
            MessageType = SessionMessageType.Input,
            Direction = MessageDirection.ClientToBroker,
            MessageId = "msg-123",
            CorrelationId = "corr-123",
            Payload = payload
        };

        Assert.DoesNotContain("swordfish", payload.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_1234567890abcdef", outputPayload.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("swordfish", envelope.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("password=", envelope.ToString(), StringComparison.Ordinal);
        Assert.Contains("PayloadType = InputChunkPayload", envelope.ToString(), StringComparison.Ordinal);
    }
}
