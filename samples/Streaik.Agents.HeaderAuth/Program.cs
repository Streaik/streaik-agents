using Streaik.Agents.AspNetCore;
using Streaik.Agents.HeaderAuth;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddStreaikAgents(builder.Configuration.GetSection("StreaikAgents"));
builder.Services.AddScoped<IAgentGatewayHeaders, HeaderGatewayHeaders>();
builder.Services.AddOpenApi(AgentOpenApi.Configure);

var app = builder.Build();
app.MapStreaikAgents();
app.MapAgentReference();
await app.RunAsync();
