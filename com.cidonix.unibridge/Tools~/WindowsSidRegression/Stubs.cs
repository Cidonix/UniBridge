using System;
using System.Collections.Generic;

namespace UnityEngine
{
    static class Debug
    {
        public static void LogWarning(object message) { }
    }
}

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    static class McpLog
    {
        static readonly List<string> messages = new List<string>();
        public static void Log(string message) { lock (messages) messages.Add(message); }
        public static void Warning(string message) { lock (messages) messages.Add("WARNING: " + message); }
        public static void Error(string message) { lock (messages) messages.Add("ERROR: " + message); }
        public static void Clear() { lock (messages) messages.Clear(); }
        public static string[] Messages { get { lock (messages) return messages.ToArray(); } }
    }
}
