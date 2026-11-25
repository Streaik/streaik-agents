using Streaik.Agents;
using Streaik.Agents.AspNetCore;
using Streaik.Agents.HeaderAuth;
using Streaik.Agents.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddStreaikAgents(builder.Configuration.GetSection("StreaikAgents"));
builder.Services.AddSingleton<IAgentSource, JsonAgentSource>();
builder.Services.AddScoped<IAgentGatewayHeaders, HeaderGatewayHeaders>();
builder.Services.AddAgentOpenApi();

var app = builder.Build();
app.MapStreaikAgents();
app.MapAgentReference();
await app.RunAsync();
