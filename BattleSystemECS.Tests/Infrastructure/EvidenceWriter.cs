using System;
using System.IO;
using System.Text.Json;

namespace BattleSystemECS.Tests.Infrastructure
{
    internal static class EvidenceWriter
    {
        public static void WriteJsonIfRequested(string environmentVariable, object value)
        {
            string? requestedPath = Environment.GetEnvironmentVariable(environmentVariable);
            if (string.IsNullOrWhiteSpace(requestedPath)) return;
            string path = Path.GetFullPath(requestedPath);
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Evidence path must have a parent directory.");
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
    }
}
