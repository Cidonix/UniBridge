using System.Diagnostics;
using System.IO;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class RuntimeProfiler
    {
        static object CaptureLease(RuntimeProfilerParams parameters, string operation)
        {
            var lease = new ProfilerCaptureLease(Path.GetDirectoryName(Application.dataPath), Process.GetCurrentProcess().Id, new UnityProfilerCaptureBackend());
            var result = lease.Execute(operation, parameters.ExpectedProjectRoot, parameters.CapturePath, parameters.CaptureSha256,
                parameters.CaptureLeaseId, ToolExecutionScheduler.ThrowIfCancellationRequested);
            return result.errorCode == null ? Response.Success("Profiler capture lease operation completed.", result) : Response.Error(result.errorCode, result);
        }
    }
}
