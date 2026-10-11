using System;
using System.Collections.Generic;
using System.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;

namespace Unity.Profiling
{
    public enum ProfilerMarkerDataUnit { Undefined, TimeNanoseconds, Bytes, Count }
}
namespace Unity.Profiling.LowLevel
{
    public enum ProfilerMarkerDataType { Int32, UInt32, Int64, UInt64, Float, Double, String }
    public enum MarkerFlags { None }
}
namespace UnityEditor.Profiling
{
    public class FrameDataView
    {
        public const int invalidMarkerId = -1;
#if UNITY_6000_6_OR_NEWER
        public struct UnityObjectInfo { public string name; public UnityEngine.EntityId relatedGameObjectEntityId; }
#endif
        public struct MethodInfo { public string methodName; public string sourceFileName; public uint sourceFileLine; }
        public struct MarkerMetadataInfo
        {
            public Unity.Profiling.LowLevel.ProfilerMarkerDataType type;
            public Unity.Profiling.ProfilerMarkerDataUnit unit;
            public string name;
        }
    }
    public sealed class NativeSample
    {
        internal RecordedProfilerSample Value;
        public int MetadataCount = 1;
        public FrameDataView.MarkerMetadataInfo[] Descriptors = { new() { type = Unity.Profiling.LowLevel.ProfilerMarkerDataType.Int64, unit = Unity.Profiling.ProfilerMarkerDataUnit.Bytes, name = "owned allocation" } };
        public bool ThrowMetadata;
        public bool ThrowCopy;
        public ulong[] Callstack = Array.Empty<ulong>();
        public bool ThrowCallstack, UnsupportedCallstack;
        public byte[] EncodedMetadata;
        public ulong RecordedObjectId;
        public bool ThrowObjectId, UnsupportedObjectId;
    }
    public sealed class NativeThread
    {
        internal RecordedProfilerThread Header;
        public NativeSample[] Samples;
        public bool Valid = true;
        public int Opened, Disposed, AccessAfterDispose;
        public int CallstackQueries, SymbolQueries, ObjectIdQueries, ObjectInfoQueries;
#if UNITY_6000_6_OR_NEWER
        public readonly Dictionary<ulong, FrameDataView.UnityObjectInfo> RecordedObjects = new();
        public readonly HashSet<ulong> ThrowObjectInfo = new();
#endif
        public readonly Dictionary<ulong,FrameDataView.MethodInfo> Symbols = new();
        public readonly HashSet<ulong> ThrowSymbols = new();
    }
    public sealed class RawFrameDataView : FrameDataView, IDisposable
    {
        readonly NativeThread thread;
        bool disposed;
        public RawFrameDataView(NativeThread thread) { this.thread = thread; thread.Opened++; }
        void Guard() { if (disposed) { thread.AccessAfterDispose++; throw new ObjectDisposedException(nameof(RawFrameDataView)); } }
        NativeSample Sample(int i) { Guard(); if (thread.Samples[i].ThrowCopy) throw new InvalidOperationException("Owned copy failure"); return thread.Samples[i]; }
        public bool valid { get { Guard(); return thread.Valid; } }
        public int frameIndex { get { Guard(); return thread.Header.FrameIndex; } }
        public int threadIndex { get { Guard(); return thread.Header.ThreadIndex; } }
        public ulong threadId { get { Guard(); return thread.Header.ThreadId; } }
        public string threadName { get { Guard(); return thread.Header.ThreadName; } }
        public string threadGroupName { get { Guard(); return thread.Header.ThreadGroup; } }
        public ulong frameStartTimeNs { get { Guard(); return thread.Header.FrameStartNs; } }
        public ulong frameTimeNs { get { Guard(); return thread.Header.FrameDurationNs; } }
        public int sampleCount { get { Guard(); return thread.Header.DeclaredSampleCount; } }
        public int GetMarkerId(string name) { Guard(); return thread.Header.GcMarkerAvailable && name == "GC.Alloc" ? 99 : -1; }
        public int GetSampleMarkerId(int i) => Sample(i).Value.MarkerId;
        public ushort GetSampleCategoryIndex(int i) => Sample(i).Value.CategoryId;
        public string GetSampleName(int i) => Sample(i).Value.Name;
        public Unity.Profiling.LowLevel.MarkerFlags GetSampleFlags(int i) { Sample(i); return Unity.Profiling.LowLevel.MarkerFlags.None; }
        public ulong GetSampleStartTimeNs(int i) => Sample(i).Value.StartNs;
        public ulong GetSampleTimeNs(int i) => Sample(i).Value.DurationNs;
        public int GetSampleChildrenCount(int i) => Sample(i).Value.Children;
        public int GetSampleChildrenCountRecursive(int i) => Sample(i).Value.Descendants;
        public (string name, int id) GetCategoryInfo(ushort id) { Guard(); return ("Scripts", id); }
        public int GetSampleMetadataCount(int i) => Sample(i).MetadataCount;
        public MarkerMetadataInfo[] GetMarkerMetadataInfo(int markerId) { Guard(); return thread.Samples.First(sample => sample.Value.MarkerId == markerId).Descriptors; }
        public long GetSampleMetadataAsLong(int i, int slot) { var sample = Sample(i); if (sample.ThrowMetadata) throw new InvalidOperationException("Owned metadata failure"); return sample.EncodedMetadata != null ? BitConverter.ToInt64(sample.EncodedMetadata,0) : sample.Value.GcBytes ?? -1; }
        public int GetSampleMetadataAsInt(int i, int slot) { var sample = Sample(i); if (sample.ThrowMetadata) throw new InvalidOperationException("Owned metadata failure"); return sample.EncodedMetadata != null ? BitConverter.ToInt32(sample.EncodedMetadata,0) : checked((int)(sample.Value.GcBytes ?? -1)); }
#if UNITY_6000_6_OR_NEWER
        public UnityEngine.EntityId GetSampleEntityId(int index)
        {
            var sample=Sample(index);thread.ObjectIdQueries++;
            if(sample.UnsupportedObjectId)throw new NotSupportedException("owned unsupported recorded object API");
            if(sample.ThrowObjectId)throw new Exception("owned object ID read failure");
            return UnityEngine.EntityId.FromULong(sample.RecordedObjectId);
        }
        public bool GetUnityObjectInfo(UnityEngine.EntityId id,out UnityObjectInfo info)
        {
            Guard();thread.ObjectInfoQueries++;var key=UnityEngine.EntityId.ToULong(id);
            if(thread.ThrowObjectInfo.Contains(key))throw new Exception("owned object info read failure");
            return thread.RecordedObjects.TryGetValue(key,out info);
        }
#endif
        public void GetSampleCallstack(int index,List<ulong> addresses) { var sample=Sample(index); thread.CallstackQueries++; if(sample.UnsupportedCallstack)throw new NotSupportedException("owned unsupported API"); if(sample.ThrowCallstack)throw new Exception("owned stack read failure");addresses.AddRange(sample.Callstack); }
        public FrameDataView.MethodInfo ResolveMethodInfo(ulong address) { Guard();thread.SymbolQueries++;if(thread.ThrowSymbols.Contains(address))throw new Exception("owned symbol read failure");return thread.Symbols.TryGetValue(address,out var method)?method:default; }
        public void Dispose() { if (disposed) throw new InvalidOperationException("Duplicate native fixture disposal"); disposed = true; thread.Disposed++; }
    }
}
namespace UnityEditorInternal
{
    public static class ProfilerDriver
    {
        public static readonly Dictionary<int,List<UnityEditor.Profiling.NativeThread>> Frames = new();
        public static bool enabled, profileEditor;
        public static int MutationCalls;
        public static int firstFrameIndex => Frames.Count == 0 ? -1 : Frames.Keys.Min();
        public static int lastFrameIndex => Frames.Count == 0 ? -1 : Frames.Keys.Max();
        public static int GetNextFrameIndex(int index) => Frames.Keys.Where(i => i > index).DefaultIfEmpty(-1).Min();
        public static int GetPreviousFrameIndex(int index) => Frames.Keys.Where(i => i < index).DefaultIfEmpty(-1).Max();
        public static UnityEditor.Profiling.RawFrameDataView GetRawFrameDataView(int frame,int thread) => new(Frames[frame][thread]);
        public static void ClearAllFrames() { MutationCalls++; }
        public static void LoadProfile(string filename,bool keepExisting) { MutationCalls++; }
    }
    public sealed class ProfilerFrameDataIterator : IDisposable
    {
        public static int Opened,Disposed;
        public ProfilerFrameDataIterator() { Opened++; }
        public int GetThreadCount(int frame) => ProfilerDriver.Frames.TryGetValue(frame,out var threads) ? threads.Count : 0;
        public void Dispose() { Disposed++; }
    }
}
