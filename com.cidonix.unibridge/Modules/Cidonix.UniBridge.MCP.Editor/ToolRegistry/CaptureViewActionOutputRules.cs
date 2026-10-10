using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using static Cidonix.UniBridge.MCP.Editor.ToolRegistry.ActionOutputSchema;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    // Exact business producers in CaptureView and Region2DCapture. The registry
    // composes project/transport/projection branches outside this family.
    internal static class CaptureViewActionOutputRules
    {
        static JObject S() => Type("string");
        static JObject I() => Type("integer");
        static JObject B() => Type("boolean");
        static JObject N() => Number();
        static JObject NS() => Nullable(S());
        static JObject NI() => Nullable(I());
        static JObject NN() => Nullable(N());
        static JObject NB() => Nullable(B());
        static JObject Strings() => Arr(S());
        static JObject Family(params JObject[] branches) => Compose(branches.Concat(new[] { Error() }));

        internal static Dictionary<string, JObject> Build()
        {
            var result = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var action in new[] { "CaptureSceneView", "CaptureSelection", "CaptureObject", "CapturePrefabStage", "CaptureSceneOverview", "CaptureAroundObject" })
                result.Add(action, Family(Success(SceneCapture())));
            result.Add("CaptureGameCamera", Family(Success(CameraCapture())));
            result.Add("CaptureGameView", Family(Success(GameViewCapture())));
            result.Add("CaptureSeries", Family(Success(Obj(
                ("requestedCount", I()), ("capturedCount", I()), ("errorCount", I()), ("source", S()),
                ("intervalSeconds", N()), ("blockingSeries", B()), ("note", S()),
                ("captures", Arr(AnyOf(SceneCapture(), CameraCapture()))), ("errors", Arr(Error()))))));
            result.Add("CaptureContactSheet", ContactSheet());
            result.Add("CaptureDiff", Family(Success(Obj(
                ("baselinePath", S()), ("comparePath", S()), ("diffPath", S()), ("fileUri", S()),
                ("width", I()), ("height", I()), ("changedPixels", I()), ("totalPixels", I()),
                ("changedPercent", N()), ("averageDelta", N()), ("threshold", I()),
                ("changedBounds", Nullable(Obj(("minX", I()), ("minY", I()), ("maxX", I()), ("maxY", I()), ("width", I()), ("height", I())))))),
                Error(Obj(("baseline", Dimensions()), ("compare", Dimensions())))));
            result.Add("ClearCaptures", Family(
                Success(Obj(("directory", S()), ("deletedCount", I()))),
                Success(Obj(("directory", S()), ("scannedCount", I()), ("deletedCount", I()), ("deleteAll", B()),
                    ("keepLatest", NI()), ("maxAgeDays", NN()), ("deleted", Arr(AnyOf(S(), Obj(("path", S()), ("error", S())))))))));
            result.Add("ListCameras", Family(Success(Obj(("project", Project()), ("activeScene", Scene()), ("cameras", Arr(Camera()))))));
            result.Add("Capture2DRegion", Region());
            return result;
        }

        static JObject Dimensions() => Obj(("width", I()), ("height", I()));
        static JObject Project() => Obj(("projectId", S()), ("projectName", S()), ("projectRoot", S()));
        static JObject Scene() => Obj(("name", NS()), ("path", NS()), ("buildIndex", I()), ("isLoaded", B()), ("isDirty", B()));
        static JObject Vector() => Obj(("x", N()), ("y", N()), ("z", N()));
        static JObject Bounds() => Obj(("center", Vector()), ("size", Vector()), ("min", Vector()), ("max", Vector()));
        static JObject Rect() => Obj(("xMin", N()), ("yMin", N()), ("xMax", N()), ("yMax", N()), ("width", N()), ("height", N()));
        static JObject GameObject() => Obj(("name", S()), ("objectId", I()), ("hierarchyPath", S()), ("scene", Scene()),
            ("tag", S()), ("layer", S()), ("activeSelf", B()), ("activeInHierarchy", B()),
            ("transform", Obj(("position", Vector()), ("rotation", Vector()), ("scale", Vector()))), ("bounds", Bounds()), ("components", Strings()));
        static JObject Camera() => Obj(("name", S()), ("objectId", I()), ("gameObjectId", I()), ("hierarchyPath", S()),
            ("scene", Scene()), ("enabled", B()), ("activeInHierarchy", B()), ("tag", S()), ("depth", N()), ("clearFlags", S()),
            ("orthographic", B()), ("fieldOfView", N()), ("orthographicSize", N()), ("nearClipPlane", N()), ("farClipPlane", N()));
        static JObject VisualObject() => Obj(("name", S()), ("objectId", I()), ("hierarchyPath", S()), ("scene", Scene()),
            ("isTarget", B()), ("isVisible", B()), ("distanceToFocus", N()), ("bounds", Bounds()), ("viewportRect", Nullable(Rect())), ("components", Strings()));
        static JObject Annotation() => Obj(("marker", I()), ("label", S()), ("objectId", I()), ("isTarget", B()), ("isVisible", B()),
            ("viewportRect", Nullable(Rect())), ("metadata", VisualObject()));
        static JObject Advance() => Obj(("requested", B()), ("advanceMs", I()), ("simulateParticles", B()), ("sampleAnimations", B()),
            ("particleSystemsAdvanced", I()), ("animatorsSampled", I()), ("legacyAnimationsSampled", I()), ("warnings", Strings()));
        static JObject Overlay() => Obj(("enabled", B()), ("separate", B()), ("composite", B()), ("itemCount", I()));
        static JObject PrefabStage() => Obj(("isOpen", B()), ("assetPath", NS()), ("rootName", NS()), ("rootHierarchyPath", NS()), ("isDirty", B()), ("mode", NS()));
        static JObject FramedCamera() => Obj(("created", B()), ("cameraName", NS()), ("orthographic", B()), ("orthographicSize", N()),
            ("fieldOfView", N()), ("nearClipPlane", N()), ("farClipPlane", N()), ("position", Nullable(Vector())),
            ("rotation", Nullable(Vector())), ("frameBounds", Bounds()));
        static JObject Request() => Obj(
            ("action", S()), ("width", NI()), ("height", NI()), ("target", NS()), ("searchMethod", NS()), ("view", S()),
            ("views", Nullable(Strings())), ("zoom", S()), ("orthographic", NB()), ("camera", NS()), ("outputDirectory", NS()),
            ("fileName", NS()), ("tag", NS()), ("transparentBackground", B()), ("overlay", B()), ("separateOverlay", B()),
            ("compositeOverlay", B()), ("includeNearbyObjects", NB()), ("nearbyRadius", NN()), ("maxObjects", NI()),
            ("seriesCount", NI()), ("seriesIntervalSeconds", NN()), ("contactSheetColumns", NI()), ("includeContactSheetLabels", NB()),
            ("baselinePath", NS()), ("comparePath", NS()), ("deleteAll", NB()), ("keepLatest", NI()), ("maxAgeDays", NN()),
            ("advanceMs", NI()), ("simulateParticles", NB()), ("sampleAnimations", NB()), ("readbackMode", S()), ("screenshotTimeoutMs", NI()));

        static List<(string Name, JObject Schema)> ImageFields() => new List<(string, JObject)>
        {
            ("captureKind", S()), ("source", S()), ("imagePath", S()), ("fileUri", S()), ("contentType", S()),
            ("width", I()), ("height", I()), ("fileSizeBytes", I()), ("createdUtc", S()), ("tag", NS()),
            ("project", Project()), ("activeScene", Scene()), ("camera", Nullable(Camera())), ("target", Nullable(GameObject())),
            ("transparentBackground", B()), ("readbackMode", S()), ("foregroundWindowRequired", B()),
            ("framedTarget", Nullable(GameObject())), ("frameBounds", Nullable(Bounds())),
            ("visibleObjects", Arr(VisualObject())), ("nearbyObjects", Arr(VisualObject())), ("annotations", Arr(Annotation()))
        };
        static JObject Image(params (string Name, JObject Schema)[] extra)
        {
            var fields = ImageFields();
            fields.AddRange(extra);
            var required = fields.Select(x => x.Name).ToArray();
            fields.AddRange(new[] {
                ("overlayImagePath", S()), ("overlayFileUri", S()), ("overlayFileSizeBytes", I()),
                ("compositeImagePath", S()), ("compositeFileUri", S()), ("compositeFileSizeBytes", I()) });
            return OptionalObj(required, fields.ToArray());
        }
        static JObject SceneCapture() => Image(("request", Request()), ("view", S()), ("zoom", S()), ("orthographic", B()),
            ("framedRenderCamera", Nullable(FramedCamera())), ("stage", Nullable(PrefabStage())), ("advance", Advance()), ("overlay", Overlay()));
        static JObject CameraCapture() => Image(("request", Request()), ("advance", Advance()), ("overlay", Overlay()));
        static JObject GameViewCapture() => Image(("request", Request()), ("advance", Advance()),
            ("gameView", Obj(("exactPostRender", B()), ("windowFound", B()), ("windowTitle", NS()), ("tempRelativePath", S()),
                ("waitedMs", N()), ("loadAttempts", I()), ("tempFileSizeBytes", I()), ("requestedWidth", NI()), ("requestedHeight", NI()), ("returnedActualSize", B()))),
            ("overlay", Obj(("requested", B()), ("rendered", B()), ("reason", NS()), ("itemCount", I()))));

        static JObject ContactSheet()
        {
            var errors = Arr(AnyOf(Error(), Obj(("success", Const(false)), ("message", S()), ("data", Nullable(SceneCapture())))));
            var cell = Obj(("index", I()), ("view", S()), ("frame", I()), ("label", S()), ("imagePath", S()), ("fileUri", S()),
                ("width", I()), ("height", I()), ("fileSizeBytes", I()), ("captureKind", NS()), ("source", NS()), ("readbackMode", NS()));
            return Family(Success(Obj(("captureKind", S()), ("source", S()), ("imagePath", S()), ("fileUri", S()), ("contentType", S()),
                ("width", I()), ("height", I()), ("fileSizeBytes", I()), ("createdUtc", S()), ("renderMode", S()),
                ("columns", I()), ("rows", I()), ("cellWidth", I()), ("cellHeight", I()), ("gutter", I()), ("labels", B()),
                ("requestedViews", Strings()), ("requestedFramesPerView", I()), ("framesPerView", I()), ("truncatedFramesPerView", B()),
                ("intervalSeconds", N()), ("capturedCount", I()), ("errorCount", I()), ("maxContactSheetCells", I()), ("blockingSeries", B()),
                ("project", Project()), ("activeScene", Scene()), ("request", Request()), ("captures", Arr(cell)), ("errors", errors))),
                Error(Obj(("requestedWidth", I()), ("requestedHeight", I()), ("views", Strings()), ("requestedFramesPerView", I()),
                    ("framesPerView", I()), ("columns", I()), ("rows", I()), ("maxContactSheetDimension", I()))),
                Error(Obj(("requestedViews", Strings()), ("requestedFramesPerView", I()), ("framesPerView", I()), ("errors", errors))));
        }

        static JObject RegionScope() => Obj(("stage", S()), ("sceneCullingMask", I()),
            ("scenes", Arr(Obj(("identity", S()), ("path", S()), ("name", S()), ("dirty", B())))), ("singleSceneIsolation", S()));
        static JObject Region()
        {
            var preview = Obj(("success", Const(true)), ("message", S()), ("data", Obj(("status", Const("preview")),
                ("captureKind", Const("Capture2DRegion")), ("preview", Const(true)), ("written", Const(false)),
                ("plan", DTO(typeof(Region2DPlan))), ("scope", RegionScope()), ("outputPaths", Strings()), ("nativePixelQualification", S()))));
            var completion = Obj(("success", B()), ("message", S()), ("data", Obj(("status", Enum("completed", "partial", "blocked")),
                ("captureKind", Const("Capture2DRegion")), ("preview", B()), ("written", B()), ("errorCode", NS()),
                ("plan", Nullable(DTO(typeof(Region2DPlan)))), ("scope", Nullable(RegionScope())), ("outputPaths", Nullable(Strings())),
                ("images", Arr(DTO(typeof(Region2DCapture.ImageResult)))), ("cleanupErrors", Strings()), ("nativePixelQualification", S()), ("seamParityScope", S()))));
            return Family(preview, completion);
        }
    }
}
