using System;
using System.IO;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
namespace UnityEngine
{
    public static class Application { public static bool isPlaying; public static string dataPath; }
    public static class Mathf { public static int Clamp(int value,int min,int max)=>Math.Min(max,Math.Max(min,value)); }
}
namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    public enum ToolExecutionPolicy { Auto, ReadOnly, Mutating }
    public static class McpToolRegistry { public static string SanitizeToolName(string name)=>name; }
    public static class ToolExecutionScheduler
    {
        public static Action Cancellation;
        public static void ThrowIfCancellationRequested()=>Cancellation?.Invoke();
        public static Task YieldIfNotCancelledAsync(){ThrowIfCancellationRequested();return Task.CompletedTask;}
    }
}
namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    internal sealed class UnityProfilerCaptureBackend : IProfilerCaptureBackend
    {
        public static IProfilerCaptureBackend Current;
        public bool HasFrames=>Current.HasFrames;
        public ProfilerCaptureState ReadState()=>Current.ReadState();
        public void Pause()=>Current.Pause();
        public void ApplyState(ProfilerCaptureState state)=>Current.ApplyState(state);
        public bool Save(string path)=>Current.Save(path);
        public bool Load(string path)=>Current.Load(path);
        public void Clear()=>Current.Clear();
    }
#if BOTTOM_UP
    internal sealed class UnityRecordedProfilerSource : IRecordedProfilerSource
    {
        public static RecordedProfilerThread Header;
        public int FirstFrame=>Header.FrameIndex;
        public int LastFrame=>Header.FrameIndex;
        public int NextFrame(int i)=>-1;
        public int PreviousFrame(int i)=>-1;
        public int ThreadCount(int i)=>1;
        public IRecordedProfilerView Open(int frame,int thread)=>new View();
        sealed class View:IRecordedProfilerView
        {
            public bool Valid=>true;
            public RecordedProfilerThread CopyHeader()=>Header;
            public RecordedProfilerSample CopySample(int index,bool metadata)=>Header.Samples[index];
            public void Dispose(){}
        }
    }
#endif
}
namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class RuntimeProfiler
    {
        const int DefaultTimeoutMs=30000;
        public static string Storage;
        public static int Exports;
        public static object RunCapture(RuntimeProfilerParams p,string op)=>CaptureLease(p,op);
#if BOTTOM_UP
        public static Task<object> RunBottomUp(RuntimeProfilerParams p)=>RecordedBottomUp(p);
#endif
        static string SavePayload(object payload,string name){Directory.CreateDirectory(Storage);string path=Path.Combine(Storage,"owned-"+(++Exports)+".json");File.WriteAllText(path,McpJson.SerializeObject(payload));return path;}
    }
}
