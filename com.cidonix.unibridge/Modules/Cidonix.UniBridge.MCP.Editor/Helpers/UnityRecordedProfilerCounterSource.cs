using System;
using System.Globalization;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEditor.Profiling;
using UnityEditorInternal;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    // Historical counter values only. Does not start recorders or change the loaded capture.
    internal sealed class UnityRecordedProfilerCounterSource : IRecordedProfilerCounterSource
    {
        public int FirstFrame => ProfilerDriver.firstFrameIndex;
        public int LastFrame => ProfilerDriver.lastFrameIndex;
        public int NextFrame(int index) => ProfilerDriver.GetNextFrameIndex(index);
        public int PreviousFrame(int index) => ProfilerDriver.GetPreviousFrameIndex(index);
        public IRecordedProfilerCounterView Open(int frameIndex) => new View(ProfilerDriver.GetRawFrameDataView(frameIndex, 0));

        sealed class View : IRecordedProfilerCounterView
        {
            RawFrameDataView raw;
            public View(RawFrameDataView raw) { this.raw = raw; }
            public bool Valid => raw != null && raw.valid;
            public int FrameIndex => raw.frameIndex;
            public RecordedProfilerCounterValue CopyCounter(RecordedProfilerCounterDefinition definition)
            {
                if (!Valid) return RecordedProfilerCounterReader.Unavailable(definition, "FRAME_VIEW_UNAVAILABLE");
                var result = RecordedProfilerCounterReader.Unavailable(definition, "MARKER_NOT_RECORDED");
                int id = raw.GetMarkerId(definition.MarkerName);
                if (id == FrameDataView.invalidMarkerId) return result;
                result.category = raw.GetCategoryInfo(raw.GetMarkerCategoryIndex(id)).name;
                if ((raw.GetMarkerFlags(id) & MarkerFlags.Counter) == 0)
                { result.status = "MARKER_IS_NOT_COUNTER"; return result; }
                if (!raw.HasCounterValue(id))
                { result.status = "COUNTER_VALUE_NOT_RECORDED"; return result; }
                var metadata = raw.GetMarkerMetadataInfo(id);
                if (metadata == null || metadata.Length != 1)
                { result.status = "COUNTER_DESCRIPTOR_UNAVAILABLE"; return result; }
                var descriptor = metadata[0];
                result.dataType = descriptor.type.ToString();
                result.recordedUnit = descriptor.unit.ToString();
                if (definition.RecordedUnit != null && definition.RecordedUnit != result.recordedUnit)
                { result.status = "COUNTER_UNIT_MISMATCH"; return result; }
                double number;
                switch (descriptor.type)
                {
                    case ProfilerMarkerDataType.Int32:
                        int signed32 = raw.GetCounterValueAsInt(id); result.rawValue = signed32.ToString(CultureInfo.InvariantCulture); number = signed32; break;
                    case ProfilerMarkerDataType.UInt32:
                        uint unsigned32 = unchecked((uint)raw.GetCounterValueAsInt(id)); result.rawValue = unsigned32.ToString(CultureInfo.InvariantCulture); number = unsigned32; break;
                    case ProfilerMarkerDataType.Int64:
                        long signed64 = raw.GetCounterValueAsLong(id); result.rawValue = signed64.ToString(CultureInfo.InvariantCulture); number = signed64; break;
                    case ProfilerMarkerDataType.UInt64:
                        ulong unsigned64 = unchecked((ulong)raw.GetCounterValueAsLong(id)); result.rawValue = unsigned64.ToString(CultureInfo.InvariantCulture); number = unsigned64; break;
                    case ProfilerMarkerDataType.Float:
                        float single = raw.GetCounterValueAsFloat(id); result.rawValue = single.ToString("R", CultureInfo.InvariantCulture); number = single; break;
                    case ProfilerMarkerDataType.Double:
                        double twice = raw.GetCounterValueAsDouble(id); result.rawValue = twice.ToString("R", CultureInfo.InvariantCulture); number = twice; break;
                    default: result.status = "COUNTER_TYPE_UNSUPPORTED"; return result;
                }
                if (definition.Unit == null)
                {
                    result.unit = descriptor.unit == ProfilerMarkerDataUnit.TimeNanoseconds ? "ms" : descriptor.unit == ProfilerMarkerDataUnit.Bytes ? "bytes" :
                        descriptor.unit == ProfilerMarkerDataUnit.Count ? "count" : descriptor.unit.ToString();
                }
                double scale = definition.Unit == null && descriptor.unit == ProfilerMarkerDataUnit.TimeNanoseconds ? 0.000001 : definition.Scale;
                if (double.IsNaN(number) || double.IsInfinity(number) || number < 0 || double.IsInfinity(number * scale))
                { result.status = "COUNTER_VALUE_OUT_OF_RANGE"; return result; }
                result.value = number * scale;
                result.status = "recorded";
                return result;
            }
            public void Dispose() { var owned = raw; raw = null; owned?.Dispose(); }
        }
    }
}
