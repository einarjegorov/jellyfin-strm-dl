using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.StrmDownload.Tests
{
    public sealed class PointerAndFilterTests
    {
        [Theory]
        [InlineData("http://streamer/view/file.mkv")]
        [InlineData("https://user:p%40ss@streamer/view/file?key=signed%2Btoken")]
        [InlineData("\uFEFF\r\n# comment\r\n https://streamer/file.mkv \r\n")]
        public async Task ReadsOneUtf8HttpUrl(string text)
        {
            using var file = new PointerFile(Encoding.UTF8.GetBytes(text));
            var uri = await StrmPointer.ReadAsync(file.Path, CancellationToken.None);
            Assert.True(StrmPointer.IsHttp(uri));
            Assert.Equal("streamer", uri.Host);
        }

        [Theory]
        [InlineData("")]
        [InlineData("# comment\n\n")]
        [InlineData("/etc/passwd")]
        [InlineData("file:///etc/passwd")]
        [InlineData("ftp://streamer/file")]
        [InlineData("plugin://example/file")]
        [InlineData("http://streamer/one\nhttp://streamer/two")]
        [InlineData("not a URL")]
        public async Task RejectsInvalidAndAmbiguousPointers(string text)
        {
            using var file = new PointerFile(Encoding.UTF8.GetBytes(text));
            await Assert.ThrowsAsync<InvalidDataException>(() => StrmPointer.ReadAsync(file.Path, CancellationToken.None));
        }

        [Fact]
        public async Task RejectsOversizedAndNonUtf8Pointers()
        {
            using var large = new PointerFile(new byte[StrmPointer.MaxBytes + 1]);
            using var invalidUtf8 = new PointerFile([0xff, 0xfe, 0xff]);
            await Assert.ThrowsAsync<InvalidDataException>(() => StrmPointer.ReadAsync(large.Path, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() => StrmPointer.ReadAsync(invalidUtf8.Path, CancellationToken.None));
        }

        [Fact]
        public async Task ReplacesOnlyTheSuccessfulCoreStrmResult()
        {
            var result = new PhysicalFileResult("/media/Film.STRM", "application/octet-stream")
            { FileDownloadName = "Film.STRM" };
            var context = CreateContext(result);
            var nextCalled = false;
            await NewFilter().OnResultExecutionAsync(context, () =>
            {
                nextCalled = true;
                return Task.FromResult(new ResultExecutedContext(context, [], context.Result, new object()));
            });
            Assert.True(nextCalled);
            Assert.IsType<StrmDownloadResult>(context.Result);
        }

        [Fact]
        public async Task DetectsStrmByDownloadNameWhenCoreResolvedASymlink()
        {
            var context = CreateContext(new PhysicalFileResult("/media/opaque-target", "application/octet-stream")
            { FileDownloadName = "Film.strm" });
            await FilterAsync(context);
            Assert.IsType<StrmDownloadResult>(context.Result);
        }

        [Fact]
        public async Task LeavesOrdinaryFilesErrorsAndOtherPluginResultsAlone()
        {
            IActionResult[] results =
            [
                new PhysicalFileResult("/media/Film.mkv", "video/x-matroska"),
                new StatusCodeResult(403), new NotFoundResult(), new BadRequestResult(),
                new FileStreamResult(new MemoryStream(), "video/mp4")
            ];
            foreach (var result in results)
            {
                var context = CreateContext(result);
                await FilterAsync(context);
                Assert.Same(result, context.Result);
            }
        }

        [Theory]
        [InlineData("GetFile", "Library")]
        [InlineData("GetDownload", "Other")]
        public async Task LeavesOtherEndpointsAlone(string action, string controller)
        {
            var result = new PhysicalFileResult("/media/Film.strm", "application/octet-stream");
            var context = CreateContext(result, action, controller);
            await FilterAsync(context);
            Assert.Same(result, context.Result);
        }

        [Fact]
        public async Task LeavesSimilarlyNamedPluginControllersAlone()
        {
            var result = new PhysicalFileResult("/media/Film.strm", "application/octet-stream");
            var context = CreateContext(result);
            ((ControllerActionDescriptor)context.ActionDescriptor).ControllerTypeInfo = typeof(PointerAndFilterTests).GetTypeInfo();
            await FilterAsync(context);
            Assert.Same(result, context.Result);
        }

        [Theory]
        [InlineData("", 422)]
        [InlineData("file:///etc/passwd", 422)]
        public async Task InvalidPointersReturnUsefulErrorsWithoutDownloadingThePointer(string text, int expectedStatus)
        {
            using var file = new PointerFile(Encoding.UTF8.GetBytes(text));
            var http = new DefaultHttpContext();
            http.Response.Body = new MemoryStream();
            await new StrmDownloadResult(NewProxy(), file.Path, "Film.strm")
                .ExecuteResultAsync(new ActionContext(http, new RouteData(), new ControllerActionDescriptor()));
            Assert.Equal(expectedStatus, http.Response.StatusCode);
            Assert.Equal("application/problem+json", http.Response.ContentType);
        }

        [Fact]
        public async Task MissingPointerReturns404()
        {
            var http = new DefaultHttpContext();
            http.Response.Body = new MemoryStream();
            await new StrmDownloadResult(NewProxy(), System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".strm"), "Film.strm")
                .ExecuteResultAsync(new ActionContext(http, new RouteData(), new ControllerActionDescriptor()));
            Assert.Equal(404, http.Response.StatusCode);
        }

        [Fact]
        public void RegistersFilterAndProxyUsingOnlyHostServices()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            new ServiceRegistrator().RegisterServices(services, null!);
            using var provider = services.BuildServiceProvider();
            Assert.NotNull(provider.GetRequiredService<DownloadProxy>());
            Assert.NotNull(provider.GetRequiredService<DownloadFilter>());
            var options = provider.GetRequiredService<IOptions<MvcOptions>>().Value;
            Assert.Contains(options.Filters, filter => filter is ServiceFilterAttribute service && service.ServiceType == typeof(DownloadFilter));
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(DownloadProxy.ClientName);
            Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        }

        private static DownloadProxy NewProxy() => new(new StubFactory(new StubHandler((_, _) =>
            throw new InvalidOperationException("Unexpected upstream access."))), NullLogger<DownloadProxy>.Instance);
        private static DownloadFilter NewFilter() => new(NewProxy());
        private static Task FilterAsync(ResultExecutingContext context) => NewFilter().OnResultExecutionAsync(context,
            () => Task.FromResult(new ResultExecutedContext(context, [], context.Result, new object())));
        private static ResultExecutingContext CreateContext(IActionResult result, string action = "GetDownload", string controller = "Library")
        {
            var descriptor = new ControllerActionDescriptor
            {
                ActionName = action,
                ControllerName = controller,
                ControllerTypeInfo = typeof(Api.Controllers.LibraryController).GetTypeInfo()
            };
            var context = new ActionContext(new DefaultHttpContext(), new RouteData(), descriptor, new ModelStateDictionary());
            return new ResultExecutingContext(context, [], result, new object());
        }
    }

    internal sealed class PointerFile : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".strm");
        internal PointerFile(byte[] contents) => File.WriteAllBytes(Path, contents);
        public void Dispose() => File.Delete(Path);
    }
}

// A descriptor with the core controller's name; no Jellyfin server is started by these unit tests.
namespace Jellyfin.Api.Controllers
{
    public sealed class LibraryController;
}
