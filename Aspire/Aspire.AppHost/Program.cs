using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// ONE project, deliberately.
//
// GameTownApp is not launched here and must not be. It is no longer independently runnable: the API
// project references it and serves its compiled bundle from its own wwwroot, so "the app" and "the
// API" are one process on one origin. That is what removed CORS, the cross-origin cookie and the
// compile-time API URL.
//
// Starting GameTownApp on its own still *appears* to work — the WASM dev server happily serves the
// SPA — but the SPA resolves its API address from wherever it was loaded from, which is then the dev
// server rather than the API. Every API call falls through that server's SPA fallback and comes back
// as index.html, so the first thing the library page does is try to parse "<!DOCTYPE html>" as JSON:
//
//     Could not load the library: ExpectedStartOfValueNotFound, <
//
// GameTownApp's launch profiles were deleted for the same reason.
builder.AddProject<Projects.API>("gametown", launchProfileName: "https")
    .WithEndpoint("https", ep => ep.IsProxied = false);

builder.Build().Run();
