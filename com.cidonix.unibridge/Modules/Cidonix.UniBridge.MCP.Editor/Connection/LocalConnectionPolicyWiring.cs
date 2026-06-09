using UnityEditor;

namespace Cidonix.UniBridge.MCP.Editor.Connection
{
    /// <summary>
    /// Installs the local-only MCP connection policy for UniBridge.
    /// </summary>
    static class LocalConnectionPolicyWiring
    {
        [InitializeOnLoadMethod]
        static void Init()
        {
            Apply();
        }

        internal static void Apply()
        {
            ConnectionCensus.SetPolicy(ConnectionPolicy.Unlimited);
        }
    }
}
