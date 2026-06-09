using Cidonix.UniBridge.MCP.Editor.Models;

namespace Cidonix.UniBridge.MCP.Editor.Security
{
    /// <summary>
    /// Determines the trust level of a connection for UI presentation.
    /// This is purely for user communication - not for enforcement.
    /// </summary>
    enum ConnectionTrustLevel
    {
        /// <summary>
        /// Unknown or untrusted - server not signed or not from the configured publisher
        /// </summary>
        Unknown,

        /// <summary>
        /// Partially trusted - recognized UniBridge server but unknown client
        /// </summary>
        Untrusted,

        /// <summary>
        /// Fully trusted - recognized UniBridge server with signed client
        /// </summary>
        Trusted
    }

    /// <summary>
    /// Classifies connections into trust levels for UI presentation.
    /// </summary>
    static class ConnectionTrustClassifier
    {
        /// <summary>
        /// Determine the trust level for a connection based on code signing validation.
        /// </summary>
        public static ConnectionTrustLevel DetermineTrustLevel(ConnectionInfo connectionInfo)
        {
            if (connectionInfo == null)
                return ConnectionTrustLevel.Unknown;

            // Step 1: Validate server is the recognized UniBridge MCP server.
            bool isTrustedServer = IsTrustedServer(connectionInfo.Server);

            // Step 2: Check if client is signed
            bool isClientSigned = IsClientSigned(connectionInfo.Client);

            // Trust logic:
            // - Unknown: Server is not recognized or not signed.
            // - Untrusted: Server is recognized, but client is unsigned/unknown.
            // - Trusted: Server is recognized AND client is signed.
            if (!isTrustedServer)
            {
                return ConnectionTrustLevel.Unknown;
            }
            else if (!isClientSigned)
            {
                return ConnectionTrustLevel.Untrusted;
            }
            else
            {
                return ConnectionTrustLevel.Trusted;
            }
        }

        /// <summary>
        /// Check if the server process is signed by the configured UniBridge publisher.
        /// </summary>
        static bool IsTrustedServer(ProcessInfo server)
        {
            if (server?.Identity == null)
                return false;

            // Server must be signed and signature must be valid
            if (!server.Identity.IsSigned || !server.Identity.SignatureValid)
                return false;

            // Check if publisher matches the configured UniBridge credentials.
            #if UNITY_EDITOR_WIN
            return server.Identity.MatchesPublisher(ValidatedConfigs.Unity.WindowsPublisher);
            #elif UNITY_EDITOR_OSX
            return server.Identity.MatchesPublisher(ValidatedConfigs.Unity.MacTeamId);
            #else
            // On Linux, code signing is not typically used
            // Consider unsigned servers as untrusted (safer default).
            return false;
            #endif
        }

        /// <summary>
        /// Check if the client process has a valid code signature.
        /// </summary>
        static bool IsClientSigned(ProcessInfo client)
        {
            if (client?.Identity == null)
                return false;

            // Client just needs to be signed with a valid signature (any publisher)
            return client.Identity.IsSigned && client.Identity.SignatureValid;
        }

        /// <summary>
        /// Get a user-friendly description of the trust level.
        /// </summary>
        public static string GetTrustLevelDescription(ConnectionTrustLevel trustLevel)
        {
            return trustLevel switch
            {
                ConnectionTrustLevel.Unknown => "Unknown or untrusted connection",
                ConnectionTrustLevel.Untrusted => "Partially trusted connection",
                ConnectionTrustLevel.Trusted => "Trusted connection",
                _ => "Unknown"
            };
        }
    }
}
