using Streaik.Agents.AspNetCore;
using Streaik.Agents.HeaderAuth;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddStreaikAgents(builder.Configuration.GetSection("StreaikAgents"));
builder.Services.AddScoped<IAgentGatewayHeaders, HeaderGatewayHeaders>();

var app = builder.Build();
app.MapStreaikAgents();
await app.RunAsync();
