#nullable disable
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    public enum ManageSceneHierarchyAction
    {
        Reparent,
        CreateContainer
    }

    public record SceneHierarchyMove
    {
        [McpDescription("GameObject objectId/EntityId to move.", Required = false)]
        public long? ObjectId { get; set; }

        [McpDescription("GameObject objectId/EntityId as a string. Prefer this in JavaScript/JSON clients because Unity 6 EntityIds can exceed the safe integer range.", Required = false)]
        public string ObjectIdString { get; set; }

        [McpDescription("Fallback target by objectId, hierarchy path, or name.", Required = false)]
        public string Target { get; set; }

        [McpDescription("Search method for Target: Auto, ById, ByPath, or ByName.", Required = false)]
        public string SearchMethod { get; set; }

        [McpDescription("New parent GameObject objectId. Omit/null for scene root.", Required = false)]
        public long? ParentObjectId { get; set; }

        [McpDescription("New parent GameObject objectId/EntityId as a string. Prefer this in JavaScript/JSON clients because Unity 6 EntityIds can exceed the safe integer range.", Required = false)]
        public string ParentObjectIdString { get; set; }

        [McpDescription("Fallback parent by objectId, hierarchy path, or name. Omit/null for scene root.", Required = false)]
        public string Parent { get; set; }

        [McpDescription("Search method for Parent: Auto, ById, ByPath, or ByName.", Required = false)]
        public string ParentSearchMethod { get; set; }

        [McpDescription("Optional sibling index after reparenting. Negative or omitted keeps the current end position.", Required = false)]
        public int? SiblingIndex { get; set; }

        [McpDescription("Preserve world transform while changing parent.", Required = false, Default = true)]
        public bool? WorldPositionStays { get; set; }
    }

    /// <summary>
    /// Parameters for UniBridge_ManageSceneHierarchy.
    /// </summary>
    public record ManageSceneHierarchyParams
    {
        [McpDescription("Operation to perform: Reparent or CreateContainer.", Required = false, Default = ManageSceneHierarchyAction.Reparent)]
        public ManageSceneHierarchyAction Action { get; set; } = ManageSceneHierarchyAction.Reparent;

        [McpDescription("Optional scene path/name used for validation counts and root placement. When omitted, currently loaded editable scenes are considered.", Required = false)]
        public string ScenePath { get; set; }

        [McpDescription("Moves for Action=Reparent. Each move should identify ObjectId and ParentObjectId where possible.", Required = false)]
        public SceneHierarchyMove[] Moves { get; set; }

        [McpDescription("Object ids to place inside a new organizational container for Action=CreateContainer.", Required = false)]
        public long[] ObjectIds { get; set; }

        [McpDescription("Object ids to place inside a new organizational container, as strings. Prefer this in JavaScript/JSON clients because Unity 6 EntityIds can exceed the safe integer range.", Required = false)]
        public string[] ObjectIdStrings { get; set; }

        [McpDescription("Container GameObject name for Action=CreateContainer.", Required = false, Default = "Scene_Group")]
        public string ContainerName { get; set; } = "Scene_Group";

        [McpDescription("Parent objectId for the new container. Omit/null to create a root container.", Required = false)]
        public long? ParentObjectId { get; set; }

        [McpDescription("Parent objectId for the new container, as a string. Prefer this in JavaScript/JSON clients because Unity 6 EntityIds can exceed the safe integer range.", Required = false)]
        public string ParentObjectIdString { get; set; }

        [McpDescription("Fallback parent for the new container by objectId, hierarchy path, or name.", Required = false)]
        public string Parent { get; set; }

        [McpDescription("Search method for Parent: Auto, ById, ByPath, or ByName.", Required = false)]
        public string ParentSearchMethod { get; set; }

        [McpDescription("Optional sibling index for the new container or final moved objects.", Required = false)]
        public int? SiblingIndex { get; set; }

        [McpDescription("Preview the operation without changing the scene.", Required = false, Default = true)]
        public bool DryRun { get; set; } = true;

        [McpDescription("Preserve world transform while changing parent.", Required = false, Default = true)]
        public bool WorldPositionStays { get; set; } = true;

        [McpDescription("Validate that scene object count changes only by the expected delta. Reparent expects 0; CreateContainer expects +1.", Required = false, Default = true)]
        public bool ValidateObjectCountUnchanged { get; set; } = true;

        [McpDescription("Clearer alias for ValidateObjectCountUnchanged: validate that the actual scene object count delta equals the operation's expected delta.", Required = false, Default = true)]
        public bool ValidateExpectedObjectCountDelta { get; set; } = true;

        [McpDescription("Include before/after object refs and transform deltas in the result.", Required = false, Default = true)]
        public bool IncludeDiff { get; set; } = true;
    }
}
