using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Cidonix.UniBridge.MCP.Editor.Tools;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry.Parameters;

// Copy only this fixture + its asmdef into a fresh owned Assets/Editor folder.
// All test content lives in a fresh OS Temp root; no scene/asset authoring/save.
public static class UniBridgeResourceBudgetLiveRegression
{
    public static string ReportPath;
    [MenuItem("Tools/UniBridge/Qualification/Remaining item 2 Resource budgets")]
    public static void Run()
    {
        var owned=Path.Combine(Path.GetTempPath(),"UniBridgeResourceBudgetNative-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(owned,"Assets"));
        var checks=new List<object>();int failed=0;
        Action<string,Func<bool>> check=(name,test)=>{try {bool pass=test();checks.Add(new{name,passed=pass});if(!pass)failed++;}catch(Exception e){failed++;checks.Add(new{name,passed=false,error=e.ToString()});}};
        var type=typeof(ResourceTools).Assembly.GetType("Cidonix.UniBridge.MCP.Editor.Helpers.McpJson",true);
        var serialize=type.GetMethod("SerializeObject",BindingFlags.Static|BindingFlags.Public,null,new[]{typeof(object)},null);
        Func<object,JObject> json=o=>JObject.Parse((string)serialize.Invoke(null,new[]{o}));
        Func<string,ReadResourceParams> input=name=>new ReadResourceParams{Uri="Assets/"+name,ProjectRoot=owned};
        var small=Encoding.UTF8.GetBytes("alpha\r\nBeta\n");File.WriteAllBytes(Path.Combine(owned,"Assets","small.txt"),small);
        string expected;using(var sha=SHA256.Create())expected=BitConverter.ToString(sha.ComputeHash(small)).Replace("-","").ToLowerInvariant();
        check("ordinary Mono read and exact full-file hash",()=>{var r=json(ResourceTools.ReadResource(input("small.txt")));return (bool)r["success"]&&(string)r["data"]["Text"]==Encoding.UTF8.GetString(small)&&(string)r["data"]["Metadata"]["Sha256"]==expected&&(bool)r["data"]["Metadata"]["Sha256Complete"];});
        using(var file=File.Create(Path.Combine(owned,"Assets","large.txt")))file.SetLength(8*1024*1024);
        check("large file byte cap gives expected error and no full SHA",()=>{var p=input("large.txt");p.MaxFileBytes=1024;var r=json(ResourceTools.ReadResource(p));return !(bool)r["success"]&&(string)r["code"]=="RESOURCE_FILE_BYTES"&&(long)r["data"]["Outcome"]["bytesScanned"]==1024&&r["data"]["Metadata"]["Sha256"].Type==JTokenType.Null&&!(bool)r["data"]["Metadata"]["Sha256Complete"];});
        File.WriteAllText(Path.Combine(owned,"Assets","utf.txt"),"a😀їz",new UTF8Encoding(false));
        check("UTF8 return cap retains scalar and true whole-file SHA",()=>{var p=input("utf.txt");p.MaxReturnBytes=5;var r=json(ResourceTools.ReadResource(p));return !(bool)r["success"]&&(string)r["code"]=="RESOURCE_RETURN_BYTES"&&(string)r["data"]["Text"]=="a😀"&&(bool)r["data"]["Metadata"]["Sha256Complete"];});
        check("invalid regex gives expected error before IO",()=>{var r=json(ResourceTools.FindInFile(new FindInFileParams{Uri="Assets/small.txt",ProjectRoot=owned,Pattern="["}));return !(bool)r["success"]&&(string)r["code"]=="RESOURCE_INVALID_REGEX"&&(long)r["data"]["outcome"]["bytesScanned"]==0;});
        File.WriteAllText(Path.Combine(owned,"Assets","regex.txt"),new string('a',12000)+"!",new UTF8Encoding(false));
        check("Mono regex match timeout at 30ms budget",()=>{var watch=Stopwatch.StartNew();var r=json(ResourceTools.FindInFile(new FindInFileParams{Uri="Assets/regex.txt",ProjectRoot=owned,Pattern="^(a+)+$",RegexTimeoutMs=30}));return !(bool)r["success"]&&(string)r["code"]=="RESOURCE_REGEX_TIMEOUT"&&watch.ElapsedMilliseconds<1500;});
        check("lazy list entry error and configured result partial success",()=>{var limited=json(ResourceTools.ListResources(new ListResourcesParams{ProjectRoot=owned,Pattern="*.none",MaxEntries=2}));var partial=json(ResourceTools.ListResources(new ListResourcesParams{ProjectRoot=owned,Pattern="*.txt",Limit=1}));return !(bool)limited["success"]&&(string)limited["code"]=="RESOURCE_ENTRY_LIMIT"&&(int)limited["data"]["Outcome"]["entriesVisited"]==2&&(bool)partial["success"]&&!(bool)partial["data"]["Outcome"]["complete"]&&(int)partial["data"]["Count"]==2;});
        var path=string.IsNullOrEmpty(ReportPath)?Path.Combine(owned,"native-result.json"):Path.GetFullPath(ReportPath);
        if(File.Exists(path))throw new IOException("Refusing to overwrite native evidence: "+path);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path,JsonConvert.SerializeObject(new{passed=failed==0,cases=checks.Count,failures=failed,nativeExecuted=true,unityVersion=Application.unityVersion,pointerSize=IntPtr.Size,ownedRoot=owned,checks},Formatting.Indented));
        UnityEngine.Debug.Log("UniBridge resource budgets: "+(failed==0?"PASS":"FAIL")+" "+path);
    }
}
