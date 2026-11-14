#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

public static class Workshop
{
    public static void LoadEnvFile(string path)
    {
        if (!File.Exists(path))
            return;

        foreach (string line in File.ReadAllLines(path))
        {
            string setting = line.Trim();
            if (setting.Length == 0 || setting.StartsWith("#", StringComparison.Ordinal))
                continue;
            if (setting.StartsWith("export ", StringComparison.Ordinal))
                setting = setting.Substring(7).TrimStart();

            int equals = setting.IndexOf('=');
            if (equals <= 0)
                continue;
            string name = setting.Substring(0, equals).Trim();
            string value = setting.Substring(equals + 1).Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[value.Length - 1] == '"') ||
                 (value[0] == '\'' && value[value.Length - 1] == '\'')))
            {
                value = value.Substring(1, value.Length - 2);
            }
            else
            {
                int comment = value.IndexOf(" #", StringComparison.Ordinal);
                if (comment >= 0)
                    value = value.Substring(0, comment).TrimEnd();
            }
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public static async Task<JsonObject> ReadResponseAsync(HttpResponseMessage response, bool browse = false)
    {
        string body = await response.Content.ReadAsStringAsync();
        int status = (int)response.StatusCode;
        if (status >= 400 || (browse && status != 200 && status != 202))
        {
            Console.WriteLine($"HTTP: {status} {response.ReasonPhrase}");
            Console.WriteLine(body.Substring(0, Math.Min(body.Length, 8000)));
            return new JsonObject();
        }

        try
        {
            JsonObject? result = JsonNode.Parse(body) as JsonObject;
            if (result != null)
            {
                Console.WriteLine(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return result;
            }
        }
        catch (JsonException)
        {
        }
        Console.WriteLine("The response was not a JSON object. Check the service response and retry.");
        return new JsonObject();
    }

    public static bool TryGetRetryDelay(JsonObject page, out double seconds)
    {
        seconds = 0;
        string? delay;
        if (!(page["retryAfter"] is JsonValue value) || !value.TryGetValue<string>(out delay) ||
            string.IsNullOrWhiteSpace(delay))
        {
            Console.WriteLine("Pending crawl omitted a valid retryAfter. Retry later manually.");
            return false;
        }

        if (delay.EndsWith("s", StringComparison.Ordinal))
        {
            if (!double.TryParse(delay.Substring(0, delay.Length - 1), NumberStyles.Float,
                CultureInfo.InvariantCulture, out seconds))
            {
                Console.WriteLine("Could not interpret retryAfter. Retry later manually.");
                return false;
            }
        }
        else if (DateTimeOffset.TryParse(delay, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out DateTimeOffset retryAt))
        {
            seconds = Math.Max(0, (retryAt - DateTimeOffset.UtcNow).TotalSeconds);
        }
        else
        {
            Console.WriteLine("Could not interpret retryAfter. Retry later manually.");
            return false;
        }

        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || seconds > 120)
        {
            Console.WriteLine($"Server requested a {seconds}s wait; retry later manually.");
            return false;
        }
        return true;
    }
}
