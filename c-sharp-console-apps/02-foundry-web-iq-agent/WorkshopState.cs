using System.Text.Json;
using System.Text.Json.Nodes;

// Only non-secret references are persisted. Prompts, outputs, credentials and tokens never enter this file.
public sealed class WorkshopState
{
    private readonly FoundrySettings settings;
    private readonly string lab;
    private string FilePath => Path.Combine(".workshop", lab + "-" + settings.Prefix + ".json");
    public string? ToolboxVersion { get; set; }
    public string? AgentName { get; set; }
    public string? AgentVersion { get; set; }
    public string? ConversationId { get; set; }
    public bool InitialCompleted { get; set; }

    public WorkshopState(FoundrySettings settings, string lab)
    {
        this.settings = settings;
        this.lab = lab;
    }

    public static WorkshopState Load(FoundrySettings settings, string lab)
    {
        var state = new WorkshopState(settings, lab);
        if (!File.Exists(state.FilePath)) return state;
        try
        {
            JsonObject value = JsonNode.Parse(File.ReadAllText(state.FilePath)) as JsonObject
                ?? throw new InvalidOperationException("Local references must be a JSON object.");
            if ((string?)value["projectEndpoint"] != settings.ProjectEndpoint
                || (string?)value["projectResourceId"] != settings.ProjectResourceId
                || (string?)value["prefix"] != settings.Prefix || (string?)value["lab"] != lab
                || (int?)value["schema"] != 1)
                throw new InvalidOperationException("Local references belong to a different project/configuration.");
            state.ToolboxVersion = (string?)value["toolboxVersion"];
            state.AgentName = (string?)value["agentName"];
            state.AgentVersion = (string?)value["agentVersion"];
            state.ConversationId = (string?)value["conversationId"];
            state.InitialCompleted = (bool?)value["initialCompleted"] ?? false;
            return state;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidOperationException("Cannot use " + state.FilePath + ". Remove this local file and restart the step commands: " + error.Message);
        }
    }

    public JsonObject AgentReference()
    {
        if (string.IsNullOrEmpty(AgentName) || string.IsNullOrEmpty(AgentVersion))
            throw new InvalidOperationException("No pinned agent reference. Run agent first (or use the independent run command).");
        return new JsonObject { ["name"] = AgentName, ["version"] = AgentVersion };
    }

    public JsonObject ConversationReference()
    {
        if (!InitialCompleted || string.IsNullOrEmpty(ConversationId))
            throw new InvalidOperationException("No completed initial question. Run ask first; a failed question cannot be followed up.");
        return new JsonObject { ["id"] = ConversationId };
    }

    public JsonObject ToolboxReference()
    {
        if (string.IsNullOrEmpty(ToolboxVersion))
            throw new InvalidOperationException("No selected toolbox version. Run toolbox or discover first.");
        return new JsonObject { ["version"] = ToolboxVersion };
    }

    public void SetAgent(JsonObject agent)
    {
        AgentName = (string?)agent["name"] ?? throw new InvalidOperationException("Agent omitted its name.");
        AgentVersion = (string?)agent["version"] ?? throw new InvalidOperationException("Agent omitted its version.");
        ClearConversation();
    }

    public void ClearAgent()
    {
        AgentName = AgentVersion = null;
        ClearConversation();
    }

    public void ClearConversation()
    {
        ConversationId = null;
        InitialCompleted = false;
    }

    public void Save()
    {
        Directory.CreateDirectory(".workshop");
        var value = new JsonObject
        {
            ["schema"] = 1,
            ["lab"] = lab,
            ["prefix"] = settings.Prefix,
            ["projectEndpoint"] = settings.ProjectEndpoint,
            ["projectResourceId"] = settings.ProjectResourceId,
            ["toolboxVersion"] = ToolboxVersion,
            ["agentName"] = AgentName,
            ["agentVersion"] = AgentVersion,
            ["conversationId"] = ConversationId,
            ["initialCompleted"] = InitialCompleted,
        };
        string pending = FilePath + ".new";
        try
        {
            File.WriteAllText(pending, value.ToJsonString());
            File.Move(pending, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(pending);
        }
    }
}
