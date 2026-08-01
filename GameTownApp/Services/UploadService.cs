using System.Text.Json;
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
    public async Task<UploadOutcome> UploadGameAsync<TProgress>(
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
                return new UploadOutcome(ApiResult.Failed("Upload cancelled."), null);

            // A transport failure carries no status code, so it cannot go through FromStatus. The
            // most common cause by far is a reverse proxy in front of GameTown closing the
            // connection — over its body-size limit, or past its read timeout — so the message says
            // so rather than blaming the network in general.
            if (response.NetworkError)
                return new UploadOutcome(ApiResult.Failed(
                    "The upload could not reach the server — the connection was closed before it finished. "
                    + "If GameTown is behind a reverse proxy, check its upload size and timeout limits."), null);

            var result = ApiResult.FromStatus(response.Status, response.Body);
            return new UploadOutcome(result, result.Success ? ReadGameId(response.Body) : null);
        }
        catch (JSException)
        {
            // Deliberately not ex.Message. Blazor composes that from the JavaScript Error object, so
            // it carries the module's file name, line number and call stack — which is exactly what
            // used to be shown to the user in the error banner.
            return new UploadOutcome(
                ApiResult.Failed("The upload could not be started. Reload the page and try again."), null);
        }
    }

    /// <summary>
    /// Pulls the new game's id out of the 201 response body.
    ///
    /// Tolerant of not finding one, and the caller treats a null id as "uploaded, but there is nothing
    /// further to do with it". That is not defensive padding: an install upgraded mid-session could
    /// still be answering the old 204, and an upload that succeeded must not be reported as a failure
    /// because the follow-up step lost its address.
    /// </summary>
    private static Guid? ReadGameId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            var response = JsonSerializer.Deserialize<AddGameResponse>(body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            return response?.Id == Guid.Empty ? null : response?.Id;
        }
        catch (JsonException)
        {
            return null;
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

    private sealed record UploadResponse(int Status, string? Body, bool Aborted, bool NetworkError);
}

/// <summary>
/// The result of an upload, plus the id of the game it created.
///
/// The id is what lets the add-game screen carry straight on to tags and box art instead of making
/// the contributor go and find what they just uploaded. Null on failure, and also on success against
/// a server still answering the old 204 — see UploadService.ReadGameId.
/// </summary>
public record UploadOutcome(ApiResult Result, Guid? GameId);
