#if NATIVE_FAILURES
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Cidonix.UniBridge.MCP.Editor.Connection;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Microsoft.Win32.SafeHandles;

/// <summary>Runs the complete production listener with deterministic native boundaries.</summary>
public static class FailureRegression
{
    static int failed;
    static int passed;

    public static int Main()
    {
        Case("token-user-success-and-resource-cleanup", () => {
            Equal(NativeFailures.SidValue, GetSid(), "Process token SID.");
            Equal(1, NativeFailures.OpenCalls, "Process token opened once.");
            Equal(2, NativeFailures.QueryCalls, "TokenUser probe and fill queries.");
            Equal(1, NativeFailures.ConversionCalls, "SID conversion called once.");
            Counts(1, 1, 1);
        });
        Case("sid-conversion-is-unicode", () => {
            // Deliberately non-SID text at the native string boundary proves UTF-16 decoding.
            NativeFailures.SidValue = "S-1-5-21-\u0141-\u6f22-\ud83d\ude42";
            Equal(NativeFailures.SidValue, GetSid(), "The returned native string must be read as UTF-16.");
            Counts(1, 1, 1);
        });
        Case("process-handle-uses-token-query-access", () => {
            GetSid();
            Equal(new IntPtr(-1), NativeFailures.OpenedProcess, "Process pseudo-handle.");
            Equal(0x0008U, NativeFailures.RequestedAccess, "Only TOKEN_QUERY is requested.");
            Equal(1, NativeFailures.InformationClass, "The requested token class is TokenUser.");
            Equal(1, NativeFailures.ProcessCalls, "Process handle acquired once.");
            Counts(1, 1, 1);
        });

        Reject("process-handle-exception", () => NativeFailures.ProcessThrows = true, 0, 0, 0);
        Reject("open-process-token-failure", () => NativeFailures.OpenResult = false, 0, 0, 0);
        Reject("open-process-token-failure-with-handle", () => {
            NativeFailures.OpenResult = false;
            NativeFailures.OpenFailureHasHandle = true;
        }, 0, 0, 1);
        Reject("open-process-token-exception-before-handle", () => NativeFailures.OpenThrowsBeforeHandle = true, 0, 0, 0);
        Reject("open-process-token-exception-after-handle", () => NativeFailures.OpenThrowsAfterHandle = true, 0, 0, 1);
        Reject("token-size-probe-unexpected-success", () => NativeFailures.ProbeResult = true, 0, 0, 1);
        Reject("token-size-probe-access-denied", () => NativeFailures.ProbeError = 5, 0, 0, 1);
        Reject("token-size-probe-no-error", () => NativeFailures.ProbeError = 0, 0, 0, 1);
        Reject("token-size-probe-zero-size", () => NativeFailures.RequiredLength = 0, 0, 0, 1);
        Reject("token-size-probe-undersized-structure", () => NativeFailures.RequiredLength = NativeFailures.TokenUserSize - 1, 0, 0, 1);
        Reject("token-size-probe-exceeds-int-max", () => NativeFailures.RequiredLength = (uint)int.MaxValue + 1U, 0, 0, 1);
        Reject("token-size-probe-max-uint", () => NativeFailures.RequiredLength = uint.MaxValue, 0, 0, 1);
        Reject("token-size-probe-exception", () => NativeFailures.ProbeThrows = true, 0, 0, 1);
        Reject("token-buffer-allocation-exception", () => NativeFailures.AllocationThrows = true, 0, 0, 1);
        Reject("token-fill-query-failure", () => NativeFailures.FillResult = false, 1, 0, 1);
        Reject("token-fill-query-zero-returned-size", () => NativeFailures.ReturnedLength = 0, 1, 0, 1);
        Reject("token-fill-query-undersized-structure", () => NativeFailures.ReturnedLength = NativeFailures.TokenUserSize - 1, 1, 0, 1);
        Reject("token-fill-query-size-exceeds-buffer", () => NativeFailures.ReturnedLength = NativeFailures.RequiredLength + 1, 1, 0, 1);
        Reject("token-fill-query-exception", () => NativeFailures.FillThrows = true, 1, 0, 1);
        Reject("token-user-null-sid", () => NativeFailures.NullSid = true, 1, 0, 1);
        Reject("sid-conversion-failure", () => NativeFailures.ConversionResult = false, 1, 0, 1);
        Reject("sid-conversion-failure-with-string", () => {
            NativeFailures.ConversionResult = false;
            NativeFailures.ConversionFailureHasString = true;
        }, 1, 1, 1);
        Reject("sid-conversion-success-with-null-string", () => NativeFailures.NullStringSid = true, 1, 0, 1);
        Reject("sid-conversion-exception-before-string", () => NativeFailures.ConversionThrowsBeforeString = true, 1, 0, 1);
        Reject("sid-conversion-exception-after-string", () => NativeFailures.ConversionThrowsAfterString = true, 1, 1, 1);

        Case("minimum-token-user-buffer-size-is-valid", () => {
            NativeFailures.RequiredLength = NativeFailures.TokenUserSize;
            NativeFailures.ReturnedLength = NativeFailures.TokenUserSize;
            Equal(NativeFailures.SidValue, GetSid(), "A complete minimum-sized TOKEN_USER is accepted.");
            Counts(1, 1, 1);
        });
        Case("token-user-returned-size-can-be-smaller-than-allocation", () => {
            NativeFailures.RequiredLength = NativeFailures.TokenUserSize + 256;
            NativeFailures.ReturnedLength = NativeFailures.TokenUserSize;
            Equal(NativeFailures.SidValue, GetSid(), "A complete returned TOKEN_USER is accepted in a larger allocated buffer.");
            Counts(1, 1, 1);
        });
        Case("failed-size-validation-stops-before-allocation", () => {
            NativeFailures.ProbeError = 5;
            Equal<string>(null, GetSid(), "Rejected probe.");
            Equal(1, NativeFailures.QueryCalls, "No second query on invalid probe.");
            Equal(0, NativeFailures.ConversionCalls, "No SID conversion on invalid probe.");
            Counts(0, 0, 1);
        });
        Case("failed-fill-query-stops-before-sid-conversion", () => {
            NativeFailures.FillResult = false;
            Equal<string>(null, GetSid(), "Rejected fill query.");
            Equal(2, NativeFailures.QueryCalls, "Probe then failed fill.");
            Equal(0, NativeFailures.ConversionCalls, "No SID conversion after failed fill.");
            Counts(1, 0, 1);
        });

        Case("secure-pipe-specific-user-and-system-sddl", () => {
            using (var pipe = CreatePipe()) AssertPipe(pipe);
            Equal("D:(A;;GA;;;" + NativeFailures.SidValue + ")(A;;GA;;;SY)", NativeFailures.LastSddl, "Explicit user and SYSTEM ACL.");
            Equal(0, NativeFailures.InheritHandle, "Pipe handles must not inherit.");
            Equal(1, NativeFailures.CreatePipeCalls, "Secure native pipe creation called once.");
            Counts(2, 2, 1);
        });
        Case("secure-pipe-owner-and-system-fallback-sddl", () => {
            NativeFailures.OpenResult = false;
            using (var pipe = CreatePipe()) AssertPipe(pipe);
            Equal("D:(A;;GA;;;OW)(A;;GA;;;SY)", NativeFailures.LastSddl, "Failed SID lookup retains owner and SYSTEM only.");
            Equal(1, NativeFailures.CreatePipeCalls, "Secure pipe still uses the descriptor on fallback.");
            Equal(0, NativeFailures.InheritHandle, "Owner fallback remains non-inheritable.");
            Counts(1, 1, 0);
        });
        Case("empty-native-sid-string-uses-owner-fallback", () => {
            NativeFailures.SidValue = "";
            using (var pipe = CreatePipe()) AssertPipe(pipe);
            Equal("D:(A;;GA;;;OW)(A;;GA;;;SY)", NativeFailures.LastSddl, "Empty SID text retains the owner ACL fallback.");
            Counts(2, 2, 1);
        });
        Case("security-descriptor-conversion-failure-cleanup", () => {
            NativeFailures.DescriptorResult = false;
            Equal<SafeFileHandle>(null, CreatePipe(), "No pipe without a security descriptor.");
            Equal(0, NativeFailures.CreatePipeCalls, "Pipe API is skipped after descriptor conversion fails.");
            Counts(1, 1, 1);
        });
        Case("security-descriptor-conversion-failure-with-buffer-cleanup", () => {
            NativeFailures.DescriptorResult = false;
            NativeFailures.DescriptorFailureHasBuffer = true;
            Equal<SafeFileHandle>(null, CreatePipe(), "Failed descriptor conversion.");
            Counts(1, 2, 1);
        });
        Case("security-descriptor-exception-after-buffer-cleanup", () => {
            NativeFailures.DescriptorThrowsAfterBuffer = true;
            Equal<SafeFileHandle>(null, CreatePipe(), "Descriptor API exception is contained.");
            Counts(1, 2, 1);
        });
        Case("security-attributes-allocation-exception-releases-descriptor", () => {
            NativeFailures.AllocationThrowOnAttempt = 2;
            Equal<SafeFileHandle>(null, CreatePipe(), "Security-attribute allocation exception is contained.");
            Counts(1, 2, 1);
        });
        Case("pipe-api-exception-releases-security-attributes-and-descriptor", () => {
            NativeFailures.CreatePipeThrows = true;
            Equal<SafeFileHandle>(null, CreatePipe(), "Native pipe exception is contained.");
            Counts(2, 2, 1);
        });
        Case("pipe-api-invalid-handle-releases-security-attributes-and-descriptor", () => {
            NativeFailures.InvalidPipeHandle = true;
            Equal<SafeFileHandle>(null, CreatePipe(), "Invalid native pipe handle is rejected.");
            Counts(2, 2, 1);
        });

        Console.WriteLine("SUMMARY\t" + passed + "\t" + failed);
        return failed == 0 ? 0 : 1;
    }

    static void Reject(string name, Action configure, int allocations, int localFrees, int tokenCloses)
    {
        Case(name, () => {
            configure();
            Equal<string>(null, GetSid(), "The native failure must return null, never a guessed SID.");
            Counts(allocations, localFrees, tokenCloses);
        });
    }

    static void Case(string name, Action test)
    {
        NativeFailures.Reset();
        McpLog.Clear();
        try
        {
            test();
            NativeFailures.AssertBalanced();
            passed++;
            Emit("PASS", name, "Production method completed with the expected native calls and no live token, HGlobal buffer, SID string, or descriptor.");
        }
        catch (Exception ex)
        {
            failed++;
            Emit("FAIL", name, ex.ToString() + "\nCalls: " + string.Join("; ", NativeFailures.Trace));
        }
        finally { NativeFailures.AbortCleanup(); }
    }

    static void Emit(string status, string name, string detail)
    {
        Console.WriteLine("CHECK\t" + status + "\t" + name + "\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(detail)));
    }

    static string GetSid()
    {
        return (string)typeof(NamedPipeListener).GetMethod("GetCurrentUserSid", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
    }

    static SafeFileHandle CreatePipe()
    {
        return (SafeFileHandle)typeof(NamedPipeListener).GetMethod("CreatePipeViaPInvoke", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { @"\\.\pipe\unibridge-sid-failure-fixture" });
    }

    static void AssertPipe(SafeFileHandle pipe)
    {
        if (pipe == null || pipe.IsInvalid) throw new Exception("The controlled secure pipe must be accepted.");
    }

    static void Counts(int allocations, int localFrees, int tokenCloses)
    {
        Equal(allocations, NativeFailures.AllocationCalls, "Expected HGlobal allocation count.");
        Equal(allocations, NativeFailures.FreeCalls, "Every HGlobal allocation must be freed once.");
        Equal(localFrees, NativeFailures.LocalFreeCalls, "Every returned native allocation must be LocalFree'd once.");
        Equal(tokenCloses, NativeFailures.CloseCalls, "Every returned token handle must close once.");
    }

    internal static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception(message + " Expected: " + expected + "; actual: " + actual);
    }
}

/// <summary>Typed replacements for native APIs and the two HGlobal boundaries.</summary>
static class NativeFailures
{
    [StructLayout(LayoutKind.Sequential)]
    struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    struct TokenUser { public SidAndAttributes User; }

    static readonly IntPtr token = new IntPtr(0x1001);
    static readonly IntPtr sid = new IntPtr(0x2002);
    static readonly HashSet<IntPtr> allocations = new HashSet<IntPtr>();
    static readonly HashSet<IntPtr> nativeAllocations = new HashSet<IntPtr>();
    static readonly List<string> boundaryViolations = new List<string>();
    static bool tokenLive;
    static IntPtr descriptor;

    internal static readonly List<string> Trace = new List<string>();
    internal static uint TokenUserSize { get { return (uint)Marshal.SizeOf(typeof(TokenUser)); } }
    internal static string SidValue;
    internal static bool ProcessThrows, OpenResult, OpenFailureHasHandle, OpenThrowsBeforeHandle, OpenThrowsAfterHandle;
    internal static bool ProbeResult, ProbeThrows, FillResult, FillThrows, NullSid, AllocationThrows;
    internal static bool ConversionResult, ConversionFailureHasString, ConversionThrowsBeforeString, ConversionThrowsAfterString, NullStringSid;
    internal static bool DescriptorResult, DescriptorFailureHasBuffer, DescriptorThrowsAfterBuffer, CreatePipeThrows, InvalidPipeHandle;
    internal static uint RequiredLength, ReturnedLength, RequestedAccess;
    internal static int ProbeError, ProcessCalls, OpenCalls, QueryCalls, ConversionCalls, AllocationCalls, FreeCalls, LocalFreeCalls, CloseCalls;
    internal static int InformationClass, CreatePipeCalls, InheritHandle;
    internal static int AllocationAttempts, AllocationThrowOnAttempt;
    internal static IntPtr OpenedProcess;
    internal static string LastSddl;

    internal static void Reset()
    {
        AbortCleanup();
        Trace.Clear();
        boundaryViolations.Clear();
        SidValue = "S-1-5-21-12345-67890-23456-1001";
        ProcessThrows = OpenFailureHasHandle = OpenThrowsBeforeHandle = OpenThrowsAfterHandle = false;
        ProbeResult = ProbeThrows = FillThrows = NullSid = AllocationThrows = false;
        ConversionFailureHasString = ConversionThrowsBeforeString = ConversionThrowsAfterString = NullStringSid = false;
        DescriptorFailureHasBuffer = DescriptorThrowsAfterBuffer = CreatePipeThrows = InvalidPipeHandle = false;
        OpenResult = FillResult = ConversionResult = DescriptorResult = true;
        RequiredLength = ReturnedLength = TokenUserSize + 32;
        RequestedAccess = 0;
        ProbeError = 122;
        ProcessCalls = OpenCalls = QueryCalls = ConversionCalls = AllocationCalls = FreeCalls = LocalFreeCalls = CloseCalls = 0;
        InformationClass = CreatePipeCalls = InheritHandle = 0;
        AllocationAttempts = AllocationThrowOnAttempt = 0;
        OpenedProcess = descriptor = IntPtr.Zero;
        LastSddl = null;
    }

    internal static IntPtr GetCurrentProcess()
    {
        ProcessCalls++;
        Trace.Add("GetCurrentProcess");
        if (ProcessThrows) throw new InvalidOperationException("Controlled process-handle exception.");
        return new IntPtr(-1);
    }

    internal static bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle)
    {
        OpenCalls++;
        Trace.Add("OpenProcessToken");
        OpenedProcess = processHandle;
        RequestedAccess = desiredAccess;
        tokenHandle = IntPtr.Zero;
        if (OpenThrowsBeforeHandle) throw new InvalidOperationException("Controlled token-open exception before handle.");
        if (OpenResult || OpenFailureHasHandle || OpenThrowsAfterHandle)
        {
            tokenHandle = token;
            tokenLive = true;
        }
        if (OpenThrowsAfterHandle) throw new InvalidOperationException("Controlled token-open exception after handle.");
        return OpenResult;
    }

    internal static bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation,
        uint tokenInformationLength, out uint returnLength)
    {
        QueryCalls++;
        InformationClass = tokenInformationClass;
        AssertEqual(token, tokenHandle, "Token query must use the opened process token.");
        AssertEqual(1, tokenInformationClass, "Query class must be TokenUser.");
        if (tokenInformation == IntPtr.Zero)
        {
            Trace.Add("GetTokenInformation:probe");
            AssertEqual(0U, tokenInformationLength, "Probe buffer length must be zero.");
            returnLength = RequiredLength;
            // This controls the same thread-local cached error read by the unchanged production method.
            Marshal.SetLastPInvokeError(ProbeError);
            if (ProbeThrows) throw new InvalidOperationException("Controlled token sizing exception.");
            return ProbeResult;
        }
        Trace.Add("GetTokenInformation:fill");
        AssertEqual(RequiredLength, tokenInformationLength, "Fill must use the probed buffer size.");
        AssertEqual(true, allocations.Contains(tokenInformation), "Fill must use a live HGlobal allocation.");
        returnLength = ReturnedLength;
        if (FillThrows) throw new InvalidOperationException("Controlled token filling exception.");
        if (FillResult) Marshal.WriteIntPtr(tokenInformation, NullSid ? IntPtr.Zero : sid);
        return FillResult;
    }

    internal static bool ConvertSidToStringSid(IntPtr value, out IntPtr ptrStringSid)
    {
        ConversionCalls++;
        Trace.Add("ConvertSidToStringSid");
        AssertEqual(sid, value, "Conversion must use TOKEN_USER.User.Sid.");
        ptrStringSid = IntPtr.Zero;
        if (ConversionThrowsBeforeString) throw new InvalidOperationException("Controlled conversion exception before allocation.");
        if (!NullStringSid && (ConversionResult || ConversionFailureHasString || ConversionThrowsAfterString))
        {
            ptrStringSid = Marshal.StringToHGlobalUni(SidValue);
            nativeAllocations.Add(ptrStringSid);
        }
        if (ConversionThrowsAfterString) throw new InvalidOperationException("Controlled conversion exception after allocation.");
        return ConversionResult;
    }

    internal static IntPtr AllocHGlobal(int size)
    {
        Trace.Add("AllocHGlobal:" + size);
        AllocationAttempts++;
        if (AllocationThrows || AllocationAttempts == AllocationThrowOnAttempt)
            throw new OutOfMemoryException("Controlled native allocation exception.");
        var memory = Marshal.AllocHGlobal(size);
        AllocationCalls++;
        allocations.Add(memory);
        return memory;
    }

    internal static void FreeHGlobal(IntPtr memory)
    {
        Trace.Add("FreeHGlobal");
        AssertEqual(true, allocations.Remove(memory), "FreeHGlobal must release a live allocation exactly once.");
        FreeCalls++;
        Marshal.FreeHGlobal(memory);
    }

    internal static IntPtr LocalFree(IntPtr memory)
    {
        Trace.Add("LocalFree");
        AssertEqual(true, nativeAllocations.Remove(memory), "LocalFree must release a native allocation exactly once.");
        LocalFreeCalls++;
        Marshal.FreeHGlobal(memory);
        return IntPtr.Zero;
    }

    internal static bool CloseHandle(IntPtr handle)
    {
        Trace.Add("CloseHandle");
        AssertEqual(token, handle, "Only the opened token is closed; the process pseudo-handle must remain untouched.");
        AssertEqual(true, tokenLive, "Token handle must close exactly once.");
        tokenLive = false;
        CloseCalls++;
        return true;
    }

    internal static bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision,
        out IntPtr securityDescriptor, IntPtr securityDescriptorSize)
    {
        Trace.Add("ConvertStringSecurityDescriptorToSecurityDescriptor");
        LastSddl = value;
        AssertEqual(1U, revision, "SDDL revision must remain 1.");
        AssertEqual(IntPtr.Zero, securityDescriptorSize, "No descriptor-size output requested.");
        securityDescriptor = IntPtr.Zero;
        if (DescriptorResult || DescriptorFailureHasBuffer || DescriptorThrowsAfterBuffer)
        {
            descriptor = Marshal.AllocHGlobal(8);
            nativeAllocations.Add(descriptor);
            securityDescriptor = descriptor;
        }
        if (DescriptorThrowsAfterBuffer) throw new InvalidOperationException("Controlled descriptor exception after allocation.");
        return DescriptorResult;
    }

    internal static SafeFileHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, IntPtr securityAttributes)
    {
        Trace.Add("CreateNamedPipe");
        CreatePipeCalls++;
        if (CreatePipeThrows) throw new InvalidOperationException("Controlled native pipe creation exception.");
        AssertEqual(true, allocations.Contains(securityAttributes), "SECURITY_ATTRIBUTES must be a live allocation.");
        AssertEqual(descriptor, Marshal.ReadIntPtr(securityAttributes, IntPtr.Size), "Pipe must receive the constructed security descriptor.");
        InheritHandle = Marshal.ReadInt32(securityAttributes, IntPtr.Size * 2);
        AssertEqual(0, InheritHandle, "SECURITY_ATTRIBUTES.bInheritHandle must be false.");
        AssertEqual(0x40000003U, openMode, "Duplex overlapped pipe flags must remain unchanged.");
        AssertEqual(0U, pipeMode, "Byte pipe mode must remain unchanged.");
        AssertEqual(255U, maxInstances, "Pipe maximum instance count must remain unchanged.");
        return new SafeFileHandle(InvalidPipeHandle ? new IntPtr(-1) : new IntPtr(0x3003), false);
    }

    internal static void AssertBalanced()
    {
        if (boundaryViolations.Count != 0)
            throw new Exception("Unexpected native boundary use: " + string.Join("; ", boundaryViolations));
        if (tokenLive || allocations.Count != 0 || nativeAllocations.Count != 0)
            throw new Exception("Resource leak: token=" + tokenLive + ", HGlobal=" + allocations.Count + ", native allocations=" + nativeAllocations.Count + ".");
    }

    static void AssertEqual<T>(T expected, T actual, string message)
    {
        try { FailureRegression.Equal(expected, actual, message); }
        catch (Exception)
        {
            // Production catches native exceptions; retain fixture misuse so an expected-null
            // case cannot accidentally hide an incorrect native call.
            boundaryViolations.Add(message);
            throw;
        }
    }

    internal static void AbortCleanup()
    {
        // Failure-only harness cleanup keeps later cases isolated; AssertBalanced runs before this.
        foreach (var value in allocations) Marshal.FreeHGlobal(value);
        foreach (var value in nativeAllocations) Marshal.FreeHGlobal(value);
        allocations.Clear();
        nativeAllocations.Clear();
        tokenLive = false;
    }
}
#endif
