using System.Net;
using System.Text;
using System.Text.Json.Nodes;

public sealed record ApimCapturedRequest(string Method, string Uri, string? Authorization, string[] Headers, JsonObject? Body);

public sealed class ApimOfflineHandler : HttpMessageHandler
{
    public const string Tenant = "00000000-0000-0000-0000-000000000000";
    public const string Broker = "11111111-1111-1111-1111-111111111111";
    public List<ApimCapturedRequest> Requests = [];
    public string Mode = "";
    public string FailurePath = "";
    public static string Jwt(string audience = Broker, string scope = "user_impersonation", string tenant = Tenant)
    {
        string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Encode("""{"alg":"none"}""") + "." + Encode(new JsonObject
        {
            ["aud"] = audience,
            ["tid"] = tenant,
            ["scp"] = scope,
            ["preferred_username"] = "offline@example.test",
            ["oid"] = "offline-user",
            ["azp"] = "04b07795-8ddb-461a-bbee-02f9e1bf7b46"
        }.ToJsonString()) + ".offline";
    }

    public static JsonObject BaseKb() => new()
    {
        ["name"] = "alice01-kb",
        ["retrievalInstructions"] = "Use alice01-kb-blob and alice01-kb-sharepoint internally, alice01-kb-webiq publicly. Do not send internal text to Web IQ.",
        ["answerInstructions"] = "Cite evidence.",
        ["models"] = new JsonArray(new JsonObject
        {
            ["kind"] = "azureOpenAI",
            ["azureOpenAIParameters"] = new JsonObject
            {
                ["resourceUri"] = "https://offline.openai.azure.com",
                ["deploymentId"] = "offline-chat",
                ["modelName"] = "gpt-5-mini"
            }
        })
    };

    public static JsonArray References() => new(
        new JsonObject
        {
            ["id"] = "0",
            ["type"] = "searchIndex",
            ["sourceData"] = new JsonObject
            {
                ["source/file_name"] = "travel.pdf",
                ["source/url"] = "https://private.example.test/travel.pdf",
                ["page_from"] = 2,
                ["page_to"] = 4,
                ["chunk"] = "private passage synthetic-key"
            }
        },
        new JsonObject
        {
            ["id"] = "1",
            ["type"] = "searchIndex",
            ["sourceData"] = new JsonObject
            {
                ["sourceFile"] = "report.pdf",
                ["webUrl"] = "https://tenant.sharepoint.com/report.pdf",
                ["page"] = 7,
                ["pageTo"] = 7
            }
        },
        new JsonObject
        {
            ["id"] = "2",
            ["type"] = "mcpServer",
            ["sourceData"] = new JsonObject { ["content"] = """{"title":"Public evidence","url":"https://example.test/public#section"}""" }
        });

    public void Reset(string mode = "", string failurePath = "")
    {
        Requests.Clear(); Mode = mode; FailurePath = failurePath;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(token))?.AsObject();
        Requests.Add(new(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
            request.Headers.Select(h => h.Key).ToArray(), body));
        string path = request.RequestUri.AbsolutePath;
        bool failure = FailurePath.Length == 0 || path.Contains(FailurePath, StringComparison.Ordinal);
        if (Mode == "network" && failure) throw new HttpRequestException("network failure Bearer " + Jwt() + " synthetic-key");
        if (Mode == "timeout" && failure) throw new TaskCanceledException();
        int status = 200;
        JsonObject result;
        if (path.StartsWith("/iq/", StringComparison.Ordinal))
        {
            if (request.Headers.Authorization?.Parameter != Jwt() || body?["messages"]?[0]?["content"]?[0]?["text"]?.ToString() == "hello")
            {
                status = Mode == "bad-rejection" ? 200 : 401;
                result = new JsonObject { ["error"] = "Rejected" };
            }
            else
            {
                result = new JsonObject
                {
                    ["response"] = new JsonArray(new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject
                        {
                            ["text"] = Mode == "echo" ? "synthetic-key synthetic-broker-secret Bearer " + Jwt() : "Offline evidence."
                        })
                    }),
                    ["activity"] = new JsonArray(new JsonObject { ["type"] = "mcpServer", ["knowledgeSourceName"] = "iq-workshop-webiq", ["count"] = 1 }),
                    ["references"] = References()
                };
                if (Mode == "activity-error") result["activity"]![0]!["error"] = new JsonObject { ["message"] = "synthetic-key source failed" };
                if (Mode == "shape") result["response"] = new JsonObject();
                if (Mode == "activity-shape") result["activity"] = new JsonArray("not an object");
                if (Mode == "reference-shape") result["references"] = new JsonObject();
                if (Mode == "no-references") result.Remove("references");
                if (Mode == "echo") result["references"]!.AsArray().Add(new JsonObject
                {
                    ["id"] = "3",
                    ["sourceData"] = new JsonObject
                    {
                        ["content"] = new JsonObject { ["x-apikey"] = "unconfigured-key" }.ToJsonString(),
                        ["client_secret"] = "unconfigured-secret"
                    }
                });
            }
        }
        else if (path.Contains("/knowledgesources/iq-workshop-webiq", StringComparison.Ordinal))
        {
            result = new JsonObject
            {
                ["kind"] = Mode == "wrong-source" ? "searchIndex" : "mcpServer",
                ["mcpServerParameters"] = new JsonObject
                {
                    ["serverURL"] = Mode == "wrong-server" ? "https://wrong.example.test" : "https://api.microsoft.ai/v3/mcp",
                    ["authentication"] = new JsonObject { ["headers"] = new JsonObject { ["x-apikey"] = "unconfigured-shared-key" } }
                }
            };
        }
        else if (path.EndsWith("/knowledgebases/alice01-kb", StringComparison.Ordinal))
        {
            result = BaseKb();
            if (Mode == "no-model") result.Remove("models");
            if (Mode == "no-routing") result.Remove("retrievalInstructions");
        }
        else if (path.Contains("/knowledgesources/", StringComparison.Ordinal))
        {
            result = new JsonObject { ["kind"] = path.EndsWith("-kb-blob", StringComparison.Ordinal) ? "azureBlob" : "searchIndex" };
            if (Mode == "no-kind") result.Remove("kind");
        }
        else result = new JsonObject();
        if (failure)
        {
            if (Mode == "missing") status = 404;
            if (Mode == "partial") status = 206;
            if (Mode == "denied") status = 403;
            if (Mode == "unauthorized") status = 401;
            if (Mode == "throttled") status = 429;
            if (status >= 400) result = new JsonObject
            {
                ["error"] = "synthetic-key synthetic-broker-secret Bearer " + Jwt(),
                ["client_secret"] = "unconfigured-secret",
                ["x-apikey"] = "unconfigured-key",
                ["padding"] = new string('x', 10000)
            };
        }
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(Mode == "malformed" && failure ? "not JSON synthetic-key" : result.ToJsonString())
        };
        response.Headers.Add("x-ms-request-id", "offline-request");
        response.Headers.Add("x-correlation-id", "offline-correlation");
        response.Headers.Add("Retry-After", "5");
        response.Headers.Date = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        return response;
    }
}
