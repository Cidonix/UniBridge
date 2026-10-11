using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.Tools;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry.Parameters;

internal static class Regression
{
    static readonly List<object> checks = new List<object>();
    static readonly List<JObject> payloads = new List<JObject>();
    static int failures;
    static string root;
    static void Check(string name, bool okay) { checks.Add(new {name,passed=okay}); if(!okay) failures++; }
    static string FileAt(string name, string text) { var p=Path.Combine(root,"Assets",name); File.WriteAllText(p,text,new UTF8Encoding(false));return p; }
    static string Hash(byte[] b) { using(var s=SHA256.Create())return BitConverter.ToString(s.ComputeHash(b)).Replace("-","").ToLowerInvariant(); }
    static JObject Payload(object response) { var value=JObject.FromObject(response);payloads.Add(value);return value; }
    static ReadResourceParams ReadInput(string name) => new ReadResourceParams {Uri="Assets/"+name,ProjectRoot=root};
    static FindInFileParams FindInput(string name,string pattern) => new FindInFileParams {Uri="Assets/"+name,Pattern=pattern,ProjectRoot=root};
    static bool Valid(JToken value,JObject rule,JObject top)
    {
        if(rule["$ref"]!=null)return Valid(value,(JObject)top.SelectToken(((string)rule["$ref"]).Replace("#/$defs/","$defs.")),top);
        if(rule["anyOf"] is JArray union)return union.Any(x=>Valid(value,(JObject)x,top));
        if(rule["const"]!=null&&!JToken.DeepEquals(rule["const"],value))return false;
        if(rule["enum"] is JArray values&&!values.Any(x=>JToken.DeepEquals(x,value)))return false;
        var t=(string)rule["type"];
        if(t=="object")
        {
            if(!(value is JObject obj))return false;
            var props=rule["properties"] as JObject;
            if(rule["required"] is JArray req&&req.Any(x=>obj[(string)x]==null))return false;
            foreach(var p in obj.Properties())
            {
                var r=props?[p.Name] as JObject;
                if(r==null) { if(rule["additionalProperties"]?.Type==JTokenType.Boolean&&!(bool)rule["additionalProperties"])return false; if(rule["additionalProperties"] is JObject extra&&!Valid(p.Value,extra,top))return false; }
                else if(!Valid(p.Value,r,top))return false;
            }
        }
        if(t=="array"&&(!(value is JArray array)||array.Any(x=>!Valid(x,(JObject)rule["items"],top))))return false;
        if(t=="integer"&&value.Type!=JTokenType.Integer)return false;
        if(t=="string"&&value.Type!=JTokenType.String)return false;
        if(t=="boolean"&&value.Type!=JTokenType.Boolean)return false;
        if(t=="null"&&value.Type!=JTokenType.Null)return false;
        return true;
    }
    public static int Main(string[] args)
    {
        root=Path.Combine(Path.GetDirectoryName(args[0]),"owned-io");Directory.CreateDirectory(Path.Combine(root,"Assets"));
        var small=FileAt("small.txt","alpha\r\nBeta beta\r\nlast\n");
        var bytes=File.ReadAllBytes(small);var hash=Hash(bytes);
        var full=Payload(ResourceTools.ReadResource(ReadInput("small.txt")));
        Check("ordinary read preserves success/text/whole-byte SHA",(bool)full["success"]&&(string)full["data"]["Text"]==Encoding.UTF8.GetString(bytes)&&(string)full["data"]["Metadata"]["Sha256"]==hash&&(bool)full["data"]["Metadata"]["Sha256Complete"]);
        var inp=ReadInput("small.txt");inp.StartLine=2;inp.LineCount=1;
        var sliced=Payload(ResourceTools.ReadResource(inp));Check("CRLF slice retains CR and whole-file SHA",(string)sliced["data"]["Text"]=="Beta beta\r"&&(string)sliced["data"]["Metadata"]["Sha256"]==hash);
        inp=ReadInput("small.txt");inp.TailLines=2;Check("tail includes trailing empty line",(string)Payload(ResourceTools.ReadResource(inp))["data"]["Text"]=="last\n");
        inp=ReadInput("small.txt");inp.HeadBytes=3;inp.TailLines=1;Check("head precedence unchanged",(string)Payload(ResourceTools.ReadResource(inp))["data"]["Text"]=="alp");
        inp=ReadInput("small.txt");inp.Request="first 2 lines";Check("natural slice reads bounded in-memory text",(string)Payload(ResourceTools.ReadResource(inp))["data"]["Text"]=="alpha\r\nBeta beta\r");
        inp.Request="last 2 lines";Check("natural tail",(string)Payload(ResourceTools.ReadResource(inp))["data"]["Text"]=="last\n");
        inp.Request="first 3 bytes";Check("natural head",(string)Payload(ResourceTools.ReadResource(inp))["data"]["Text"]=="alp");
        FileAt("method.txt","class Example\n{\n public void Tick()\n { }\n}\n");inp=ReadInput("method.txt");inp.Request="show 3 lines around Tick";Check("method natural window",(string)Payload(ResourceTools.ReadResource(inp))["data"]["Text"]=="{\n public void Tick()\n { }");
        inp.Request="first 9999999999999999 lines";Check("request integer overflow is blocked",!(bool)Payload(ResourceTools.ReadResource(inp))["success"]);
        var bom=Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("x\n")).ToArray();File.WriteAllBytes(Path.Combine(root,"Assets","bom.txt"),bom);
        Check("BOM identity and byte hash retained",(string)Payload(ResourceTools.ReadResource(ReadInput("bom.txt")))["data"]["Text"]==Encoding.UTF8.GetString(bom));
        FileAt("empty.txt","");var empty=Payload(ResourceTools.ReadResource(ReadInput("empty.txt")));Check("empty EOF exact SHA",(bool)empty["success"]&&(string)empty["data"]["Metadata"]["Sha256"]==Hash(new byte[0]));
        inp=ReadInput("small.txt");inp.MaxFileBytes=bytes.Length;Check("exact scan boundary is complete",(bool)Payload(ResourceTools.ReadResource(inp))["success"]);
        var large=Path.Combine(root,"Assets","large.txt");using(var f=File.Create(large))f.SetLength(8*1024*1024);
        inp=ReadInput("large.txt");inp.MaxFileBytes=1024;var cutoff=Payload(ResourceTools.ReadResource(inp));Check("large file scan stays within cap and never claims full SHA",!(bool)cutoff["success"]&&(long)cutoff["data"]["Outcome"]["bytesScanned"]==1024&&cutoff["data"]["Metadata"]["Sha256"].Type==JTokenType.Null&&!(bool)cutoff["data"]["Metadata"]["Sha256Complete"]);
        inp.TailLines=3;var noTail=Payload(ResourceTools.ReadResource(inp));Check("partial scan cannot fabricate file tail",(string)noTail["data"]["Text"]==""&&(string)noTail["data"]["Outcome"]["reason"]=="tail_unavailable_without_eof");
        FileAt("unicode.txt","a😀їz");inp=ReadInput("unicode.txt");inp.MaxReturnBytes=5;var utf=Payload(ResourceTools.ReadResource(inp));Check("return byte cap preserves Unicode scalar and full scanned SHA",!(bool)utf["success"]&&(string)utf["data"]["Text"]=="a😀"&&(long)utf["data"]["Outcome"]["returnedBytes"]==5&&(bool)utf["data"]["Metadata"]["Sha256Complete"]);
        var found=Payload(ResourceTools.FindInFile(FindInput("small.txt","beta")));Check("find first per line, old serialized coordinates",(bool)found["success"]&&(int)found["data"]["count"]==1&&(int)found["data"]["matches"][0]["startLine"]==2&&(int)found["data"]["matches"][0]["startCol"]==1&&(int)found["data"]["matches"][0]["endCol"]==5);
        var invalid=Payload(ResourceTools.FindInFile(FindInput("small.txt","[")));Check("invalid regex is an error before bytes scanned",!(bool)invalid["success"]&&(string)invalid["code"]=="RESOURCE_INVALID_REGEX"&&(long)invalid["data"]["outcome"]["bytesScanned"]==0);
        FileAt("regex.txt",new string('a',12000)+"!");var fp=FindInput("regex.txt","^(a+)+$");fp.RegexTimeoutMs=30;var sw=Stopwatch.StartNew();var regex=Payload(ResourceTools.FindInFile(fp));Check("pathological regex is bounded at 30ms match budget",!(bool)regex["success"]&&(string)regex["code"]=="RESOURCE_REGEX_TIMEOUT"&&sw.ElapsedMilliseconds<1500&&(bool)regex["data"]["sha256Complete"]);
        fp=FindInput("large.txt","nomatch");fp.MaxWorkUnits=1;var work=Payload(ResourceTools.FindInFile(fp));Check("scan work cutoff truthful",!(bool)work["success"]&&(string)work["code"]=="RESOURCE_WORK_LIMIT"&&!(bool)work["data"]["sha256Complete"]);
        FileAt("many.txt",string.Concat(Enumerable.Repeat("x\n",500000)));fp=FindInput("many.txt","nomatch");fp.TimeoutMs=1;var timed=Payload(ResourceTools.FindInFile(fp));Check("whole-operation observation timeout",!(bool)timed["success"]&&(string)timed["code"]=="RESOURCE_TIMEOUT");
        using(var c=new CancellationTokenSource())
        {
            c.Cancel();ToolExecutionScheduler.CurrentCancellationToken=c.Token;
            var cancelled=Payload(ResourceTools.ReadResource(ReadInput("large.txt")));Check("pre-cancel read scans zero bytes",!(bool)cancelled["success"]&&(string)cancelled["code"]=="RESOURCE_CANCELLED"&&(long)cancelled["data"]["Outcome"]["bytesScanned"]==0);
            var list=Payload(ResourceTools.ListResources(new ListResourcesParams {ProjectRoot=root}));Check("pre-cancel list visits zero entries",!(bool)list["success"]&&(int)list["data"]["Outcome"]["entriesVisited"]==0);
            var f=Payload(ResourceTools.FindInFile(FindInput("large.txt","x")));Check("pre-cancel search scans zero bytes",!(bool)f["success"]&&(long)f["data"]["outcome"]["bytesScanned"]==0);
        }
        using(var c=new CancellationTokenSource())
        {
            ToolExecutionScheduler.CurrentCancellationToken=c.Token;c.CancelAfter(10);sw.Restart();fp=FindInput("many.txt","nomatch");fp.TimeoutMs=3000;
            var f=Payload(ResourceTools.FindInFile(fp));Check("in-flight CTS cancels actual file search without long wait",!(bool)f["success"]&&(string)f["code"]=="RESOURCE_CANCELLED"&&sw.ElapsedMilliseconds<1500&&(long)f["data"]["outcome"]["workUnits"]>0);
        }
        ToolExecutionScheduler.CurrentCancellationToken=CancellationToken.None;
        FileAt("matches.txt","x\nx\nx");fp=FindInput("matches.txt","x");fp.MaxResults=1;var partial=Payload(ResourceTools.FindInFile(fp));Check("configured match limit remains success but partial coverage explicit",(bool)partial["success"]&&!(bool)partial["data"]["outcome"]["complete"]&&(string)partial["data"]["outcome"]["reason"]=="result_limit");
        fp.MaxFileBytes=3;var strong=Payload(ResourceTools.FindInFile(fp));Check("result limit cannot mask partial byte scan as success",!(bool)strong["success"]&&(string)strong["code"]=="RESOURCE_FILE_BYTES"&&!(bool)strong["data"]["sha256Complete"]);
        var listing=Payload(ResourceTools.ListResources(new ListResourcesParams {ProjectRoot=root,Pattern="*.TXT",Limit=1}));Check("lazy list result limit and special resource preserve old counts",(bool)listing["success"]&&(int)listing["data"]["Count"]==2&&(string)listing["data"]["Uris"][1]=="unity://spec/script-edits"&&!(bool)listing["data"]["Outcome"]["complete"]);
        listing=Payload(ResourceTools.ListResources(new ListResourcesParams {ProjectRoot=root,Pattern="*.none",MaxEntries=2}));Check("entry scan cutoff is an error with visited bound",!(bool)listing["success"]&&(string)listing["code"]=="RESOURCE_ENTRY_LIMIT"&&(int)listing["data"]["Outcome"]["entriesVisited"]==2);
        Check("missing path keeps existing error",!(bool)Payload(ResourceTools.ReadResource(ReadInput("missing.txt")))["success"]);
        var schemas=new Dictionary<string,JObject>{{"read",ResourceActionOutputRules.Build("UniBridge_ReadResource")},{"find",ResourceActionOutputRules.Build("UniBridge_FindInFile")},{"list",ResourceActionOutputRules.Build("UniBridge_ListResources")}};
        foreach(var p in payloads) { string family=p["data"]?["matches"]!=null?"find":p["data"]?["Uris"]!=null?"list":"read";Check("closed schema accepts actual "+family+" payload "+checks.Count,Valid(p,schemas[family],schemas[family])); }
        var rogue=(JObject)full.DeepClone();rogue["data"]["Outcome"]["unexpected"]=true;Check("closed schema rejects unexpected nested outcome property",!Valid(rogue,schemas["read"],schemas["read"]));
        File.WriteAllText(args[0],JsonConvert.SerializeObject(new {passed=failures==0,cases=checks.Count,failures,nativeExecuted=false,ownedRoot=root,checks,payloads,schemas},Formatting.Indented));
        Console.WriteLine(JsonConvert.SerializeObject(new {passed=failures==0,cases=checks.Count,failures,report=args[0]}));return failures==0?0:1;
    }
}
