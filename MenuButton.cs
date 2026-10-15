using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Apocasetter
{
    public static partial class GameMenu
    {
        private static readonly string[] TemplateNames = { "Settings", "Credits", "Tutorial", "Codex", "Quit", "Quit_To_Menu", "Exit", "Options" };
        private class Slot
        {
            public Canvas Canvas;
            public GameObject Button;
            public RectTransform Rect;
            public Button Native;
            public Button[] GameButtons;
            public string LastError;
            public float RetryAt;
        }

        private static readonly List<Slot> _slots = new List<Slot>();
        private static readonly List<Rect> _visibleRects = new List<Rect>();
        private static readonly Vector3[] _corners = new Vector3[4];
        private static float _nextScan;
        private static int _rectFrame = -1, _clickFrame = -1;
        private static Action _onClick;
        private static Slot _fallback;

        public static bool HasButton
        {
            get
            {
                foreach (var slot in _slots) if (slot.Button != null) return true;
                return _fallback != null && _fallback.Button != null;
            }
        }

        private static bool IsTemplate(Button button)
        {
            if (button == null || button.name == "Apocasetter_Mods") return false;
            foreach (var name in TemplateNames)
                if (string.Equals(name, button.name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool MenuButtonVisible(Button button)
        {
            return IsTemplate(button) && button.isActiveAndEnabled && button.IsInteractable()
                && button.targetGraphic != null && button.targetGraphic.canvasRenderer.GetInheritedAlpha() > 0.01f;
        }

        // Discovery is throttled. Once discovered, checking the few cached menu buttons has no scene searches
        // or array allocations. Scene changes force discovery immediately; destroyed buttons are rebuilt.
        public static void Tick(Action onClick)
        {
            _onClick = onClick;
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 2f;
                _slots.RemoveAll(slot => slot.Canvas == null);
                foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
                {
                    // These are the two vanilla menu roots. Do not inject into another mod's Settings canvas.
                    if (canvas.name != "Canvas_MM" && canvas.name != "MainMenu_Canvas") continue;
                    Slot slot = null;
                    foreach (var candidate in _slots) if (candidate.Canvas == canvas) { slot = candidate; break; }
                    if (slot == null) { slot = new Slot { Canvas = canvas }; _slots.Add(slot); }
                    slot.GameButtons = canvas.GetComponentsInChildren<Button>(true);
                }
            }

            bool anyVisible = false;
            foreach (var slot in _slots)
            {
                var canvas = slot.Canvas;
                if (canvas == null) continue;
                Button template = null;
                if (slot.GameButtons != null)
                    foreach (var button in slot.GameButtons)
                        if (MenuButtonVisible(button)) { template = button; if (button.name == "Settings") break; }
                bool visible = canvas.isActiveAndEnabled && canvas.transform.lossyScale.x > 0.0001f && template != null;
                if (slot.Button != null && (slot.Native == null || slot.Rect == null || slot.Native.targetGraphic == null))
                { slot.Button.SetActive(false); UnityEngine.Object.Destroy(slot.Button); slot.Button = null; }
                if (visible && slot.Button == null && Time.unscaledTime >= slot.RetryAt) Inject(slot, template);
                SetVisible(slot, visible && !InputBlocker.Active);
                Rect rect;
                if (visible && TryScreenRect(slot, out rect)) anyVisible = true;
            }

            // A native fallback also works when a menu replacement removes the vanilla button templates.
            bool fallbackVisible = !anyVisible && !InputBlocker.Active && (MenuVisible || (!InGame && Cursor.visible));
            if (fallbackVisible && (_fallback == null || _fallback.Canvas == null)) CreateFallback();
            if (_fallback != null)
            {
                if (_fallback.Canvas != null && _fallback.Button == null && Time.unscaledTime >= _fallback.RetryAt) Inject(_fallback, null);
                SetVisible(_fallback, fallbackVisible);
            }
            _rectFrame = -1;
            PollClick();
        }

        private static void SetVisible(Slot slot, bool visible)
        {
            if (slot.Button == null) return;
            if (slot.Button.activeSelf != visible) slot.Button.SetActive(visible);
            if (visible)
            {
                // Recover if another mod disabled the button component.
                if (!slot.Native.enabled) slot.Native.enabled = true;
                if (!slot.Native.interactable) slot.Native.interactable = true;
            }
        }

        private static void CreateFallback()
        {
            var root = new GameObject("Apocasetter.MenuFallback", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster)) { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(root);
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32760;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;
            _fallback = new Slot { Canvas = canvas };
            Inject(_fallback, null);
        }

        // Copy visual properties only. Instantiating a live game button can run its Awake/OnEnable FSMs
        // before they can be stripped, and can copy persistent onClick listeners or confirmation dialogs.
        private static void Inject(Slot slot, Button template)
        {
            GameObject go = null;
            try
            {
                go = new GameObject("Apocasetter_Mods", typeof(RectTransform));
                go.SetActive(false);
                go.transform.SetParent(slot.Canvas.transform, false);
                go.layer = slot.Canvas.gameObject.layer;
                // Isolate rendering order without fighting other mods over last-sibling position every frame.
                var overlay = go.AddComponent<Canvas>();
                overlay.overrideSorting = true; overlay.sortingOrder = 32760;
                go.AddComponent<GraphicRaycaster>();
                var image = go.AddComponent<Image>();
                var sourceImage = template != null ? template.GetComponent<Image>() : null;
                image.sprite = sourceImage != null ? sourceImage.sprite : null;
                image.type = sourceImage != null ? sourceImage.type : Image.Type.Simple;
                image.color = sourceImage != null ? sourceImage.color : new Color(0.2f, 0.18f, 0.15f, 1);
                image.fillCenter = sourceImage == null || sourceImage.fillCenter;
                // The rusty button face is a separate RawImage child; the root Image is just its border.
                if (template != null)
                    foreach (Transform child in template.transform)
                    {
                        var source = child.GetComponent<RawImage>();
                        if (source == null) continue;
                        var background = new GameObject(child.name, typeof(RectTransform), typeof(RawImage));
                        background.layer = go.layer;
                        background.transform.SetParent(go.transform, false);
                        var from = (RectTransform)child;
                        var to = background.GetComponent<RectTransform>();
                        to.anchorMin = from.anchorMin; to.anchorMax = from.anchorMax; to.pivot = from.pivot;
                        to.sizeDelta = from.sizeDelta; to.anchoredPosition3D = from.anchoredPosition3D;
                        to.localRotation = from.localRotation; to.localScale = from.localScale;
                        var graphic = background.GetComponent<RawImage>();
                        graphic.texture = source.texture; graphic.uvRect = source.uvRect;
                        graphic.color = source.color; graphic.material = source.material;
                        graphic.raycastTarget = false; graphic.enabled = source.enabled;
                        background.SetActive(child.gameObject.activeSelf);
                    }
                var button = go.AddComponent<Button>();
                button.targetGraphic = image;
                if (template != null) { button.colors = template.colors; button.spriteState = template.spriteState; }
                // No copied animation controller: ColorTint works without one.
                button.transition = Selectable.Transition.ColorTint;
                button.navigation = new Navigation { mode = Navigation.Mode.None };
                button.onClick.AddListener(() => { if (!InputBlocker.Active && _onClick != null) _onClick(); });

                var label = new GameObject("Text", typeof(RectTransform));
                label.transform.SetParent(go.transform, false);
                label.layer = go.layer;
                var labelRect = label.GetComponent<RectTransform>();
                labelRect.anchorMin = Vector2.zero; labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
                var tmp = template != null ? template.GetComponentInChildren<TMPro.TMP_Text>(true) : null;
                var text = template != null ? template.GetComponentInChildren<Text>(true) : null;
                // A TMP sourceFontFile can be a raw, uninstalled TTF. TMP renders it correctly, but
                // Unity's legacy/IMGUI text can draw nothing with it (ApocaLanguage overlay fonts).
                // Keep the game's legacy font, or GameSkin's system fallback for TMP-only templates.
                if (text != null && text.font != null) GameSkin.SetGameFont(text.font);
                if (tmp != null && tmp.font != null)
                {
                    var target = label.AddComponent<TMPro.TextMeshProUGUI>();
                    target.font = tmp.font; target.fontSize = tmp.fontSize; target.fontStyle = tmp.fontStyle;
                    target.color = tmp.color; target.alignment = TMPro.TextAlignmentOptions.Center;
                    target.text = "MODS"; target.raycastTarget = false;
                }
                else
                {
                    var target = label.AddComponent<Text>();
                    target.font = text != null && text.font != null ? text.font : Resources.GetBuiltinResource<Font>("Arial.ttf");
                    target.fontSize = text != null ? text.fontSize : 26;
                    target.fontStyle = text != null ? text.fontStyle : FontStyle.Bold;
                    target.color = text != null ? text.color : Color.white;
                    target.alignment = TextAnchor.MiddleCenter;
                    target.text = "MODS"; target.raycastTarget = false;
                }

                var rt = go.GetComponent<RectTransform>();
                var sourceRect = template != null ? template.GetComponent<RectTransform>() : null;
                var size = sourceRect != null ? sourceRect.rect.size : new Vector2(160, 46);
                if (size.x < 10 || size.y < 10) size = new Vector2(200, 50);
                rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.one;
                rt.sizeDelta = size; rt.anchoredPosition = new Vector2(-30, -30);
                go.AddComponent<LayoutElement>().ignoreLayout = true;
                go.transform.SetAsLastSibling();
                slot.Button = go; slot.Rect = rt; slot.Native = button; slot.LastError = null;
                Plugin.Verbose("MODS button built on " + slot.Canvas.name);
            }
            catch (Exception e)
            {
                if (go != null) UnityEngine.Object.Destroy(go);
                slot.Button = null; slot.RetryAt = Time.unscaledTime + 5f;
                if (slot.LastError != e.Message)
                { slot.LastError = e.Message; Plugin.Log.LogWarning("MODS button on " + slot.Canvas.name + ": " + e.Message); }
            }
        }

        public static void PollClick()
        {
            if (InputBlocker.Active || _onClick == null || !Application.isFocused || _clickFrame == Time.frameCount) return;
            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame && OpenAt(mouse.position.ReadValue())) return;
            if (Input.GetMouseButtonDown(0) && OpenAt(Input.mousePosition)) return;
            // Virtual pointers and other mods can change Mouse.current after a physical mouse event.
            foreach (var device in InputSystem.devices)
            {
                var other = device as Mouse;
                if (other != null && other != mouse && other.leftButton.wasPressedThisFrame && OpenAt(other.position.ReadValue())) return;
            }
        }

        private static bool OpenAt(Vector2 point)
        {
            point.y = Screen.height - point.y;
            foreach (var rect in VisibleButtonRects())
            {
                if (!rect.Contains(point)) continue;
                _clickFrame = Time.frameCount;
                _onClick();
                return true;
            }
            return false;
        }

        // The returned list is reused and read-only to callers; repeated queries within a frame use its cache.
        public static List<Rect> VisibleButtonRects()
        {
            if (_rectFrame == Time.frameCount) return _visibleRects;
            _rectFrame = Time.frameCount;
            _visibleRects.Clear();
            foreach (var slot in _slots) AddVisibleRect(slot);
            if (_fallback != null) AddVisibleRect(_fallback);
            return _visibleRects;
        }

        public static bool ButtonOnScreen { get { return VisibleButtonRects().Count > 0; } }

        private static void AddVisibleRect(Slot slot)
        {
            Rect rect;
            if (TryScreenRect(slot, out rect)) _visibleRects.Add(rect);
        }

        private static bool TryScreenRect(Slot slot, out Rect rect)
        {
            rect = default(Rect);
            if (slot.Button == null || !slot.Button.activeInHierarchy || slot.Canvas == null || !slot.Canvas.isActiveAndEnabled
                || slot.Native == null || slot.Native.targetGraphic == null || !slot.Native.targetGraphic.isActiveAndEnabled
                || slot.Native.targetGraphic.canvasRenderer.GetInheritedAlpha() <= 0.01f) return false;
            slot.Rect.GetWorldCorners(_corners);
            var cam = slot.Canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : slot.Canvas.worldCamera;
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var corner in _corners)
            {
                Vector2 point = cam != null ? (Vector2)cam.WorldToScreenPoint(corner) : (Vector2)corner;
                min = Vector2.Min(min, point); max = Vector2.Max(max, point);
            }
            if (max.x <= 0 || max.y <= 0 || min.x >= Screen.width || min.y >= Screen.height || max.x - min.x < 2 || max.y - min.y < 2) return false;
            rect = new Rect(min.x, Screen.height - max.y, max.x - min.x, max.y - min.y);
            return true;
        }
    }
}
