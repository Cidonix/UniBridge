#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    // Independent point-10 candidate. No Unity API, rendering or filesystem work.
    internal sealed class Region2DException : ArgumentException
    {
        public readonly string Code;
        public Region2DException(string code, string message) : base(message) { Code = code; }
    }

    internal sealed class Region2DRect
    {
        public double xMin, yMin, xMax, yMax;
    }

    internal sealed class Region2DOrigin { public double x, y; }

    internal sealed class Region2DRequest
    {
        public Region2DRect worldRect;
        public Region2DOrigin gridOrigin = new Region2DOrigin();
        public double pixelsPerUnit;
        public bool tiling, preview, transparentBackground;
        public int tileSize = 4096;
        public string stage = "MainStage", readbackMode = "Immediate", outputDirectory, fileName;
    }

    internal sealed class Region2DCameraPlan
    {
        public float x, y, z, orthographicSize, aspect, nearClipPlane, farClipPlane;
        public double maximumEdgeErrorPixels, estimatedPixelsPerUnitX, estimatedPixelsPerUnitY;
        public string forward = "+Z", up = "+Y";
    }

    internal sealed class Region2DTile
    {
        public int index, column, row, x, y, width, height, topLeftX, topLeftY;
        public Region2DRect worldRect;
        public Region2DCameraPlan camera;
    }

    internal sealed class Region2DPlan
    {
        public string captureKind = "Capture2DRegion";
        public string roundingPolicy = "outward; snap to nearest integer within min(4 ULP, 0.000001 pixel)";
        public string pixelBoundsConvention = "half-open; x/y relative to master bottom-left";
        public string reconstructionConvention = "PNG top-left origin; use topLeftX/topLeftY";
        public Region2DRect requestedWorldRect, effectiveWorldRect;
        public Region2DOrigin gridOrigin;
        public double pixelsPerUnit;
        public long gridXMin, gridYMin, gridXMax, gridYMax, totalPixels, maximumTileWorkingBytes;
        public int width, height, columns, rows, maximumTextureDimension;
        public List<Region2DTile> tiles = new List<Region2DTile>();
    }

    internal static class Region2DPlanner
    {
        internal const long MaxTotalPixels = 16L * 1024 * 1024;
        internal const int MaxTiles = 64;
        internal const long MaxWorkingBytes = 256L * 1024 * 1024;
        internal const double MaxCameraEdgeErrorPixels = 1.0 / 64;
        const double MaxExactInteger = 9007199254740991d;
        static readonly HashSet<string> Accepted = new HashSet<string>(new[] {
            "action", "worldrect", "pixelsperunit", "gridorigin", "tiling", "tilesize", "preview",
            "stage", "outputdirectory", "filename", "transparentbackground", "readbackmode"
        });

        internal static Region2DRequest Parse(JObject input)
        {
            if (input == null) Fail("REQUEST_REQUIRED", "Capture2DRegion requires an object request.");
            var properties = Normalize(input, Accepted, "request");
            if (!string.Equals(String(properties, "action", null), "Capture2DRegion", StringComparison.OrdinalIgnoreCase))
                Fail("ACTION_INVALID", "Action must be Capture2DRegion; unknown actions never fall back to SceneView.");
            var result = new Region2DRequest {
                worldRect = Rect(Object(properties, "worldrect", true)),
                pixelsPerUnit = Number(properties, "pixelsperunit", true, 0),
                tiling = Boolean(properties, "tiling", false), preview = Boolean(properties, "preview", false),
                transparentBackground = Boolean(properties, "transparentbackground", false),
                tileSize = Integer(properties, "tilesize", 4096),
                stage = String(properties, "stage", "MainStage"),
                readbackMode = String(properties, "readbackmode", "Immediate"),
                outputDirectory = String(properties, "outputdirectory", null), fileName = String(properties, "filename", null)
            };
            if (properties.ContainsKey("gridorigin")) {
                var origin = Normalize(Object(properties, "gridorigin", true), new HashSet<string>(new[] { "x", "y" }), "GridOrigin");
                result.gridOrigin = new Region2DOrigin { x = Number(origin, "x", true, 0), y = Number(origin, "y", true, 0) };
            }
            if (result.stage != "MainStage" && result.stage != "CurrentPrefabStage") Fail("STAGE_INVALID", "Stage must be MainStage or CurrentPrefabStage.");
            if (result.readbackMode != "Immediate" && result.readbackMode != "GpuReadback") Fail("READBACK_INVALID", "ReadbackMode must be Immediate or GpuReadback.");
            return result;
        }

        internal static Region2DPlan Build(Region2DRequest request, int hardwareMaxTextureDimension)
        {
            if (request == null || request.worldRect == null || request.gridOrigin == null) Fail("REQUEST_REQUIRED", "A world rectangle and grid origin are required.");
            var r = request.worldRect;
            foreach (var n in new[] { r.xMin, r.yMin, r.xMax, r.yMax, request.gridOrigin.x, request.gridOrigin.y, request.pixelsPerUnit })
                if (!Finite(n)) Fail("NUMBER_NOT_FINITE", "All region numbers must be finite.");
            if (r.xMax <= r.xMin || r.yMax <= r.yMin) Fail("RECT_INVALID", "WorldRect maximum edges must exceed minimum edges.");
            if (request.pixelsPerUnit <= 0) Fail("PPU_INVALID", "PixelsPerUnit must be positive.");
            if (hardwareMaxTextureDimension < 1) Fail("HARDWARE_LIMIT", "No positive hardware texture dimension is available.");
            if (request.tileSize < 1 || request.tileSize > 4096) Fail("TILE_SIZE_INVALID", "TileSize must be an integer from 1 to 4096.");
            if (request.stage != "MainStage" && request.stage != "CurrentPrefabStage") Fail("STAGE_INVALID", "Unsupported stage.");
            if (request.readbackMode != "Immediate" && request.readbackMode != "GpuReadback") Fail("READBACK_INVALID", "Unsupported readback mode.");
            var plan = new Region2DPlan {
                requestedWorldRect = Copy(r), gridOrigin = new Region2DOrigin { x = request.gridOrigin.x, y = request.gridOrigin.y },
                pixelsPerUnit = request.pixelsPerUnit, maximumTextureDimension = Math.Min(4096, hardwareMaxTextureDimension)
            };
            plan.gridXMin = Edge(r.xMin, request.gridOrigin.x, request.pixelsPerUnit, false);
            plan.gridYMin = Edge(r.yMin, request.gridOrigin.y, request.pixelsPerUnit, false);
            plan.gridXMax = Edge(r.xMax, request.gridOrigin.x, request.pixelsPerUnit, true);
            plan.gridYMax = Edge(r.yMax, request.gridOrigin.y, request.pixelsPerUnit, true);
            var width = checked(plan.gridXMax - plan.gridXMin); var height = checked(plan.gridYMax - plan.gridYMin);
            if (width < 1 || height < 1) Fail("PIXEL_EXTENT_INVALID", "The declared grid policy must produce a positive pixel extent.");
            if (width > int.MaxValue || height > int.MaxValue) Fail("PIXEL_BUDGET", "Master dimensions exceed representable pixel dimensions.");
            if (width > MaxTotalPixels || height > MaxTotalPixels || width > MaxTotalPixels / height) Fail("PIXEL_BUDGET", "The region exceeds the 16777216-pixel operation budget.");
            plan.width = (int)width; plan.height = (int)height; plan.totalPixels = width * height;
            plan.effectiveWorldRect = World(plan, 0, 0, plan.width, plan.height);
            var tileDimension = request.tiling ? Math.Min(request.tileSize, plan.maximumTextureDimension) : plan.maximumTextureDimension;
            if (!request.tiling && (width > tileDimension || height > tileDimension))
                Fail("TILING_REQUIRED", "The requested density exceeds a single texture; enable Tiling instead of rescaling.");
            plan.columns = (int)((width + tileDimension - 1) / tileDimension);
            plan.rows = (int)((height + tileDimension - 1) / tileDimension);
            if ((long)plan.columns * plan.rows > MaxTiles) Fail("TILE_BUDGET", "The operation exceeds 64 tiles.");
            // Color RT + depth + CPU texture + readback + PNG/verification headroom. Sequential tiles only.
            plan.maximumTileWorkingBytes = checked((long)Math.Min(plan.width, tileDimension) * Math.Min(plan.height, tileDimension) * 16);
            if (plan.maximumTileWorkingBytes > MaxWorkingBytes) Fail("MEMORY_BUDGET", "The largest tile exceeds the 256 MiB working budget.");
            for (int row = 0; row < plan.rows; row++) for (int column = 0; column < plan.columns; column++) {
                int x = checked(column * tileDimension), y = checked(row * tileDimension);
                int w = Math.Min(tileDimension, plan.width - x), h = Math.Min(tileDimension, plan.height - y);
                var bounds = World(plan, x, y, w, h);
                plan.tiles.Add(new Region2DTile { index = plan.tiles.Count, column = column, row = row,
                    x = x, y = y, width = w, height = h, topLeftX = x, topLeftY = plan.height - y - h,
                    worldRect = bounds, camera = Camera(bounds, w, h, request.pixelsPerUnit, -1000, 1000) });
            }
            return plan;
        }

        internal static Region2DCameraPlan Camera(Region2DRect r, int width, int height, double ppu, double zMin, double zMax)
        {
            double worldWidth = r.xMax - r.xMin, worldHeight = r.yMax - r.yMin;
            if (!Finite(worldWidth) || !Finite(worldHeight) || worldWidth <= 0 || worldHeight <= 0) Fail("WORLD_PRECISION", "Effective world edges are not separately representable.");
            var c = new Region2DCameraPlan { x = (float)(r.xMin + worldWidth / 2), y = (float)(r.yMin + worldHeight / 2),
                z = (float)(zMin - 1), nearClipPlane = 1, farClipPlane = (float)(zMax - zMin + 1),
                orthographicSize = (float)(worldHeight / 2), aspect = (float)((double)width / height) };
            foreach (var n in new[] { c.x, c.y, c.z, c.orthographicSize, c.aspect, c.nearClipPlane, c.farClipPlane })
                if (!Finite(n)) Fail("CAMERA_PRECISION", "The region cannot be represented by Unity's float camera geometry.");
            if (c.orthographicSize <= 0 || c.aspect <= 0 || c.farClipPlane <= c.nearClipPlane) Fail("CAMERA_PRECISION", "Camera extent or depth collapses in float geometry.");
            double halfWidth = (double)c.orthographicSize * c.aspect, halfHeight = c.orthographicSize;
            c.maximumEdgeErrorPixels = new[] { Math.Abs(c.x - halfWidth - r.xMin), Math.Abs(c.x + halfWidth - r.xMax),
                Math.Abs(c.y - halfHeight - r.yMin), Math.Abs(c.y + halfHeight - r.yMax) }.Max() * ppu;
            if (!Finite(c.maximumEdgeErrorPixels) || c.maximumEdgeErrorPixels > MaxCameraEdgeErrorPixels)
                Fail("CAMERA_PRECISION", "Float camera edge error exceeds 1/64 pixel at the requested density.");
            double zNearError = Math.Abs((double)c.z + c.nearClipPlane - zMin);
            double zFarError = Math.Abs((double)c.z + c.farClipPlane - zMax);
            if (Math.Max(zNearError, zFarError) > Math.Max(1e-6, (zMax - zMin) * 1e-6)) Fail("DEPTH_PRECISION", "Float clip planes cannot represent the declared depth interval.");
            c.estimatedPixelsPerUnitX = width / (2 * halfWidth); c.estimatedPixelsPerUnitY = height / (2 * halfHeight);
            return c;
        }

        static Region2DRect World(Region2DPlan p, int x, int y, int width, int height)
        {
            return new Region2DRect {
                xMin = p.gridOrigin.x + (p.gridXMin + x) / p.pixelsPerUnit,
                yMin = p.gridOrigin.y + (p.gridYMin + y) / p.pixelsPerUnit,
                xMax = p.gridOrigin.x + (p.gridXMin + x + width) / p.pixelsPerUnit,
                yMax = p.gridOrigin.y + (p.gridYMin + y + height) / p.pixelsPerUnit
            };
        }

        static long Edge(double edge, double origin, double ppu, bool maximum)
        {
            double pixel = (edge - origin) * ppu;
            if (!Finite(pixel) || Math.Abs(pixel) > MaxExactInteger) Fail("GRID_OVERFLOW", "World edges exceed the exact integer pixel-grid range.");
            double nearest = Math.Round(pixel, MidpointRounding.ToEven);
            double magnitude = Math.Abs(pixel);
            double ulp = magnitude == 0 ? double.Epsilon : BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(magnitude) + 1) - magnitude;
            if (Math.Abs(pixel - nearest) <= Math.Min(4 * ulp, 1e-6)) pixel = nearest;
            return checked((long)(maximum ? Math.Ceiling(pixel) : Math.Floor(pixel)));
        }

        static Region2DRect Copy(Region2DRect r) => new Region2DRect { xMin = r.xMin, yMin = r.yMin, xMax = r.xMax, yMax = r.yMax };
        static Region2DRect Rect(JObject source)
        {
            var values = Normalize(source, new HashSet<string>(new[] { "xmin", "ymin", "xmax", "ymax" }), "WorldRect");
            return new Region2DRect { xMin = Number(values, "xmin", true, 0), yMin = Number(values, "ymin", true, 0), xMax = Number(values, "xmax", true, 0), yMax = Number(values, "ymax", true, 0) };
        }
        static Dictionary<string, JToken> Normalize(JObject obj, HashSet<string> accepted, string location)
        {
            var result = new Dictionary<string, JToken>();
            foreach (var property in obj.Properties()) {
                string key = property.Name.Replace("_", "").ToLowerInvariant();
                if (!accepted.Contains(key)) Fail("PARAMETER_UNSUPPORTED", $"{location}: '{property.Name}' is unsupported; inherited framing/advancement parameters do not apply.");
                if (result.ContainsKey(key)) Fail("PARAMETER_DUPLICATE", $"{location}: duplicate aliases for '{property.Name}'.");
                result.Add(key, property.Value);
            }
            return result;
        }
        static JObject Object(Dictionary<string, JToken> p, string key, bool required)
        {
            if (!p.TryGetValue(key, out var token)) { if (required) Fail("PARAMETER_REQUIRED", $"{key} is required."); return null; }
            if (!(token is JObject obj)) Fail("TYPE_INVALID", $"{key} must be an object.");
            return (JObject)token;
        }
        static double Number(Dictionary<string, JToken> p, string key, bool required, double fallback)
        {
            if (!p.TryGetValue(key, out var t)) { if (required) Fail("PARAMETER_REQUIRED", $"{key} is required."); return fallback; }
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float) Fail("TYPE_INVALID", $"{key} must be a JSON number.");
            double n;
            try { n = Convert.ToDouble(((JValue)t).Value, CultureInfo.InvariantCulture); }
            catch (Exception) { Fail("NUMBER_NOT_FINITE", $"{key} is outside numeric range."); return 0; }
            if (!Finite(n)) Fail("NUMBER_NOT_FINITE", $"{key} must be finite.");
            return n;
        }
        static int Integer(Dictionary<string, JToken> p, string key, int fallback)
        {
            if (!p.TryGetValue(key, out var t)) return fallback;
            if (t.Type != JTokenType.Integer) Fail("TYPE_INVALID", $"{key} must be a JSON integer.");
            try { return checked(Convert.ToInt32(((JValue)t).Value, CultureInfo.InvariantCulture)); }
            catch (Exception) { Fail("INTEGER_RANGE", $"{key} is outside Int32 range."); return 0; }
        }
        static bool Boolean(Dictionary<string, JToken> p, string key, bool fallback)
        {
            if (!p.TryGetValue(key, out var t)) return fallback;
            if (t.Type != JTokenType.Boolean) Fail("TYPE_INVALID", $"{key} must be a JSON boolean.");
            return (bool)((JValue)t).Value;
        }
        static string String(Dictionary<string, JToken> p, string key, string fallback)
        {
            if (!p.TryGetValue(key, out var t)) return fallback;
            if (t.Type != JTokenType.String) Fail("TYPE_INVALID", $"{key} must be a JSON string.");
            return (string)((JValue)t).Value;
        }
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static void Fail(string code, string message) => throw new Region2DException(code, message);
    }
}
