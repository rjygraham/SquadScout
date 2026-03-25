namespace SquadScout.Contracts.Realtime;

public static class PubSubNegotiateRequestValidator
{
    public static bool TryValidate(PubSubNegotiateRequest request, out string validationError)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.IsDefined(request.ParticipantKind))
        {
            validationError = "participantKind must be either 'client' or 'broker'.";
            return false;
        }

        if (request.ParticipantKind == PubSubParticipantKind.Client &&
            !string.IsNullOrWhiteSpace(request.BrokerId))
        {
            validationError = "brokerId is only valid for broker-scoped negotiate requests.";
            return false;
        }

        return SessionGroupName.TryCreate(
            request.ProjectId,
            request.SessionId,
            request.BrokerId,
            out _,
            out validationError);
    }
}
