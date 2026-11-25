using Streaik.Agents.AspNetCore;
using Streaik.Agents.AzureAuth;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddStreaikAgents(builder.Configuration.GetSection("StreaikAgents"));
builder.Services.AddAzureAgentSources(builder.Configuration.GetSection("BlobAgents"));
builder.Services.AddAzureAgentAuth(builder.Configuration);

var app = builder.Build();
app.MapAzureAuthAgents();
await app.RunAsync();
