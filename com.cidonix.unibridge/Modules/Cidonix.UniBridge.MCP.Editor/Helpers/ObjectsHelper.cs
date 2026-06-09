using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    /// <summary>
    /// Helper class for finding and locating GameObjects in the scene using various search methods.
    /// </summary>
    public static class ObjectsHelper
    {
        /// <summary>
        /// Finds a single GameObject based on token (ID, name, path) and search method.
        /// </summary>
        /// <param name="targetToken">Token representing the target GameObject (ID, name, or path).</param>
        /// <param name="searchMethod">Search method to use (by_id, by_name, by_path, by_tag, by_layer, by_component, or by_id_or_name_or_path).</param>
        /// <param name="findParams">Optional parameters including find_all, search_term, search_in_children, and search_inactive flags.</param>
        /// <returns>The first matching GameObject, or null if no match is found.</returns>
        public static GameObject FindObject(
            JToken targetToken,
            string searchMethod,
            JObject findParams = null
        )
        {
            return SceneObjectLocator.FindObject(targetToken, searchMethod, findParams);
        }

        /// <summary>
        /// Core logic for finding GameObjects based on various criteria.
        /// </summary>
        /// <param name="targetToken">Token representing the target GameObject(s) (ID, name, or path).</param>
        /// <param name="searchMethod">Search method to use (by_id, by_name, by_path, by_tag, by_layer, by_component, or by_id_or_name_or_path).</param>
        /// <param name="findAll">Whether to return all matching GameObjects or just the first one.</param>
        /// <param name="findParams">Optional parameters including search_term, search_in_children, and search_inactive flags.</param>
        /// <returns>List of matching GameObjects (may be empty if no matches found).</returns>
        public static List<GameObject> FindObjects(
            JToken targetToken,
            string searchMethod,
            bool findAll,
            JObject findParams = null
        )
        {
            return SceneObjectLocator.FindObjects(targetToken, searchMethod, findAll, findParams);
        }
    }
}
