using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.API>("api",launchProfileName:"https")
    .WithEndpoint("https",ep=>ep.IsProxied = false);
builder.AddProject<Projects.GameTownApp>("app",launchProfileName:"https")
    .WithEndpoint("https",ep=>ep.IsProxied = false);



builder.Build().Run();
