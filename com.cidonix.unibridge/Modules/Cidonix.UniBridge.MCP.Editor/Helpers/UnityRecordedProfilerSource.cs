using System;
using System.Collections.Generic;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEditor.Profiling;
using UnityEditorInternal;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    // Read the existing buffer only; no recording, history, target or selected-frame setter.
    internal sealed class UnityRecordedProfilerSource : IRecordedProfilerSource
    {
        public int FirstFrame => ProfilerDriver.firstFrameIndex;
        public int LastFrame => ProfilerDriver.lastFrameIndex;
        public int NextFrame(int index) => ProfilerDriver.GetNextFrameIndex(index);
        public int PreviousFrame(int index) => ProfilerDriver.GetPreviousFrameIndex(index);
        public int ThreadCount(int frame)
        {
            using var iterator = new ProfilerFrameDataIterator();
            return iterator.GetThreadCount(frame);
        }
        public IRecordedProfilerView Open(int frame, int threadIndex)
        {
            return new View(ProfilerDriver.GetRawFrameDataView(frame, threadIndex));
        }

        sealed class View : IRecordedProfilerView
        {
            RawFrameDataView raw;
            readonly Dictionary<ushort, string> categories = new();
            readonly Dictionary<int, FrameDataView.MarkerMetadataInfo[]> metadata = new();
            int gcMarker = FrameDataView.invalidMarkerId;
            public View(RawFrameDataView raw) { this.raw = raw; }
            public bool Valid => raw != null && raw.valid;
            public RecordedProfilerThread CopyHeader()
            {
                RequireView();
                gcMarker = raw.GetMarkerId("GC.Alloc");
                return new RecordedProfilerThread
                {
                    FrameIndex = raw.frameIndex, ThreadIndex = raw.threadIndex, ThreadId = raw.threadId,
                    ThreadName = raw.threadName, ThreadGroup = raw.threadGroupName,
                    FrameStartNs = raw.frameStartTimeNs, FrameDurationNs = raw.frameTimeNs,
                    DeclaredSampleCount = raw.sampleCount, GcMarkerAvailable = gcMarker != FrameDataView.invalidMarkerId
                };
            }
            public RecordedProfilerSample CopySample(int index, bool includeMetadata)
            {
                RequireView();
                int markerId = raw.GetSampleMarkerId(index);
                ushort categoryId = raw.GetSampleCategoryIndex(index);
                if (!categories.TryGetValue(categoryId, out var category))
                {
                    category = raw.GetCategoryInfo(categoryId).name;
                    categories[categoryId] = category;
                }
                var sample = new RecordedProfilerSample
                {
                    Index = index, MarkerId = markerId, Name = raw.GetSampleName(index),
                    Category = category, CategoryId = categoryId, Flags = raw.GetSampleFlags(index).ToString(),
                    StartNs = raw.GetSampleStartTimeNs(index), DurationNs = raw.GetSampleTimeNs(index),
                    Children = raw.GetSampleChildrenCount(index), Descendants = raw.GetSampleChildrenCountRecursive(index),
                    IsGcAllocation = gcMarker != FrameDataView.invalidMarkerId && markerId == gcMarker
                };
                if (sample.IsGcAllocation) ReadAllocationMetadata(sample, includeMetadata);
                return sample;
            }
            void ReadAllocationMetadata(RecordedProfilerSample sample, bool enabled)
            {
                if (!enabled) { sample.GcMetadataStatus = "METADATA_DISABLED"; return; }
                try
                {
                    if (raw.GetSampleMetadataCount(sample.Index) < 1)
                    { sample.GcMetadataStatus = "METADATA_SLOT_MISSING"; return; }
                    if (!metadata.TryGetValue(sample.MarkerId, out var descriptors))
                    {
                        descriptors = raw.GetMarkerMetadataInfo(sample.MarkerId);
                        metadata[sample.MarkerId] = descriptors;
                    }
                    if (descriptors == null || descriptors.Length < 1)
                    { sample.GcMetadataStatus = "METADATA_DESCRIPTOR_MISSING"; return; }
                    var descriptor = descriptors[0];
                    if (descriptor.unit != ProfilerMarkerDataUnit.Bytes)
                    { sample.GcMetadataStatus = "METADATA_UNIT_UNSUPPORTED:" + descriptor.unit; return; }
                    long bytes;
                    if (descriptor.type == ProfilerMarkerDataType.Int64 || descriptor.type == ProfilerMarkerDataType.UInt64)
                        bytes = raw.GetSampleMetadataAsLong(sample.Index, 0);
                    else if (descriptor.type == ProfilerMarkerDataType.Int32 || descriptor.type == ProfilerMarkerDataType.UInt32)
                        bytes = raw.GetSampleMetadataAsInt(sample.Index, 0);
                    else { sample.GcMetadataStatus = "METADATA_TYPE_UNSUPPORTED:" + descriptor.type; return; }
                    if (bytes < 0) { sample.GcMetadataStatus = "METADATA_VALUE_OUT_OF_RANGE"; return; }
                    sample.GcBytes = bytes;
                    sample.GcMetadataStatus = "recorded";
                }
                catch (Exception ex)
                {
                    sample.GcMetadataStatus = "METADATA_READ_FAILED:" + ex.Message;
                }
            }
            void RequireView()
            {
                if (raw == null) throw new ObjectDisposedException(nameof(View));
                if (!raw.valid) throw new InvalidOperationException("Recorded thread view is no longer available.");
            }
            public void Dispose()
            {
                var owned = raw;
                raw = null;
                owned?.Dispose();
            }
        }
    }
}
