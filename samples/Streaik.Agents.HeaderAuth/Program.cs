using Streaik.Agents;
using Streaik.Agents.AspNetCore;
using Streaik.Agents.HeaderAuth;
using Streaik.Agents.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddStreaikAgents(builder.Configuration.GetSection("StreaikAgents"));
builder.Services.AddSingleton<IAgentSource, JsonAgentSource>();
builder.Services.AddScoped<IAgentGatewayHeaders, HeaderGatewayHeaders>();
builder.Services.AddAgentOpenApi();

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
           .AllowAnyHeader()
           .AllowAnyOrigin()
           .AllowAnyMethod()
            .WithExposedHeaders("WWW-Authenticate")));

var app = builder.Build();
app.UseCors();
app.MapStreaikAgents();
app.MapAgentReference();
await app.RunAsync();
