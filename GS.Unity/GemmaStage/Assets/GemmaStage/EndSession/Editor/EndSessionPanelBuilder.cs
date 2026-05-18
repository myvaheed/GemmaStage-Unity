using System.IO;
using GemmaStage.EndSession;
using GemmaStage.LiveQA;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace GemmaStage.EndSession.Editor
{
    // Run once after opening the GemmaStage-Unity-End project in the Unity Editor:
    //   GemmaStage menu → Build EndSession Panel Prefab
    //
    // The resulting prefab is saved to
    //   Assets/GemmaStage/EndSession/UI/Prefabs/EndSessionPanel.prefab
    // It mirrors the LiveQaPanel structure with State_Answered / countdown stripped
    // and LiveQaPanelView replaced by EndSessionPanelView.
    public static class EndSessionPanelBuilder
    {
        const string SrcPrefabPath = "Assets/GemmaStage/LiveQA/UI/Prefabs/LiveQaPanel.prefab";
        const string DstPrefabPath = "Assets/GemmaStage/EndSession/UI/Prefabs/EndSessionPanel.prefab";

        [MenuItem("GemmaStage/Build EndSession Panel Prefab")]
        public static void Build()
        {
            if (!File.Exists(SrcPrefabPath))
            {
                Debug.LogError($"[EndSessionPanelBuilder] Source prefab not found: {SrcPrefabPath}");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(DstPrefabPath));

            var root = PrefabUtility.LoadPrefabContents(SrcPrefabPath);
            try
            {
                root.name = "EndSessionPanel";

                // State_Ask → Content (keep header + button row; strip countdown)
                var stateAsk = root.transform.Find("State_Ask");
                if (stateAsk == null) { Debug.LogError("[EndSessionPanelBuilder] State_Ask not found."); return; }
                stateAsk.gameObject.name = "Content";

                var countdown = stateAsk.Find("CountdownText");
                if (countdown != null) Object.DestroyImmediate(countdown.gameObject);

                var buttonRow = stateAsk.Find("ButtonRow_Ask");
                if (buttonRow != null) buttonRow.gameObject.name = "ButtonRow";

                // Remove Answered state
                var stateAnswered = root.transform.Find("State_Answered");
                if (stateAnswered != null) Object.DestroyImmediate(stateAnswered.gameObject);

                // Update header text
                var header = stateAsk.Find("HeaderText")?.GetComponent<TMP_Text>();
                if (header != null) header.text = "End Session?";

                // Find buttons
                var noButton  = buttonRow?.Find("Button_No")?.GetComponent<Button>();
                var yesButton = buttonRow?.Find("Button_Yes")?.GetComponent<Button>();
                if (noButton == null || yesButton == null)
                {
                    Debug.LogError("[EndSessionPanelBuilder] Yes/No buttons not found.");
                    return;
                }

                // Swap LiveQaPanelView for EndSessionPanelView
                var oldView = root.GetComponent<LiveQaPanelView>();
                if (oldView != null) Object.DestroyImmediate(oldView);

                var view = root.AddComponent<EndSessionPanelView>();
                var cg   = root.GetComponent<CanvasGroup>();

                var so = new SerializedObject(view);
                so.FindProperty("yesButton").objectReferenceValue  = yesButton;
                so.FindProperty("noButton").objectReferenceValue   = noButton;
                so.FindProperty("canvasGroup").objectReferenceValue = cg;
                so.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, DstPrefabPath, out bool ok);
                if (ok) Debug.Log($"[EndSessionPanelBuilder] Saved: {DstPrefabPath}");
                else    Debug.LogError("[EndSessionPanelBuilder] SaveAsPrefabAsset failed.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
    }
}
