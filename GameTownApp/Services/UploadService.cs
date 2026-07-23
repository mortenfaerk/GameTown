using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace GameTownApp.Services;

/// <summary>
/// Uploads a game archive with progress, via wwwroot/js/upload.js.
///
/// This is the one place the app reaches for JavaScript, and only because it has to: Blazor WASM
/// sends through fetch(), which reports no upload progress. See the module for the details.
/// </summary>
public class UploadService(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    private async Task<IJSObjectReference> GetModuleAsync()
        => _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/upload.js");

    /// <summary>
    /// Posts the file currently selected in <paramref name="fileInput"/> to the add-game endpoint.
    /// <paramref name="progressTarget"/> must expose a [JSInvokable] OnUploadProgress(long, long).
    /// </summary>
    public async Task<ApiResult> UploadGameAsync<TProgress>(
        ElementReference fileInput,
        string uploadUrl,
        AddGameRequest request,
        DotNetObjectReference<TProgress> progressTarget)
        where TProgress : class
    {
        // No pre-flight token refresh any more. This used to check a JWT's expiry and renew it
        // before starting, because a long upload could outlive the token; the auth cookie carries no
        // client-visible expiry and renews server-side, so there is nothing to check.
        //
        // Keys must match the [FromForm(Name = ...)] attributes on the API's AddGameWithFileForm.
        var fields = new Dictionary<string, string?>
        {
            ["title"] = request.Title,
            ["howTo"] = request.HowTo,
            ["rawgGameId"] = request.RawgGameId
        };

        try
        {
            var module = await GetModuleAsync();
            var response = await module.InvokeAsync<UploadResponse>(
                "uploadGame", fileInput, uploadUrl, fields, progressTarget);

            if (response.Aborted)
                return ApiResult.Failed("Upload cancelled.");

            return ApiResult.FromStatus(response.Status, response.Body);
        }
        catch (JSException ex)
        {
            return ApiResult.Failed(ex.Message);
        }
    }

    public async Task AbortAsync()
    {
        if (_module is null) return;
        try { await _module.InvokeVoidAsync("abortUpload"); }
        catch (JSDisconnectedException) { /* page going away */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await _module.DisposeAsync(); }
        catch (JSDisconnectedException) { /* page going away */ }
        _module = null;
    }

    private sealed record UploadResponse(int Status, string? Body, bool Aborted);
}
