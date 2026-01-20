using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(MultiRigidbodyForceRecorderPlayer))]
public class MultiRigidbodyForceRecorderPlayerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        // Рисуем стандартный инспектор
        DrawDefaultInspector();

        GUILayout.Space(10);
        GUILayout.Label("Recorder Controls", EditorStyles.boldLabel);

        MultiRigidbodyForceRecorderPlayer recorder =
            (MultiRigidbodyForceRecorderPlayer)target;

        GUI.enabled = Application.isPlaying;

        if (GUILayout.Button("▶ Start Record", GUILayout.Height(30)))
        {
            recorder.StartRecord();
        }

        if (GUILayout.Button("⏹ Stop & Save Record", GUILayout.Height(30)))
        {
            recorder.StopRecordAndSave();
        }

        GUILayout.Space(5);

        if (GUILayout.Button("▶▶ Play From File", GUILayout.Height(30)))
        {
            recorder.StartPlayFromFile();
        }

        if (GUILayout.Button("⏹ Stop Playback", GUILayout.Height(30)))
        {
            recorder.StopPlay();
        }

        GUI.enabled = true;

        if (!Application.isPlaying)
        {
            GUILayout.Space(5);
            EditorGUILayout.HelpBox(
                "Buttons are enabled only in Play Mode.",
                MessageType.Info
            );
        }
    }
}
