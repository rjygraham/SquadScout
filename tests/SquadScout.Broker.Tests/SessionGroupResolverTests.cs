using SquadScout.Broker.Relay;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests;

public sealed class SessionGroupResolverTests
{
    private readonly SessionGroupResolver _resolver = new();

    [Fact]
    public void ResolveSessionDescriptorUsesApprovedBaseSessionGroup()
    {
        var group = _resolver.Resolve(new SessionDescriptor
        {
            ProjectId = "proj-01",
            SessionId = "session-abc"
        });

        Assert.Equal("session:proj-01:session-abc", group);
    }

    [Fact]
    public void ResolveEnvelopeUsesApprovedBaseSessionGroup()
    {
        var group = _resolver.Resolve(new MessageEnvelope<InputChunkPayload>
        {
            ProjectId = "proj-01",
            SessionId = "session-abc",
            MessageType = SessionMessageType.Input,
            Direction = MessageDirection.ClientToBroker,
            Payload = new InputChunkPayload
            {
                Content = "status"
            }
        });

        Assert.Equal("session:proj-01:session-abc", group);
    }

    [Fact]
    public void ResolveRejectsUnsafeSegments()
    {
        var exception = Assert.Throws<ArgumentException>(() => _resolver.Resolve(new SessionDescriptor
        {
            ProjectId = "proj:01",
            SessionId = "session-abc"
        }));

        Assert.Contains("projectId", exception.Message, StringComparison.Ordinal);
    }
}
