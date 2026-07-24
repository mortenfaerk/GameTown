using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
builder.AddProject<Projects.API>("gametown", launchProfileName: "https")
    .WithEndpoint("https", ep => ep.IsProxied = false)
    .WithUrls(context =>
    {
        // The https endpoint is the one the AppHost launches (launchProfileName above). If it is
        // somehow unallocated, leave Aspire's own URLs alone rather than emitting broken links.
        var https = context.Urls.FirstOrDefault(u => u.Endpoint?.EndpointName == "https")?.Endpoint;
        if (https is null)
        {
            return;
        }

        var root = https.Url.TrimEnd('/');

        context.Urls.Clear();

        // All three stay SummaryAndDetails (the default). DetailsOnly does not mean "one click
        // further in under the row's URL popup" — it drops the URL from the resource row's URLs
        // column altogether, leaving only the bare "GameTown" link and no hint the others exist.
        // DisplayOrder sorts highest-first, so the app stays the link you land on.
        context.Urls.Add(new ResourceUrlAnnotation
        {
            Url = root,
            DisplayText = "GameTown",
            Endpoint = https,
            DisplayOrder = 300
        });

        // The first-run wizard, which creates the first administrator. A Razor Page rather than an
        // SPA route (API/Pages/Setup.cshtml), and it 404s once an admin exists — so on a configured
        // install this link is a dead end by design rather than a way back into the wizard.
        context.Urls.Add(new ResourceUrlAnnotation
        {
            Url = $"{root}/setup",
            DisplayText = "Setup (first run)",
            DisplayOrder = 200
        });

        // Scalar is mapped only in Development (API/Startup/OpenApiConfig.cs), which is the only
        // environment the AppHost launches, so this link is always live here.
        context.Urls.Add(new ResourceUrlAnnotation
        {
            Url = $"{root}/scalar/v1",
            DisplayText = "API docs (Scalar)",
            DisplayOrder = 100
        });
    });

builder.Build().Run();
