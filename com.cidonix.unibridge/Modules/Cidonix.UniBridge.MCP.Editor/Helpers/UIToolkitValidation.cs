#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    public sealed class UIToolkitDiagnostic
    {
        public string severity;
        public string code;
        public string message;
        public string file;
        public int line;
        public int column;
    }

    public sealed class UIToolkitValidationResult
    {
        public string format;
        public bool valid;
        public string scope = "structural";
        public string semanticValidation = "not_run";
        public List<UIToolkitDiagnostic> diagnostics = new();
    }

    public sealed class UIToolkitValidationException : InvalidOperationException
    {
        public readonly UIToolkitValidationResult Validation;
        public UIToolkitValidationException(UIToolkitValidationResult validation)
            : base(validation.diagnostics.FirstOrDefault(item => item.severity == "error")?.message ?? "Invalid UI Toolkit content.")
        { Validation = validation; }
    }

    /// <summary>Checks source structure without importing or writing an asset. Unity remains the semantic authority.</summary>
    public static class UIToolkitValidation
    {
        static readonly HashSet<string> MarkupElements = new(StringComparer.Ordinal)
        { "Style", "Template", "Instance", "AttributeOverrides", "TemplateOverrides", "UxmlObject", "Bindings" };

        public static UIToolkitValidationResult ValidateUxml(string content, Func<string, string, bool?> knownElement = null)
        {
            var result = new UIToolkitValidationResult { format = "uxml" };
            if (string.IsNullOrWhiteSpace(content))
            { Add(result, "EMPTY_UXML", "UXML content cannot be empty.", 1, 1); return result; }
            XDocument document;
            try
            {
                using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
                document = XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
            }
            catch (XmlException error)
            {
                if (error.LineNumber > 0 && error.LinePosition > 0)
                    Add(result, "UXML_XML_SYNTAX", error.Message, error.LineNumber, error.LinePosition);
                else
                    At(result, content, Math.Max(0, content.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)), "UXML_XML_SYNTAX",
                        error.Message + " The parser supplied no position; the diagnostic points to the document declaration or start.");
                return result;
            }
            var root = document.Root;
            if (root == null || root.Name.LocalName != "UXML" ||
                (root.Name.NamespaceName.Length != 0 && root.Name.NamespaceName != "UnityEngine.UIElements"))
                Add(result, "UXML_ROOT", "The document root must be UXML in the UnityEngine.UIElements namespace (or the legacy empty namespace).", root);
            if (root != null)
            {
                foreach (var element in root.Descendants())
                {
                    if ((element.Name.NamespaceName.Length == 0 || element.Name.NamespaceName == "UnityEngine.UIElements") && MarkupElements.Contains(element.Name.LocalName))
                    {
                        if ((element.Name.LocalName == "Style" || element.Name.LocalName == "Template") &&
                            string.IsNullOrWhiteSpace(element.Attribute("src")?.Value) &&
                            string.IsNullOrWhiteSpace(element.Attribute("path")?.Value))
                            Add(result, "UXML_REFERENCE_SOURCE", element.Name.LocalName + " requires a nonempty src or path attribute.", element);
                        if (element.Name.LocalName == "Template" && string.IsNullOrWhiteSpace(element.Attribute("name")?.Value))
                            Add(result, "UXML_TEMPLATE_NAME", "Template requires a nonempty name attribute.", element);
                        if (element.Name.LocalName == "Instance" && string.IsNullOrWhiteSpace(element.Attribute("template")?.Value))
                            Add(result, "UXML_INSTANCE_TEMPLATE", "Instance requires a nonempty template attribute.", element);
                    }
                    else if (knownElement != null)
                    {
                        var known = knownElement(element.Name.NamespaceName, element.Name.LocalName);
                        if (known == false)
                            Add(result, "UXML_UNKNOWN_ELEMENT", "No loaded UI Toolkit element type matches " + element.Name + ".", element);
                    }
                    var style = element.Attribute("style");
                    if (style != null)
                    {
                        var inline = ValidateUss(".inline {" + style.Value + "}");
                        foreach (var diagnostic in inline.diagnostics)
                        {
                            var info = (IXmlLineInfo)style;
                            diagnostic.code = "UXML_INLINE_" + diagnostic.code;
                            diagnostic.line = info.HasLineInfo() ? info.LineNumber : 0;
                            diagnostic.column = info.HasLineInfo() ? info.LinePosition : 0;
                            result.diagnostics.Add(diagnostic);
                        }
                    }
                }
                foreach (var duplicate in root.Descendants().Select(element => element.Attribute("name")?.Value)
                    .Where(name => !string.IsNullOrWhiteSpace(name)).GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1))
                    result.diagnostics.Add(new UIToolkitDiagnostic { severity = "warning", code = "UXML_DUPLICATE_NAME",
                        message = "More than one element has name '" + duplicate.Key + "'; name-based edits may be ambiguous." });
            }
            result.valid = !result.diagnostics.Any(item => item.severity == "error");
            return result;
        }

        public static UIToolkitValidationResult ValidateUss(string content)
        {
            var result = new UIToolkitValidationResult { format = "uss" };
            if (content == null) { Add(result, "MISSING_USS", "USS content is required.", 1, 1); return result; }
            // Mask literal/comment contents while retaining offsets and all structural delimiters outside them.
            var text = content.ToCharArray();
            var opening = new Stack<(char value, int index)>();
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    var start = i; text[i++] = ' '; text[i] = ' '; var closed = false;
                    while (++i < text.Length)
                    {
                        if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/')
                        { text[i++] = ' '; text[i] = ' '; closed = true; break; }
                        if (text[i] != '\n' && text[i] != '\r') text[i] = ' ';
                    }
                    if (!closed) { At(result, content, start, "USS_COMMENT", "Unterminated USS comment."); break; }
                }
                else if (text[i] == '\'' || text[i] == '"')
                {
                    var quote = text[i]; var start = i; var closed = false;
                    while (++i < text.Length)
                    {
                        if (text[i] == '\\')
                        {
                            text[i] = ' ';
                            if (++i < text.Length)
                            {
                                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                                else if (text[i] != '\n' && text[i] != '\r') text[i] = ' ';
                            }
                        }
                        else if (text[i] == quote) { closed = true; break; }
                        else
                        {
                            if (text[i] == '\n' || text[i] == '\r') At(result, content, i, "USS_STRING_NEWLINE", "A string newline must be escaped.");
                            else text[i] = ' ';
                        }
                    }
                    if (!closed) { At(result, content, start, "USS_STRING", "Unterminated USS string."); break; }
                }
                else if (text[i] == '\\')
                {
                    text[i] = ' ';
                    if (++i < text.Length) text[i] = ' ';
                    else At(result, content, i - 1, "USS_ESCAPE", "An escape requires a following character.");
                }
                else if (text[i] == '(' || text[i] == '[') opening.Push((text[i], i));
                else if (text[i] == ')' || text[i] == ']')
                {
                    var expected = text[i] == ')' ? '(' : '[';
                    if (opening.Count == 0 || opening.Peek().value != expected)
                        At(result, content, i, "USS_DELIMITER", "Mismatched function or selector delimiter.");
                    else opening.Pop();
                }
            }
            foreach (var item in opening) At(result, content, item.index, "USS_DELIMITER", "Unclosed function or selector delimiter.");
            var masked = new string(text);
            var block = false; var segmentStart = 0; var parentheses = 0; var brackets = 0;
            for (var i = 0; i < masked.Length; i++)
            {
                var c = masked[i];
                if (c == '(') parentheses++; else if (c == ')') parentheses--;
                if (c == '[') brackets++; else if (c == ']') brackets--;
                if (parentheses != 0 || brackets != 0) continue;
                if (c == '{')
                {
                    if (block) At(result, content, i, "USS_NESTED_RULE", "USS rules cannot contain nested rule blocks.");
                    else if (string.IsNullOrWhiteSpace(masked.Substring(segmentStart, i - segmentStart)))
                        At(result, content, i, "USS_SELECTOR", "A rule requires a selector.");
                    block = true; segmentStart = i + 1;
                }
                else if (c == '}' || c == ';')
                {
                    var part = masked.Substring(segmentStart, i - segmentStart);
                    if (block) ValidateDeclaration(result, content, part, segmentStart);
                    else if (c == '}') At(result, content, i, "USS_BLOCK", "Closing brace has no matching rule.");
                    else if (!string.IsNullOrWhiteSpace(part) && !part.TrimStart().StartsWith("@import", StringComparison.Ordinal))
                        At(result, content, segmentStart, "USS_TOP_LEVEL", "A declaration must be inside a selector rule.");
                    if (c == '}') block = false;
                    segmentStart = i + 1;
                }
            }
            if (block) At(result, content, segmentStart, "USS_BLOCK", "A rule is missing its closing brace.");
            else if (!string.IsNullOrWhiteSpace(masked.Substring(Math.Min(segmentStart, masked.Length))))
                At(result, content, segmentStart, "USS_RULE", "A selector requires a rule block; an import requires a semicolon.");
            result.valid = !result.diagnostics.Any(item => item.severity == "error");
            return result;
        }

        static void ValidateDeclaration(UIToolkitValidationResult result, string content, string part, int offset)
        {
            if (string.IsNullOrWhiteSpace(part)) return;
            var colon = part.IndexOf(':');
            if (colon <= 0) { At(result, content, offset, "USS_DECLARATION", "A declaration requires property: value."); return; }
            var name = part.Substring(0, colon).Trim();
            if (!Regex.IsMatch(name, "^(--[A-Za-z0-9_-]+|-?[A-Za-z_][A-Za-z0-9_-]*)$"))
                At(result, content, offset, "USS_PROPERTY", "Invalid USS property name.");
            var value = part.Substring(colon + 1).Trim();
            if (value.Length == 0) At(result, content, offset + colon + 1, "USS_VALUE", "A declaration requires a nonempty value.");
            var depth = 0;
            for (var i = colon + 1; i < part.Length; i++)
            {
                if (part[i] == '(') depth++; else if (part[i] == ')') depth--;
                else if (part[i] == ':' && depth == 0 && !name.StartsWith("--", StringComparison.Ordinal))
                { At(result, content, offset + i, "USS_DECLARATION_SEPARATOR", "Declarations must be separated by a semicolon."); break; }
            }
        }

        public static Dictionary<string, string> ReadInlineDeclarations(string content)
        {
            content ??= string.Empty;
            var validation = ValidateUss(".inline {" + content + "}");
            if (!validation.valid) throw new UIToolkitValidationException(validation);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var start = 0; var colon = -1; var depth = 0; var quote = '\0'; var comment = false;
            for (var i = 0; i <= (content ?? string.Empty).Length; i++)
            {
                var c = i < content.Length ? content[i] : ';';
                if (comment) { if (c == '*' && i + 1 < content.Length && content[i + 1] == '/') { comment = false; i++; } continue; }
                if (quote != '\0') { if (c == '\\') i++; else if (c == quote) quote = '\0'; continue; }
                if (c == '\\') { i++; continue; }
                if (c == '/' && i + 1 < content.Length && content[i + 1] == '*') { comment = true; i++; continue; }
                if (c == '\'' || c == '"') { quote = c; continue; }
                if (c == '(') depth++; else if (c == ')') depth--;
                else if (c == ':' && depth == 0 && colon < start) colon = i;
                else if (c == ';' && depth == 0)
                {
                    if (colon >= start)
                    {
                        var name = content.Substring(start, colon - start).Trim();
                        // Comments before a property are allowed, and need not become part of its name.
                        name = Regex.Replace(name, @"/\*.*?\*/", "", RegexOptions.Singleline).Trim();
                        if (!name.StartsWith("--", StringComparison.Ordinal)) name = name.ToLowerInvariant();
                        result[name] = content.Substring(colon + 1, i - colon - 1).Trim();
                    }
                    start = i + 1; colon = -1;
                }
            }
            return result;
        }

        static void Add(UIToolkitValidationResult result, string code, string message, XObject node)
        {
            var info = node as IXmlLineInfo;
            Add(result, code, message, info?.HasLineInfo() == true ? info.LineNumber : 0, info?.HasLineInfo() == true ? info.LinePosition : 0);
        }
        static void Add(UIToolkitValidationResult result, string code, string message, int line, int column)
            => result.diagnostics.Add(new UIToolkitDiagnostic { severity = "error", code = code, message = message, line = line, column = column });
        static void At(UIToolkitValidationResult result, string content, int index, string code, string message)
        {
            var line = 1; var column = 1;
            for (var i = 0; i < Math.Min(index, content.Length); i++) { if (content[i] == '\n') { line++; column = 1; } else column++; }
            Add(result, code, message, line, column);
        }
    }
}
