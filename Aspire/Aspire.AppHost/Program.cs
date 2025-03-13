var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.API>("api");
builder.AddProject<Projects.GameTownApp>("app");

builder.Build().Run();
