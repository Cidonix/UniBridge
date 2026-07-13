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
        static GameObject BuildPanelTemplate(GameObject parent, string name, ManageUIParams parameters, Dictionary<string, GameObject> roles)
        {
            var panel = CreateTemplateObject(parent, name, true, ReadColor(parameters.BackgroundColor, new Color(0.07f, 0.09f, 0.12f, 0.96f)));
            roles["panel"] = panel;
            SetCenteredTemplateRect(panel.GetComponent<RectTransform>(), ReadTemplateSize(parameters, new Vector2(560f, 380f)));
            ConfigureVerticalTemplateGroup(panel, new RectOffset(24, 24, 22, 22), 12f, TextAnchor.UpperLeft);

            AddTemplateHeader(panel, parameters, roles, "Header", ResolveTemplateTitle(parameters), parameters.Subtitle);

            var items = ResolveTemplateItems(parameters);
            if (items.Length > 0)
            {
                var body = CreateTemplateObject(panel, "Body", true, new Color(1f, 1f, 1f, 0.035f));
                roles["body"] = body;
                ConfigureVerticalTemplateGroup(body, new RectOffset(14, 14, 12, 12), 8f, TextAnchor.UpperLeft);
                AddLayoutElement(body, preferredHeight: Mathf.Min(220f, Mathf.Max(74f, items.Length * 34f)), flexibleWidth: 1f, flexibleHeight: 1f);
                foreach (var item in items)
                {
                    CreateTemplateLabel(body, "Item", item, 17, UITextAlignment.MiddleLeft, ReadColor(parameters.Color, new Color(0.86f, 0.91f, 0.98f, 1f)), 30f, parameters);
                }
            }

            AddTemplateActionRow(panel, parameters, roles, ResolveTemplateActions(parameters), defaultButtons: Array.Empty<string>());
            return panel;
        }

        static GameObject BuildModalTemplate(GameObject parent, string name, ManageUIParams parameters, Dictionary<string, GameObject> roles)
        {
            var overlay = CreateTemplateObject(parent, name, true, ReadColor(parameters.BackgroundColor, new Color(0f, 0f, 0f, 0.58f)));
            roles["overlay"] = overlay;
            ConfigureStretchRect(overlay.GetComponent<RectTransform>());

            var card = CreateTemplateObject(overlay, "Modal Card", true, new Color(0.08f, 0.095f, 0.12f, 0.98f));
            roles["modal"] = card;
            SetCenteredTemplateRect(card.GetComponent<RectTransform>(), ReadTemplateSize(parameters, new Vector2(560f, 340f)));
            ConfigureVerticalTemplateGroup(card, new RectOffset(26, 26, 24, 22), 12f, TextAnchor.UpperLeft);

            AddTemplateHeader(card, parameters, roles, "Header", ResolveTemplateTitle(parameters), parameters.Subtitle);

            var bodyText = ResolveTemplateItems(parameters).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(bodyText))
            {
                var body = CreateTemplateLabel(card, "Body", WrapTemplateText(bodyText, 52), 18, UITextAlignment.UpperLeft, ReadColor(parameters.Color, new Color(0.84f, 0.9f, 0.98f, 1f)), 90f, parameters);
                AddLayoutElement(body, preferredHeight: 96f, flexibleWidth: 1f, flexibleHeight: 1f);
                roles["body"] = body;
            }

            AddTemplateActionRow(card, parameters, roles, ResolveTemplateActions(parameters), defaultButtons: new[] { "Cancel", "OK" });
            return overlay;
        }

        static GameObject BuildToolbarTemplate(GameObject parent, string name, ManageUIParams parameters, Dictionary<string, GameObject> roles)
        {
            var toolbar = CreateTemplateObject(parent, name, true, ReadColor(parameters.BackgroundColor, new Color(0.055f, 0.07f, 0.095f, 0.96f)));
            roles["toolbar"] = toolbar;
            SetAnchoredTemplateRect(toolbar.GetComponent<RectTransform>(), RectLayoutHorizontal.Stretch, RectLayoutVertical.Top, new Vector2(0f, 72f), Vector2.zero);
            ConfigureHorizontalTemplateGroup(toolbar, new RectOffset(16, 16, 10, 10), 10f, TextAnchor.MiddleLeft, childControlWidth: false);

            var title = CreateTemplateLabel(toolbar, "Title", ResolveTemplateTitle(parameters), 22, UITextAlignment.MiddleLeft, ReadColor(parameters.Color, Color.white), 48f, parameters, preferredWidth: 240f);
            roles["title"] = title;

            var actions = ResolveTemplateActions(parameters);
            if (actions.Length == 0)
            {
                actions = ResolveTemplateItems(parameters);
            }
            if (actions.Length == 0)
            {
                actions = new[] { "Primary", "Secondary", "Settings" };
            }

            foreach (var action in actions)
            {
                CreateTemplateButton(toolbar, action, action, parameters, preferredWidth: 132f, preferredHeight: 46f);
            }

            return toolbar;
        }

        static GameObject BuildListTemplate(GameObject parent, string name, ManageUIParams parameters, Dictionary<string, GameObject> roles)
        {
            var panel = CreateTemplateObject(parent, name, true, ReadColor(parameters.BackgroundColor, new Color(0.065f, 0.08f, 0.11f, 0.96f)));
            roles["panel"] = panel;
            SetCenteredTemplateRect(panel.GetComponent<RectTransform>(), ReadTemplateSize(parameters, new Vector2(600f, 520f)));
            ConfigureVerticalTemplateGroup(panel, new RectOffset(22, 22, 20, 20), 12f, TextAnchor.UpperLeft);

            AddTemplateHeader(panel, parameters, roles, "Header", ResolveTemplateTitle(parameters), parameters.Subtitle);

            var items = ResolveTemplateItems(parameters);
            if (items.Length == 0)
            {
                items = new[] { "First item", "Second item", "Third item", "Fourth item", "Fifth item" };
            }

            var scroll = CreateTemplateScrollView(panel, "List", parameters, roles, UILayoutGroupType.Vertical, UIScrollDirection.Vertical, new Vector2(0f, 44f));
            roles["list"] = scroll.scrollView;
            var itemParameters = parameters with
            {
                ElementType = UIElementType.Empty,
                ItemSizeDelta = parameters.ItemSizeDelta ?? new[] { 0f, 46f },
                FontSize = parameters.FontSize ?? 18
            };
            CreateScrollItems(scroll.content, itemParameters, items, ResolveTemplateTextElementType(parameters));
            RebuildLayout(scroll.content);

            AddTemplateActionRow(panel, parameters, roles, ResolveTemplateActions(parameters), defaultButtons: Array.Empty<string>());
            return panel;
        }

        static GameObject BuildCardGridTemplate(GameObject parent, string name, ManageUIParams parameters, Dictionary<string, GameObject> roles)
        {
            var panel = CreateTemplateObject(parent, name, true, ReadColor(parameters.BackgroundColor, new Color(0.065f, 0.075f, 0.1f, 0.96f)));
            roles["panel"] = panel;
            SetCenteredTemplateRect(panel.GetComponent<RectTransform>(), ReadTemplateSize(parameters, new Vector2(760f, 540f)));
            ConfigureVerticalTemplateGroup(panel, new RectOffset(22, 22, 20, 20), 12f, TextAnchor.UpperLeft);

            AddTemplateHeader(panel, parameters, roles, "Header", ResolveTemplateTitle(parameters), parameters.Subtitle);

            var columns = Mathf.Max(1, parameters.Columns ?? parameters.ConstraintCount ?? 3);
            var cellSize = TryReadVector2(parameters.CellSize, out var customCellSize) ? customCellSize : new Vector2(200f, 120f);
            var scroll = CreateTemplateScrollView(panel, "Card Grid", parameters, roles, UILayoutGroupType.Grid, UIScrollDirection.Vertical, cellSize, columns);
            roles["grid"] = scroll.scrollView;

            var items = ResolveTemplateItems(parameters);
            if (items.Length == 0)
            {
                items = new[] { "Card 01", "Card 02", "Card 03", "Card 04", "Card 05", "Card 06" };
            }

            foreach (var item in items)
            {
                CreateTemplateCard(scroll.content.gameObject, item, parameters, cellSize);
            }

            RebuildLayout(scroll.content);
            AddTemplateActionRow(panel, parameters, roles, ResolveTemplateActions(parameters), defaultButtons: Array.Empty<string>());
            return panel;
        }

        static GameObject BuildHudTemplate(GameObject parent, string name, ManageUIParams parameters, Dictionary<string, GameObject> roles)
        {
            var root = CreateTemplateObject(parent, name, false, Color.clear);
            roles["hud"] = root;
            ConfigureStretchRect(root.GetComponent<RectTransform>());

            var topBar = CreateTemplateObject(root, "Top Bar", true, ReadColor(parameters.BackgroundColor, new Color(0.04f, 0.055f, 0.075f, 0.82f)));
            roles["topBar"] = topBar;
            SetAnchoredTemplateRect(topBar.GetComponent<RectTransform>(), RectLayoutHorizontal.Stretch, RectLayoutVertical.Top, new Vector2(0f, 64f), Vector2.zero);
            ConfigureHorizontalTemplateGroup(topBar, new RectOffset(18, 18, 8, 8), 12f, TextAnchor.MiddleLeft, childControlWidth: false);
            var title = CreateTemplateLabel(topBar, "Title", ResolveTemplateTitle(parameters), 22, UITextAlignment.MiddleLeft, ReadColor(parameters.Color, Color.white), 48f, parameters, preferredWidth: 360f);
            AddLayoutElement(title, preferredWidth: 360f, preferredHeight: 48f, flexibleWidth: 1f);

            var stats = CreateTemplateObject(root, "Stats", true, new Color(0.05f, 0.07f, 0.095f, 0.78f));
            roles["stats"] = stats;
            SetAnchoredTemplateRect(stats.GetComponent<RectTransform>(), RectLayoutHorizontal.Left, RectLayoutVertical.Top, new Vector2(300f, 220f), new Vector2(24f, -88f));
            ConfigureVerticalTemplateGroup(stats, new RectOffset(14, 14, 12, 12), 8f, TextAnchor.UpperLeft);
            foreach (var item in ResolveTemplateItems(parameters).DefaultIfEmpty("Status: Ready"))
            {
                CreateTemplateLabel(stats, "Stat", item, 17, UITextAlignment.MiddleLeft, ReadColor(parameters.Color, new Color(0.88f, 0.94f, 1f, 1f)), 30f, parameters);
            }

            var actions = ResolveTemplateActions(parameters);
            if (actions.Length > 0)
            {
                var actionBar = CreateTemplateObject(root, "Action Bar", true, new Color(0.045f, 0.055f, 0.075f, 0.82f));
                roles["actionBar"] = actionBar;
                SetAnchoredTemplateRect(actionBar.GetComponent<RectTransform>(), RectLayoutHorizontal.Right, RectLayoutVertical.Bottom, new Vector2(Mathf.Max(180f, actions.Length * 124f + 28f), 66f), new Vector2(-24f, 24f));
                ConfigureHorizontalTemplateGroup(actionBar, new RectOffset(10, 10, 10, 10), 8f, TextAnchor.MiddleRight, childControlWidth: false);
                foreach (var action in actions)
                {
                    CreateTemplateButton(actionBar, action, action, parameters, preferredWidth: 116f, preferredHeight: 46f);
                }
            }

            return root;
        }



        static string DefaultTemplateName(ManageUIParams parameters)
        {
            if (!string.IsNullOrWhiteSpace(parameters.Name))
            {
                return parameters.Name.Trim();
            }

            return parameters.TemplateType switch
            {
                UITemplateType.Modal => "UniBridge Modal",
                UITemplateType.Toolbar => "UniBridge Toolbar",
                UITemplateType.List => "UniBridge List",
                UITemplateType.CardGrid => "UniBridge Card Grid",
                UITemplateType.HUD => "UniBridge HUD",
                _ => "UniBridge Panel"
            };
        }

        static string ResolveTemplateTitle(ManageUIParams parameters)
        {
            if (!string.IsNullOrWhiteSpace(parameters.Title))
                return parameters.Title.Trim();
            if (!string.IsNullOrWhiteSpace(parameters.Text))
                return parameters.Text.Trim();
            if (!string.IsNullOrWhiteSpace(parameters.Name))
                return parameters.Name.Trim();

            return parameters.TemplateType switch
            {
                UITemplateType.Modal => "Modal",
                UITemplateType.Toolbar => "Toolbar",
                UITemplateType.List => "List",
                UITemplateType.CardGrid => "Cards",
                UITemplateType.HUD => "HUD",
                _ => "Panel"
            };
        }

        static string[] ResolveTemplateItems(ManageUIParams parameters)
        {
            return parameters.ItemTexts?
                       .Where(text => !string.IsNullOrWhiteSpace(text))
                       .Select(text => text.Trim())
                       .ToArray()
                   ?? Array.Empty<string>();
        }

        static string[] ResolveTemplateActions(ManageUIParams parameters)
        {
            return parameters.ActionTexts?
                       .Where(text => !string.IsNullOrWhiteSpace(text))
                       .Select(text => text.Trim())
                       .ToArray()
                   ?? Array.Empty<string>();
        }

        static bool ShouldUseTemplateTextMeshPro(ManageUIParams parameters)
        {
            return (parameters.UseTextMeshPro ?? true) && GetTextMeshProUGUIType() != null;
        }

        static UIElementType ResolveTemplateTextElementType(ManageUIParams parameters)
        {
            return ShouldUseTemplateTextMeshPro(parameters)
                ? UIElementType.TextMeshProText
                : UIElementType.Text;
        }

        static Vector2 ReadTemplateSize(ManageUIParams parameters, Vector2 fallback)
        {
            return TryReadVector2(parameters.SizeDelta, out var size)
                ? new Vector2(Mathf.Max(1f, size.x), Mathf.Max(1f, size.y))
                : fallback;
        }

        static GameObject CreateTemplateObject(GameObject parent, string name, bool addImage, Color color)
        {
            var obj = new GameObject(name, typeof(RectTransform));
            SetCreatedObjectParent(obj, parent.transform);
            GameObjectUtility.EnsureUniqueNameForSibling(obj);

            if (addImage)
            {
                var image = obj.AddComponent<Image>();
                image.color = color;
                image.raycastTarget = color.a > 0.05f;
            }

            return obj;
        }

        static void SetCenteredTemplateRect(RectTransform rectTransform, Vector2 size)
        {
            ApplyLayoutPreset(rectTransform, RectLayoutHorizontal.Center, RectLayoutVertical.Middle, alsoSetPivot: true, alsoSetPosition: true);
            rectTransform.anchoredPosition = Vector2.zero;
            rectTransform.sizeDelta = size;
        }

        static void SetAnchoredTemplateRect(
            RectTransform rectTransform,
            RectLayoutHorizontal horizontal,
            RectLayoutVertical vertical,
            Vector2 size,
            Vector2 anchoredPosition)
        {
            ApplyLayoutPreset(rectTransform, horizontal, vertical, alsoSetPivot: true, alsoSetPosition: true);
            rectTransform.anchoredPosition = anchoredPosition;
            rectTransform.sizeDelta = size;
        }

        static void ConfigureVerticalTemplateGroup(GameObject target, RectOffset padding, float spacing, TextAnchor alignment)
        {
            var group = target.GetComponent<VerticalLayoutGroup>() ?? target.AddComponent<VerticalLayoutGroup>();
            group.padding = padding;
            group.spacing = spacing;
            group.childAlignment = alignment;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = true;
            group.childForceExpandHeight = false;
        }

        static void ConfigureHorizontalTemplateGroup(
            GameObject target,
            RectOffset padding,
            float spacing,
            TextAnchor alignment,
            bool childControlWidth)
        {
            var group = target.GetComponent<HorizontalLayoutGroup>() ?? target.AddComponent<HorizontalLayoutGroup>();
            group.padding = padding;
            group.spacing = spacing;
            group.childAlignment = alignment;
            group.childControlWidth = childControlWidth;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
        }

        static LayoutElement AddLayoutElement(
            GameObject target,
            float? preferredWidth = null,
            float? preferredHeight = null,
            float? flexibleWidth = null,
            float? flexibleHeight = null,
            float? minWidth = null,
            float? minHeight = null)
        {
            var layoutElement = target.GetComponent<LayoutElement>() ?? target.AddComponent<LayoutElement>();
            if (preferredWidth.HasValue)
                layoutElement.preferredWidth = preferredWidth.Value;
            if (preferredHeight.HasValue)
                layoutElement.preferredHeight = preferredHeight.Value;
            if (flexibleWidth.HasValue)
                layoutElement.flexibleWidth = flexibleWidth.Value;
            if (flexibleHeight.HasValue)
                layoutElement.flexibleHeight = flexibleHeight.Value;
            if (minWidth.HasValue)
                layoutElement.minWidth = minWidth.Value;
            if (minHeight.HasValue)
                layoutElement.minHeight = minHeight.Value;
            return layoutElement;
        }

        static void AddTemplateHeader(
            GameObject parent,
            ManageUIParams parameters,
            Dictionary<string, GameObject> roles,
            string roleName,
            string title,
            string subtitle)
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                var titleObject = CreateTemplateLabel(parent, "Title", title, 28, UITextAlignment.MiddleLeft, ReadColor(parameters.Color, Color.white), 42f, parameters);
                roles[$"{roleName}.title"] = titleObject;
            }

            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                var subtitleObject = CreateTemplateLabel(parent, "Subtitle", subtitle.Trim(), 17, UITextAlignment.MiddleLeft, new Color(0.68f, 0.76f, 0.86f, 1f), 30f, parameters);
                roles[$"{roleName}.subtitle"] = subtitleObject;
            }
        }

        static void AddTemplateActionRow(
            GameObject parent,
            ManageUIParams parameters,
            Dictionary<string, GameObject> roles,
            string[] actions,
            string[] defaultButtons)
        {
            if (actions.Length == 0)
            {
                actions = defaultButtons ?? Array.Empty<string>();
            }

            if (actions.Length == 0)
            {
                return;
            }

            var row = CreateTemplateObject(parent, "Actions", false, Color.clear);
            roles["actions"] = row;
            ConfigureHorizontalTemplateGroup(row, new RectOffset(0, 0, 8, 0), 10f, TextAnchor.MiddleRight, childControlWidth: false);
            AddLayoutElement(row, preferredHeight: 58f, flexibleWidth: 1f);

            foreach (var action in actions)
            {
                CreateTemplateButton(row, action, action, parameters, preferredWidth: 128f, preferredHeight: 46f);
            }
        }

        static GameObject CreateTemplateLabel(
            GameObject parent,
            string name,
            string text,
            int fontSize,
            UITextAlignment alignment,
            Color color,
            float preferredHeight,
            ManageUIParams parameters,
            float? preferredWidth = null)
        {
            var label = new GameObject(name, typeof(RectTransform));
            SetCreatedObjectParent(label, parent.transform);
            GameObjectUtility.EnsureUniqueNameForSibling(label);
            label.GetComponent<RectTransform>().sizeDelta = new Vector2(preferredWidth ?? 0f, preferredHeight);

            var textParameters = parameters with
            {
                Alignment = alignment,
                BestFit = true,
                MinFontSize = parameters.MinFontSize ?? Mathf.Max(10, fontSize - 8),
                MaxFontSize = parameters.MaxFontSize ?? fontSize,
                CreateTmpFontAssetIfMissing = parameters.CreateTmpFontAssetIfMissing ?? true
            };

            if (ShouldUseTemplateTextMeshPro(parameters))
            {
                ConfigureTextMeshProText(label, text, color, fontSize, textParameters);
            }
            else
            {
                ConfigureText(label, text, color, fontSize, ConvertTextAnchor(alignment), textParameters);
            }

            AddLayoutElement(label, preferredWidth: preferredWidth, preferredHeight: preferredHeight, flexibleWidth: preferredWidth.HasValue ? 0f : 1f);
            return label;
        }

        static GameObject CreateTemplateButton(
            GameObject parent,
            string name,
            string text,
            ManageUIParams parameters,
            float preferredWidth,
            float preferredHeight)
        {
            var button = new GameObject(ToSafeObjectName(name, "Button"), typeof(RectTransform));
            SetCreatedObjectParent(button, parent.transform);
            GameObjectUtility.EnsureUniqueNameForSibling(button);
            button.GetComponent<RectTransform>().sizeDelta = new Vector2(preferredWidth, preferredHeight);

            var buttonParameters = parameters with
            {
                Text = text,
                Alignment = UITextAlignment.MiddleCenter,
                FontSize = parameters.FontSize ?? 18,
                BestFit = true,
                MinFontSize = parameters.MinFontSize ?? 11,
                MaxFontSize = parameters.MaxFontSize ?? 18,
                BackgroundColor = new[] { 0.15f, 0.27f, 0.44f, 1f },
                Color = parameters.Color ?? new[] { 1f, 1f, 1f, 1f },
                CreateTmpFontAssetIfMissing = parameters.CreateTmpFontAssetIfMissing ?? true
            };

            if (ShouldUseTemplateTextMeshPro(parameters))
            {
                ConfigureTextMeshProButton(button, buttonParameters);
            }
            else
            {
                ConfigureButton(button, buttonParameters);
            }

            AddLayoutElement(button, preferredWidth: preferredWidth, preferredHeight: preferredHeight);
            return button;
        }

        static void CreateTemplateCard(GameObject parent, string item, ManageUIParams parameters, Vector2 cellSize)
        {
            var parts = item.Split(new[] { '|' }, 2);
            var title = parts[0].Trim();
            var subtitle = parts.Length > 1 ? parts[1].Trim() : null;

            var card = CreateTemplateObject(parent, ToSafeObjectName(title, "Card"), true, new Color(0.11f, 0.145f, 0.19f, 0.92f));
            ConfigureVerticalTemplateGroup(card, new RectOffset(12, 12, 10, 10), 6f, TextAnchor.UpperLeft);
            AddLayoutElement(card, preferredWidth: cellSize.x, preferredHeight: cellSize.y);

            CreateTemplateLabel(card, "Title", title, 18, UITextAlignment.MiddleLeft, ReadColor(parameters.Color, Color.white), 32f, parameters);
            CreateTemplateLabel(
                card,
                "Subtitle",
                string.IsNullOrWhiteSpace(subtitle) ? "Ready" : subtitle,
                14,
                UITextAlignment.UpperLeft,
                new Color(0.68f, 0.77f, 0.88f, 1f),
                Mathf.Max(38f, cellSize.y - 58f),
                parameters);
        }

        static (GameObject scrollView, RectTransform content) CreateTemplateScrollView(
            GameObject parent,
            string name,
            ManageUIParams parameters,
            Dictionary<string, GameObject> roles,
            UILayoutGroupType layoutGroupType,
            UIScrollDirection direction,
            Vector2 itemSize,
            int? columns = null)
        {
            var scrollView = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            SetCreatedObjectParent(scrollView, parent.transform);
            GameObjectUtility.EnsureUniqueNameForSibling(scrollView);
            scrollView.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 320f);
            scrollView.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.035f);
            AddLayoutElement(scrollView, preferredHeight: 320f, flexibleWidth: 1f, flexibleHeight: 1f);

            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            SetCreatedObjectParent(viewport, scrollView.transform);
            ConfigureStretchRect(viewport.GetComponent<RectTransform>());
            viewport.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.02f);

            var content = new GameObject("Content", typeof(RectTransform));
            SetCreatedObjectParent(content, viewport.transform);
            var contentRect = content.GetComponent<RectTransform>();
            ConfigureScrollContentRect(contentRect, direction, new Vector2(520f, 320f));

            var scrollParameters = parameters with
            {
                ScrollDirection = direction,
                LayoutGroupType = layoutGroupType,
                ItemSizeDelta = new[] { itemSize.x, itemSize.y },
                CellSize = layoutGroupType == UILayoutGroupType.Grid ? new[] { itemSize.x, itemSize.y } : parameters.CellSize,
                Constraint = layoutGroupType == UILayoutGroupType.Grid ? UIGridConstraint.FixedColumnCount : parameters.Constraint,
                ConstraintCount = layoutGroupType == UILayoutGroupType.Grid ? Mathf.Max(1, columns ?? parameters.Columns ?? 3) : parameters.ConstraintCount,
                Spacing = parameters.Spacing ?? new[] { 10f, 10f },
                Padding = parameters.Padding ?? new[] { 12f, 12f, 12f, 12f }
            };
            ConfigureScrollContentLayout(contentRect, scrollParameters);
            ConfigureScrollRect(scrollView.GetComponent<ScrollRect>(), viewport.GetComponent<RectTransform>(), contentRect, scrollParameters);

            roles[$"{name}.viewport"] = viewport;
            roles[$"{name}.content"] = content;
            return (scrollView, contentRect);
        }

        static string ToSafeObjectName(string text, string fallback)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }

            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var safe = new string(text.Trim().Select(ch => invalid.Contains(ch) || ch == '/' || ch == '\\' ? '_' : ch).ToArray());
            return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
        }

        static string WrapTemplateText(string text, int maxLineLength)
        {
            if (string.IsNullOrWhiteSpace(text) || maxLineLength <= 8)
            {
                return text;
            }

            var words = text.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var lines = new List<string>();
            var current = string.Empty;
            foreach (var word in words)
            {
                if (current.Length == 0)
                {
                    current = word;
                    continue;
                }

                if (current.Length + 1 + word.Length > maxLineLength)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current += " " + word;
                }
            }

            if (!string.IsNullOrWhiteSpace(current))
            {
                lines.Add(current);
            }

            return string.Join("\n", lines);
        }

        static Font GetBuiltinFont()
        {
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                   ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
        }


    }
}
