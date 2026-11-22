using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.StrmDownload;

public sealed class DownloadProxy(IHttpClientFactory clients, ILogger<DownloadProxy> logger)
{
    internal const string ClientName = "Jellyfin.Plugin.StrmDownload";
    internal const int BufferSize = 64 * 1024;
    private const int MaxRedirects = 10;
    private static readonly string[] RequestHeaders =
    [
        HeaderNames.Range, HeaderNames.IfRange, HeaderNames.IfMatch, HeaderNames.IfNoneMatch,
        HeaderNames.IfModifiedSince, HeaderNames.IfUnmodifiedSince
    ];
    private static readonly string[] ResponseHeaders =
    [
        HeaderNames.AcceptRanges, HeaderNames.ETag, HeaderNames.LastModified, HeaderNames.ContentRange
    ];

    internal async Task ExecuteAsync(HttpContext context, Uri source, string originalName)
    {
        try
        {
            using var client = clients.CreateClient(ClientName);
            using var upstream = await OpenAsync(client, source, context.Request);
            var status = (int)upstream.StatusCode;
            if (status is not (200 or 206 or 304 or 412 or 416))
            {
                logger.LogWarning("STRM upstream returned HTTP {StatusCode}.", status);
                await FailAsync(context, 502, "The media server could not supply the download.");
                return;
            }

            // These statuses carry no media. Do not relay upstream error pages or credentials.
            if (status is 304 or 412 or 416)
            {
                SetHeaders(context.Response, upstream);
                if (status != 304)
                    context.Response.ContentLength = 0;
                return;
            }

            await using var body = await upstream.Content.ReadAsStreamAsync(context.RequestAborted);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                // Get the first bytes before committing a 200/206, so startup errors can be reported.
                var read = await ReadAsync(body, buffer, idle);
                var expectedLength = upstream.Content.Headers.ContentLength;
                if (read == 0 && expectedLength is > 0)
                    throw new IOException("Upstream ended before its advertised length.");

                context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                SetHeaders(context.Response, upstream);
                DownloadFileInfo.SetHeaders(context.Response, upstream, originalName);
                context.Response.Headers["X-Accel-Buffering"] = "no";
                long copied = 0;
                while (read > 0)
                {
                    idle.CancelAfter(TimeSpan.FromMinutes(2));
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), idle.Token);
                    await context.Response.Body.FlushAsync(idle.Token);
                    copied += read;
                    read = await ReadAsync(body, buffer, idle);
                }
                if (expectedLength.HasValue && copied != expectedLength.Value)
                    throw new IOException("Upstream ended before its advertised length.");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("STRM download timed out.");
            await FailAsync(context, 504, "The media server timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            // Exception messages can contain full URLs and Basic/signed credentials.
            logger.LogWarning("STRM download failed ({FailureType}).", ex.GetType().Name);
            await FailAsync(context, 502, "The media download was interrupted or unavailable.");
        }
    }

    private static async Task<int> ReadAsync(Stream body, byte[] buffer, CancellationTokenSource idle)
    {
        idle.CancelAfter(TimeSpan.FromMinutes(2));
        return await body.ReadAsync(buffer.AsMemory(0, BufferSize), idle.Token);
    }

    private static async Task<HttpResponseMessage> OpenAsync(HttpClient client, Uri uri, HttpRequest incoming)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(incoming.HttpContext.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromMinutes(1));
        AuthenticationHeaderValue? authorization = null;
        for (var redirects = 0; ; redirects++)
        {
            if (!StrmPointer.IsHttp(uri))
                throw new InvalidDataException("Unsupported redirect protocol.");
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                var credentials = Uri.UnescapeDataString(parts[0]) + ":"
                    + (parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "");
                authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
                uri = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = authorization;
            request.Headers.TryAddWithoutValidation(HeaderNames.AcceptEncoding, "identity");
            foreach (var header in RequestHeaders)
            {
                if (incoming.Headers.TryGetValue(header, out var value))
                    request.Headers.TryAddWithoutValidation(header, value.ToArray());
            }

            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;

            using (response)
            {
                if (redirects >= MaxRedirects || response.Headers.Location is not { } location)
                    throw new InvalidDataException("Invalid or excessive redirects.");
                var target = new Uri(uri, location);
                if (target.Scheme != uri.Scheme || target.IdnHost != uri.IdnHost || target.Port != uri.Port)
                    authorization = null;
                uri = target;
            }
        }
    }

    private static void SetHeaders(HttpResponse response, HttpResponseMessage upstream)
    {
        response.StatusCode = (int)upstream.StatusCode;
        response.Headers.CacheControl = "private, no-store";
        foreach (var header in ResponseHeaders)
        {
            if (upstream.Headers.TryGetValues(header, out var values)
                || upstream.Content.Headers.TryGetValues(header, out values))
                response.Headers[header] = values.ToArray();
        }
    }

    internal static async Task FailAsync(HttpContext context, int status, string title)
    {
        if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
            return;
        }
        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "private, no-store";
        await context.Response.WriteAsJsonAsync(new { status, title },
            options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
}
