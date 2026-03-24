using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace SquadScout.Functions;

public sealed class NegotiateFunction
{
    [Function("Negotiate")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "negotiate")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var response = request.CreateResponse(HttpStatusCode.NotImplemented);
        await response.WriteStringAsync("Negotiate endpoint scaffolded. Token minting arrives in backlog item #7.", cancellationToken);
        return response;
    }
}
