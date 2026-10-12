using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;

class Regression
{
    static readonly List<object> cases = new();
    static string owned;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Equal(double value, double expected) => Check(Math.Abs(value-expected)<0.0000001,"Expected "+expected+", got "+value);
    static void Case(string name, Action body) { try { body(); cases.Add(new { name, passed=true }); Console.WriteLine("PASS "+name); } catch(Exception e) { cases.Add(new { name,passed=false,error=e.Message }); Console.WriteLine("FAIL "+name+": "+e.Message); } }
    sealed class Backend : IProfilerCaptureBackend
    {
        public string buffer="original";
        public ProfilerCaptureState state = new() { enabled=true, profileEditor=false, allocationCallstacks=true, memoryRecordMode=3 };
        public bool failSave, failOriginalLoad, failRestoreFlags, failPauseAfterChange, canonicalizeHeaders;
        bool hasLoaded;
        public int loads, saves, pauses, clears;
        public bool HasFrames => buffer!=null;
        public ProfilerCaptureState ReadState() => new() { enabled=state.enabled,profileEditor=state.profileEditor,allocationCallstacks=state.allocationCallstacks,memoryRecordMode=state.memoryRecordMode };
        public void Pause() { pauses++;state.enabled=false;if(failPauseAfterChange)throw new IOException("owned partially applied Pause failure"); }
        public void ApplyState(ProfilerCaptureState value) { if(failRestoreFlags)throw new IOException("owned flags failure");state=new(){enabled=value.enabled,profileEditor=value.profileEditor,allocationCallstacks=value.allocationCallstacks,memoryRecordMode=value.memoryRecordMode}; }
        public bool Save(string path) { saves++;if(failSave)return false;File.WriteAllText(path,canonicalizeHeaders ? (hasLoaded?"loaded-header:":"saved-header:")+(buffer??"") : buffer??"");return true; }
        public bool Load(string path) { loads++;var data=File.ReadAllText(path);if(data.StartsWith("saved-header:"))data=data.Substring(13);else if(data.StartsWith("loaded-header:"))data=data.Substring(14);hasLoaded=true;buffer=data;if(data=="invalid")return false;if(failOriginalLoad&&Path.GetFileName(path)=="original.data")return false;return true; }
        public void Clear(){clears++;buffer=null;}
    }
    sealed class Context
    {
        public string root, requested, hash;
        public Backend backend = new();
        public ProfilerCaptureLease Controller(int pid=123) => new(root,pid,backend);
        public Context(string data="requested") { root=Path.Combine(owned,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);requested=Path.Combine(root,"known.data");File.WriteAllText(requested,data);hash=ProfilerCaptureLease.Hash(requested); }
        public ProfilerCaptureLeaseResult Load()=>Controller().Execute("load",root,"known.data",hash,null);
        public ProfilerCaptureLeaseResult Restore(ProfilerCaptureLeaseResult loaded)=>Controller().Execute("restore",root,null,null,loaded.leaseId);
    }
#if BOTTOM_UP
    static RecordedProfilerSample S(int index,int marker,double start,double duration,int children=0,int descendants=0,long? gc=null,string name=null,ushort category=1)
        => new(){Index=index,MarkerId=marker,Name=name??("Marker"+marker),Category="Scripts",CategoryId=category,StartNs=(ulong)(start*1000000),DurationNs=(ulong)(duration*1000000),Children=children,Descendants=descendants,IsGcAllocation=gc.HasValue,GcBytes=gc,GcMetadataStatus=gc.HasValue?"recorded":null};
    static RecordedProfilerReadResult Read(RecordedProfilerSample[] samples,ulong thread=9007199254740993,int frame=17,bool complete=true,bool gcMarker=true)
    {
        var header=new RecordedProfilerThread { FrameIndex=frame,ThreadId=thread,ThreadName="Main Thread",ThreadGroup="",FrameDurationNs=100000000,DeclaredSampleCount=samples.Length,GcMarkerAvailable=gcMarker,Samples=samples };
        var f=new RecordedProfilerFrame { Index=frame,AvailableThreadCount=1,ExaminedThreadViews=1 };f.Threads.Add(RecordedProfilerFrameAnalysis.Analyze(header));
        var r=new RecordedProfilerReadResult { Complete=complete,SelectedFrameIndices=new[]{frame},CopiedSamples=samples.Length,RequestedFrameCount=1,FirstAvailableFrame=frame,LastAvailableFrame=frame };r.Frames.Add(f);return r;
    }
    static RecordedBottomUpResult Aggregate(RecordedProfilerReadResult read,int max=40,RecordedProfilerSort sort=RecordedProfilerSort.SelfTime,Func<bool> expired=null,Action cancelled=null)
        =>RecordedProfilerBottomUp.Analyze(read,max,null,null,null,sort,expired,cancelled);
#endif
    static void Schema(JToken value,JObject schema)
    {
        if(schema["anyOf"] is JArray branches){foreach(JObject b in branches){try{Schema(value,b);return;}catch{}}throw new Exception("No schema branch: "+value.Path);}
        if(schema["const"]!=null)Check(JToken.DeepEquals(value,schema["const"]),"Const "+value.Path);
        if(schema["type"]!=null){var allowed=schema["type"] is JArray a?a.Values<string>().ToArray():new[]{schema.Value<string>("type")};var type=value.Type switch{JTokenType.Null=>"null",JTokenType.Object=>"object",JTokenType.Array=>"array",JTokenType.Integer=>"integer",JTokenType.Float=>"number",JTokenType.Boolean=>"boolean",_=>"string"};Check(allowed.Contains(type)||type=="integer"&&allowed.Contains("number"),"Type "+value.Path);}
        if(value is JObject obj&&schema["properties"] is JObject props){foreach(var key in schema["required"]?.Values<string>()??Array.Empty<string>())Check(obj.Property(key)!=null,"Missing "+key);foreach(var p in obj.Properties()){Check(props.Property(p.Name)!=null||schema.Value<bool?>("additionalProperties")!=false,"Extra "+p.Name);if(props[p.Name] is JObject s)Schema(p.Value,s);}}
        if(value is JArray array&&schema["items"] is JObject item)foreach(var v in array)Schema(v,item);
    }
    static int Main(string[] args)
    {
        owned=args[1];Directory.CreateDirectory(owned);
        Case("Requested SHA mismatch does not touch native state",()=>{var c=new Context();var result=c.Controller().Execute("load",c.root,"known.data",new string('0',64),null);Check(result.errorCode=="CAPTURE_HASH_MISMATCH"&&c.backend.loads==0&&c.backend.pauses==0&&c.backend.state.enabled,"Request mutated before hash check");});
        Case("Explicit expected project root is mandatory",()=>{var c=new Context();var result=c.Controller().Execute("load",owned,"known.data",c.hash,null);Check(result.errorCode=="PROJECT_ROOT_MISMATCH"&&c.backend.loads==0,"Project mismatch not guarded");});
        Case("Traversal and other extensions are rejected before native",()=>{var c=new Context();foreach(var p in new[]{"../outside.data","known.json",c.requested}){var result=c.Controller().Execute("load",c.root,p,c.hash,null);Check(result.errorCode!=null&&c.backend.loads==0&&c.backend.pauses==0,"Unsafe path accepted");}});
        Case("Stopped capture load produces durable exact lease",()=>{var c=new Context();var result=c.Load();Check(result.errorCode==null&&result.state=="loaded"&&c.backend.buffer=="requested"&&!c.backend.state.enabled&&File.Exists(result.receiptPath),"Load failed");Check(result.captureSha256==c.hash&&result.hadOriginalFrames,"Missing provenance");});
        Case("Reconstructed controller restores original buffer and all flags",()=>{var c=new Context();var original=c.backend.ReadState();var result=c.Restore(c.Load());Check(result.errorCode==null&&result.restorationVerified&&result.state=="restored"&&c.backend.buffer=="original"&&c.backend.ReadState().SameAs(original),"Domain-reload reconstruction failed");});
        Case("Native save/load header canonicalization is qualified without weakening integrity",()=>{var c=new Context();c.backend.canonicalizeHeaders=true;var loaded=c.Load();Check(loaded.errorCode==null&&loaded.originalSha256!=loaded.originalLoadedSha256&&loaded.originalLoadedSha256!=null,"Distinct loaded serialization not qualified");var originalPath=Path.Combine(Path.GetDirectoryName(loaded.receiptPath),"original.data");Check(ProfilerCaptureLease.Hash(originalPath)==loaded.originalSha256,"Exact backup bytes changed");var restored=c.Restore(loaded);Check(restored.errorCode==null&&restored.restorationVerified&&c.backend.buffer=="original"&&c.backend.state.enabled,"Canonical restore failed");});
        Case("Original empty buffer restores empty rather than guessed data",()=>{var c=new Context();c.backend.buffer=null;var result=c.Restore(c.Load());Check(result.errorCode==null&&c.backend.buffer==null&&c.backend.clears==1,"Empty buffer was not restored");});
        Case("Malformed load that replaces native state restores baseline",()=>{var c=new Context("invalid");var result=c.Load();Check(result.errorCode=="CAPTURE_LOAD_FAILED_RESTORED"&&result.restorationVerified&&c.backend.buffer=="original"&&c.backend.state.enabled,"Failed load leaked mutation");});
        Case("Baseline save failure never loads requested capture",()=>{var c=new Context();c.backend.failSave=true;var result=c.Load();Check(result.errorCode=="CAPTURE_LOAD_FAILED_RESTORED"&&result.restorationVerified&&c.backend.loads==0&&c.backend.state.enabled&&c.backend.buffer=="original","Failed baseline save touched buffer or flags");});
        Case("Partially applied pause failure restores original flags without load",()=>{var c=new Context();c.backend.failPauseAfterChange=true;var result=c.Load();Check(result.errorCode=="CAPTURE_LOAD_FAILED_RESTORED"&&result.restorationVerified&&c.backend.loads==0&&c.backend.state.enabled&&c.backend.buffer=="original","Partial pause leaked native state");});
        Case("Second load is blocked without replay",()=>{var c=new Context();c.Load();int loads=c.backend.loads;var result=c.Load();Check(result.errorCode=="CAPTURE_LEASE_ACTIVE"&&c.backend.loads==loads,"Lease was overwritten");});
        Case("Wrong lease cannot restore",()=>{var c=new Context();c.Load();int loads=c.backend.loads;var result=c.Controller().Execute("restore",c.root,null,null,new string('0',32));Check(result.errorCode=="CAPTURE_LEASE_MISMATCH"&&c.backend.loads==loads,"Wrong lease restored");});
        Case("Different Editor process cannot restore old lease",()=>{var c=new Context();var loaded=c.Load();var result=c.Controller(999).Execute("restore",c.root,null,null,loaded.leaseId);Check(result.errorCode=="CAPTURE_LEASE_EDITOR_MISMATCH"&&c.backend.buffer=="requested","Foreign process restored lease");});
        Case("Parallel buffer edits block restore and are retained",()=>{var c=new Context();var loaded=c.Load();c.backend.buffer="author replacement";int loads=c.backend.loads;var result=c.Restore(loaded);Check(result.errorCode=="CAPTURE_BUFFER_CHANGED"&&c.backend.loads==loads&&c.backend.buffer=="author replacement","Parallel edits overwritten");});
        Case("Parallel recording flag edits block restore",()=>{var c=new Context();var loaded=c.Load();c.backend.state.enabled=true;int loads=c.backend.loads;var result=c.Restore(loaded);Check(result.errorCode=="CAPTURE_STATE_CHANGED"&&c.backend.loads==loads&&c.backend.state.enabled,"Recording was overridden");});
        Case("Corrupted original bytes are blocked and requested buffer rolled back",()=>{var c=new Context();var loaded=c.Load();File.WriteAllText(Path.Combine(Path.GetDirectoryName(loaded.receiptPath),"original.data"),"corrupt");var result=c.Restore(loaded);Check(result.errorCode=="CAPTURE_RESTORE_FAILED"&&result.state=="loaded"&&c.backend.buffer=="requested"&&!c.backend.state.enabled,"Corrupt original applied");});
        Case("Native baseline restore failure rolls back to lease buffer",()=>{var c=new Context();var loaded=c.Load();c.backend.failOriginalLoad=true;var result=c.Restore(loaded);Check(result.errorCode=="CAPTURE_RESTORE_FAILED"&&result.state=="loaded"&&c.backend.buffer=="requested","Failed restore leaked state");});
        Case("Cancelled admission never pauses or loads",()=>{var c=new Context();bool cancelled=false;try{c.Controller().Execute("load",c.root,"known.data",c.hash,null,()=>throw new OperationCanceledException());}catch(OperationCanceledException){cancelled=true;}Check(cancelled&&c.backend.loads==0&&c.backend.pauses==0,"Admission cancellation did not propagate");});
        Case("Status never saves, loads or mutates recording",()=>{var c=new Context();var r=c.Controller().Execute("status",null,null,null,null);Check(r.state=="none"&&r.errorCode==null&&c.backend.saves==0&&c.backend.loads==0&&c.backend.pauses==0&&c.backend.state.enabled,"Status mutated capture");});
        Case("Closed lease success/error schema projects exact producer",()=>{var c=new Context();var success=Response.Success("loaded",c.Load());Schema(McpJson.TokenFromObject(success),RuntimeProfilerActionOutputRules.Build()["LoadCapture"]);Schema(McpJson.TokenFromObject(Response.Error("CAPTURE_LEASE_ACTIVE",c.Load())),RuntimeProfilerActionOutputRules.Build()["LoadCapture"]);var invalid=McpJson.ObjectFromObject(success);((JObject)invalid["data"])["extra"]=true;bool rejected=false;try{Schema(invalid,RuntimeProfilerActionOutputRules.Build()["LoadCapture"]);}catch{rejected=true;}Check(rejected,"Closed schema opened");});
        Case("Exact capture tool wrapper projects real load and restore DTO",()=>{var c=new Context();UnityEngine.Application.dataPath=Path.Combine(c.root,"Assets");UnityProfilerCaptureBackend.Current=c.backend;var loaded=McpJson.ObjectFromObject(RuntimeProfiler.RunCapture(new RuntimeProfilerParams{ExpectedProjectRoot=c.root,CapturePath="known.data",CaptureSha256=c.hash},"load"));Schema(loaded,RuntimeProfilerActionOutputRules.Build()["LoadCapture"]);Check(loaded.Value<bool>("success"),"Actual wrapper load failed");var restored=McpJson.ObjectFromObject(RuntimeProfiler.RunCapture(new RuntimeProfilerParams{ExpectedProjectRoot=c.root,CaptureLeaseId=loaded["data"].Value<string>("leaseId")},"restore"));Schema(restored,RuntimeProfilerActionOutputRules.Build()["RestoreCapture"]);Check(restored.Value<bool>("success")&&c.backend.buffer=="original","Actual wrapper restore failed");});
        Case("Actual policy fragment shields both capture mutations",()=>{foreach(var action in new[]{"LoadCapture","RestoreCapture","6","7"})Check(ProductionPolicy.Resolve(new JObject{{"Action",action}})==ToolExecutionPolicy.Mutating,"Mutation not shielded: "+action);Check(ProductionPolicy.Resolve(new JObject{{"Action",6}})==ToolExecutionPolicy.Mutating,"Numeric mutation unshielded");});
        Case("Actual policy preserves all six existing read actions",()=>{foreach(var action in new[]{"Snapshot","Sample","Hierarchy","Metrics","RecordedFrames","RecordedCounters","GetCaptureLease"})Check(ProductionPolicy.Resolve(new JObject{{"Action",action}})==ToolExecutionPolicy.ReadOnly,"Read policy changed");Check(ProductionPolicy.Resolve(new JObject{{"Action","LoadCapture"},{"action","Snapshot"}})==ToolExecutionPolicy.ReadOnly&&ProductionPolicy.Resolve(new JObject{{"Action","Snapshot"},{"action","LoadCapture"}})==ToolExecutionPolicy.Mutating,"Typed duplicate action assignment mismatch");});
#if BOTTOM_UP
        BottomUp();
#endif
        var result=new{passed=cases.All(c=>(bool)c.GetType().GetProperty("passed").GetValue(c)),passedCount=cases.Count(c=>(bool)c.GetType().GetProperty("passed").GetValue(c)),failedCount=cases.Count(c=>!(bool)c.GetType().GetProperty("passed").GetValue(c)),cases,scope="Exact production capture lease filesystem/native seams and copied-physical bottom-up algorithms; native Unity methods separately qualified"};
        File.WriteAllText(args[0],McpJson.SerializeObject(result,Formatting.Indented));return result.passed?0:1;
    }
#if BOTTOM_UP
    static void BottomUp()
    {
        Case("Repeated calls are aggregated before top-N",()=>{var r=Read(new[]{S(0,7,0,1),S(1,7,2,1),S(2,7,4,1),S(3,8,6,2)});var a=Aggregate(r,1);Check(a.groups.Length==1&&a.groups[0].markerId==7&&a.groups[0].physicalCallCount==3&&a.groupsTruncated,"Individual spike outranked aggregate");Equal(a.groups[0].selfTimeMs.Value,3);Equal(a.groups[0].inclusiveTimeMs,3);});
        Case("Recursive inclusive time uses union and GC event only once",()=>{var r=Read(new[]{S(0,7,0,10,1,2),S(1,7,2,5,1,1),S(2,99,3,0,0,0,4096,"GC.Alloc")});var a=Aggregate(r).groups.Single(g=>g.markerId==7);Check(a.physicalCallCount==2&&a.gcAllocationEvents==1&&a.gcAllocationBytes==4096,"Recursive allocation double counted");Equal(a.inclusiveTimeMs,10);Equal(a.selfTimeMs.Value,10);});
        Case("Intervals from separate frames are never merged",()=>{var first=Read(new[]{S(0,7,0,3)});var second=Read(new[]{S(0,7,0,3)},frame:18);first.Frames.AddRange(second.Frames);var g=Aggregate(first).groups.Single();Equal(g.inclusiveTimeMs,6);Check(g.framesObserved==2,"Frame scopes lost");});
        Case("UInt64 physical thread identities remain separate",()=>{var r=Read(new[]{S(0,7,0,3)});r.Frames[0].Threads.Add(Read(new[]{S(0,7,0,3)},thread:9007199254740994).Frames[0].Threads[0]);var g=Aggregate(r).groups;Check(g.Length==2&&g.Select(x=>x.threadId).Distinct().Count()==2,"Thread identity merged or rounded");});
        Case("Names/category IDs prevent false marker alias merge",()=>{var r=Read(new[]{S(0,7,0,1,name:"A"),S(1,7,2,1,name:"B"),S(2,7,4,1,name:"A",category:2)});Check(Aggregate(r).groups.Length==3,"Distinct markers aliased");});
        Case("Incomplete coverage keeps aggregates explicitly unknown",()=>{var r=Read(new[]{S(0,7,0,1)},complete:false);var a=Aggregate(r);Check(!a.complete&&a.groups[0].selfTimeMs==null&&a.groups[0].gcAllocationBytes==null&&!a.groups[0].rawCoverageComplete,"Partial became complete");});
        Case("Absent GC marker is unknown rather than measured zero",()=>{var a=Aggregate(Read(new[]{S(0,7,0,1)},gcMarker:false)).groups.Single();Check(a.gcAllocationBytes==null&&a.knownGcAllocationBytes==0,"Unrecorded allocation guessed zero");});
        Case("Unknown allocation metadata remains unknown for ancestor",()=>{var r=Read(new[]{S(0,7,0,1,1,1),S(1,99,0.5,0,0,0,1,"GC.Alloc")});r.Frames[0].Threads[0].Nodes[1].Raw.GcBytes=null;var g=Aggregate(r).groups.Single(x=>x.markerId==7);Check(g.unknownGcAllocationEvents==1&&g.gcAllocationBytes==null,"Missing allocation metadata guessed");});
        Case("Group IDs never alias physical sample IDs",()=>{var g=Aggregate(Read(new[]{S(0,7,0,1)})).groups.Single();Check(g.groupId.StartsWith("aggregate:")&&g.sampleIds[0]=="17:9007199254740993:0","Physical drilldown identity lost");});
        Case("Physical drilldown list is bounded",()=>{var g=Aggregate(Read(Enumerable.Range(0,40).Select(i=>S(i,7,i*2,1)).ToArray())).groups.Single();Check(g.sampleIds.Length==8&&g.sampleIdsTruncated&&g.physicalCallCount==40,"Unbounded IDs or wrong call count");});
        Case("Aggregation deadline produces truthful partial",()=>{var a=Aggregate(Read(Enumerable.Range(0,200).Select(i=>S(i,7,i*2,1)).ToArray()),expired:()=>true);Check(a.timedOut&&!a.complete&&a.issues.Contains("AGGREGATION_TIMEOUT"),"Deadline ignored");});
        Case("Aggregation cancellation propagates",()=>{bool stopped=false;try{Aggregate(Read(new[]{S(0,7,0,1)}),cancelled:()=>throw new OperationCanceledException());}catch(OperationCanceledException){stopped=true;}Check(stopped,"Cancellation swallowed");});
        Case("Logical group work budget is bounded and reported",()=>{var a=Aggregate(Read(Enumerable.Range(0,600).Select(i=>S(i,i,i*2,1)).ToArray()),300);Check(!a.complete&&a.observedGroups==512&&a.groups.Length==300&&a.issues.Contains("AGGREGATE_GROUP_LIMIT"),"Group budget silently expanded");});
        Case("Exact bottom-up producer obeys closed schema and default no-stack read",()=>{UnityRecordedProfilerSource.Header=Read(new[]{S(0,7,0,1),S(1,7,2,1)}).Frames[0].Threads[0].Thread;RuntimeProfiler.Storage=Path.Combine(owned,"producer");var result=McpJson.ObjectFromObject(RuntimeProfiler.RunBottomUp(new RuntimeProfilerParams{SaveToFile=false,FrameIndex=17,FrameCount=1}).GetAwaiter().GetResult());Schema(result,RuntimeProfilerActionOutputRules.Build()["RecordedBottomUp"]);Check(result.Value<bool>("success")&&result["data"]["groups"][0].Value<int>("physicalCallCount")==2&&RuntimeProfiler.Exports==0,"Producer aggregate/export mismatch");});
        Case("Exact bottom-up producer respects play-mode precondition",()=>{UnityEngine.Application.isPlaying=false;var result=McpJson.ObjectFromObject(RuntimeProfiler.RunBottomUp(new RuntimeProfilerParams{RequirePlayMode=true}).GetAwaiter().GetResult());Schema(result,RuntimeProfilerActionOutputRules.Build()["RecordedBottomUp"]);Check(!result.Value<bool>("success")&&result.Value<string>("code")=="PLAY_MODE_REQUIRED","Precondition ignored");});
    }
#endif
}
