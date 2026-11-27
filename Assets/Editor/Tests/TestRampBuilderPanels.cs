// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TiltBrush
{
    public class TestRampBuilderPanels
    {
        [Test]
        public void QuestRegistersOneToolsPanelPerModeAndLeavesRemovedPanelsDisabled()
        {
            const string path = "Assets/Scenes/Main.unity";
            Scene scene = SceneManager.GetSceneByPath(path);
            bool opened = !scene.isLoaded;
            if (opened)
                scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);

            try
            {
                var manager = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<PanelManager>(true)).Single();
                SerializedProperty map = new SerializedObject(manager).FindProperty("m_PanelMap");
                var registered = Enumerable.Range(0, map.arraySize)
                    .Select(map.GetArrayElementAtIndex)
                    .Where(entry => entry.FindPropertyRelative("m_ModeQuest").boolValue)
                    .Select(entry => (Entry: entry, Prefab:
                        (GameObject)entry.FindPropertyRelative("m_PanelPrefab").objectReferenceValue))
                    .Where(item => item.Prefab != null)
                    .ToDictionary(item => System.IO.Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(item.Prefab)),
                        item => item.Entry);

                Assert.That(registered.Keys, Does.Contain("ToolsPanel").And.Contain("AdvancedToolsPanel"));
                Assert.That(registered["ToolsPanel"].FindPropertyRelative("m_Basic").boolValue, Is.True);
                Assert.That(registered["ToolsPanel"].FindPropertyRelative("m_Advanced").boolValue, Is.False);
                Assert.That(registered["AdvancedToolsPanel"].FindPropertyRelative("m_Basic").boolValue, Is.False);
                Assert.That(registered["AdvancedToolsPanel"].FindPropertyRelative("m_Advanced").boolValue, Is.True);

                foreach (string retired in new[] { "ExtraPanel", "BackdropPanel", "ReferencePanel", "IcosaPanel",
                    "CameraPanel", "CameraPathPanel", "LayersPanel" })
                    Assert.That(registered.Keys, Does.Not.Contain(retired));
                foreach (string retained in new[] { "LightsPanel", "GuideToolsPanel", "SnapSettingsPanel",
                    "SplinePanel", "TransitionSettingsPanel", "ModularCopingPanel", "BrushesPanel_Mobile",
                    "SketchbookPanel_Mobile" })
                    Assert.That(registered.Keys, Does.Contain(retained));
            }
            finally
            {
                if (opened)
                    EditorSceneManager.CloseScene(scene, removeScene: true);
            }
        }

        [TestCase("ColorPickerPanel", 0f)]
        [TestCase("BrushesPanel_Mobile", 120f)]
        [TestCase("ToolsPanel", 240f)]
        [TestCase("AdvancedToolsPanel", 240f)]
        public void QuestCarouselResetsDockedMainPanelsToTheSameHeight(string prefabName, float expectedAngle)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Prefabs/Panels/{prefabName}.prefab");
            GameObject instance = Object.Instantiate(prefab);
            try
            {
                var panel = instance.GetComponent<BasePanel>();
                InitializeWandAttachment(panel);
                panel.m_Fixed = true;
                panel.m_WandAttachYOffset = panel.m_WandAttachYOffset_Target = panel.m_WandAttachYOffset_Stable = 1.4f;
                panel.m_WandAttachAngle = expectedAngle + 120f;

                PanelManager.AlignMobileCarouselPanel(panel);

                Assert.That(panel.m_WandAttachYOffset, Is.Zero);
                Assert.That(panel.m_WandAttachYOffset_Target, Is.Zero);
                Assert.That(panel.m_WandAttachYOffset_Stable, Is.Zero);
                Assert.That(panel.m_WandAttachAngle, Is.EqualTo(expectedAngle));
                panel.m_WandAttachYOffset = 1f;
                panel.ResetPanelToInitialPosition();
                Assert.That(panel.m_WandAttachYOffset, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void CurveEditKeepsClearanceAboveBrushesWhenDockOffsetsAreRestored()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Panels/SplinePanel.prefab");
            var brushes = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Panels/BrushesPanel_Mobile.prefab")
                .GetComponent<BasePanel>();
            GameObject instance = Object.Instantiate(prefab);
            try
            {
                var curve = instance.GetComponent<BasePanel>();
                InitializeWandAttachment(curve);
                Assert.That(curve.m_WandAttachAngle, Is.EqualTo(brushes.m_WandAttachAngle));
                Assert.That(curve.m_WandAttachYOffset_Stable, Is.EqualTo(curve.m_WandAttachYOffset));
                float gap = curve.m_WandAttachYOffset_Stable - brushes.m_WandAttachYOffset -
                    HighlightedHalfHeight(curve) - HighlightedHalfHeight(brushes);
                Assert.That(gap, Is.GreaterThan(0.1f));
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        private static void InitializeWandAttachment(BasePanel panel) =>
            typeof(BasePanel).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(panel, null);

        private static float HighlightedHalfHeight(BasePanel panel)
        {
            var properties = new SerializedObject(panel);
            var collider = (BoxCollider)properties.FindProperty("m_Collider").objectReferenceValue;
            float scale = properties.FindProperty("m_GazeHighlightScaleMultiplier").floatValue;
            return Mathf.Max(panel.m_WandAttachHalfHeight, collider.size.y * 0.5f) * scale;
        }

        [Test]
        public void AdvancedToolsGridAlignsNativeAndNestedIcons()
        {
            var panel = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Prefabs/Panels/AdvancedToolsPanel.prefab");
            var buttons = panel.GetComponentsInChildren<BaseButton>(true);
            var grid = new[]
            {
                "Button_Draw", "Button_Eraser", "Button_Dropper", "Button_Repaint",
                "Button_Selection", "Button_Snip", "Button_Join", "TransformPanel",
                "Guides", "SnapSettings", "Lights", "PluginsScripts",
            };
            const float spacing = 0.44f;
            var iconParent = buttons.Single(button => button.name == "Button_Eraser").transform.parent;
            for (int slot = 0; slot < grid.Length; ++slot)
            {
                string name = grid[slot];
                var button = buttons.Single(item => item.name == name);
                Assert.That(button.gameObject.activeSelf, Is.True, name);
                Assert.That(button.transform.parent, Is.EqualTo(iconParent), name);
                var expected = new Vector3(-0.66f + spacing * (slot % 4), spacing * (1 - slot / 4), 0.05f);
                Assert.That(Vector3.Distance(button.transform.localPosition, expected), Is.LessThan(0.0001f), name);
                var collider = button.GetComponentInChildren<BoxCollider>();
                Assert.That(collider, Is.Not.Null, name);
                var hitSize = Vector3.Scale(collider.size, button.transform.localScale);
                Assert.That(hitSize.x, Is.LessThan(spacing), name);
                Assert.That(hitSize.y, Is.LessThan(spacing), name);
            }
        }

        [TestCase("ToolsPanel", -0.22f, 0f)]
        [TestCase("AdvancedToolsPanel", -0.66f, 0.44f)]
        public void ToolsDrawButtonStartsDrawing(string prefabName, float x, float y)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Prefabs/Panels/{prefabName}.prefab");
            var panel = prefab.GetComponent<RampToolsPanel>();
            Assert.That(panel, Is.Not.Null);
            var button = prefab.GetComponentsInChildren<TextActionButton>(true).Single(item => item.name == "Button_Draw");
            var eraser = prefab.GetComponentsInChildren<BaseButton>(true).Single(item => item.name == "Button_Eraser");
            Assert.That(button.gameObject.activeSelf, Is.True);
            Assert.That(button.enabled, Is.True);
            Assert.That(button.m_ButtonLabel, Is.EqualTo("Draw"));
            Assert.That(button.transform.parent, Is.EqualTo(eraser.transform.parent));
            Assert.That(Vector3.Distance(button.transform.localPosition, new Vector3(x, y, 0.05f)), Is.LessThan(0.0001f));

            var serializedButton = new SerializedObject(button);
            var calls = serializedButton.FindProperty("m_Action.m_PersistentCalls.m_Calls");
            Assert.That(calls.arraySize, Is.EqualTo(1));
            var call = calls.GetArrayElementAtIndex(0);
            Assert.That(call.FindPropertyRelative("m_Target").objectReferenceValue, Is.EqualTo(panel));
            Assert.That(call.FindPropertyRelative("m_MethodName").stringValue, Is.EqualTo(nameof(RampToolsPanel.StartDrawing)));
            Assert.That(call.FindPropertyRelative("m_CallState").intValue, Is.EqualTo((int)UnityEngine.Events.UnityEventCallState.RuntimeOnly));
        }

        [Test]
        public void SnapGridAndAngleButtonsAlignWithTheirAxisToggles()
        {
            var panel = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Panels/SnapSettingsPanel.prefab");
            var toggles = panel.GetComponentsInChildren<SnapSettingsPanelToggleButton>(true)
                .Where(button => button.name.StartsWith("Lock Snap ", System.StringComparison.Ordinal)).ToArray();
            var positions = new[] { SnapSettingsPanelToggleType.LockSnapTranslateX,
                SnapSettingsPanelToggleType.LockSnapTranslateY, SnapSettingsPanelToggleType.LockSnapTranslateZ };
            var rotations = new[] { SnapSettingsPanelToggleType.LockSnapAngleX,
                SnapSettingsPanelToggleType.LockSnapAngleY, SnapSettingsPanelToggleType.LockSnapAngleZ };

            void CheckRow(string selectorName, SnapSettingsPanelToggleType[] axisTypes)
            {
                var selector = panel.GetComponentsInChildren<Transform>(true).Single(item => item.name == selectorName);
                var start = panel.transform.InverseTransformPoint(selector.position);
                var axisButtons = axisTypes.Select(type => toggles.Single(button => button.m_ButtonType == type)).ToArray();
                var x = axisButtons.Select(button => panel.transform.InverseTransformPoint(button.transform.position).x).ToArray();
                float spacing = x[0] - start.x;
                Assert.That(spacing, Is.GreaterThan(selector.lossyScale.x));
                for (int i = 0; i < axisButtons.Length; i++)
                {
                    var axis = axisButtons[i].transform;
                    var center = panel.transform.InverseTransformPoint(axis.position);
                    Assert.That(center, Is.EqualTo(start + Vector3.right * (spacing * (i + 1)))
                        .Using(UnityEngine.TestTools.Utils.Vector3ComparerWithEqualsOperator.Instance));
                    Assert.That(axis.lossyScale, Is.EqualTo(selector.lossyScale)
                        .Using(UnityEngine.TestTools.Utils.Vector3ComparerWithEqualsOperator.Instance));
                }
            }

            CheckRow("ChangeSnapGrid", positions);
            CheckRow("ChangeSnapAngle", rotations);
        }

        [Test]
        public void RoomCaptureIsWiredToLighting()
        {
            GameObject lighting = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Panels/LightsPanel.prefab");
            GameObject backdrop = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Panels/BackdropPanel.prefab");
            Assert.That(lighting, Is.Not.Null);
            Assert.That(backdrop, Is.Not.Null);
            Assert.That(backdrop.GetComponentInChildren<RoomCaptureControls>(true), Is.Null);

            var capture = lighting.GetComponentsInChildren<RoomCaptureControls>(true).Single();
            Assert.That(capture.gameObject.activeSelf, Is.True);
            var controls = new SerializedObject(capture);
            foreach (string field in new[] { "m_CaptureButton", "m_ClearCaptureButton", "m_CaptureStatus" })
            {
                var component = controls.FindProperty(field).objectReferenceValue as Component;
                Assert.That(component, Is.Not.Null, field);
                Assert.That(component.transform.IsChildOf(lighting.transform), Is.True, field);
            }
        }
    }
}
