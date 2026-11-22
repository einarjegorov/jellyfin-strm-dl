using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.StrmDownload.Tests;

public sealed class ProxyTests
{
    [Fact]
    public async Task CopiesFullMediaWithAttachmentAndValidators()
    {
        using var fixture = new ProxyFixture((_, _) =>
        {
            var response = ProxyFixture.Media("media bytes");
            response.Headers.AcceptRanges.Add("bytes");
            response.Headers.ETag = new EntityTagHeaderValue("\"release-1\"");
            response.Content.Headers.LastModified = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline")
            { FileNameStar = "Film ä.mkv" };
            response.Headers.TryAddWithoutValidation("Set-Cookie", "private=secret");
            return Task.FromResult(response);
        });

        await fixture.RunAsync();

        Assert.Equal(200, fixture.Context.Response.StatusCode);
        Assert.Equal("media bytes", fixture.Text);
        Assert.Equal(11, fixture.Context.Response.ContentLength);
        Assert.Equal("video/x-matroska", fixture.Context.Response.ContentType);
        Assert.StartsWith("attachment;", fixture.Context.Response.Headers.ContentDisposition.ToString());
        Assert.Contains("filename*=UTF-8''Film%20%C3%A4.mkv", fixture.Context.Response.Headers.ContentDisposition.ToString());
        Assert.Equal("bytes", fixture.Context.Response.Headers.AcceptRanges);
        Assert.Equal("\"release-1\"", fixture.Context.Response.Headers.ETag);
        Assert.NotEmpty(fixture.Context.Response.Headers.LastModified.ToString());
        Assert.False(fixture.Context.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.Equal("private, no-store", fixture.Context.Response.Headers.CacheControl);
        Assert.Equal("no", fixture.Context.Response.Headers["X-Accel-Buffering"]);
    }

    [Theory]
    [InlineData("bytes=4-7")]
    [InlineData("bytes=4-")]
    [InlineData("bytes=-4")]
    [InlineData("bytes=0-1,4-5")]
    public async Task ForwardsRangeAndIfRangeWithoutSlicingAgain(string range)
    {
        using var fixture = new ProxyFixture((request, _) =>
        {
            Assert.Equal(RangeHeaderValue.Parse(range).ToString(), request.Headers.Range!.ToString());
            Assert.Equal("\"release-1\"", request.Headers.GetValues("If-Range").Single());
            var response = ProxyFixture.Media("4567", HttpStatusCode.PartialContent);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(4, 7, 10);
            return Task.FromResult(response);
        });
        fixture.Context.Request.Headers.Range = range;
        fixture.Context.Request.Headers.IfRange = "\"release-1\"";

        await fixture.RunAsync();

        Assert.Equal(206, fixture.Context.Response.StatusCode);
        Assert.Equal("bytes 4-7/10", fixture.Context.Response.Headers.ContentRange);
        Assert.Equal(4, fixture.Context.Response.ContentLength);
        Assert.Equal("4567", fixture.Text);
    }

    [Fact]
    public async Task PassesThroughMultipartRangesUnchanged()
    {
        using var fixture = new ProxyFixture((_, _) =>
        {
            var response = ProxyFixture.Media("--boundary\r\npart1\r\n--boundary--", HttpStatusCode.PartialContent);
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/byteranges; boundary=boundary");
            return Task.FromResult(response);
        });
        fixture.Context.Request.Headers.Range = "bytes=0-1,4-5";
        await fixture.RunAsync();
        Assert.Equal(206, fixture.Context.Response.StatusCode);
        Assert.Equal("multipart/byteranges; boundary=boundary", fixture.Context.Response.ContentType);
        Assert.Equal("--boundary\r\npart1\r\n--boundary--", fixture.Text);
    }

    [Fact]
    public async Task UpstreamIgnoringRangeReturnsFull200()
    {
        using var fixture = new ProxyFixture((_, _) => Task.FromResult(ProxyFixture.Media("full media")));
        fixture.Context.Request.Headers.Range = "bytes=5-";
        await fixture.RunAsync();
        Assert.Equal(200, fixture.Context.Response.StatusCode);
        Assert.Equal("full media", fixture.Text);
        Assert.False(fixture.Context.Response.Headers.ContainsKey("Content-Range"));
    }

    [Theory]
    [InlineData(304)]
    [InlineData(412)]
    [InlineData(416)]
    public async Task RelaysConditionalAndUnsatisfiableStatusesWithoutErrorBody(int status)
    {
        using var fixture = new ProxyFixture((_, _) =>
        {
            var response = ProxyFixture.Media("private upstream error", (HttpStatusCode)status);
            response.Headers.ETag = new EntityTagHeaderValue("\"release-1\"");
            if (status == 416)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(100);
            return Task.FromResult(response);
        });
        await fixture.RunAsync();
        Assert.Equal(status, fixture.Context.Response.StatusCode);
        Assert.Empty(fixture.Text);
        Assert.Equal("\"release-1\"", fixture.Context.Response.Headers.ETag);
        Assert.Equal(status == 304 ? null : (long?)0, fixture.Context.Response.ContentLength);
        if (status == 416)
            Assert.Equal("bytes */100", fixture.Context.Response.Headers.ContentRange);
    }

    [Theory]
    [InlineData("If-Match", "\"v1\"")]
    [InlineData("If-None-Match", "\"v1\"")]
    [InlineData("If-Modified-Since", "Thu, 01 Jan 2026 00:00:00 GMT")]
    [InlineData("If-Unmodified-Since", "Thu, 01 Jan 2026 00:00:00 GMT")]
    public async Task ForwardsConditionalHeaders(string header, string value)
    {
        using var fixture = new ProxyFixture((request, _) =>
        {
            Assert.Equal(value, request.Headers.GetValues(header).Single());
            return Task.FromResult(ProxyFixture.Media("data"));
        });
        fixture.Context.Request.Headers[header] = value;
        await fixture.RunAsync();
        Assert.Equal("data", fixture.Text);
    }

    [Fact]
    public async Task DoesNotForwardJellyfinCredentialsOrBrowserCookies()
    {
        using var fixture = new ProxyFixture((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("X-Emby-Authorization"));
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.DoesNotContain("jellyfin-secret", request.RequestUri!.ToString());
            Assert.Equal("identity", request.Headers.AcceptEncoding.Single().Value);
            return Task.FromResult(ProxyFixture.Media("data"));
        });
        fixture.Context.Request.Headers.Authorization = "MediaBrowser Token=jellyfin-secret";
        fixture.Context.Request.Headers["X-Emby-Authorization"] = "jellyfin-secret";
        fixture.Context.Request.Headers.Cookie = "session=secret";
        fixture.Context.Request.QueryString = new QueryString("?api_key=jellyfin-secret");
        await fixture.RunAsync();
        Assert.Equal("data", fixture.Text);
    }

    [Theory]
    [InlineData("/next.mkv", true)]
    [InlineData("http://streamer/next.mkv", true)]
    [InlineData("http://other/next.mkv", false)]
    [InlineData("http://streamer:8080/next.mkv", false)]
    [InlineData("https://streamer/next.mkv", false)]
    public async Task RedirectsKeepRangesAndOnlyKeepBasicAuthOnSameOrigin(string target, bool keepAuth)
    {
        var calls = 0;
        using var fixture = new ProxyFixture((request, _) =>
        {
            Assert.Equal("bytes=4-", request.Headers.GetValues("Range").Single());
            Assert.Empty(request.RequestUri!.UserInfo);
            if (calls++ == 0)
            {
                Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
                Assert.Equal("user:p@ss", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization!.Parameter!)));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                { Headers = { Location = new Uri(target, UriKind.RelativeOrAbsolute) } });
            }
            Assert.Equal(keepAuth, request.Headers.Authorization is not null);
            return Task.FromResult(ProxyFixture.Media("data"));
        });
        fixture.Context.Request.Headers.Range = "bytes=4-";
        await fixture.RunAsync("http://user:p%40ss@streamer/start.mkv");
        Assert.Equal(2, calls);
        Assert.Equal("data", fixture.Text);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://streamer/file.mkv")]
    public async Task RejectsNonHttpRedirects(string target)
    {
        var calls = 0;
        using var fixture = new ProxyFixture((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
            { Headers = { Location = new Uri(target) } });
        });
        await fixture.RunAsync();
        Assert.Equal(1, calls);
        Assert.Equal(502, fixture.Context.Response.StatusCode);
    }

    [Fact]
    public async Task RedirectLoopsAreBounded()
    {
        var calls = 0;
        using var fixture = new ProxyFixture((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
            { Headers = { Location = new Uri("/loop", UriKind.Relative) } });
        });
        await fixture.RunAsync();
        Assert.Equal(11, calls);
        Assert.Equal(502, fixture.Context.Response.StatusCode);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task HidesUpstreamFailureDetails(int status)
    {
        using var fixture = new ProxyFixture((_, _) => Task.FromResult(ProxyFixture.Media("streamer-secret", (HttpStatusCode)status)));
        await fixture.RunAsync("http://user:streamer-secret@streamer/file?key=streamer-secret");
        Assert.Equal(502, fixture.Context.Response.StatusCode);
        Assert.DoesNotContain("streamer-secret", fixture.Text + string.Join("", fixture.Logs));
    }

    [Fact]
    public async Task HidesUrlsInExceptionMessages()
    {
        using var fixture = new ProxyFixture((_, _) => throw new HttpRequestException("http://user:secret@streamer/?token=secret"));
        await fixture.RunAsync();
        Assert.Equal(502, fixture.Context.Response.StatusCode);
        Assert.DoesNotContain("secret", fixture.Text + string.Join("", fixture.Logs));
    }

    [Fact]
    public async Task HeaderTimeoutBecomes504()
    {
        using var fixture = new ProxyFixture((_, _) => throw new TaskCanceledException("secret"));
        await fixture.RunAsync();
        Assert.Equal(504, fixture.Context.Response.StatusCode);
    }

    [Fact]
    public async Task ClientDisconnectCancelsUpstream()
    {
        using var fixture = new ProxyFixture(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return ProxyFixture.Media("unreachable");
        });
        var transfer = fixture.RunAsync();
        fixture.Lifetime.Cancel();
        await transfer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Lifetime.Aborted);
        Assert.Empty(fixture.Text);
    }

    [Fact]
    public async Task ClientDisconnectDuringBodyDisposesUpstream()
    {
        using var source = new PausingStream();
        using var fixture = new ProxyFixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(source) }));
        var transfer = fixture.RunAsync();
        await source.SecondReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Lifetime.Cancel();
        await transfer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Lifetime.Aborted);
        Assert.True(source.IsDisposed);
        Assert.Equal("first", fixture.Text);
    }

    [Fact]
    public async Task PreservesSignedQueryWithUrlCredentials()
    {
        using var fixture = new ProxyFixture((request, _) =>
        {
            Assert.Equal("?key=a%2Bb%2Fc%3D&dup=a&dup=b", request.RequestUri!.Query);
            return Task.FromResult(ProxyFixture.Media("data"));
        });
        await fixture.RunAsync("http://user:password@streamer/file.mkv?key=a%2Bb%2Fc%3D&dup=a&dup=b");
        Assert.Equal("data", fixture.Text);
    }

    [Fact]
    public async Task StreamsBeforeUpstreamCompletesAndBoundsReadBuffer()
    {
        using var source = new PausingStream();
        using var fixture = new ProxyFixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(source) }));
        var transfer = fixture.RunAsync();
        await source.SecondReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(transfer.IsCompleted);
        Assert.Equal("first", fixture.Text);
        Assert.InRange(source.LargestRead, 1, 64 * 1024);
        source.Release.TrySetResult();
        await transfer;
        Assert.Equal("firstlast", fixture.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyFailuresReturnErrorBeforeStartAndAbortAfterStart(bool afterStart)
    {
        using var source = new PausingStream { Fail = true, FailImmediately = !afterStart };
        source.Release.TrySetResult();
        using var fixture = new ProxyFixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(source) }));
        await fixture.RunAsync();
        if (afterStart)
        {
            Assert.True(fixture.Lifetime.Aborted);
            Assert.Equal("first", fixture.Text);
        }
        else
        {
            Assert.Equal(502, fixture.Context.Response.StatusCode);
            Assert.DoesNotContain("secret", fixture.Text);
        }
    }

    [Fact]
    public async Task TruncatedContentLengthAbortsTransfer()
    {
        using var fixture = new ProxyFixture((_, _) =>
        {
            var response = ProxyFixture.Media("short");
            response.Content.Headers.ContentLength = 100;
            return Task.FromResult(response);
        });
        await fixture.RunAsync();
        Assert.True(fixture.Lifetime.Aborted);
        Assert.Equal("short", fixture.Text);
    }

    [Theory]
    [InlineData("http://streamer/a/movie.mp4?token=secret", "video/mp4", "movie.mp4")]
    [InlineData("http://streamer/view/opaque?token=secret", "video/x-matroska", "Film.mkv")]
    [InlineData("http://streamer/view/opaque", "application/octet-stream", "Film.bin")]
    public async Task ChoosesMediaFilenameWithoutStrmOrQueryString(string source, string type, string name)
    {
        using var fixture = new ProxyFixture((_, _) =>
        {
            var response = ProxyFixture.Media("data");
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
            return Task.FromResult(response);
        });
        await fixture.RunAsync(source);
        Assert.Contains($"filename={name}", fixture.Context.Response.Headers.ContentDisposition.ToString());
        Assert.DoesNotContain(".strm", fixture.Context.Response.Headers.ContentDisposition.ToString());
        Assert.DoesNotContain("secret", fixture.Context.Response.Headers.ContentDisposition.ToString());
    }
}
