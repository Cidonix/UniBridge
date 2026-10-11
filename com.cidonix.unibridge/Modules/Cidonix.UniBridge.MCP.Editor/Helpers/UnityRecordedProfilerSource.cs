using System;
using System.Collections.Generic;
using System.Globalization;
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

        sealed class View : IRecordedProfilerView, IRecordedProfilerCallstackView, IRecordedProfilerObjectMetadataView
        {
            RawFrameDataView raw;
            readonly Dictionary<ushort, string> categories = new();
            readonly Dictionary<int, FrameDataView.MarkerMetadataInfo[]> metadata = new();
            int gcMarker = FrameDataView.invalidMarkerId;
            readonly List<ulong> callstackAddresses = new();
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
            public RecordedProfilerCallstack CopyCallstack(int sampleIndex, int maxDepth, Action cancelled)
            {
                var result = new RecordedProfilerCallstack { status = "CALLSTACK_READ_FAILED" };
                try
                {
                    RequireView();
                    cancelled?.Invoke();
                    callstackAddresses.Clear();
                    raw.GetSampleCallstack(sampleIndex, callstackAddresses);
                    result.recordedDepth = callstackAddresses.Count;
                    if (callstackAddresses.Count == 0) { result.status = "CALLSTACK_NOT_RECORDED"; return result; }
                    int count = Math.Min(maxDepth, callstackAddresses.Count);
                    result.depthTruncated = count < callstackAddresses.Count;
                    var entries = new RecordedProfilerCallstackEntry[count];
                    for (int i = 0; i < count; i++)
                    {
                        cancelled?.Invoke();
                        var entry = new RecordedProfilerCallstackEntry
                        { address = "0x" + callstackAddresses[i].ToString("X16", CultureInfo.InvariantCulture), status = "SYMBOL_UNRESOLVED" };
                        entries[i] = entry;
                        try
                        {
                            var method = raw.ResolveMethodInfo(callstackAddresses[i]);
                            entry.method = Bounded(method.methodName, 512, entry);
                            entry.sourceFile = Bounded(method.sourceFileName, 1024, entry);
                            entry.sourceLine = method.sourceFileLine > 0 ? method.sourceFileLine : (uint?)null;
                            if (!string.IsNullOrEmpty(entry.method)) entry.status = "resolved";
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { entry.status = "SYMBOL_READ_FAILED"; entry.issue = Bounded(ex.Message, 512, entry); }
                    }
                    result.entries = entries;
                    result.status = "recorded";
                }
                catch (OperationCanceledException) { throw; }
                catch (MissingMethodException ex) { result.status = "CALLSTACK_API_UNSUPPORTED"; result.issue = ex.Message; }
                catch (NotSupportedException ex) { result.status = "CALLSTACK_API_UNSUPPORTED"; result.issue = ex.Message; }
                catch (Exception ex) { result.issue = ex.Message?.Length > 512 ? ex.Message.Substring(0, 512) : ex.Message; }
                return result;
            }
            public RecordedProfilerObjectMetadata CopyObjectMetadata(int sampleIndex, Action cancelled)
            {
#if UNITY_6000_6_OR_NEWER
                var result = new RecordedProfilerObjectMetadata { status = "OBJECT_METADATA_READ_FAILED", capability = "recorded_entity_id", identityKind = "EntityId.UInt64" };
                try
                {
                    RequireView(); cancelled?.Invoke();
                    var id = raw.GetSampleEntityId(sampleIndex);
                    if (!id.IsValid()) { result.status = "OBJECT_ID_NOT_RECORDED"; return result; }
                    result.objectId = UnityEngine.EntityId.ToULong(id).ToString(CultureInfo.InvariantCulture);
                    cancelled?.Invoke();
                    if (!raw.GetUnityObjectInfo(id, out var info)) { result.status = "OBJECT_INFO_NOT_RECORDED"; return result; }
                    result.objectName = BoundedObjectText(info.name, result);
                    result.status = "recorded";
                    var related = info.relatedGameObjectEntityId;
                    if (!related.IsValid()) { result.relatedGameObjectStatus = "NONE_RECORDED"; return result; }
                    result.relatedGameObjectId = UnityEngine.EntityId.ToULong(related).ToString(CultureInfo.InvariantCulture);
                    try
                    {
                        cancelled?.Invoke();
                        if (raw.GetUnityObjectInfo(related, out var gameObjectInfo))
                        { result.relatedGameObjectName = BoundedObjectText(gameObjectInfo.name, result); result.relatedGameObjectStatus = "recorded"; }
                        else result.relatedGameObjectStatus = "OBJECT_INFO_NOT_RECORDED";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { result.relatedGameObjectStatus = "OBJECT_METADATA_READ_FAILED"; result.issue = BoundedObjectText(ex.Message, result); }
                }
                catch (OperationCanceledException) { throw; }
                catch (MissingMethodException ex) { result.status = "OBJECT_METADATA_API_UNSUPPORTED"; result.capability = "unsupported"; result.issue = BoundedObjectText(ex.Message, result); }
                catch (NotSupportedException ex) { result.status = "OBJECT_METADATA_API_UNSUPPORTED"; result.capability = "unsupported"; result.issue = BoundedObjectText(ex.Message, result); }
                catch (Exception ex) { result.issue = BoundedObjectText(ex.Message, result); }
                return result;
#else
                // Older raw views have no physical sample-to-object identity API.
                // Do not infer IDs from metadata or resolve them in the live Editor.
                cancelled?.Invoke();
                return new RecordedProfilerObjectMetadata { status = "OBJECT_METADATA_API_UNSUPPORTED", capability = "unsupported" };
#endif
            }
            static string BoundedObjectText(string value, RecordedProfilerObjectMetadata result)
            {
                if (value == null || value.Length <= 512) return value;
                result.textTruncated = true;
                return value.Substring(0, 512);
            }
            static string Bounded(string value, int length, RecordedProfilerCallstackEntry entry)
            {
                if (value == null || value.Length <= length) return value;
                entry.textTruncated = true;
                return value.Substring(0, length);
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
