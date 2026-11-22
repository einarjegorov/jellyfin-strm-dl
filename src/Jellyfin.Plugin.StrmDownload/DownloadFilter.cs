using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.StrmDownload;

public sealed class DownloadFilter(DownloadProxy proxy) : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        // Result filters run AFTER the core action's authorization, eligibility checks and logging.
        // Never replace errors, other endpoints or other plugins' custom download results.
        if (context.ActionDescriptor is ControllerActionDescriptor
            {
                ControllerName: "Library",
                ActionName: "GetDownload"
            } action
            && action.ControllerTypeInfo.FullName == "Jellyfin.Api.Controllers.LibraryController"
            && context.Result is PhysicalFileResult file
            && (IsStrm(file.FileDownloadName) || IsStrm(file.FileName)))
        {
            context.Result = new StrmDownloadResult(proxy, file.FileName, file.FileDownloadName);
        }

        await next();
    }

    private static bool IsStrm(string? path) =>
        string.Equals(Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase);
}

internal sealed class StrmDownloadResult(DownloadProxy proxy, string path, string downloadName) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        Uri source;
        try
        {
            source = await StrmPointer.ReadAsync(path, context.HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            context.HttpContext.Abort();
            return;
        }
        catch (FileNotFoundException)
        {
            await DownloadProxy.FailAsync(context.HttpContext, 404, "STRM file not found.");
            return;
        }
        catch (DirectoryNotFoundException)
        {
            await DownloadProxy.FailAsync(context.HttpContext, 404, "STRM file not found.");
            return;
        }
        catch (InvalidDataException)
        {
            await DownloadProxy.FailAsync(context.HttpContext, 422, "STRM must contain one HTTP(S) file URL.");
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await DownloadProxy.FailAsync(context.HttpContext, 500, "Unable to read STRM file.");
            return;
        }

        await proxy.ExecuteAsync(context.HttpContext, source,
            string.IsNullOrEmpty(downloadName) ? Path.GetFileName(path) : downloadName);
    }
}
