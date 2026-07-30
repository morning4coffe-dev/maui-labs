using System.Net;
using Microsoft.Maui.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core.Network;

namespace Microsoft.Maui.DevFlow.Tests;

public class NetworkCaptureTests
{
    [Fact]
    public void ProfileMode_DisablesNetworkMonitoring()
    {
        var options = new AgentOptions();

        options.ApplyBuildMetadata("true", "profile");

        Assert.False(options.EnableNetworkMonitoring);
        Assert.Equal(0, options.MaxNetworkBodySize);
        Assert.True(options.ReadOnly);
        Assert.True(options.EnableProfiler);
    }

    [Fact]
    public void DisabledBuildMetadata_RejectsAgentRegistration()
    {
        var options = new AgentOptions();

        var error = Assert.Throws<InvalidOperationException>(
            () => options.ApplyBuildMetadata("false", "disabled"));

        Assert.Contains("disabled for this build", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ZeroBodyLimit_DoesNotReadRequestOrStreamingResponseBodies()
    {
        var store = new NetworkRequestStore();
        using var handler = new DevFlowHttpHandler(
            store,
            new StreamingResponseHandler(),
            maxBodySize: 0);
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/stream")
        {
            Content = new ThrowIfReadContent()
        };

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(store.GetRecent());
        var captured = store.GetRecent()[0];
        Assert.Null(captured.RequestBody);
        Assert.Null(captured.ResponseBody);
    }

    private sealed class StreamingResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ThrowIfReadContent()
            });
    }

    private sealed class ThrowIfReadContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
            => throw new InvalidOperationException("The body must not be buffered.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
