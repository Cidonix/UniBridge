using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Connection;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Microsoft.Win32.SafeHandles;

static class WindowsSidRegression
{
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentThread();
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ImpersonateAnonymousToken(IntPtr thread);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool RevertToSelf();
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern uint GetSecurityInfo(IntPtr handle, int objectType, uint securityInformation,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")]
    static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);

    static int failed;
    static string expectedSid;
    static int iterations = 2000;

    static int Main(string[] args)
    {
        if (args.Length > 0) iterations = int.Parse(args[0]);
#if UNITY_EDITOR_WIN
        expectedSid = Environment.GetEnvironmentVariable("UNIBRIDGE_SID_TEST_ORACLE");
        if (string.IsNullOrEmpty(expectedSid))
            using (var identity = WindowsIdentity.GetCurrent()) expectedSid = identity.User.Value;
        Console.WriteLine("ORACLE\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(expectedSid)));
        if (args.Length > 1)
        {
            Test("live-editor-pipe-current-user-and-system-dacl", () => InspectLivePipe(args[1]));
            return failed == 0 ? 0 : 1;
        }
        Test("native-process-user-sid", () => {
            Equal(expectedSid, GetSid(), "SID must equal the independent managed Windows identity oracle.");
            return "Returned the full Windows process user SID.";
        });
        Test("anonymous-thread-denial-is-safe", AnonymousThread);
        Test("secure-pipe-current-user-and-system-dacl", PipeAcl);
        Test("same-user-production-transport-roundtrip", Roundtrip);
        Test("native-token-and-pipe-handle-cleanup", HandlePressure);
#else
        Test("non-windows-listener-rejects-native-pipe", () => {
            using (var listener = new NamedPipeListener())
            {
                listener.Start("unibridge-sid-nonwin");
                try { listener.AcceptClientAsync(CancellationToken.None).GetAwaiter().GetResult(); }
                catch (PlatformNotSupportedException) { return "Non-Windows conditional contains no native pipe execution."; }
                throw new Exception("Non-Windows listener must throw PlatformNotSupportedException.");
            }
        });
#endif
        return failed == 0 ? 0 : 1;
    }

    static void Test(string name, Func<string> test)
    {
        try { Emit("PASS", name, test()); }
        catch (Exception ex) { failed++; Emit("FAIL", name, ex.ToString()); }
    }

    static void Emit(string status, string name, string detail)
    {
        Console.WriteLine("CHECK\t" + status + "\t" + name + "\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(detail)));
    }

    static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception(message + " Expected: " + expected + "; actual: " + actual);
    }

#if UNITY_EDITOR_WIN
    static string GetSid()
    {
        return (string)typeof(NamedPipeListener).GetMethod("GetCurrentUserSid", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
    }

    static SafeFileHandle CreateSecurePipe(string name)
    {
        return (SafeFileHandle)typeof(NamedPipeListener).GetMethod("CreatePipeViaPInvoke", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { @"\\.\pipe\" + name });
    }

    static string AnonymousThread()
    {
        if (!ImpersonateAnonymousToken(GetCurrentThread()))
            throw new Exception("Cannot establish anonymous thread impersonation: Win32 " + Marshal.GetLastWin32Error());
        string resolved;
        try
        {
            resolved = GetSid();
            if (resolved != null && resolved != expectedSid)
                throw new Exception("Anonymous caller returned a SID belonging to neither the process user nor the safe failure result.");
        }
        finally
        {
            if (!RevertToSelf()) throw new Exception("Cannot restore harness thread identity: Win32 " + Marshal.GetLastWin32Error());
        }
        return resolved == null ? "Anonymous caller was denied token/account access safely; impersonation was reverted." :
            "Anonymous caller retained the original process SID; impersonation was reverted.";
    }

    static string PipeAcl()
    {
        McpLog.Clear();
        using (var handle = CreateSecurePipe("unibridge-sid-acl-" + Guid.NewGuid().ToString("N")))
        {
            if (handle == null || handle.IsInvalid) throw new Exception("Production secure pipe creation failed.");
            CheckDacl(handle.DangerousGetHandle());
        }
        if (McpLog.Messages.Any(message => message.Contains("owner-based") || message.Contains("unsecured") || message.StartsWith("ERROR:")))
            throw new Exception("Production pipe unexpectedly used a security fallback: " + string.Join("; ", McpLog.Messages));
        return "Actual native pipe DACL grants full access only to the process user and SYSTEM; no Everyone ACE.";
    }

    static void CheckDacl(IntPtr pipeHandle)
    {
            IntPtr owner, group, dacl, sacl, descriptor;
            var status = GetSecurityInfo(pipeHandle, 6, 4, out owner, out group, out dacl, out sacl, out descriptor);
            if (status != 0) throw new Exception("Cannot inspect native pipe security descriptor: Win32 " + status);
            try
            {
                int size = checked((int)GetSecurityDescriptorLength(descriptor));
                var bytes = new byte[size];
                Marshal.Copy(descriptor, bytes, 0, size);
                var raw = new RawSecurityDescriptor(bytes, 0);
                if (raw.DiscretionaryAcl == null) throw new Exception("Pipe has a null DACL.");
                Equal(2, raw.DiscretionaryAcl.Count, "Pipe DACL must contain exactly two ACEs.");
                var seen = new HashSet<string>();
                foreach (GenericAce entry in raw.DiscretionaryAcl)
                {
                    var ace = entry as CommonAce;
                    if (ace == null || ace.AceQualifier != AceQualifier.AccessAllowed || ace.IsCallback)
                        throw new Exception("Unexpected pipe ACE type.");
                    string sid = ace.SecurityIdentifier.Value;
                    if (sid != expectedSid && sid != "S-1-5-18") throw new Exception("Unexpected pipe access grant: " + sid);
                    if ((ace.AccessMask & 0x10000000) == 0 && (ace.AccessMask & 0x001F01FF) != 0x001F01FF)
                        throw new Exception("Expected full pipe access for intended principals, mask " + ace.AccessMask);
                    seen.Add(sid);
                }
                if (!seen.Contains(expectedSid) || !seen.Contains("S-1-5-18"))
                    throw new Exception("Pipe DACL is missing current user or SYSTEM.");
            }
            finally { LocalFree(descriptor); }
    }

    static string InspectLivePipe(string path)
    {
        const string prefix = @"\\.\pipe\";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || path.Length <= prefix.Length)
            throw new Exception("Pass the exact local discovery pipe path (\\\\.\\pipe\\name).");
        using (var client = new NamedPipeClientStream(".", path.Substring(prefix.Length), PipeDirection.InOut, PipeOptions.Asynchronous))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            client.Connect(5000);
            // The Editor eagerly writes a newline-terminated handshake. Consume
            // it before closing our client so that this read-only ACL probe does
            // not interrupt the server's initial write/flush.
            var handshake = new List<byte>();
            var buffer = new byte[8192];
            var complete = false;
            while (!complete)
            {
                var count = client.ReadAsync(buffer, 0, buffer.Length, timeout.Token).GetAwaiter().GetResult();
                if (count == 0) throw new Exception("Editor closed the pipe before its initial handshake completed.");
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] == 10) { complete = true; break; }
                    handshake.Add(buffer[index]);
                    if (handshake.Count > 4 * 1024 * 1024)
                        throw new Exception("Editor handshake exceeded the 4 MiB inspection limit.");
                }
            }
            if (handshake.Count == 0) throw new Exception("Editor returned an empty initial handshake.");
            CheckDacl(client.SafePipeHandle.DangerousGetHandle());
        }
        return "Connected once to the exact live Editor pipe, consumed its initial handshake within 5 seconds, inspected its explicit process-user/SYSTEM DACL and closed only this harness client; no protocol commands were sent.";
    }

    static string Roundtrip()
    {
        McpLog.Clear();
        var pipeName = "unibridge-sid-roundtrip-" + Guid.NewGuid().ToString("N");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        using (var listener = new NamedPipeListener())
        using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            listener.Start(pipeName);
            var accept = listener.AcceptClientAsync(timeout.Token);
            client.ConnectAsync(5000, timeout.Token).GetAwaiter().GetResult();
            using (var transport = accept.GetAwaiter().GetResult())
            {
                var request = new byte[] { 0, 1, 2, 128, 255, 10 };
                var receive = transport.ReadUntilDelimiterAsync(10, 1024, 5000, timeout.Token);
                client.WriteAsync(request, 0, request.Length, timeout.Token).GetAwaiter().GetResult();
                var received = receive.GetAwaiter().GetResult();
                if (!request.SequenceEqual(received)) throw new Exception("Production transport corrupted the client request.");
                var response = Encoding.UTF8.GetBytes("UniBridge SID ✓\n");
                var send = transport.WriteAsync(response, timeout.Token);
                var reply = new byte[response.Length];
                var read = 0;
                while (read < reply.Length)
                {
                    var count = client.ReadAsync(reply, read, reply.Length - read, timeout.Token).GetAwaiter().GetResult();
                    if (count == 0) throw new Exception("Client disconnected before full response.");
                    read += count;
                }
                if (!response.SequenceEqual(reply)) throw new Exception("Production transport corrupted the server response.");
                send.GetAwaiter().GetResult();
                Equal(Process.GetCurrentProcess().Id, transport.GetClientProcessId(), "Native client PID must be the harness process.");
            }
        }
        if (McpLog.Messages.Any(message => message.Contains("unsecured") || message.StartsWith("ERROR:")))
            throw new Exception("Roundtrip used insecure fallback: " + string.Join("; ", McpLog.Messages));
        return "Production listener/transport accepted a same-user client and exchanged binary/UTF-8 messages in both directions.";
    }

    static string HandlePressure()
    {
        for (var i = 0; i < 100; i++) GetSid();
        using (var warmup = CreateSecurePipe("unibridge-sid-warmup-" + Guid.NewGuid().ToString("N"))) { }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using (var process = Process.GetCurrentProcess())
        {
            process.Refresh();
            var before = process.HandleCount;
            for (var i = 0; i < iterations; i++) Equal(expectedSid, GetSid(), "Repeated SID resolution changed identity.");
            for (var i = 0; i < 100; i++)
                using (var handle = CreateSecurePipe("unibridge-sid-pressure-" + Guid.NewGuid().ToString("N")))
                    if (handle == null || handle.IsInvalid) throw new Exception("Secure pipe failed under native resource pressure.");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            process.Refresh();
            var after = process.HandleCount;
            if (after > before + 4) throw new Exception("Native handles leaked: before " + before + ", after " + after);
            return iterations + " SID queries and 100 create/dispose cycles: handle count " + before + " -> " + after + " (allowed runtime noise 4).";
        }
    }
#endif
}
