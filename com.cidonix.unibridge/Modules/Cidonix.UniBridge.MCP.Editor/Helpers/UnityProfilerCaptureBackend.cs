using UnityEditorInternal;
using UnityEngine.Profiling;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    internal sealed class UnityProfilerCaptureBackend : IProfilerCaptureBackend
    {
        public bool HasFrames => ProfilerDriver.firstFrameIndex >= 0 && ProfilerDriver.lastFrameIndex >= ProfilerDriver.firstFrameIndex;
        public ProfilerCaptureState ReadState() => new ProfilerCaptureState { enabled = ProfilerDriver.enabled, profileEditor = ProfilerDriver.profileEditor,
            allocationCallstacks = Profiler.enableAllocationCallstacks, memoryRecordMode = (int)ProfilerDriver.memoryRecordMode };
        public void Pause() => ProfilerDriver.enabled = false;
        public void ApplyState(ProfilerCaptureState state)
        {
            ProfilerDriver.enabled = false; ProfilerDriver.profileEditor = state.profileEditor;
            ProfilerDriver.memoryRecordMode = (ProfilerMemoryRecordMode)state.memoryRecordMode;
            Profiler.enableAllocationCallstacks = state.allocationCallstacks; ProfilerDriver.enabled = state.enabled;
        }
        public bool Save(string path) => ProfilerDriver.SaveProfile(path);
        public bool Load(string path) => ProfilerDriver.LoadProfile(path, false);
        public void Clear() => ProfilerDriver.ClearAllFrames();
    }
}
