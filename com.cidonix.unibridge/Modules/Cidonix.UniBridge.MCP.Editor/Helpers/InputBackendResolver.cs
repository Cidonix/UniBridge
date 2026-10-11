using System;
using UnityEditor;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    internal enum InputBackendMode
    {
        Unknown,
        Old,
        New,
        Both
    }

    internal sealed class InputBackendResolution
    {
        public readonly InputBackendMode Mode;
        public readonly string Source;
        public readonly string Detail;

        public InputBackendResolution(InputBackendMode mode, string source, string detail)
        {
            Mode = mode;
            Source = source;
            Detail = detail;
        }
    }

    /// <summary>Reads the project setting without changing PlayerSettings or requiring the optional Input System package.</summary>
    internal static class InputBackendResolver
    {
        public static InputBackendResolution Resolve()
        {
            try
            {
                var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
                foreach (var setting in settings)
                {
                    if (!(setting is PlayerSettings)) continue;
                    var serialized = new SerializedObject(setting);
                    serialized.Update();
                    var property = serialized.FindProperty("activeInputHandler");
                    if (property != null)
                    {
                        var mode = ResolveSerializedValue(property.intValue);
                        return new InputBackendResolution(mode, "PlayerSettings.activeInputHandler",
                            mode == InputBackendMode.Unknown ? "The project input setting has an unsupported value." : null);
                    }
                    break;
                }
            }
            catch (Exception exception)
            {
                // A failed read must not silently authorize replacing a user's input module.
                return new InputBackendResolution(InputBackendMode.Unknown, "PlayerSettings.activeInputHandler", exception.Message);
            }

            return new InputBackendResolution(CompileTimeMode(), "compiled input backend",
                "The serialized project setting is unavailable; using the backends enabled for this Editor compilation.");
        }

        public static InputBackendMode ResolveSerializedValue(int value)
        {
            switch (value)
            {
                case 0: return InputBackendMode.Old;
                case 1: return InputBackendMode.New;
                case 2: return InputBackendMode.Both;
                default: return InputBackendMode.Unknown;
            }
        }

        public static InputBackendMode SelectUiModule(InputBackendMode mode, bool newModuleAvailable,
            bool hasOldModule, bool hasNewModule)
        {
            if (mode == InputBackendMode.Old) return InputBackendMode.Old;
            if (mode == InputBackendMode.New) return newModuleAvailable ? InputBackendMode.New : InputBackendMode.Unknown;
            if (mode != InputBackendMode.Both) return InputBackendMode.Unknown;

            // Both supports either module. Keep an existing compatible choice instead of replacing it.
            if (hasNewModule && newModuleAvailable) return InputBackendMode.New;
            if (hasOldModule) return InputBackendMode.Old;
            return newModuleAvailable ? InputBackendMode.New : InputBackendMode.Old;
        }

        static InputBackendMode CompileTimeMode()
        {
#if ENABLE_INPUT_SYSTEM && ENABLE_LEGACY_INPUT_MANAGER
            return InputBackendMode.Both;
#elif ENABLE_INPUT_SYSTEM
            return InputBackendMode.New;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return InputBackendMode.Old;
#elif !UNITY_2019_1_OR_NEWER
            // Editors predating the Input System always provide the legacy input manager.
            return InputBackendMode.Old;
#else
            return InputBackendMode.Unknown;
#endif
        }
    }
}
