using System;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    /// <summary>
    /// Compatibility stub for projects whose AssetDatabase still references the old parameter file.
    /// New code should use <see cref="CaptureViewParams"/>.
    /// </summary>
    [Obsolete("Use CaptureViewParams.")]
    public record CaptureGameViewParams : CaptureViewParams
    {
    }
}
