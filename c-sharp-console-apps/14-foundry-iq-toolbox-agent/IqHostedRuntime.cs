#nullable enable

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

public sealed partial class IqWorkshop
{
    private static readonly string[] SourceFiles = { "WorkshopIqAgent.csproj", "Program.cs" };

    public void WriteFiles(string root)
    {
        string folder = settings.Folder(root);
        Directory.CreateDirectory(folder);
        RejectLinkedPath(folder);
        foreach (string name in SourceFiles) RejectLinkedPath(Path.Combine(folder, name));
        File.WriteAllText(Path.Combine(folder, SourceFiles[0]), IqSources.Project + Environment.NewLine);
        File.WriteAllText(Path.Combine(folder, SourceFiles[1]), IqSources.Program + Environment.NewLine);
        Console.WriteLine("Wrote C# hosted toolbox agent source to " + folder);
    }

    public string Package(string root)
    {
        string folder = settings.Folder(root);
        RejectLinkedPath(folder);
        foreach (string name in SourceFiles)
        {
            string path = Path.Combine(folder, name);
            RejectLinkedPath(path);
            if (!File.Exists(path)) throw new InvalidOperationException("Missing " + name + ". Run write-agent first.");
        }
        string zip = Path.Combine(folder, "agent.zip");
        RejectLinkedPath(zip);
        using (var stream = new FileStream(zip, FileMode.Create, FileAccess.Write))
        using (var bundle = new ZipArchive(stream, ZipArchiveMode.Create))
            foreach (string name in SourceFiles)
                bundle.CreateEntryFromFile(Path.Combine(folder, name), name, CompressionLevel.Optimal);
        Console.WriteLine("Packaged exactly the two existing C# source files: " + zip);
        return zip;
    }

    public JsonObject CodeMetadata() => new JsonObject
    {
        ["definition"] = new JsonObject
        {
            ["kind"] = "hosted",
            ["cpu"] = "0.5",
            ["memory"] = "1Gi",
            ["protocol_versions"] = new JsonArray(new JsonObject { ["protocol"] = "responses", ["version"] = "2.0.0" }),
            ["environment_variables"] = new JsonObject
            {
                ["AZURE_AI_MODEL_DEPLOYMENT_NAME"] = settings.Model,
                ["TOOLBOX_NAME"] = settings.ToolboxName,
            },
            ["code_configuration"] = new JsonObject
            {
                ["runtime"] = "dotnet_10",
                ["entry_point"] = new JsonArray("dotnet", "WorkshopIqAgent.dll"),
                ["dependency_resolution"] = "remote_build",
            },
        },
        ["description"] = "Hosted agent grounded in a Foundry IQ knowledge base through a toolbox",
        ["metadata"] = new JsonObject { ["workshop"] = "iq", ["attendee"] = settings.Prefix },
    };

    public async Task<JsonObject> DeployAsync(string root)
    {
        string folder = settings.Folder(root), zip = Path.Combine(folder, "agent.zip");
        RejectLinkedPath(folder);
        RejectLinkedPath(zip);
        if (!File.Exists(zip)) throw new InvalidOperationException("Missing agent.zip. Run package first.");
        using var stream = File.OpenRead(zip);
        if (stream.Length == 0 || stream.Length > 250L * 1024 * 1024)
            throw new InvalidOperationException("Code zip must be non-empty and no larger than 250 MB.");
        using (var bundle = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
            if (!bundle.Entries.Select(e => e.FullName).OrderBy(n => n).SequenceEqual(SourceFiles.OrderBy(n => n)))
                throw new InvalidOperationException("Code zip must contain only the two expected source files. Run package again.");
        stream.Position = 0;
        string digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        await CheckToolboxAsync();
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(CodeMetadata().ToJsonString(), Encoding.UTF8, "application/json"), "metadata");
        var code = new StreamContent(stream);
        code.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(code, "code", "agent.zip");
        using var request = new HttpRequestMessage(HttpMethod.Post, AgentUrl("/versions")) { Content = content };
        request.Headers.Add("x-ms-code-zip-sha256", digest);
        IqReply reply = await SendRequestAsync(request, "https://ai.azure.com", new[] { 200 }, TimeSpan.FromMinutes(5));
        JsonObject version = reply.Json ?? throw new InvalidOperationException("Deployment omitted its version object.");
        ValidateHostedVersion(version);
        Console.WriteLine("Created " + settings.AgentName + " version " + version["version"] + " status: " + version["status"]);
        return version;
    }

    public async Task<JsonObject> LatestAsync()
    {
        JsonObject agent = await RequiredAsync(HttpMethod.Get, AgentUrl(""), "https://ai.azure.com", missing: "Run deploy first.");
        return LatestVersion(agent);
    }

    private JsonObject LatestVersion(JsonObject agent)
    {
        if ((string?)agent["name"] != settings.AgentName || agent["versions"]?["latest"] is not JsonObject version)
            throw new InvalidOperationException("Agent lookup did not return this prefix's latest version.");
        ValidateHostedVersion(version);
        return version;
    }

    public async Task<JsonObject> StatusAsync(string versionId)
    {
        ValidateVersionId(versionId);
        JsonObject version = await RequiredAsync(HttpMethod.Get, AgentUrl("/versions/" + Uri.EscapeDataString(versionId)), "https://ai.azure.com");
        ValidateHostedVersion(version);
        if ((string?)version["version"] != versionId) throw new InvalidOperationException("Foundry returned another agent version.");
        Console.WriteLine("Version " + versionId + " status: " + version["status"]);
        return version;
    }

    public async Task<JsonObject> WaitAsync(string versionId, TimeSpan? timeout = null)
    {
        ValidateVersionId(versionId);
        TimeSpan budget = timeout ?? TimeSpan.FromMinutes(15);
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromMinutes(15)) throw new ArgumentException("Wait timeout must be between zero and 15 minutes.");
        DateTimeOffset deadline = utcNow() + budget;
        while (true)
        {
            TimeSpan remaining = deadline - utcNow();
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Still not active after the wait timeout. Rerun wait, not deploy.");
            using var request = new HttpRequestMessage(HttpMethod.Get, AgentUrl("/versions/" + Uri.EscapeDataString(versionId)));
            IqReply reply = await SendRequestAsync(request, "https://ai.azure.com", new[] { 200 }, remaining);
            JsonObject version = reply.Json ?? throw new InvalidOperationException("Status omitted the version object.");
            ValidateHostedVersion(version);
            if ((string?)version["version"] != versionId) throw new InvalidOperationException("Foundry returned another agent version.");
            string status = RequiredText(version, "status").ToLowerInvariant();
            Console.WriteLine("Version " + versionId + " status: " + status);
            if (status == "active") return version;
            if (status is "failed" or "deleting" or "deleted")
                throw new InvalidOperationException("Hosted provisioning ended with status " + status + ". Inspect Foundry build/runtime diagnostics.");
            remaining = deadline - utcNow();
            if (remaining > TimeSpan.Zero) await delay(remaining < TimeSpan.FromSeconds(15) ? remaining : TimeSpan.FromSeconds(15));
        }
    }

    public async Task<JsonObject> InvokeAsync(string question, bool diagnostics = false)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Use a non-empty question.");
        using var request = new HttpRequestMessage(HttpMethod.Post, AgentUrl("/endpoint/protocols/openai/responses"))
        {
            Content = new StringContent(new JsonObject { ["input"] = question, ["store"] = false }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        IqReply reply = await SendRequestAsync(request, "https://ai.azure.com", new[] { 200 }, TimeSpan.FromMinutes(5));
        JsonObject response = reply.Json ?? throw new InvalidOperationException("Responses API returned no response object.");
        if ((string?)response["status"] != "completed" || response["error"] != null || response["incomplete_details"] != null)
            throw new InvalidOperationException("Hosted response did not complete. No automatic question retry was made.");
        JsonArray output = response["output"] as JsonArray ?? throw new InvalidOperationException("Hosted response omitted output.");
        bool successfulRetrieval = false;
        foreach (JsonObject item in output.OfType<JsonObject>())
        {
            string type = (string?)item["type"] ?? "", name = (string?)item["name"] ?? "";
            if (type == "mcp_approval_request") throw new InvalidOperationException("Unexpected tool approval request. Check the toolbox approval policy.");
            if (type is "mcp_call" or "function_call_output")
            {
                if (item["error"] != null && item["error"]!.ToJsonString() is not ("null" or "\"\"" or "{}") ||
                    (string?)item["status"] is "failed" or "incomplete")
                    throw new InvalidOperationException("Tool retrieval failed: " + SafeText(name + " " + item["error"], 1500));
                successfulRetrieval |= type == "mcp_call" && name.EndsWith("knowledge_base_retrieve", StringComparison.Ordinal) &&
                    item["output"] != null && !string.IsNullOrWhiteSpace(item["output"]!.ToString());
            }
            Console.WriteLine("output item: " + SafeText(type + " " + name, 300));
            if (diagnostics && type is "mcp_call" or "function_call_output")
                Console.WriteLine("Tool result (may contain internal document text): " + SafeText(item.ToJsonString(), 8000));
        }
        string answer = string.Join(Environment.NewLine, output.OfType<JsonObject>()
            .Where(item => (string?)item["type"] == "message")
            .SelectMany(item => (item["content"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            .Where(item => (string?)item["type"] == "output_text").Select(item => (string?)item["text"] ?? ""));
        if (string.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException("Hosted response contained no answer text.");
        Console.WriteLine(SafeText(answer, 16000));
        Console.WriteLine("Session: " + SafeText((string?)response["agent_session_id"] ?? "(not returned)", 300));
        foreach (JsonObject annotation in output.OfType<JsonObject>()
            .SelectMany(item => (item["content"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            .SelectMany(part => (part["annotations"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()))
        {
            string? url = (string?)annotation["url"] ?? (string?)annotation["url_citation"]?["url"];
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0)
                Console.WriteLine("Source: " + SafeText(url!, 2000));
        }
        Console.WriteLine(successfulRetrieval
            ? "A non-empty knowledge-base tool result was observed. Review the answer/citations against its evidence."
            : "No successful knowledge-base call is visible in this output. Answer wording alone does not prove grounding or an auth failure.");
        return response;
    }

    public static void ValidateVersionId(string version)
    {
        if (!Regex.IsMatch(version, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,79}\z") || version is "." or "..")
            throw new ArgumentException("Use a valid version identifier, not a path or URL.");
    }

    private void ValidateHostedVersion(JsonObject version)
    {
        if ((string?)version["name"] != settings.AgentName || (string?)version["definition"]?["kind"] != "hosted")
            throw new InvalidOperationException("Expected this prefix's hosted IQ agent version.");
        ValidateVersionId(RequiredText(version, "version"));
        RequiredText(version, "status");
    }

    private static void RejectLinkedPath(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing a linked generated-code path: " + path);
    }
}
