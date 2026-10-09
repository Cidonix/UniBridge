#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    public sealed class UIToolkitImportResult
    {
        public bool @checked;
        public bool assetLoaded;
        public bool hasErrors;
        public bool hasWarnings;
        public string guid;
        public string semanticValidation = "not_run";
        public string semanticValidationScope = "unity_import";
        public string instantiationValidation = "not_run";
        public List<UIToolkitDiagnostic> diagnostics = new();
    }

    public sealed class UIToolkitAssetWriteResult
    {
        public string path;
        public string guid;
        public string status = "blocked";
        public bool succeeded;
        public bool preview;
        public bool mutationAttempted;
        public bool written;
        public bool imported;
        public bool restoredBaseline;
        public string beforeSha256;
        public string expectedSha256;
        public string currentSha256;
        public string recoveryPath;
        public UIToolkitValidationResult validation;
        public UIToolkitImportResult import = new();
        public UIToolkitImportResult restoreImport;
        public List<UIToolkitDiagnostic> diagnostics = new();
    }

    /// <summary>Explicit single-file writes with before-state checks and non-overwriting recovery captures.</summary>
    public static class UIToolkitAssetWriter
    {
        public static UIToolkitAssetWriteResult Write(string assetPath, string absolutePath, byte[] payload,
            UIToolkitValidationResult validation, bool preview, string recoveryRoot, Action prepareWrite,
            Func<UIToolkitImportResult> importAsset, bool failOnImportWarnings = true, string expectedBeforeSha256 = null)
        {
            var result = new UIToolkitAssetWriteResult { path = assetPath, validation = validation, preview = preview };
            if (validation == null || !validation.valid)
            { result.diagnostics.AddRange(validation?.diagnostics ?? new List<UIToolkitDiagnostic>()); return result; }
            byte[] before = null;
            string beforeMeta = null;
            try
            {
                AssertOrdinaryPath(absolutePath);
                AssertOrdinaryPath(absolutePath + ".meta");
                before = ReadOptional(absolutePath);
                beforeMeta = HashOptional(absolutePath + ".meta");
                result.beforeSha256 = before == null ? null : Hash(before);
                result.expectedSha256 = Hash(payload);
                result.currentSha256 = result.beforeSha256;
                if (expectedBeforeSha256 != null && !string.Equals(result.beforeSha256, expectedBeforeSha256, StringComparison.OrdinalIgnoreCase))
                {
                    Issue(result, "SOURCE_PRECONDITION_MISMATCH", "The UXML source changed after the original read; the proposed edit was refused without writing.");
                    return result;
                }
                if (preview) { result.status = "preview"; result.succeeded = true; return result; }
                result.mutationAttempted = true;
                prepareWrite?.Invoke();
                AssertOrdinaryPath(absolutePath);
                var recovery = Path.Combine(recoveryRoot, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(recovery);
                result.recoveryPath = recovery;
                if (before != null) WriteNew(Path.Combine(recovery, "before" + Path.GetExtension(absolutePath)), before);
                WriteNew(Path.Combine(recovery, "candidate" + Path.GetExtension(absolutePath)), payload);
                if (!string.Equals(HashOptional(absolutePath + ".meta"), beforeMeta, StringComparison.Ordinal))
                    throw new InvalidOperationException("Metadata changed after preflight; write refused.");
                // An exclusive handle closes the check/write race, including delete and rename sharing.
                using (var stream = new FileStream(absolutePath, before == null ? FileMode.CreateNew : FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    if (before == null) result.written = true; // CreateNew itself creates source bytes, even if a later guard refuses the payload.
                    if (before != null && !string.Equals(Hash(stream), result.beforeSha256, StringComparison.Ordinal))
                        throw new InvalidOperationException("Current source changed after preflight; write refused.");
                    if (!string.Equals(HashOptional(absolutePath + ".meta"), beforeMeta, StringComparison.Ordinal))
                        throw new InvalidOperationException("Metadata changed after preflight; write refused.");
                    try
                    {
                        result.written = true; // A write may change bytes before an I/O exception; never describe that uncertainty as no write.
                        ReplaceContents(stream, payload);
                        result.currentSha256 = Hash(stream);
                        if (result.currentSha256 != result.expectedSha256) throw new IOException("Written source did not match the intended payload.");
                    }
                    catch
                    {
                        // Only our own write can modify this still-exclusively-held handle.
                        if (before != null)
                        {
                            try { ReplaceContents(stream, before); result.restoredBaseline = Hash(stream) == result.beforeSha256; }
                            catch (Exception error) { Issue(result, "WRITE_RECOVERY_FAILED", error.Message); }
                        }
                        throw;
                    }
                }
                result.import = importAsset?.Invoke() ?? new UIToolkitImportResult();
                result.guid = result.import.guid;
                result.currentSha256 = HashOptional(absolutePath);
                result.imported = result.import.@checked && result.import.assetLoaded && !result.import.hasErrors
                    && (!failOnImportWarnings || !result.import.hasWarnings) && result.currentSha256 == result.expectedSha256;
                if (result.currentSha256 != result.expectedSha256) Issue(result, "IMPORT_SOURCE_CHANGED", "The source changed during import; current bytes are preserved.");
                if (!result.import.@checked) Issue(result, "IMPORT_NOT_VERIFIED", "Unity import diagnostics were not verified.");
                if (!result.import.assetLoaded) Issue(result, "IMPORT_ASSET_MISSING", "Unity did not load the expected asset type.");
                if (result.import.hasErrors) Issue(result, "IMPORT_ERRORS", "Unity reported import errors.");
                if (failOnImportWarnings && result.import.hasWarnings) Issue(result, "IMPORT_WARNINGS", "Unity import warnings prevent acceptance under FailOnImportWarnings.");
                if (result.imported)
                {
                    result.succeeded = true;
                    result.status = result.import.hasWarnings ? "completed_with_warnings" : "completed";
                    return result;
                }
            }
            catch (Exception error) { Issue(result, result.written ? "IMPORT_OR_READBACK_FAILED" : "WRITE_FAILED", error.Message); }

            if (result.written && before != null && !result.restoredBaseline)
            {
                try
                {
                    using var current = new FileStream(absolutePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    if (Hash(current) != result.expectedSha256 || !string.Equals(HashOptional(absolutePath + ".meta"), beforeMeta, StringComparison.Ordinal))
                        Issue(result, "BASELINE_RESTORE_REFUSED", "Source or metadata changed independently; recovery copies are retained and current bytes are preserved.");
                    else
                    {
                        ReplaceContents(current, before);
                        result.restoredBaseline = Hash(current) == result.beforeSha256;
                    }
                }
                catch (Exception error) { Issue(result, "BASELINE_RESTORE_FAILED", error.Message); }
            }
            if (result.restoredBaseline)
            {
                try { result.restoreImport = importAsset?.Invoke(); }
                catch (Exception error) { Issue(result, "BASELINE_REIMPORT_FAILED", error.Message); }
            }
            try { result.currentSha256 = HashOptional(absolutePath); }
            catch (Exception error) { result.currentSha256 = null; Issue(result, "FINAL_READBACK_FAILED", error.Message); }
            if (result.restoredBaseline && result.currentSha256 != result.beforeSha256)
            { result.restoredBaseline = false; Issue(result, "BASELINE_CHANGED_AFTER_REIMPORT", "Current source changed after baseline restoration; new bytes are preserved."); }
            result.status = result.written ? (result.restoredBaseline ? "restored_baseline" : "partial") : "blocked";
            return result;
        }

        static void AssertOrdinaryPath(string absolutePath)
        {
            var current = Path.GetFullPath(absolutePath);
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current) && current == Path.GetFullPath(absolutePath)) throw new IOException("The asset path names a directory.");
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("UI Toolkit writes refuse reparse-point paths.");
                current = Path.GetDirectoryName(current);
            }
        }
        static byte[] ReadOptional(string path)
        {
            try { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray(); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
        static string HashOptional(string path) { var bytes = ReadOptional(path); return bytes == null ? null : Hash(bytes); }
        static string Hash(byte[] bytes) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static string Hash(Stream stream) { stream.Position = 0; using var sha = SHA256.Create(); var hash = sha.ComputeHash(stream); stream.Position = 0; return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant(); }
        static void ReplaceContents(FileStream stream, byte[] bytes) { stream.Position = 0; stream.Write(bytes, 0, bytes.Length); stream.SetLength(bytes.Length); stream.Flush(true); }
        static void WriteNew(string path, byte[] bytes) { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        static void Issue(UIToolkitAssetWriteResult result, string code, string message)
            => result.diagnostics.Add(new UIToolkitDiagnostic { severity = "error", code = code, message = message, file = result.path });
    }
}
