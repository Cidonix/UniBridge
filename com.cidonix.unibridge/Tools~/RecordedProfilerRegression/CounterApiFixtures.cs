using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Profiling;
using Unity.Profiling.LowLevel;

namespace Unity.Profiling { public enum ProfilerMarkerDataUnit { Undefined, TimeNanoseconds, Bytes, Count, Percent } }
namespace Unity.Profiling.LowLevel
{
    public enum ProfilerMarkerDataType { Int32, UInt32, Int64, UInt64, Float, Double, String16 }
    [Flags] public enum MarkerFlags { Default = 0, Counter = 128 }
}
namespace UnityEditor.Profiling
{
    public class FrameDataView
    {
        public const int invalidMarkerId = -1;
        public struct MarkerMetadataInfo { public ProfilerMarkerDataType type; public ProfilerMarkerDataUnit unit; public string name; }
    }
    public sealed class NativeCounter
    {
        public string Name;
        public object Value;
        public bool Present = true;
        public MarkerFlags Flags = MarkerFlags.Counter;
        public FrameDataView.MarkerMetadataInfo[] Metadata = { new() { type = ProfilerMarkerDataType.Int64, unit = ProfilerMarkerDataUnit.Count } };
        public bool ThrowRead;
    }
    public sealed class NativeFrame
    {
        public int Index;
        public bool Valid = true;
        public int Opened, Disposed, AccessAfterDispose, ValueReads;
        public NativeCounter[] Counters = Array.Empty<NativeCounter>();
    }
    public sealed class RawFrameDataView : FrameDataView, IDisposable
    {
        readonly NativeFrame frame;
        bool disposed;
        public RawFrameDataView(NativeFrame frame) { this.frame = frame; frame.Opened++; }
        void Guard() { if (disposed) { frame.AccessAfterDispose++; throw new ObjectDisposedException(nameof(RawFrameDataView)); } }
        NativeCounter Read(int id) { Guard(); var counter = frame.Counters[id]; if (!counter.Present) throw new Exception("Absent native value was read"); if (counter.ThrowRead) throw new Exception("owned read failure"); frame.ValueReads++; return counter; }
        public bool valid { get { Guard(); return frame.Valid; } }
        public int frameIndex { get { Guard(); return frame.Index; } }
        public int GetMarkerId(string name) { Guard(); return Array.FindIndex(frame.Counters, item => item.Name == name); }
        public ushort GetMarkerCategoryIndex(int id) { Guard(); return 1; }
        public (string name, int id) GetCategoryInfo(ushort id) { Guard(); return ("Scripts", id); }
        public MarkerFlags GetMarkerFlags(int id) { Guard(); return frame.Counters[id].Flags; }
        public bool HasCounterValue(int id) { Guard(); return frame.Counters[id].Present; }
        public MarkerMetadataInfo[] GetMarkerMetadataInfo(int id) { Guard(); return frame.Counters[id].Metadata; }
        public int GetCounterValueAsInt(int id) { var value = Read(id).Value; return value is uint unsigned ? unchecked((int)unsigned) : (int)value; }
        public long GetCounterValueAsLong(int id) { var value = Read(id).Value; return value is ulong unsigned ? unchecked((long)unsigned) : (long)value; }
        public float GetCounterValueAsFloat(int id) => (float)Read(id).Value;
        public double GetCounterValueAsDouble(int id) => (double)Read(id).Value;
        public void Dispose() { if (disposed) throw new Exception("Duplicate view disposal"); disposed = true; frame.Disposed++; }
    }
}
namespace UnityEditorInternal
{
    public static class ProfilerDriver
    {
        public static readonly Dictionary<int, UnityEditor.Profiling.NativeFrame> Frames = new();
        public static int firstFrameIndex => Frames.Count == 0 ? -1 : Frames.Keys.Min();
        public static int lastFrameIndex => Frames.Count == 0 ? -1 : Frames.Keys.Max();
        public static int GetNextFrameIndex(int index) => Frames.Keys.Where(item => item > index).DefaultIfEmpty(-1).Min();
        public static int GetPreviousFrameIndex(int index) => Frames.Keys.Where(item => item < index).DefaultIfEmpty(-1).Max();
        public static UnityEditor.Profiling.RawFrameDataView GetRawFrameDataView(int frame, int thread)
        { if (thread != 0) throw new Exception("Counter reader used a sample thread"); return new(Frames[frame]); }
    }
}
