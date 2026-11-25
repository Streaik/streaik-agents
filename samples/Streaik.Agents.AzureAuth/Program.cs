using Streaik.Agents.AspNetCore;
using Streaik.Agents.AzureAuth;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(230);
    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(230);
    o.Limits.MaxRequestBodySize = null;
});

builder.Services.AddStreaikAgents(builder.Configuration.GetSection("StreaikAgents"));
builder.Services.AddAzureAgentSources(builder.Configuration.GetSection("BlobAgents"));
builder.Services.AddAzureAgentAuth(builder.Configuration);

var app = builder.Build();
app.MapAzureAuthAgents();
await app.RunAsync();
