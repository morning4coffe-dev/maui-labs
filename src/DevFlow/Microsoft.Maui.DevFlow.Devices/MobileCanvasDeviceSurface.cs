using System.Net.Http.Json;
using System.Text.Json;

namespace Microsoft.Maui.DevFlow.Devices;

/// <summary>
/// An <see cref="IDeviceSurface"/> backed by a locally installed Mobile Canvas host.
/// <para>
/// The host is a separate product with its own lifecycle, so this adapter treats it as an
/// untrusted, optional peer: every call tolerates it being absent, restarted, or a version whose
/// payloads do not deserialise. Anything unexpected degrades to "unavailable" rather than
/// propagating, because a DevFlow session must never fail because an optional device layer
/// hiccupped.
/// </para>
/// </summary>
public sealed class MobileCanvasDeviceSurface : IDeviceSurface, IDisposable
{
    private const string ApiPrefix = "/api/v1";

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly Func<MobileCanvasHostState?> _stateProvider;

    public MobileCanvasDeviceSurface(HttpClient? httpClient = null, Func<MobileCanvasHostState?>? stateProvider = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _stateProvider = stateProvider ?? MobileCanvasHost.TryRead;
    }

    public async Task<DeviceHostHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var state = _stateProvider();
        if (state is null)
            return DeviceHostHealth.Unavailable;

        if (!MobileCanvasProtocol.IsSupported(state.SchemaVersion))
            return DeviceHostHealth.Incompatible(state.SchemaVersion);

        try
        {
            using var request = CreateRequest(HttpMethod.Get, state, "/status");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return DeviceHostHealth.Unauthorized();
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // The route we depend on is gone, which means this host is not one we know how to
                // drive. Saying so beats reporting an empty device list forever.
                return DeviceHostHealth.Incompatible(state.SchemaVersion);
            }

            if (!response.IsSuccessStatusCode)
                return DeviceHostHealth.NotResponding($"The device host answered with {(int)response.StatusCode}.");

            return new DeviceHostHealth
            {
                Availability = DeviceHostAvailability.Available,
                Version = state.Version,
                ProtocolVersion = state.SchemaVersion,
            };
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            // A stale host.json outlives a crashed host; that is a normal state, not a fault.
            return DeviceHostHealth.NotResponding("The device host is not responding.");
        }
    }

    public async Task<IReadOnlyList<DeviceTarget>?> ListAsync(CancellationToken cancellationToken = default)
    {
        // A null result distinguishes "could not enumerate" from "enumerated, none present".
        // Callers cache the latter and fall back on the former.
        return await GetJsonAsync(
            "/devices",
            MobileCanvasJsonContext.Default.DeviceTargetArray,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceTarget?> GetAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        return await GetJsonAsync(
            $"/devices/{Uri.EscapeDataString(deviceId)}",
            MobileCanvasJsonContext.Default.DeviceTarget,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<DeviceOperationResult> BootAsync(string deviceId, CancellationToken cancellationToken = default) =>
        PostAsync(deviceId, "boot", content: null, cancellationToken);

    public Task<DeviceOperationResult> ShutdownAsync(string deviceId, CancellationToken cancellationToken = default) =>
        PostAsync(deviceId, "shutdown", content: null, cancellationToken);

    public Task<DeviceOperationResult> TapAsync(string deviceId, DevicePoint point, CancellationToken cancellationToken = default) =>
        PostAsync(
            deviceId,
            "input/tap",
            JsonContent.Create(new DevicePointPayload(point.X, point.Y), MobileCanvasJsonContext.Default.DevicePointPayload),
            cancellationToken);

    public async Task<byte[]?> ScreenshotAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var state = _stateProvider();
        if (state is null || string.IsNullOrWhiteSpace(deviceId))
            return null;

        try
        {
            using var request = CreateRequest(HttpMethod.Get, state, $"/devices/{Uri.EscapeDataString(deviceId)}/screenshot");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Builds a request carrying the host's control token.
    /// <para>
    /// The token is a bearer credential for a trusted local client, distinct from the single-use
    /// bootstrap secret the host issues to canvas panels. Every call needs it, so it is applied
    /// here rather than at each call site where one could be forgotten.
    /// </para>
    /// </summary>
    private static HttpRequestMessage CreateRequest(HttpMethod method, MobileCanvasHostState state, string path)
    {
        var request = new HttpRequestMessage(method, $"{state.BaseUrl}{ApiPrefix}{path}");
        if (!string.IsNullOrWhiteSpace(state.ControlToken))
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", state.ControlToken);
        }

        return request;
    }

    private async Task<T?> GetJsonAsync<T>(
        string path,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var state = _stateProvider();
        if (state is null)
            return default;

        try
        {
            using var request = CreateRequest(HttpMethod.Get, state, path);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return default;

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer
                    .DeserializeAsync(stream, typeInfo, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsTransport(ex) || ex is JsonException)
        {
            return default;
        }
    }

    private async Task<DeviceOperationResult> PostAsync(
        string deviceId,
        string action,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return DeviceOperationResult.Failed("A device id is required.");

        var state = _stateProvider();
        if (state is null)
            return DeviceOperationResult.NoHost();

        try
        {
            using var request = CreateRequest(HttpMethod.Post, state, $"/devices/{Uri.EscapeDataString(deviceId)}/{action}");
            request.Content = content;
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                return DeviceOperationResult.Failed(
                    "The device host rejected DevFlow's control token. It was most likely restarted.");
            }

            if (!response.IsSuccessStatusCode)
                return DeviceOperationResult.Failed($"The device host refused {action} with {(int)response.StatusCode}.");

            return DeviceOperationResult.Ok();
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            return DeviceOperationResult.Failed("The device host is not responding.");
        }
        finally
        {
            content?.Dispose();
        }
    }

    private static bool IsTransport(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException;

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}
