namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    public enum ProjectGuidelinesAction { Status, Read }

    public sealed class ProjectGuidelinesParams
    {
        public ProjectGuidelinesAction Action { get; set; } = ProjectGuidelinesAction.Status;
        public int? MaxCharsPerSource { get; set; }
        public int? MaxTotalChars { get; set; }
        public int? MaxFileBytes { get; set; }
        public int? TimeoutMs { get; set; }
    }
}
