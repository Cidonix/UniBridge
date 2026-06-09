#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class ManageUI
    {
        static void ApplyLayoutPreset(
            RectTransform rectTransform,
            RectLayoutHorizontal horizontal,
            RectLayoutVertical vertical,
            bool alsoSetPivot,
            bool alsoSetPosition)
        {
            if (alsoSetPivot)
            {
                var pivot = new Vector2(PivotForHorizontal(horizontal), PivotForVertical(vertical));
                SetPivot(rectTransform, pivot, keepWorldPosition: !alsoSetPosition);
            }

            SetAnchorAxis(rectTransform, axis: 0, AnchorMinForHorizontal(horizontal), AnchorMaxForHorizontal(horizontal), horizontal == RectLayoutHorizontal.Stretch, alsoSetPosition);
            SetAnchorAxis(rectTransform, axis: 1, AnchorMinForVertical(vertical), AnchorMaxForVertical(vertical), vertical == RectLayoutVertical.Stretch, alsoSetPosition);
        }

        static void SetAnchorAxis(RectTransform rectTransform, int axis, float min, float max, bool stretch, bool setPosition)
        {
            var parent = rectTransform.parent as RectTransform;
            var parentSize = parent != null ? parent.rect.size[axis] : 0f;
            var oldAnchorMin = rectTransform.anchorMin;
            var oldAnchorMax = rectTransform.anchorMax;
            var newAnchorMin = oldAnchorMin;
            var newAnchorMax = oldAnchorMax;

            newAnchorMin[axis] = min;
            newAnchorMax[axis] = max;

            if (!setPosition)
            {
                var offset = (oldAnchorMin[axis] - newAnchorMin[axis]) * parentSize;
                var anchoredPosition = rectTransform.anchoredPosition;
                anchoredPosition[axis] += offset;
                rectTransform.anchoredPosition = anchoredPosition;
            }

            rectTransform.anchorMin = newAnchorMin;
            rectTransform.anchorMax = newAnchorMax;

            if (setPosition)
            {
                var anchoredPosition = rectTransform.anchoredPosition;
                anchoredPosition[axis] = 0f;
                rectTransform.anchoredPosition = anchoredPosition;
            }

            if (stretch && setPosition)
            {
                var sizeDelta = rectTransform.sizeDelta;
                sizeDelta[axis] = 0f;
                rectTransform.sizeDelta = sizeDelta;
            }
        }

        static void SetPivot(RectTransform rectTransform, Vector2 pivot, bool keepWorldPosition)
        {
            if (!keepWorldPosition)
            {
                rectTransform.pivot = pivot;
                return;
            }

            var oldWorldPosition = rectTransform.position;
            rectTransform.pivot = pivot;
            var delta = rectTransform.position - oldWorldPosition;
            rectTransform.anchoredPosition -= (Vector2)rectTransform.InverseTransformVector(delta);
        }

        static void ApplyOptionalRectValues(RectTransform rectTransform, ManageUIParams parameters, bool recordUndo)
        {
            if (recordUndo)
            {
                Undo.RecordObject(rectTransform, "Set UniBridge RectTransform");
            }

            if (TryReadVector2(parameters.AnchorMin, out var anchorMin))
                rectTransform.anchorMin = anchorMin;
            if (TryReadVector2(parameters.AnchorMax, out var anchorMax))
                rectTransform.anchorMax = anchorMax;
            if (TryReadVector2(parameters.Pivot, out var pivot))
                SetPivot(rectTransform, pivot, parameters.MaintainWorldPosition ?? true);
            if (TryReadVector2(parameters.AnchoredPosition, out var anchoredPosition))
                rectTransform.anchoredPosition = anchoredPosition;
            if (TryReadVector2(parameters.SizeDelta, out var sizeDelta))
                rectTransform.sizeDelta = sizeDelta;
            if (TryReadVector2(parameters.OffsetMin, out var offsetMin))
                rectTransform.offsetMin = offsetMin;
            if (TryReadVector2(parameters.OffsetMax, out var offsetMax))
                rectTransform.offsetMax = offsetMax;
            if (TryReadVector3(parameters.LocalScale, out var localScale))
                rectTransform.localScale = localScale;
        }

        static Type GetLayoutGroupComponentType(UILayoutGroupType layoutGroupType)
        {
            return layoutGroupType switch
            {
                UILayoutGroupType.Horizontal => typeof(HorizontalLayoutGroup),
                UILayoutGroupType.Grid => typeof(GridLayoutGroup),
                _ => typeof(VerticalLayoutGroup)
            };
        }

        static LayoutGroup GetFirstLayoutGroup(GameObject target, Type componentType)
        {
            return target.GetComponents(componentType).OfType<LayoutGroup>().FirstOrDefault();
        }

        static void RemoveLayoutGroupsExcept(GameObject target, Type keepType)
        {
            foreach (var group in target.GetComponents<LayoutGroup>())
            {
                if (group.GetType() != keepType)
                {
                    Undo.DestroyObjectImmediate(group);
                }
            }
        }

        static void RemoveDuplicateLayoutGroups(GameObject target, Type componentType, LayoutGroup keep)
        {
            foreach (var group in target.GetComponents(componentType).OfType<LayoutGroup>())
            {
                if (group != keep)
                {
                    Undo.DestroyObjectImmediate(group);
                }
            }
        }

        static void ApplyLayoutGroupSettings(LayoutGroup group, ManageUIParams parameters)
        {
            if (parameters.ChildAlignment.HasValue)
            {
                group.childAlignment = ConvertTextAnchor(parameters.ChildAlignment.Value);
            }

            if (TryReadRectOffset(parameters.Padding, out var padding))
            {
                group.padding = padding;
            }

            switch (group)
            {
                case HorizontalOrVerticalLayoutGroup horizontalOrVertical:
                    if (TryReadSpacing(parameters.Spacing, out var spacing))
                        horizontalOrVertical.spacing = spacing;
                    if (parameters.ChildControlWidth.HasValue)
                        horizontalOrVertical.childControlWidth = parameters.ChildControlWidth.Value;
                    if (parameters.ChildControlHeight.HasValue)
                        horizontalOrVertical.childControlHeight = parameters.ChildControlHeight.Value;
                    if (parameters.ChildForceExpandWidth.HasValue)
                        horizontalOrVertical.childForceExpandWidth = parameters.ChildForceExpandWidth.Value;
                    if (parameters.ChildForceExpandHeight.HasValue)
                        horizontalOrVertical.childForceExpandHeight = parameters.ChildForceExpandHeight.Value;
                    if (parameters.ChildScaleWidth.HasValue)
                        horizontalOrVertical.childScaleWidth = parameters.ChildScaleWidth.Value;
                    if (parameters.ChildScaleHeight.HasValue)
                        horizontalOrVertical.childScaleHeight = parameters.ChildScaleHeight.Value;
                    if (parameters.ReverseArrangement.HasValue)
                        horizontalOrVertical.reverseArrangement = parameters.ReverseArrangement.Value;
                    break;
                case GridLayoutGroup grid:
                    if (TryReadVector2(parameters.CellSize, out var cellSize))
                        grid.cellSize = cellSize;
                    if (TryReadVector2Flexible(parameters.Spacing, out var gridSpacing))
                        grid.spacing = gridSpacing;
                    grid.startCorner = ConvertGridStartCorner(parameters.StartCorner);
                    grid.startAxis = ConvertGridStartAxis(parameters.StartAxis);
                    grid.constraint = ConvertGridConstraint(parameters.Constraint);
                    if (parameters.ConstraintCount.HasValue || parameters.Constraint != UIGridConstraint.Flexible)
                    {
                        grid.constraintCount = Mathf.Max(1, parameters.ConstraintCount ?? grid.constraintCount);
                    }
                    break;
            }
        }

        static void ApplyLayoutElementSettings(LayoutElement layoutElement, ManageUIParams parameters)
        {
            if (parameters.IgnoreLayout.HasValue)
                layoutElement.ignoreLayout = parameters.IgnoreLayout.Value;
            if (parameters.MinWidth.HasValue)
                layoutElement.minWidth = parameters.MinWidth.Value;
            if (parameters.MinHeight.HasValue)
                layoutElement.minHeight = parameters.MinHeight.Value;
            if (parameters.PreferredWidth.HasValue)
                layoutElement.preferredWidth = parameters.PreferredWidth.Value;
            if (parameters.PreferredHeight.HasValue)
                layoutElement.preferredHeight = parameters.PreferredHeight.Value;
            if (parameters.FlexibleWidth.HasValue)
                layoutElement.flexibleWidth = parameters.FlexibleWidth.Value;
            if (parameters.FlexibleHeight.HasValue)
                layoutElement.flexibleHeight = parameters.FlexibleHeight.Value;
            if (parameters.LayoutPriority.HasValue)
                layoutElement.layoutPriority = parameters.LayoutPriority.Value;
        }

        static bool HasLayoutElementSettings(ManageUIParams parameters)
        {
            return parameters.IgnoreLayout.HasValue
                   || parameters.MinWidth.HasValue
                   || parameters.MinHeight.HasValue
                   || parameters.PreferredWidth.HasValue
                   || parameters.PreferredHeight.HasValue
                   || parameters.FlexibleWidth.HasValue
                   || parameters.FlexibleHeight.HasValue
                   || parameters.LayoutPriority.HasValue;
        }

        static object BuildLayoutGroupPlan(ManageUIParams parameters)
        {
            return new
            {
                layoutGroupType = parameters.LayoutGroupType.ToString(),
                padding = parameters.Padding,
                spacing = parameters.Spacing,
                childAlignment = parameters.ChildAlignment?.ToString(),
                childControlWidth = parameters.ChildControlWidth,
                childControlHeight = parameters.ChildControlHeight,
                childForceExpandWidth = parameters.ChildForceExpandWidth,
                childForceExpandHeight = parameters.ChildForceExpandHeight,
                childScaleWidth = parameters.ChildScaleWidth,
                childScaleHeight = parameters.ChildScaleHeight,
                reverseArrangement = parameters.ReverseArrangement,
                cellSize = parameters.CellSize,
                startCorner = parameters.StartCorner.ToString(),
                startAxis = parameters.StartAxis.ToString(),
                constraint = parameters.Constraint.ToString(),
                constraintCount = parameters.ConstraintCount
            };
        }

        static object BuildLayoutElementPlan(ManageUIParams parameters)
        {
            return new
            {
                ignoreLayout = parameters.IgnoreLayout,
                minWidth = parameters.MinWidth,
                minHeight = parameters.MinHeight,
                preferredWidth = parameters.PreferredWidth,
                preferredHeight = parameters.PreferredHeight,
                flexibleWidth = parameters.FlexibleWidth,
                flexibleHeight = parameters.FlexibleHeight,
                layoutPriority = parameters.LayoutPriority
            };
        }

        static ContentSizeFitter.FitMode ConvertFitMode(UILayoutFitMode fitMode)
        {
            return fitMode switch
            {
                UILayoutFitMode.MinSize => ContentSizeFitter.FitMode.MinSize,
                UILayoutFitMode.PreferredSize => ContentSizeFitter.FitMode.PreferredSize,
                _ => ContentSizeFitter.FitMode.Unconstrained
            };
        }

        static GridLayoutGroup.Corner ConvertGridStartCorner(UIGridStartCorner corner)
        {
            return corner switch
            {
                UIGridStartCorner.UpperRight => GridLayoutGroup.Corner.UpperRight,
                UIGridStartCorner.LowerLeft => GridLayoutGroup.Corner.LowerLeft,
                UIGridStartCorner.LowerRight => GridLayoutGroup.Corner.LowerRight,
                _ => GridLayoutGroup.Corner.UpperLeft
            };
        }

        static GridLayoutGroup.Axis ConvertGridStartAxis(UIGridStartAxis axis)
        {
            return axis == UIGridStartAxis.Vertical
                ? GridLayoutGroup.Axis.Vertical
                : GridLayoutGroup.Axis.Horizontal;
        }

        static GridLayoutGroup.Constraint ConvertGridConstraint(UIGridConstraint constraint)
        {
            return constraint switch
            {
                UIGridConstraint.FixedColumnCount => GridLayoutGroup.Constraint.FixedColumnCount,
                UIGridConstraint.FixedRowCount => GridLayoutGroup.Constraint.FixedRowCount,
                _ => GridLayoutGroup.Constraint.Flexible
            };
        }

        static bool TryReadRectOffset(float[] values, out RectOffset rectOffset)
        {
            rectOffset = null;
            if (values == null || values.Length < 4)
            {
                return false;
            }

            rectOffset = new RectOffset(
                Mathf.RoundToInt(values[0]),
                Mathf.RoundToInt(values[1]),
                Mathf.RoundToInt(values[2]),
                Mathf.RoundToInt(values[3]));
            return true;
        }

        static bool TryReadSpacing(float[] values, out float spacing)
        {
            spacing = default;
            if (values == null || values.Length < 1)
            {
                return false;
            }

            spacing = values[0];
            return true;
        }

        static bool TryReadVector2Flexible(float[] values, out Vector2 vector)
        {
            vector = default;
            if (values == null || values.Length < 1)
            {
                return false;
            }

            vector = values.Length >= 2
                ? new Vector2(values[0], values[1])
                : new Vector2(values[0], values[0]);
            return true;
        }


    }
}
