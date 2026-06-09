using System;
using Cidonix.UniBridge.MCP.Editor.Models;
using Cidonix.UniBridge.MCP.Editor.Security;

namespace Cidonix.UniBridge.MCP.Editor
{
    /// <summary>
    /// Represents a recorded connection attempt with validation outcome
    /// </summary>
    [Serializable]
    class ConnectionRecord
    {
        public ConnectionInfo Info;
        public ValidationStatus Status;
        public string ValidationReason;
        public ConnectionIdentity Identity; // Cached identity for fast comparison
        public bool DialogShown; // Whether approval dialog was shown for this connection identity
    }
}
