#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.VersionControl;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    /// <summary>
    /// Small Unity Version Control facade for checkout-aware asset write preflight.
    /// Keeps checkout-aware write preflight out of individual tools.
    /// </summary>
    public static class VersionControlUtility
    {
        public static object BuildProjectStatus()
        {
            return new
            {
                providerEnabled = Provider.enabled,
                providerActive = Provider.isActive,
                checkoutSupport = Provider.hasCheckoutSupport,
                checkoutRequired = IsCheckoutRequiredInProject(),
                note = IsCheckoutRequiredInProject()
                    ? "Unity Version Control provider is active and supports checkout."
                    : "No checkout is required by the active Unity Version Control provider."
            };
        }

        public static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var normalized = path.Trim().Replace('\\', '/');
            if (normalized.StartsWith("unity://path/", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring("unity://path/".Length);

            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase))
                return normalized;

            if (normalized.Equals("Assets", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Packages", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("ProjectSettings", StringComparison.OrdinalIgnoreCase))
                return normalized;

            return "Assets/" + normalized.TrimStart('/');
        }

        public static bool IsCheckoutRequiredInProject()
        {
            return Provider.enabled && Provider.isActive && Provider.hasCheckoutSupport;
        }

        public static AssetEditability InspectAsset(string assetPath)
        {
            var normalized = NormalizeAssetPath(assetPath);
            if (string.IsNullOrWhiteSpace(normalized))
                return AssetEditability.Error(assetPath, "Asset path is required.");

            var exists = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(normalized) != null ||
                         AssetDatabase.IsValidFolder(normalized);
            var providerAsset = SafeGetAsset(normalized);
            var tracked = providerAsset != null && !providerAsset.IsState(Asset.States.Local);
            var openForEdit = providerAsset == null || SafeIsOpenForEdit(providerAsset);
            var checkoutRequired = IsCheckoutRequiredInProject() && tracked && !openForEdit;
            var checkoutValid = !checkoutRequired || SafeCheckoutIsValid(providerAsset);

            return new AssetEditability
            {
                assetPath = normalized,
                exists = exists,
                providerEnabled = Provider.enabled,
                providerActive = Provider.isActive,
                checkoutSupport = Provider.hasCheckoutSupport,
                checkoutRequiredInProject = IsCheckoutRequiredInProject(),
                tracked = tracked,
                openForEdit = openForEdit,
                checkoutRequired = checkoutRequired,
                checkoutValid = checkoutValid,
                canWrite = !checkoutRequired || checkoutValid,
                status = !IsCheckoutRequiredInProject()
                    ? "checkout_not_required"
                    : !tracked
                        ? "untracked"
                        : openForEdit
                            ? "open_for_edit"
                            : checkoutValid
                                ? "checkout_available"
                                : "checkout_blocked",
                error = checkoutRequired && !checkoutValid
                    ? $"Asset '{normalized}' is tracked but cannot be checked out by the active Unity Version Control provider."
                    : null
            };
        }

        public static CheckoutResult EnsureAssetEditable(string assetPath, bool checkout, bool throwOnBlocked = false)
        {
            var before = InspectAsset(assetPath);
            if (!string.IsNullOrWhiteSpace(before.error))
            {
                if (throwOnBlocked)
                    throw new InvalidOperationException(before.error);
                return CheckoutResult.From(before, attempted: false, checkedOut: false, before.error);
            }

            if (!before.checkoutRequired)
                return CheckoutResult.From(before, attempted: false, checkedOut: false, null);

            if (!checkout)
            {
                var message = $"Asset '{before.assetPath}' requires checkout before mutation.";
                if (throwOnBlocked)
                    throw new InvalidOperationException(message);
                return CheckoutResult.From(before, attempted: false, checkedOut: false, message);
            }

            if (!before.checkoutValid)
            {
                var message = before.error ?? $"Asset '{before.assetPath}' cannot be checked out.";
                if (throwOnBlocked)
                    throw new InvalidOperationException(message);
                return CheckoutResult.From(before, attempted: false, checkedOut: false, message);
            }

            try
            {
                var task = Provider.Checkout(before.assetPath, (CheckoutMode)3);
                task?.Wait();
            }
            catch (Exception ex)
            {
                var message = $"Checkout failed for '{before.assetPath}': {ex.Message}";
                if (throwOnBlocked)
                    throw new InvalidOperationException(message, ex);
                return CheckoutResult.From(before, attempted: true, checkedOut: false, message);
            }

            var after = InspectAsset(before.assetPath);
            var checkedOut = !after.checkoutRequired && after.openForEdit;
            if (!checkedOut && after.checkoutRequired)
            {
                var message = $"Checkout completed but '{before.assetPath}' is still not open for edit.";
                if (throwOnBlocked)
                    throw new InvalidOperationException(message);
                return new CheckoutResult
                {
                    assetPath = before.assetPath,
                    attempted = true,
                    checkedOut = false,
                    before = before,
                    after = after,
                    error = message
                };
            }

            return new CheckoutResult
            {
                assetPath = before.assetPath,
                attempted = true,
                checkedOut = checkedOut,
                before = before,
                after = after
            };
        }

        public static object[] InspectAssets(IEnumerable<string> assetPaths)
        {
            return (assetPaths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => InspectAsset(path))
                .Cast<object>()
                .ToArray();
        }

        static Asset SafeGetAsset(string assetPath)
        {
            try
            {
                return Provider.GetAssetByPath(assetPath);
            }
            catch
            {
                return null;
            }
        }

        static bool SafeIsOpenForEdit(Asset asset)
        {
            try
            {
                return asset == null || Provider.IsOpenForEdit(asset);
            }
            catch
            {
                return true;
            }
        }

        static bool SafeCheckoutIsValid(Asset asset)
        {
            try
            {
                return asset == null || Provider.CheckoutIsValid(asset);
            }
            catch
            {
                return false;
            }
        }

        public sealed class AssetEditability
        {
            public string assetPath { get; set; }
            public bool exists { get; set; }
            public bool providerEnabled { get; set; }
            public bool providerActive { get; set; }
            public bool checkoutSupport { get; set; }
            public bool checkoutRequiredInProject { get; set; }
            public bool tracked { get; set; }
            public bool openForEdit { get; set; }
            public bool checkoutRequired { get; set; }
            public bool checkoutValid { get; set; }
            public bool canWrite { get; set; }
            public string status { get; set; }
            public string error { get; set; }

            public static AssetEditability Error(string assetPath, string error)
            {
                return new AssetEditability
                {
                    assetPath = NormalizeAssetPath(assetPath),
                    status = "error",
                    error = error,
                    canWrite = false
                };
            }
        }

        public sealed class CheckoutResult
        {
            public string assetPath { get; set; }
            public bool attempted { get; set; }
            public bool checkedOut { get; set; }
            public AssetEditability before { get; set; }
            public AssetEditability after { get; set; }
            public string error { get; set; }

            public static CheckoutResult From(AssetEditability status, bool attempted, bool checkedOut, string error)
            {
                return new CheckoutResult
                {
                    assetPath = status?.assetPath,
                    attempted = attempted,
                    checkedOut = checkedOut,
                    before = status,
                    after = status,
                    error = error
                };
            }
        }
    }
}
