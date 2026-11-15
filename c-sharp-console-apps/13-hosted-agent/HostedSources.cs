public static class HostedSources
{
    public const string Project = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <AssemblyName>WorkshopAgent</AssemblyName>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.Agents.AI.Foundry.Hosting" Version="1.22.0-preview.260918.1" />
            <PackageReference Include="Azure.AI.Projects" Version="3.0.0-beta.2" />
          </ItemGroup>
        </Project>
        """;

    public const string Program = """
        using Azure.AI.AgentServer.Core;
        using Azure.AI.Projects;
        using Azure.Identity;
        using Microsoft.Agents.AI;
        using Microsoft.Agents.AI.Foundry.Hosting;
        using Microsoft.Extensions.AI;

        var projectEndpoint = new Uri(Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
            ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT is not set."));
        var deployment = Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME")
            ?? throw new InvalidOperationException("AZURE_AI_MODEL_DEPLOYMENT_NAME is not set.");

        AIAgent agent = new AIProjectClient(projectEndpoint, new DefaultAzureCredential())
            .AsAIAgent(
                model: deployment,
                instructions: "You are a friendly workshop assistant. Use get_workshop_tip when asked about a workshop topic.",
                name: "workshop-assistant",
                tools: [
                    AIFunctionFactory.Create(WorkshopTips.Get, "get_workshop_tip",
                        "Return a one-line tip about a workshop topic such as web iq, foundry iq or indexer.")
                ]);

        var builder = AgentHost.CreateBuilder(args);
        builder.Services.AddFoundryResponses(agent);
        builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());
        var app = builder.Build();
        app.Run();
        """;

    public const string Tips = """
        public static class WorkshopTips
        {
            private static readonly Dictionary<string, string> Tips = new(StringComparer.OrdinalIgnoreCase)
            {
                ["web iq"] = "Web IQ grounds answers in the public web through one API key.",
                ["foundry iq"] = "Foundry IQ puts several knowledge sources behind one knowledge base.",
                ["indexer"] = "An indexer pulls documents from a data source into an AI Search index.",
            };

            public static string Get(string topic) => Tips.TryGetValue(topic, out var tip)
                ? tip : "No tip for that topic. Try: " + string.Join(", ", Tips.Keys);
        }
        """;
}
