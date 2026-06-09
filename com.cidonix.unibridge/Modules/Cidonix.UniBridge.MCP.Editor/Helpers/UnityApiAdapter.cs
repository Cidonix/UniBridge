using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    /// <summary>
    /// Provides a compatibility layer for Unity API changes across versions.
    /// Centralizes all version-specific API differences to avoid scattered #if directives.
    /// </summary>
    static class UnityApiAdapter
    {
        /// <summary>
        /// Gets the stable Unity object identifier used by MCP responses.
        /// </summary>
        public static long GetObjectId(Object obj)
        {
            if (obj == null)
            {
                return 0;
            }

#if UNITY_6000_0_OR_NEWER
            return (long)EntityId.ToULong(obj.GetEntityId());
#else
#pragma warning disable 0618
            return obj.GetInstanceID();
#pragma warning restore 0618
#endif
        }

        /// <summary>
        /// Checks whether a Unity object matches an MCP object identifier.
        /// </summary>
        public static bool ObjectIdEquals(Object obj, long id)
        {
            return obj != null && GetObjectId(obj) == id;
        }

        /// <summary>
        /// Gets a Unity Object from its MCP identifier.
        /// </summary>
        public static Object GetObjectFromId(long id)
        {
            if (id == 0)
            {
                return null;
            }

#if UNITY_6000_0_OR_NEWER
            return EditorUtility.EntityIdToObject(EntityId.FromULong(unchecked((ulong)id)));
#else
#pragma warning disable 0618
            return EditorUtility.InstanceIDToObject(unchecked((int)id));
#pragma warning restore 0618
#endif
        }

        /// <summary>
        /// Gets the ID of the active selected object.
        /// </summary>
        public static long GetActiveSelectionId()
        {
#if UNITY_6000_0_OR_NEWER
            return (long)EntityId.ToULong(Selection.activeEntityId);
#else
#pragma warning disable 0618
            return Selection.activeInstanceID;
#pragma warning restore 0618
#endif
        }

        /// <summary>
        /// Gets the field name for the LogEntry ID field used in reflection.
        /// </summary>
        public static string GetLogEntryIdFieldName()
        {
#if UNITY_6000_0_OR_NEWER
            return "entityId";
#else
            return "instanceID";
#endif
        }

        /// <summary>
        /// Finds objects without exposing obsolete sorting-mode overloads to tool code.
        /// </summary>
        public static Object[] FindObjectsByType(Type type, FindObjectsInactive findInactive)
        {
#if UNITY_6000_0_OR_NEWER
            return Object.FindObjectsByType(type, findInactive);
#else
#pragma warning disable 0618
            return Object.FindObjectsByType(type, findInactive, FindObjectsSortMode.None);
#pragma warning restore 0618
#endif
        }

        /// <summary>
        /// Adds Collider2D composite information using the current Unity API.
        /// </summary>
        public static void AddCollider2DCompositeInfo(IDictionary<string, object> data, Collider2D collider2D)
        {
            if (data == null || collider2D == null)
            {
                return;
            }

#if UNITY_6000_0_OR_NEWER
            data["usedByComposite"] = collider2D.compositeOperation != Collider2D.CompositeOperation.None;
            data["compositeOperation"] = collider2D.compositeOperation.ToString();
#else
#pragma warning disable 0618
            data["usedByComposite"] = collider2D.usedByComposite;
#pragma warning restore 0618
#endif
        }
    }
}
