#nullable enable

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

public sealed class HostedWorkshop
{
    public const string Question = "Give me a tip about foundry iq.";
    public const string ToolQuestion = "What is the workshop tip for indexer? Quote it exactly.";
    // Match the current azure-ai-projects allow_preview agent-operation header.
    public const string Features = "A2AAgents=V1Preview,WorkflowAgents=V1Preview,DraftAgents=V1Preview," +
        "VoiceAgents=V1Preview,DigitalWorker=V1Preview,GitHubCopilot=V1Preview,Skills=V1Preview," +
        "MicrosoftCopilot=V1Preview,ModelRouterControls=V1Preview";
    private static readonly string[] SourceFiles = { "WorkshopAgent.csproj", "Program.cs", "WorkshopTips.cs" };
    private readonly HostedSettings settings;
    private readonly HttpClient client;
    private readonly Func<Task<string>> getToken;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<TimeSpan, Task> delay;

    public HostedWorkshop(HostedSettings settings, HttpClient client, Func<Task<string>>? getToken = null,
        Func<DateTimeOffset>? utcNow = null, Func<TimeSpan, Task>? delay = null)
    {
        settings.Validate();
        this.settings = settings;
        this.client = client;
        this.getToken = getToken ?? settings.GetTokenAsync;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.delay = delay ?? (duration => Task.Delay(duration));
    }

    public void WriteFiles(string root)
    {
        string folder = settings.Folder(root);
        Directory.CreateDirectory(folder);
        RejectLinkedPath(folder);
        foreach (string name in SourceFiles) RejectLinkedPath(Path.Combine(folder, name));
        File.WriteAllText(Path.Combine(folder, SourceFiles[0]), HostedSources.Project + Environment.NewLine);
        File.WriteAllText(Path.Combine(folder, SourceFiles[1]), HostedSources.Program + Environment.NewLine);
        File.WriteAllText(Path.Combine(folder, SourceFiles[2]), HostedSources.Tips + Environment.NewLine);
        Console.WriteLine("Wrote C# hosted-agent source to " + folder);
    }

    public string Package(string root)
    {
        string folder = settings.Folder(root);
        RejectLinkedPath(folder);
        foreach (string name in SourceFiles)
        {
            string path = Path.Combine(folder, name);
            RejectLinkedPath(path);
            if (!File.Exists(path))
                throw new InvalidOperationException("Missing " + name + ". Run write-agent first.");
        }
        string zip = Path.Combine(folder, "agent.zip");
        RejectLinkedPath(zip);
        using (var stream = new FileStream(zip, FileMode.Create, FileAccess.Write))
        using (var bundle = new ZipArchive(stream, ZipArchiveMode.Create))
            foreach (string name in SourceFiles)
                bundle.CreateEntryFromFile(Path.Combine(folder, name), name, CompressionLevel.Optimal);
        Console.WriteLine("Packaged exactly the three C# source files: " + zip);
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
            ["environment_variables"] = new JsonObject { ["AZURE_AI_MODEL_DEPLOYMENT_NAME"] = settings.Model },
            ["code_configuration"] = new JsonObject
            {
                ["runtime"] = "dotnet_10",
                ["entry_point"] = new JsonArray("dotnet", "WorkshopAgent.dll"),
                ["dependency_resolution"] = "remote_build",
            },
        },
        ["description"] = "Workshop hosted C# agent with one function tool",
        ["metadata"] = new JsonObject { ["workshop"] = "iq", ["attendee"] = settings.Prefix },
    };

    public async Task<JsonObject> DeployAsync(string root)
    {
        string zip = Path.Combine(settings.Folder(root), "agent.zip");
        RejectLinkedPath(settings.Folder(root));
        RejectLinkedPath(zip);
        if (!File.Exists(zip)) throw new InvalidOperationException("Missing agent.zip. Run package first.");
        using var stream = File.OpenRead(zip);
        if (stream.Length == 0 || stream.Length > 250L * 1024 * 1024)
            throw new InvalidOperationException("Code zip must be non-empty and no larger than 250 MB.");
        using (var bundle = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (!bundle.Entries.Select(e => e.FullName).OrderBy(n => n).SequenceEqual(SourceFiles.OrderBy(n => n)))
                throw new InvalidOperationException("Code zip must contain only the three expected source files. Run package again.");
        }
        stream.Position = 0;
        string digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(CodeMetadata().ToJsonString(), Encoding.UTF8, "application/json"), "metadata");
        var code = new StreamContent(stream);
        code.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(code, "code", "agent.zip");
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/versions")) { Content = content };
        request.Headers.Add("x-ms-code-zip-sha256", digest);
        JsonObject version = await SendAsync(request);
        ValidateVersion(version);
        Console.WriteLine("Created version " + version["version"] + " status: " + version["status"]);
        return version;
    }

    public async Task<JsonObject> LatestAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(""));
        JsonObject agent = await SendAsync(request);
        if ((string?)agent["name"] != settings.AgentName || agent["versions"]?["latest"] is not JsonObject version)
            throw new InvalidOperationException("Agent lookup did not return this prefix's latest version.");
        ValidateVersion(version);
        return version;
    }

    public async Task<JsonObject> StatusAsync(string versionId)
    {
        ValidateVersionId(versionId);
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("/versions/" + Uri.EscapeDataString(versionId)));
        JsonObject version = await SendAsync(request);
        ValidateVersion(version);
        if ((string?)version["version"] != versionId)
            throw new InvalidOperationException("Foundry returned a different version than requested.");
        Console.WriteLine("Version " + versionId + " status: " + version["status"]);
        return version;
    }

    public async Task<JsonObject> WaitAsync(string versionId, TimeSpan? timeout = null)
    {
        ValidateVersionId(versionId);
        TimeSpan budget = timeout ?? TimeSpan.FromMinutes(15);
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromMinutes(15))
            throw new ArgumentException("Wait timeout must be greater than zero and at most 15 minutes.");
        DateTimeOffset deadline = utcNow() + budget;
        while (true)
        {
            TimeSpan remaining = deadline - utcNow();
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("Agent version is still not active after the wait timeout. Run wait again; do not redeploy.");
            using var request = new HttpRequestMessage(HttpMethod.Get, Url("/versions/" + Uri.EscapeDataString(versionId)));
            JsonObject version = await SendAsync(request, remaining);
            ValidateVersion(version);
            if ((string?)version["version"] != versionId)
                throw new InvalidOperationException("Foundry returned a different version than requested.");
            string status = ((string?)version["status"] ?? "").ToLowerInvariant();
            Console.WriteLine("Version " + versionId + " status: " + status);
            if (status == "active") return version;
            if (status is "failed" or "deleted" or "deleting")
                throw new InvalidOperationException("Agent provisioning ended with status " + status + ". Inspect Foundry build/runtime diagnostics.");
            remaining = deadline - utcNow();
            if (remaining > TimeSpan.Zero)
                await delay(remaining < TimeSpan.FromSeconds(15) ? remaining : TimeSpan.FromSeconds(15));
        }
    }

    public async Task<JsonObject> InvokeAsync(string question, bool showOutputTypes = false)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Use a non-empty question.");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            Url("/endpoint/protocols/openai/responses"))
        {
            Content = new StringContent(new JsonObject { ["input"] = question, ["store"] = false }.ToJsonString(),
                Encoding.UTF8, "application/json"),
        };
        JsonObject response = await SendAsync(request);
        if ((string?)response["status"] != "completed" || response["error"] != null ||
            response["incomplete_details"] != null)
            throw new InvalidOperationException("Hosted response did not complete. No automatic question retry was made.");
        if (response["output"] is not JsonArray output)
            throw new InvalidOperationException("Hosted response omitted its output array.");
        string answer = string.Join(Environment.NewLine, output.OfType<JsonObject>()
            .Where(item => (string?)item["type"] == "message")
            .SelectMany(item => (item["content"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            .Where(item => (string?)item["type"] == "output_text").Select(item => (string?)item["text"] ?? ""));
        if (string.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException("Hosted response contained no answer text.");
        Console.WriteLine(SafeText(answer, 16000));
        Console.WriteLine("Session: " + SafeText((string?)response["agent_session_id"] ?? "(not returned)", 300));
        if (showOutputTypes)
        {
            foreach (JsonObject item in output.OfType<JsonObject>())
                Console.WriteLine("output item: " + SafeText((string?)item["type"] ?? "(unknown)", 100));
            Console.WriteLine("Review the tool-call output/logs. An exact-looking answer alone does not prove a function call.");
        }
        return response;
    }

    public async Task CleanupAsync(bool confirm)
    {
        if (!confirm) throw new ArgumentException("Cleanup requires exactly: cleanup --confirm.");
        JsonObject latest = await LatestAsync();
        if ((string?)latest["metadata"]?["workshop"] != "iq" ||
            (string?)latest["metadata"]?["attendee"] != settings.Prefix)
            throw new InvalidOperationException("Refusing cleanup: latest version is not marked as this prefix's workshop agent.");
        using var request = new HttpRequestMessage(HttpMethod.Delete, Url(""));
        await SendAsync(request);
        Console.WriteLine("Deleted only " + settings.AgentName + " and its versions. Local source files and shared resources retained.");
    }

    public static void ValidateVersionId(string version)
    {
        if (!Regex.IsMatch(version, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,79}\z") || version is "." or "..")
            throw new ArgumentException("Use a valid Foundry version identifier, not a path or URL.");
    }

    private void ValidateVersion(JsonObject version)
    {
        if ((string?)version["name"] != settings.AgentName || (string?)version["definition"]?["kind"] != "hosted")
            throw new InvalidOperationException("Foundry did not return this prefix's hosted agent version.");
        ValidateVersionId((string?)version["version"] ?? "");
        if (string.IsNullOrWhiteSpace((string?)version["status"]))
            throw new InvalidOperationException("Foundry version omitted its provisioning status.");
    }

    private string Url(string suffix) => settings.ProjectEndpoint + "/agents/" +
        Uri.EscapeDataString(settings.AgentName) + suffix + "?api-version=v1";

    private async Task<JsonObject> SendAsync(HttpRequestMessage request, TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
        string token = await getToken().WaitAsync(cancellation.Token);
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("Token acquisition returned an invalid bearer token.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Foundry-Features", Features);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);
        string body = (await response.Content.ReadAsStringAsync(cancellation.Token)).Replace(token, "<redacted>");
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            Console.Error.WriteLine("HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase);
            Console.Error.WriteLine("UTC (Date): " + response.Headers.Date);
            foreach (string header in new[] { "x-ms-request-id", "apim-request-id", "x-ms-correlation-request-id" })
                if (response.Headers.TryGetValues(header, out var values))
                    Console.Error.WriteLine(header + ": " + SafeText(string.Join(", ", values).Replace(token, "<redacted>"), 300));
            Console.Error.WriteLine("Response body: " + SafeText(body, 4000));
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict && request.Method == HttpMethod.Delete)
                Console.Error.WriteLine("Active sessions may block deletion. Close them in Foundry and rerun cleanup. This lab never force-deletes sessions.");
            throw new InvalidOperationException("Foundry request failed with HTTP " + (int)response.StatusCode + ". No automatic retry was made.");
        }
        try { return JsonNode.Parse(body) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidOperationException("Foundry returned invalid JSON instead of an object."); }
    }

    private static void RejectLinkedPath(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to read or overwrite a linked path: " + path);
    }

    private static string SafeText(string text, int max) =>
        new string(text.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').Take(max).ToArray());
}
