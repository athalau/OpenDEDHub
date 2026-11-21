using System;
using System.IO;

namespace OpenDEDHub
{
    internal static class ConfigLoader
    {
        private const string DefaultVidPid = "VID_2E8A&PID_000A";
        private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "config.ini");

        public static string GetTargetVidPid(Action<string> log = null)
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    CreateDefaultConfig(log);
                    return DefaultVidPid;
                }

                foreach (string rawLine in File.ReadAllLines(ConfigPath))
                {
                    string line = rawLine.Trim();

                    // Skip empty lines, comments, and [Section] headers
                    if (string.IsNullOrEmpty(line) || line.StartsWith(';') || line.StartsWith('#') || line.StartsWith('['))
                        continue;

                    int equalsIndex = line.IndexOf('=');
                    if (equalsIndex <= 0)
                        continue;

                    string key = line[..equalsIndex].Trim();
                    string value = line[(equalsIndex + 1)..].Trim();

                    if (key.Equals("TargetVidPid", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }

                log?.Invoke($"Config Warning: 'TargetVidPid' not found in config.ini. Falling back to {DefaultVidPid}.");
            }
            catch (Exception ex)
            {
                log?.Invoke($"Config Error: Failed to read config.ini ({ex.Message}). Using default {DefaultVidPid}.");
            }

            return DefaultVidPid;
        }

        private static void CreateDefaultConfig(Action<string> log)
        {
            try
            {
                string defaultContent =
                    "; OpenDEDHub Configuration\r\n" +
                    "; Find your Hardware ID in Windows Device Manager -> Simgears DED -> Properties -> Details -> Hardware Ids\r\n" +
                    "[USB]\r\n" +
                    $"TargetVidPid={DefaultVidPid}\r\n";

                File.WriteAllText(ConfigPath, defaultContent);
                log?.Invoke($"Created default configuration file at: {ConfigPath}");
            }
            catch (Exception ex)
            {
                log?.Invoke($"Config Error: Could not create default config.ini ({ex.Message}).");
            }
        }
    }
}
