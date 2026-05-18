using System;
using SimpleFileBrowser;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace GemmaStage.Lobby
{
    public readonly struct FilePickerFilter
    {
        public readonly string Label;
        public readonly string[] Extensions;

        public FilePickerFilter(string label, params string[] extensions)
        {
            Label = label;
            Extensions = extensions;
        }
    }

    public static class FilePicker
    {
        const float DistanceFromCamera = 0.75f;
        const float WorldScale = 0.0015f;

        static bool _vrConfigured;

        public static void OpenFile(string title, FilePickerFilter[] filters, Action<string> onResult)
        {
            var sfbFilters = new FileBrowser.Filter[filters.Length];
            for (int i = 0; i < filters.Length; i++)
            {
                var exts = filters[i].Extensions;
                var dotted = new string[exts.Length];
                for (int j = 0; j < exts.Length; j++)
                    dotted[j] = "." + exts[j].TrimStart('.');
                sfbFilters[i] = new FileBrowser.Filter(filters[i].Label, dotted);
            }
            FileBrowser.SetFilters(false, sfbFilters);

            ConfigureForVr();
            PositionInFrontOfCamera();

            FileBrowser.ShowLoadDialog(
                onSuccess: paths => onResult(paths != null && paths.Length > 0 ? paths[0] : string.Empty),
                onCancel:  () => onResult(string.Empty),
                pickMode: FileBrowser.PickMode.Files,
                allowMultiSelection: false,
                initialPath: null,
                initialFilename: null,
                title: title,
                loadButtonText: "Select");
        }

        static void ConfigureForVr()
        {
            if (_vrConfigured) return;
            _vrConfigured = true;

            var go = FileBrowser.Instance.gameObject;
            var cam = ResolveCamera();

            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            if (cam != null) canvas.worldCamera = cam;

            // Replace the default GraphicRaycaster with TrackedDeviceGraphicRaycaster so
            // the XR ray interactors can hit the dialog the same way they hit the lobby panels.
            var gr = go.GetComponent<GraphicRaycaster>();
            if (gr != null && go.GetComponent<TrackedDeviceGraphicRaycaster>() == null)
            {
                UnityEngine.Object.Destroy(gr);
                go.AddComponent<TrackedDeviceGraphicRaycaster>();
            }

            go.transform.localScale = Vector3.one * WorldScale;
        }

        static void PositionInFrontOfCamera()
        {
            var cam = ResolveCamera();
            if (cam == null) return;

            var t = FileBrowser.Instance.transform;
            var camT = cam.transform;
            var forward = camT.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
            forward.Normalize();

            t.position = camT.position + forward * DistanceFromCamera;
            t.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }

        static Camera ResolveCamera()
        {
            return Camera.main != null
                ? Camera.main
                : UnityEngine.Object.FindAnyObjectByType<Camera>();
        }
    }
}
