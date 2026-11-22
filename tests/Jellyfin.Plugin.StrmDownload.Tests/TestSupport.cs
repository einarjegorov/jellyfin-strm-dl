using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmDownload.Tests;

internal sealed class ProxyFixture : IDisposable
{
    internal DefaultHttpContext Context { get; } = new();
    internal LifetimeFeature Lifetime { get; } = new();
    internal List<string> Logs { get; } = [];
    private readonly DownloadProxy _proxy;
    private readonly MemoryStream _body;
    private readonly StubHandler _handler;
    internal string Text => Encoding.UTF8.GetString(_body.ToArray());

    internal ProxyFixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        var response = new StartedResponseFeature();
        _body = new StartedMemoryStream(response);
        Context.Features.Set<IHttpResponseFeature>(response);
        Context.Features.Set<IHttpRequestLifetimeFeature>(Lifetime);
        Context.Response.Body = _body;
        _handler = new StubHandler(send);
        _proxy = new DownloadProxy(new StubFactory(_handler), new RecordingLogger(Logs));
    }

    internal Task RunAsync(string source = "http://streamer/file.mkv") =>
        _proxy.ExecuteAsync(Context, new Uri(source), "Film.strm");

    internal static HttpResponseMessage Media(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
            { Headers = { ContentType = new MediaTypeHeaderValue("video/x-matroska") } }
        };

    public void Dispose()
    {
        _handler.Dispose();
        _body.Dispose();
        Lifetime.Dispose();
    }
}

internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await send(request, cancellationToken);
        response.RequestMessage = request;
        return response;
    }
}

internal sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
}

internal sealed class RecordingLogger(List<string> logs) : ILogger<DownloadProxy>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => logs.Add(formatter(state, exception));
}

internal sealed class LifetimeFeature : IHttpRequestLifetimeFeature, IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    public CancellationToken RequestAborted { get => _cancellation.Token; set => throw new NotSupportedException(); }
    internal bool Aborted { get; private set; }
    internal void Cancel() => _cancellation.Cancel();
    public void Abort() { Aborted = true; _cancellation.Cancel(); }
    public void Dispose() => _cancellation.Dispose();
}

internal sealed class StartedResponseFeature : HttpResponseFeature
{
    internal bool Started { get; set; }
    public override bool HasStarted => Started;
}

internal sealed class StartedMemoryStream(StartedResponseFeature response) : MemoryStream
{
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        response.Started = true;
        return base.WriteAsync(buffer, cancellationToken);
    }
}

internal sealed class PausingStream : Stream
{
    internal TaskCompletionSource SecondReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int LargestRead { get; private set; }
    internal bool Fail { get; init; }
    internal bool FailImmediately { get; init; }
    internal bool IsDisposed { get; private set; }
    private int _reads;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        LargestRead = Math.Max(LargestRead, buffer.Length);
        if (FailImmediately)
            throw new IOException("secret");
        if (_reads++ == 0)
        {
            "first"u8.CopyTo(buffer.Span);
            return 5;
        }
        SecondReadStarted.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken);
        if (Fail)
            throw new IOException("secret");
        if (_reads == 2)
        {
            "last"u8.CopyTo(buffer.Span);
            return 4;
        }
        return 0;
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
}
