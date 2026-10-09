using System;
using System.Collections.Generic;

internal sealed class ObsoleteFixture
{
    public string Name;
    public string Source;
    public string AdditionalSource;
    public string UsageText;
    public string Message;
    public bool IsError;
    public bool ExpectHint;
    public string[] Defines = Array.Empty<string>();
    public bool Incomplete;
}

internal static class ObsoleteFixtures
{
    const string Api = "using System; public class Api { [Obsolete(\"Use Modern instead\")] public static void Old() {} public static void Modern() {} }\n";

    public static IEnumerable<ObsoleteFixture> All()
    {
        yield return Positive("method-message", Api + "class Use { void Run() { Api.Old(); } }", "Api.Old", "Use Modern instead");
        yield return Positive("fully-qualified-method", Api + "class Use { void Run() { global::Api.Old(); } }", "global::Api.Old", "Use Modern instead");
        yield return Positive("type-alias", "using Alias = Api;\n" + Api + "class Use { void Run() { Alias.Old(); } }", "Alias.Old", "Use Modern instead");
        yield return Positive("namespace-alias", "using NS = Domain;\nnamespace Domain { class Api { [System.Obsolete(\"Use New\")] public static void Old() {} } } class Use { void Run() { NS.Api.Old(); } }", "NS.Api.Old", "Use New");
        yield return Positive("obsolete-attribute-alias", "using Dep = System.ObsoleteAttribute; class Api { [Dep(\"Use New\")] public static void Old() {} } class Use { void Run() { Api.Old(); } }", "Api.Old", "Use New");
        yield return Positive("selected-obsolete-overload", "class Api { [System.Obsolete(\"Use string overload\")] public static void Send(int value) {} public static void Send(string value) {} } class Use { void Run() { Api.Send(1); } }", "Api.Send", "Use string overload");
        yield return Negative("selected-current-overload", "class Api { [System.Obsolete(\"Use string overload\")] public static void Send(int value) {} public static void Send(string value) {} } class Use { void Run() { Api.Send(\"value\"); } }");
        yield return Positive("obsolete-no-message", "class Api { [System.Obsolete] public static void Old() {} } class Use { void Run() { Api.Old(); } }", "Api.Old", null);
        yield return Positive("obsolete-error-flag", "class Api { [System.Obsolete(\"Remove this API\", true)] public static void Old() {} } class Use { void Run() { Api.Old(); } }", "Api.Old", "Remove this API", true);
        yield return Positive("named-is-error", "class Api { [System.Obsolete(\"Replace it\", error: true)] public static void Old() {} } class Use { void Run() { Api.Old(); } }", "Api.Old", "Replace it", true);
        yield return Positive("obsolete-constructor-only", "class Api { [System.Obsolete(\"Use factory\")] public Api() {} } class Use { object Run() { return new Api(); } }", "new Api()", "Use factory");
        yield return Positive("obsolete-type-reference", "[System.Obsolete(\"Use CurrentType\")] class OldType {} class Use { OldType value; }", "OldType value", "Use CurrentType");
        yield return Positive("obsolete-type-typeof", "[System.Obsolete(\"Use CurrentType\")] class OldType {} class Use { object Run() { return typeof(OldType); } }", "OldType);", "Use CurrentType");
        yield return Positive("obsolete-type-generic-argument", "[System.Obsolete(\"Use CurrentType\")] class OldType {} class Use { System.Collections.Generic.List<OldType> values; }", "OldType> values", "Use CurrentType");
        yield return Positive("obsolete-field", "class Api { [System.Obsolete(\"Use NewField\")] public static int OldField; } class Use { int Run() { return Api.OldField; } }", "Api.OldField", "Use NewField");
        yield return Positive("obsolete-property", "class Api { [System.Obsolete(\"Use NewProperty\")] public static int OldProperty {get; set;} } class Use { int Run() { return Api.OldProperty; } }", "Api.OldProperty", "Use NewProperty");
        yield return Positive("obsolete-event", "class Api { [System.Obsolete(\"Use NewEvent\")] public static event System.Action OldEvent; } class Use { void Run() { Api.OldEvent += () => {}; } }", "Api.OldEvent", "Use NewEvent");
        yield return Positive("obsolete-indexer", "class Api { [System.Obsolete(\"Use At\")] public int this[int index] {get {return index;}} } class Use { int Run(Api api) { return api[1]; } }", "api[1]", "Use At");
        yield return Positive("obsolete-operator", "class Api { [System.Obsolete(\"Use Combine\")] public static Api operator +(Api left, Api right) {return left;} } class Use { Api Run(Api left, Api right) { return left + right; } }", "left + right", "Use Combine");
        yield return Positive("obsolete-extension", "static class Api { [System.Obsolete(\"Use Modern\")] public static void Old(this string value) {} } class Use { void Run() { \"value\".Old(); } }", "\"value\".Old", "Use Modern");
        yield return Negative("comment-and-string-names", Api + "class Use { string value = \"Api.Old()\"; /* Api.Old(); */ void Run() { // Api.Old();\n Api.Modern(); } }");
        yield return Negative("same-name-current-type", "namespace Old { [System.Obsolete(\"Use New\")] public class Api {} } namespace Current { public class Api {} } class Use { Current.Api value; }");
        yield return Negative("same-name-current-member", Api + "class Current { public static void Old() {} } class Use { void Run() { Current.Old(); } }");
        yield return Negative("unrelated-custom-obsolete-attribute", "class ObsoleteAttribute : System.Attribute { public ObsoleteAttribute(string message) {} } class Api { [Obsolete(\"Domain annotation\")] public static void Old() {} } class Use { void Run() { Api.Old(); } }");
        yield return Negative("obsolete-declaration-only", Api);
        yield return Negative("inactive-preprocessor-branch", Api + "class Use { void Run() {\n#if NEVER_DEFINED\n Api.Old();\n#else\n Api.Modern();\n#endif\n} }");
        var active = Positive("active-preprocessor-branch", Api + "class Use { void Run() {\n#if LEGACY\n Api.Old();\n#else\n Api.Modern();\n#endif\n} }", "Api.Old", "Use Modern instead");
        active.Defines = new[] {"LEGACY"};
        yield return active;
        yield return Positive("pragma-suppressed-obsolete-warning", "#pragma warning disable CS0618, CS0612\n" + Api + "class Use { void Run() { Api.Old(); } }", "Api.Old", "Use Modern instead");
        yield return Negative("nonobsolete-derived-override", "class Api { [System.Obsolete(\"Use New\")] public virtual void Old() {} } class Current : Api { public override void Old() {} } class Use { void Run(Current api) { api.Old(); } }");
        var unresolved = Negative("unresolved-symbol-does-not-invent-hint", "class Use { void Run() { MissingApi.Old(); } }");
        unresolved.Incomplete = true;
        yield return unresolved;
        var additional = Positive("cross-tree-source-symbol", "class Use { void Run() { Api.Old(); } }", "Api.Old", "Use Modern instead");
        additional.AdditionalSource = Api;
        yield return additional;
        yield return Positive("crlf-unicode-source-location", Api.Replace("\n", "\r\n") + "// Український текст\r\nclass Use\r\n{\r\n void Run()\r\n {\r\n   Api.Old();\r\n }\r\n}\r\n", "Api.Old", "Use Modern instead");
        yield return Positive("obsolete-property-getter-read", "class Api { public int Value { [System.Obsolete(\"Use Read\")] get {return 1;} set {} } } class Use { int Run(Api api) {return api.Value;} }", "api.Value", "Use Read");
        yield return Negative("obsolete-getter-not-used-by-write", "class Api { public int Value { [System.Obsolete(\"Use Read\")] get {return 1;} set {} } } class Use { void Run(Api api) {api.Value = 1;} }");
        yield return Negative("obsolete-getter-not-used-by-parenthesized-write", "class Api { public int Value { [System.Obsolete(\"Use Read\")] get {return 1;} set {} } } class Use { void Run(Api api) {(api.Value) = 1;} }");
        yield return Negative("obsolete-getter-not-used-by-deconstruction-write", "class Api { public int Value { [System.Obsolete(\"Use Read\")] get {return 1;} set {} } } class Use { void Run(Api api) {int other; (api.Value, other) = (1, 2);} }");
        yield return Positive("obsolete-property-setter-write", "class Api { public int Value { get {return 1;} [System.Obsolete(\"Use Write\")] set {} } } class Use { void Run(Api api) {api.Value = 1;} }", "api.Value", "Use Write");
        yield return Negative("obsolete-setter-not-used-by-read", "class Api { public int Value { get {return 1;} [System.Obsolete(\"Use Write\")] set {} } } class Use { int Run(Api api) {return api.Value;} }");
        yield return Positive("obsolete-getter-used-by-compound-write", "class Api { public int Value { [System.Obsolete(\"Use Read\")] get {return 1;} set {} } } class Use { void Run(Api api) {api.Value += 1;} }", "api.Value", "Use Read");
        yield return Positive("obsolete-conditional-indexer", "class Api { [System.Obsolete(\"Use At\")] public int this[int index] {get {return 1;}} } class Use { int? Run(Api api) {return api?[1];} }", "api?[1]", "Use At");
        var inaccessible = Negative("inaccessible-obsolete-call-is-not-resolved", "class Api { [System.Obsolete(\"Unreachable\")] private static void Old() {} } class Use { void Run() {Api.Old();} }");
        inaccessible.Incomplete = true;
        yield return inaccessible;
    }

    static ObsoleteFixture Positive(string name, string source, string usage, string message, bool isError = false)
    {
        return new ObsoleteFixture {Name=name, Source=source, UsageText=usage, Message=message, IsError=isError, ExpectHint=true};
    }

    static ObsoleteFixture Negative(string name, string source)
    {
        return new ObsoleteFixture {Name=name, Source=source};
    }
}
