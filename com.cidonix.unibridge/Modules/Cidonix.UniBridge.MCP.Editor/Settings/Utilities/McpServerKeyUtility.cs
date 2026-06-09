using System.Text;
using Cidonix.UniBridge.MCP.Editor.Helpers;

namespace Cidonix.UniBridge.MCP.Editor.Settings.Utilities
{
    static class McpServerKeyUtility
    {
        public const string LegacyUniBridgeKey = "unibridge";

        public static string GetProjectServerKey(ProjectIdentity.Snapshot identity)
        {
            var slug = Slugify(identity.ProjectName);
            var id = identity.ProjectId ?? string.Empty;
            var suffix = id.Length >= 8 ? id.Substring(0, 8) : "project";
            return $"unibridge_{slug}_{suffix}";
        }

        public static string GetProjectServerKey()
        {
            return GetProjectServerKey(ProjectIdentity.GetOrCreate());
        }

        public static string GetCodexTomlSection(string serverKey)
        {
            return $"[mcp_servers.{serverKey}]";
        }

        static string Slugify(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "unity_project";

            var builder = new StringBuilder(value.Length);
            var lastWasSeparator = true;
            var previousWasLowerOrDigit = false;

            foreach (var c in value.Trim())
            {
                if (char.IsUpper(c) && previousWasLowerOrDigit && !lastWasSeparator)
                {
                    builder.Append('_');
                    lastWasSeparator = true;
                }

                if (c >= 'A' && c <= 'Z')
                {
                    builder.Append(char.ToLowerInvariant(c));
                    lastWasSeparator = false;
                    previousWasLowerOrDigit = true;
                }
                else if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    builder.Append(c);
                    lastWasSeparator = false;
                    previousWasLowerOrDigit = true;
                }
                else if (!lastWasSeparator)
                {
                    builder.Append('_');
                    lastWasSeparator = true;
                    previousWasLowerOrDigit = false;
                }
                else
                {
                    previousWasLowerOrDigit = false;
                }
            }

            var slug = builder.ToString().Trim('_');
            return string.IsNullOrWhiteSpace(slug) ? "unity_project" : slug;
        }
    }
}
