using Azure.Storage.Blobs;
using Streaik.Agents.Blob;
using Streaik.Agents.Json;

namespace Streaik.Agents.AzureAuth;

internal static class AzureAgentSources
{
    public static void AddAzureAgentSources(this IServiceCollection services, IConfigurationSection blob)
    {
        services.AddSingleton<IAgentSource, JsonAgentSource>();

        var connectionString = blob["ConnectionString"];
        var containerName = blob["ContainerName"];
        var prefix = blob["Prefix"];
        if (!string.IsNullOrWhiteSpace(connectionString) && !string.IsNullOrWhiteSpace(containerName))
            services.AddSingleton<IAgentSource>(_ => new BlobAgentSource(new BlobContainerClient(connectionString, containerName), prefix));
    }
}
