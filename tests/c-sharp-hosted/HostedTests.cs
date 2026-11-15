using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

int cases = 0;
void Check(bool condition, string message)
{
    cases++;
    if (!condition) throw new Exception(message);
}
async Task Fails(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception error) when (error.Message.Contains(message, StringComparison.OrdinalIgnoreCase))
    {
        cases++;
        return;
    }
    throw new Exception("Expected failure: " + message);
}
string root = Path.GetFullPath(Path.Combine(".workshop", "hosted13-test-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
try
{
    var settings = new HostedSettings("00000000-0000-0000-0000-000000000000",
        "https://workshop.services.ai.azure.com/api/projects/project", "workshop-chat", "alice01");
    File.WriteAllText(Path.Combine(root, ".env"), "WORKSHOP_PREFIX=wrong01\nAZURE_AI_MODEL_DEPLOYMENT_NAME=wrong-model\n");
    File.WriteAllText(Path.Combine(root, ".env.azure"), "WORKSHOP_PREFIX='alice01'\n" +
        "AZURE_TENANT_ID=" + settings.Tenant + "\nAZURE_AI_PROJECT_ENDPOINT=" + settings.ProjectEndpoint +
        "\nAZURE_AI_MODEL_DEPLOYMENT_NAME=workshop-chat\n");
    var handler = new HostedOfflineHandler();
    using var client = new HttpClient(handler);
    int tokens = 0;
    var clock = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    var lab = new HostedWorkshop(settings, client, () => { tokens++; return Task.FromResult("synthetic-hosted-token"); },
        () => clock, duration => { clock += duration; return Task.CompletedTask; });
    lab.WriteFiles(root);
    Check(handler.Requests.Count == 0 && tokens == 0, "Write acquires credentials or contacts Azure");
    string folder = settings.Folder(root);
    string tipsPath = Path.Combine(folder, "WorkshopTips.cs");
    File.AppendAllText(tipsPath, "\n// Synthetic edited tip experiment.\n");
    File.WriteAllText(Path.Combine(folder, ".env"), "TOKEN=must-not-upload");
    Directory.CreateDirectory(Path.Combine(folder, "bin"));
    File.WriteAllText(Path.Combine(folder, "bin", "should-not-upload.dll"), "synthetic");
    string zip = lab.Package(root);
    using (var bundle = ZipFile.OpenRead(zip))
    {
        Check(bundle.Entries.Select(e => e.FullName).OrderBy(n => n).SequenceEqual(
            new[] { "Program.cs", "WorkshopAgent.csproj", "WorkshopTips.cs" }), "Archive uploads secrets or build artifacts");
        using var reader = new StreamReader(bundle.GetEntry("WorkshopTips.cs")!.Open());
        Check(reader.ReadToEnd().Contains("Synthetic edited tip"), "Packaging overwrites edits");
    }
    Check(tokens == 0 && handler.Requests.Count == 0, "Package contacts Azure");
    await lab.DeployAsync(root);
    Check(handler.Requests.Count == 1 && handler.Requests[0].Method == "POST", "Deploy performs implicit steps");
    CapturedHostedRequest upload = handler.Requests[0];
    Check(upload.Url.EndsWith("/agents/alice01-hosted-agent/versions?api-version=v1"), "Upload endpoint");
    Check(upload.Sha == Convert.ToHexString(SHA256.HashData(upload.Code!)).ToLowerInvariant(), "Archive digest mismatch");
    Check(upload.Code!.SequenceEqual(File.ReadAllBytes(zip)), "Deploy changes existing archive");
    Check(upload.Body!["definition"]!["code_configuration"]!["runtime"]!.ToString() == "dotnet_10", "Not a CSharp runtime");
    Check(JsonNode.DeepEquals(upload.Body, lab.CodeMetadata()), "Multipart metadata shape");
    Check(upload.Body!.ToJsonString().Contains(settings.Model) && !upload.Body.ToJsonString().Contains("TOKEN"), "Deployment copies secrets");
    foreach (var request in handler.Requests)
    {
        Check(request.Authorization == "Bearer synthetic-hosted-token", "Foundry auth audience/header");
        Check(request.Features == HostedWorkshop.Features && !request.HasApiKey, "Preview/auth headers");
    }

    handler.Reset();
    await lab.WaitAsync("1");
    Check(handler.Requests.Count == 2 && handler.Requests.All(r => r.Method == "GET" &&
        r.Url.Contains("/versions/1?")), "Wait creates version or switches version");
    Check(clock.Second == 15, "Wait poll interval");
    handler.Reset();
    await lab.StatusAsync("1");
    Check(handler.Requests.Count == 1 && handler.Requests[0].Method == "GET", "Status polls/writes");
    handler.Reset();
    await HostedAgentProgram.RunAsync(new[] { "status" }, root, client, () => Task.FromResult("synthetic-hosted-token"));
    Check(handler.Requests.Count == 1 && handler.Requests[0].Url.EndsWith("/agents/alice01-hosted-agent?api-version=v1"), "Latest lookup shape");

    foreach (string mode in new[] { "failed", "deleting", "deleted", "version-mismatch" })
    {
        handler.Reset(mode);
        await Fails(() => lab.WaitAsync("1"), mode == "version-mismatch" ? "different version" : mode);
        Check(handler.Requests.Count == 1, "Wait retries terminal provisioning error");
    }
    handler.Reset("creating");
    await Fails(() => lab.WaitAsync("1", TimeSpan.FromSeconds(20)), "wait timeout");
    Check(handler.Requests.Count == 2, "Timeout ignores remaining poll budget");

    handler.Reset();
    await lab.InvokeAsync(HostedWorkshop.Question);
    Check(handler.Requests.Count == 1 && handler.Requests[0].Method == "POST", "Invoke deploys or repeats question");
    Check(handler.Requests[0].Url.EndsWith("/endpoint/protocols/openai/responses?api-version=v1"), "Not hosted endpoint");
    Check(JsonNode.DeepEquals(handler.Requests[0].Body,
        new JsonObject { ["input"] = HostedWorkshop.Question, ["store"] = false }), "Model/session forwarded unexpectedly");
    handler.Reset();
    await lab.InvokeAsync(HostedWorkshop.ToolQuestion, true);
    Check(handler.Requests.Count == 1 && handler.Requests[0].Body!["input"]!.ToString() == HostedWorkshop.ToolQuestion, "Tool question hidden actions");

    foreach (string mode in new[] { "denied", "partial", "failed-response", "incomplete", "no-answer", "malformed", "echo" })
    {
        handler.Reset(mode);
        var output = new StringWriter();
        var errors = new StringWriter();
        TextWriter savedOutput = Console.Out, savedError = Console.Error;
        try
        {
            Console.SetOut(output);
            Console.SetError(errors);
            if (mode == "echo") await lab.InvokeAsync(HostedWorkshop.Question);
            else await Fails(() => lab.InvokeAsync(HostedWorkshop.Question), mode switch
            {
                "denied" => "HTTP 403",
                "partial" => "HTTP 206",
                "failed-response" or "incomplete" => "did not complete",
                "no-answer" => "no answer text",
                _ => "invalid JSON",
            });
        }
        finally { Console.SetOut(savedOutput); Console.SetError(savedError); }
        Check(handler.Requests.Count == 1, "Billable retry after failure");
        Check(!(output + errors.ToString()).Contains("synthetic-hosted-token"), "Credential echo leaked");
        if (mode == "denied")
            foreach (string expected in new[] { "UTC (Date):", "synthetic-request", "Response body:", "<redacted>" })
                Check(errors.ToString().Contains(expected), "Missing bounded HTTP diagnostics");
    }
    handler.Reset();
    var noAuth = new HostedWorkshop(settings, client, () => throw new InvalidOperationException("Synthetic authentication failure"));
    await Fails(() => noAuth.InvokeAsync("test"), "authentication failure");
    await Fails(() => lab.InvokeAsync(" "), "non-empty");
    await Fails(() => lab.WaitAsync("../1"), "identifier");
    await Fails(() => lab.CleanupAsync(false), "cleanup --confirm");
    Check(handler.Requests.Count == 0, "Validation fails after HTTP");
    handler.Reset();
    await lab.CleanupAsync(true);
    Check(handler.Requests.Count == 2 && handler.Requests[1].Method == "DELETE" &&
        handler.Requests[1].Url == settings.ProjectEndpoint + "/agents/alice01-hosted-agent?api-version=v1", "Cleanup scope/force");
    Check(File.Exists(tipsPath), "Cleanup removes source files");
    foreach (string mode in new[] { "other-prefix", "prompt-kind", "other-workshop" })
    {
        handler.Reset(mode);
        await Fails(() => lab.CleanupAsync(true), mode switch
        {
            "other-prefix" or "prompt-kind" => "this prefix",
            _ => "Refusing cleanup",
        });
        Check(handler.Requests.Count == 1 && handler.Requests[0].Method == "GET", "Unsafe delete");
    }
    handler.Reset("conflict");
    await Fails(() => lab.CleanupAsync(true), "HTTP 409");
    Check(handler.Requests.Count == 2 && !handler.Requests[1].Url.Contains("force"), "Force deletes active sessions");
    handler.Reset();
    await HostedAgentProgram.RunAsync(new[] { "run" }, root, client,
        () => Task.FromResult("synthetic-hosted-token"), () => clock,
        duration => { clock += duration; return Task.CompletedTask; });
    Check(handler.Requests.Count == 5 && handler.Requests.Count(r => r.Method == "POST") == 3 &&
        handler.Requests.All(r => r.Method != "DELETE"), "Run hides extra calls/cleanup");

    foreach (string command in new[] { "invoke", "invoke-tool", "deploy" })
    {
        handler.Reset();
        await HostedAgentProgram.RunAsync(new[] { command }, root, client, () => Task.FromResult("synthetic-hosted-token"));
        Check(handler.Requests.Count == 1 && handler.Requests[0].Method == "POST", "Independent command " + command);
    }
    string empty = Path.Combine(root, "empty");
    Directory.CreateDirectory(empty);
    handler.Reset();
    await Fails(() => { lab.Package(empty); return Task.CompletedTask; }, "write-agent");
    await Fails(() => lab.DeployAsync(empty), "package");
    Check(handler.Requests.Count == 0, "Missing local prerequisites auto-deploy");

    string invalidRoot = Path.Combine(root, "invalid");
    Directory.CreateDirectory(invalidRoot);
    File.WriteAllText(Path.Combine(invalidRoot, ".env"), "WORKSHOP_PREFIX=invalid!\n");
    foreach (string[] arguments in new[]
    {
        new[] { "cleanup" }, new[] { "cleanup", "--confirm", "other" }, new[] { "run", "extra" },
        new[] { "status", "../1" }, new[] { "invoke", "" }, new[] { "invoke", "one", "two" },
        new[] { "invoke-tool", "extra" }, new[] { "unknown" },
    })
        await Fails(() => HostedAgentProgram.RunAsync(arguments, invalidRoot, client), arguments[0] == "status"
            ? "identifier" : arguments[0] == "invoke" && arguments.Length == 2 ? "non-empty" : "help");
    foreach (string[] arguments in new[] { Array.Empty<string>(), new[] { "help" }, new[] { "--help" }, new[] { "-h" }, new[] { "HELP" } })
        Check(await HostedAgentProgram.RunAsync(arguments, invalidRoot, client) == 0, "Help requires config/auth");
    Check(handler.Requests.Count == 0 && Directory.GetFiles(invalidRoot).Length == 1, "Arguments/help side effects");
    Console.WriteLine($"Passed {cases} offline hosted-agent console cases.");
}
finally { Directory.Delete(root, recursive: true); }

public sealed record CapturedHostedRequest(string Method, string Url, string? Authorization, string? Features,
    bool HasApiKey, JsonObject? Body, byte[]? Code, string? Sha);

public sealed class HostedOfflineHandler : HttpMessageHandler
{
    public List<CapturedHostedRequest> Requests = new();
    public string Mode = "";
    private int polls;

    public void Reset(string mode = "") { Requests.Clear(); Mode = mode; polls = 0; }

    public JsonObject Version(string status = "active") => new JsonObject
    {
        ["name"] = Mode == "other-prefix" ? "bob01-hosted-agent" : "alice01-hosted-agent",
        ["version"] = Mode == "version-mismatch" ? "2" : "1",
        ["status"] = status,
        ["definition"] = new JsonObject { ["kind"] = Mode == "prompt-kind" ? "prompt" : "hosted" },
        ["metadata"] = new JsonObject { ["workshop"] = Mode == "other-workshop" ? "other" : "iq", ["attendee"] = "alice01" },
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        JsonObject? body = null;
        byte[]? code = null;
        if (request.Content is MultipartFormDataContent multipart)
        {
            foreach (HttpContent part in multipart)
            {
                string name = part.Headers.ContentDisposition!.Name!.Trim('"');
                if (name == "metadata") body = JsonNode.Parse(await part.ReadAsStringAsync(cancellation))!.AsObject();
                else if (name == "code") code = await part.ReadAsByteArrayAsync(cancellation);
                else throw new Exception("Unexpected multipart field");
            }
        }
        else if (request.Content != null) body = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellation))!.AsObject();
        Requests.Add(new CapturedHostedRequest(request.Method.Method, request.RequestUri!.ToString(),
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("Foundry-Features", out var features) ? features.Single() : null,
            request.Headers.Contains("api-key") || request.Headers.Contains("x-apikey"), body, code,
            request.Headers.TryGetValues("x-ms-code-zip-sha256", out var sha) ? sha.Single() : null));
        string text;
        int status = 200;
        string path = request.RequestUri.AbsolutePath;
        if (Mode == "denied") { status = 403; text = "{\"error\":\"synthetic-hosted-token\"}"; }
        else if (Mode == "partial") { status = 206; text = "{}"; }
        else if (Mode == "malformed") text = "{invalid";
        else if (request.Method == HttpMethod.Delete)
        {
            status = Mode == "conflict" ? 409 : 200;
            text = "{}";
        }
        else if (path.EndsWith("/responses"))
            text = new JsonObject
            {
                ["status"] = Mode == "failed-response" ? "failed" : Mode == "incomplete" ? "incomplete" : "completed",
                ["agent_session_id"] = "synthetic-session",
                ["output"] = new JsonArray(
                    new JsonObject { ["type"] = "function_call", ["name"] = "get_workshop_tip" },
                    new JsonObject
                    {
                        ["type"] = "message",
                        ["content"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "output_text",
                            ["text"] = Mode == "no-answer" ? "" : Mode == "echo" ? "synthetic-hosted-token" : "Synthetic workshop tip.",
                        }),
                    }),
            }.ToJsonString();
        else if (request.Method == HttpMethod.Post) text = Version("creating").ToJsonString();
        else if (path.Contains("/versions/"))
        {
            polls++;
            text = Version(Mode is "failed" or "deleted" or "deleting" or "creating" ? Mode :
                polls == 1 ? "creating" : "active").ToJsonString();
        }
        else text = new JsonObject { ["name"] = "alice01-hosted-agent", ["versions"] = new JsonObject { ["latest"] = Version() } }.ToJsonString();
        var response = new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        response.Headers.Date = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        response.Headers.Add("x-ms-request-id", "synthetic-request");
        return response;
    }
}
