#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Unity.Profiling;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

public static class UniBridgeProfilerExtensionsFixture
{
    static readonly ProfilerMarker Repeated = new("UniBridge Extensions Repeated 20261012");
    static bool running, previousEnabled, previousEditor, previousCallstacks, hadFrames, baselineSaved, captureStarted;
    static ProfilerMemoryRecordMode previousMode;
    static string folder;
    static int ticks;
    static double deadline;
    static readonly List<object> cases = new();
    static string Root => Path.GetDirectoryName(Application.dataPath);
    static string Hash(string path) { using var hash=SHA256.Create();return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-","").ToLowerInvariant(); }
    [MenuItem("Tools/UniBridge Validation/Profiler Extensions/PrepareKnownCapture")]
    public static void Prepare()
    {
        if(running)throw new InvalidOperationException("Owned fixture already running.");
        folder=Path.Combine(Root,"Library","AgentValidation","ProfilerExtensions-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        SessionState.SetString("UniBridge.ProfilerExtensions.OwnedFolder",folder);
        previousEnabled=ProfilerDriver.enabled;previousEditor=ProfilerDriver.profileEditor;previousCallstacks=Profiler.enableAllocationCallstacks;previousMode=ProfilerDriver.memoryRecordMode;
        hadFrames=ProfilerDriver.firstFrameIndex>=0&&ProfilerDriver.lastFrameIndex>=ProfilerDriver.firstFrameIndex;
        baselineSaved=captureStarted=false;
        try
        {
            ProfilerDriver.enabled=false;
            if(hadFrames){baselineSaved=ProfilerDriver.SaveProfile(Path.Combine(folder,"baseline.data"));if(!baselineSaved)throw new IOException("Original buffer backup failed.");}
            ProfilerDriver.ClearAllFrames();ProfilerDriver.profileEditor=true;ProfilerDriver.enabled=true;
            captureStarted=true;
            ticks=0;deadline=EditorApplication.timeSinceStartup+40;running=true;cases.Clear();EditorApplication.update+=Tick;
        }
        catch(Exception failure){Finish(failure);}
    }
    [MenuItem("Tools/UniBridge Validation/Profiler Extensions/RequestDomainReload")]
    public static void RequestDomainReload()
    {
        string owned=SessionState.GetString("UniBridge.ProfilerExtensions.OwnedFolder","");
        if(string.IsNullOrEmpty(owned)||!File.Exists(Path.Combine(owned,"native-input.json")))throw new InvalidOperationException("Prepare owned fixture first.");
        File.WriteAllText(Path.Combine(owned,"before-reload.json"),JsonConvert.SerializeObject(new {editorPid=System.Diagnostics.Process.GetCurrentProcess().Id,requestedUtc=DateTime.UtcNow}));
        UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
    }
    [InitializeOnLoadMethod]
    static void AfterReload()
    {
        string owned=SessionState.GetString("UniBridge.ProfilerExtensions.OwnedFolder","");
        if(!string.IsNullOrEmpty(owned)&&File.Exists(Path.Combine(owned,"before-reload.json"))&&!File.Exists(Path.Combine(owned,"after-reload.json")))
            File.WriteAllText(Path.Combine(owned,"after-reload.json"),JsonConvert.SerializeObject(new {editorPid=System.Diagnostics.Process.GetCurrentProcess().Id,completedUtc=DateTime.UtcNow,enabled=ProfilerDriver.enabled}));
    }
    static void Tick()
    {
        try
        {
            for(int i=0;i<20;i++)using(Repeated.Auto()) { System.Threading.Thread.SpinWait(1000); using(Repeated.Auto()){var bytes=new byte[1024];bytes[0]=17;GC.KeepAlive(bytes);} }
            if(++ticks>=25){Finish(null);return;}
            if(EditorApplication.timeSinceStartup>=deadline)Finish(new TimeoutException("Owned capture fixture exceeded 40 seconds."));
        }
        catch(Exception failure){Finish(failure);}
    }
    static void Finish(Exception failure)
    {
        EditorApplication.update-=Tick;running=false;
        string capture=Path.Combine(folder,"known.data"),second=Path.Combine(folder,"known-second.data"),invalid=Path.Combine(folder,"invalid.data");
        try
        {
            ProfilerDriver.enabled=false;
            if(failure==null)
            {
                if(!ProfilerDriver.SaveProfile(capture)||!ProfilerDriver.SaveProfile(second))throw new IOException("Known capture save failed.");
                if(Hash(capture)!=Hash(second))throw new IOException("Stopped capture SaveProfile is not byte-stable.");
                File.WriteAllText(invalid,"Owned invalid capture fixture 20261012");
            }
        }
        catch(Exception error){failure=error;}
        bool restored=false;
        try
        {
            if(!captureStarted)restored=true;
            else if(hadFrames&&baselineSaved)restored=ProfilerDriver.LoadProfile(Path.Combine(folder,"baseline.data"),false);
            else {ProfilerDriver.ClearAllFrames();restored=ProfilerDriver.firstFrameIndex<0;}
            ProfilerDriver.profileEditor=previousEditor;ProfilerDriver.memoryRecordMode=previousMode;Profiler.enableAllocationCallstacks=previousCallstacks;ProfilerDriver.enabled=previousEnabled;
        }
        catch(Exception error){failure=error;}
        var output=new{passed=failure==null&&restored,unityVersion=Application.unityVersion,processBits=IntPtr.Size*8,originalBufferRestored=restored,
            expectedProjectRoot=Root,capturePath=capture.Substring(Root.Length+1).Replace('\\','/'),captureSha256=File.Exists(capture)?Hash(capture):null,
            invalidPath=invalid.Substring(Root.Length+1).Replace('\\','/'),invalidSha256=File.Exists(invalid)?Hash(invalid):null,
            expectedMarker="UniBridge Extensions Repeated 20261012",minimumCallsPerFrame=40,ticks,error=failure?.ToString()};
        File.WriteAllText(Path.Combine(folder,"native-input.json"),JsonConvert.SerializeObject(output,Formatting.Indented));
        Debug.Log("UniBridge Profiler Extensions native input: "+Path.Combine(folder,"native-input.json"));
    }
}
#endif
