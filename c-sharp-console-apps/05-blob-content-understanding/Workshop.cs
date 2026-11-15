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
}
