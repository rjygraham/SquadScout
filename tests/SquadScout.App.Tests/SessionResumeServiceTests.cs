using System.Text.Json;
using SquadScout.App.Services;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Projects;
using SquadScout.Contracts.Sessions;

namespace SquadScout.App.Tests;

public sealed class SessionResumeServiceTests
{
    [Fact]
    public async Task SaveAsync_ThenRestoreAsync_RehydratesSnapshotAndRecentTraffic()
    {
        var storagePath = CreateStoragePath();
        var initialState = new ActiveSessionState();
        var service = new SessionResumeService(storagePath, initialState);

        var persisted = new ActiveSessionResumeState
        {
            Snapshot = new ActiveSessionSnapshot(
                new RegisteredProject
                {
                    ProjectId = "squadscout",
                    DisplayName = "SquadScout",
                    RepositoryRoot = @"D:\GitHub\SquadScout"
                },
                new SessionDescriptor
                {
                    ProjectId = "squadscout",
                    SessionId = "session-restore",
                    State = SessionState.Running,
                    CreatedAtUtc = new DateTimeOffset(2026, 03, 25, 11, 30, 00, TimeSpan.Zero)
                },
                SessionActivationSource.Broker,
                "Persisted session."),
            Connection = new MessageConnectionResumeState
            {
                Generation = 2,
                AcknowledgedSequence = 5
            },
            RecentTraffic =
            [
                new MessageEnvelopeTraffic
                {
                    Direction = MessageTrafficDirection.Incoming,
                    Summary = "output",
                    Envelope = ToJsonEnvelope(new MessageEnvelope<OutputChunkPayload>
                    {
                        ProjectId = "squadscout",
                        SessionId = "session-restore",
                        Generation = 2,
                        MessageType = SessionMessageType.Output,
                        Direction = MessageDirection.BrokerToClient,
                        Sequence = 5,
                        TimestampUtc = DateTimeOffset.UtcNow,
                        MessageId = "broker-5",
                        CorrelationId = "corr",
                        Payload = new OutputChunkPayload
                        {
                            Content = "restored output"
                        }
                    })
                }
            ]
        };

        await service.SaveAsync(persisted);

        var restoredState = new ActiveSessionState();
        var restoredService = new SessionResumeService(storagePath, restoredState);
        await restoredService.RestoreAsync();

        Assert.NotNull(restoredService.CurrentState);
        Assert.Equal(2, restoredService.CurrentState!.Connection.Generation);
        Assert.Equal(5, restoredService.CurrentState.Connection.AcknowledgedSequence);
        Assert.Single(restoredService.CurrentState.RecentTraffic);
        Assert.True(restoredState.GetSnapshot().HasActiveSession);
        Assert.Contains("Recovered", restoredState.GetSnapshot().Summary, StringComparison.Ordinal);
        Assert.Equal("session-restore", restoredState.GetSnapshot().Session?.SessionId);
    }

    [Fact]
    public async Task ClearAsync_RemovesPersistedState()
    {
        var storagePath = CreateStoragePath();
        var service = new SessionResumeService(storagePath, new ActiveSessionState());
        await service.SaveAsync(new ActiveSessionResumeState
        {
            Snapshot = new ActiveSessionSnapshot(
                new RegisteredProject
                {
                    ProjectId = "squadscout",
                    DisplayName = "SquadScout",
                    RepositoryRoot = @"D:\GitHub\SquadScout"
                },
                new SessionDescriptor
                {
                    ProjectId = "squadscout",
                    SessionId = "session-clear",
                    State = SessionState.Pending,
                    CreatedAtUtc = DateTimeOffset.UtcNow
                },
                SessionActivationSource.Broker,
                "Persisted session.")
        });

        Assert.True(File.Exists(storagePath));

        await service.ClearAsync();

        Assert.False(File.Exists(storagePath));
        Assert.Null(service.CurrentState);
    }

    private static string CreateStoragePath() =>
        Path.Combine(AppContext.BaseDirectory, "session-resume-tests", Guid.NewGuid().ToString("N"), "active-session.json");

    private static MessageEnvelope<JsonElement> ToJsonEnvelope<TPayload>(MessageEnvelope<TPayload> envelope) =>
        new()
        {
            ContractVersion = envelope.ContractVersion,
            ProjectId = envelope.ProjectId,
            SessionId = envelope.SessionId,
            Generation = envelope.Generation,
            MessageType = envelope.MessageType,
            Direction = envelope.Direction,
            Sequence = envelope.Sequence,
            ClientSequence = envelope.ClientSequence,
            AcknowledgedSequence = envelope.AcknowledgedSequence,
            TimestampUtc = envelope.TimestampUtc,
            MessageId = envelope.MessageId,
            CorrelationId = envelope.CorrelationId,
            CausationId = envelope.CausationId,
            Payload = JsonSerializer.SerializeToElement(envelope.Payload, SessionMessageSerializer.DefaultOptions)
        };
}
