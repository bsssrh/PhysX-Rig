using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

[DisallowMultipleComponent]
public class MultiRigidbodyForceRecorderPlayer : MonoBehaviour
{
    [Header("Bodies to Record / Play (ONLY Rigidbody parts)")]
    public Rigidbody[] bodies;

    [Header("File (saved to Downloads)")]
    public string fileName = "phys_clip.json";
    public bool overwriteFile = true;

    [Header("Playback Source (JSON Asset)")]
    public TextAsset clipAsset;

    [Header("Recording")]
    [Min(1)] public int recordEveryNFixedSteps = 1;

    [Header("Playback")]
    public bool applyRecordedForces = true;
    public bool matchRecordedVelocities = true;
    public bool poseCorrection = true;

    [Range(0f, 1f)]
    public float poseCorrectionStrength = 0.15f;

    public bool disableGravityDuringPlayback = false;

    [Header("Debug")]
    public bool logState = true;

    // ================= DATA =================

    [Serializable]
    public class Clip
    {
        public float fixedDeltaTime;
        public Frame[] frames;
    }

    [Serializable]
    public class Frame
    {
        public float t;
        public BodySample[] samples;
    }

    [Serializable]
    public class BodySample
    {
        public Vector3 pos;
        public Quaternion rot;
        public Vector3 vel;
        public Vector3 angVel;

        public bool hasApplied;
        public Vector3 appliedAccel;
        public Vector3 appliedAngAccel;
    }

    private Clip clip;
    private readonly List<Frame> framesBuffer = new List<Frame>(4096);

    private bool isRecording;
    private bool isPlaying;

    private float recordTime;
    private int fixedStepCounter;

    private float playTime;
    private int playFrameIndex;

    private Vector3 playbackPositionOffset;
    private Quaternion playbackRotationOffset;
    private bool hasPlaybackOffset;

    private Dictionary<Rigidbody, IForceRecordSource> sourceByBody;
    private bool[] prevUseGravity;

    private string FullPath => Path.Combine(GetDownloadsPath(), fileName);

    // ================= INSPECTOR BUTTONS =================

    [ContextMenu("▶ Start Record")]
    public void StartRecord()
    {
        if (bodies == null || bodies.Length == 0)
        {
            Debug.LogError("Recorder: No Rigidbody bodies assigned.");
            return;
        }

        BuildForceSourceMap();

        framesBuffer.Clear();
        recordTime = 0f;
        fixedStepCounter = 0;

        clip = new Clip
        {
            fixedDeltaTime = Time.fixedDeltaTime,
            frames = null
        };

        isRecording = true;
        isPlaying = false;

        if (logState)
            Debug.Log($"[Recorder] RECORD START → {FullPath}");
    }

    [ContextMenu("⏹ Stop & Save Record")]
    public void StopRecordAndSave()
    {
        if (!isRecording) return;

        isRecording = false;
        clip.frames = framesBuffer.ToArray();

        try
        {
            Directory.CreateDirectory(GetDownloadsPath());
            string json = JsonUtility.ToJson(clip, true);

            string path = FullPath;
            if (!overwriteFile && File.Exists(path))
            {
                string stamped =
                    Path.GetFileNameWithoutExtension(fileName) +
                    "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") +
                    Path.GetExtension(fileName);

                path = Path.Combine(GetDownloadsPath(), stamped);
            }

            File.WriteAllText(path, json);

            if (logState)
                Debug.Log($"[Recorder] RECORD SAVED → {path}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Recorder] Save failed: {e}");
        }
    }

    [ContextMenu("▶▶ Play From File")]
    public void StartPlayFromFile()
    {
        if (!clipAsset)
        {
            Debug.LogError("[Recorder] No JSON clip assigned. Set Clip Asset in the инспектор.");
            return;
        }

        try
        {
            string json = clipAsset.text;
            clip = JsonUtility.FromJson<Clip>(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Recorder] Load failed: {e}");
            return;
        }

        if (clip == null || clip.frames == null || clip.frames.Length == 0)
        {
            Debug.LogError("[Recorder] Clip is empty.");
            return;
        }

        if (bodies == null || bodies.Length == 0)
        {
            Debug.LogError("[Recorder] No Rigidbody bodies assigned.");
            return;
        }

        if (clip.frames[0].samples == null || clip.frames[0].samples.Length != bodies.Length)
        {
            Debug.LogError("[Recorder] Clip sample count does not match bodies.");
            return;
        }

        BuildForceSourceMap();

        hasPlaybackOffset = false;
        Frame startFrame = clip.frames[0];
        for (int i = 0; i < bodies.Length; i++)
        {
            Rigidbody body = bodies[i];
            if (!body) continue;

            BodySample startSample = startFrame.samples[i];
            playbackRotationOffset = body.rotation * Quaternion.Inverse(startSample.rot);
            playbackPositionOffset = body.position - (playbackRotationOffset * startSample.pos);
            hasPlaybackOffset = true;
            break;
        }

        if (!hasPlaybackOffset)
        {
            Debug.LogError("[Recorder] No valid Rigidbody bodies found for playback offset.");
            return;
        }

        playTime = 0f;
        playFrameIndex = 0;

        isPlaying = true;
        isRecording = false;

        prevUseGravity = new bool[bodies.Length];
        if (disableGravityDuringPlayback)
        {
            for (int i = 0; i < bodies.Length; i++)
            {
                if (!bodies[i]) continue;
                prevUseGravity[i] = bodies[i].useGravity;
                bodies[i].useGravity = false;
            }
        }

        if (logState)
            Debug.Log("[Recorder] PLAY START → JSON Clip Asset");
    }

    [ContextMenu("⏹ Stop Playback")]
    public void StopPlay()
    {
        if (!isPlaying) return;

        isPlaying = false;

        if (disableGravityDuringPlayback && prevUseGravity != null)
        {
            for (int i = 0; i < bodies.Length; i++)
            {
                if (!bodies[i]) continue;
                bodies[i].useGravity = prevUseGravity[i];
            }
        }

        if (logState)
            Debug.Log("[Recorder] PLAY STOP");
    }

    // ================= LOOP =================

    private void FixedUpdate()
    {
        if (isRecording)
        {
            fixedStepCounter++;
            if (fixedStepCounter % recordEveryNFixedSteps == 0)
                RecordTick(Time.fixedDeltaTime);
        }

        if (isPlaying)
            PlayTick(Time.fixedDeltaTime);
    }

    // ================= RECORD =================

    private void RecordTick(float dt)
    {
        recordTime += dt;

        var f = new Frame
        {
            t = recordTime,
            samples = new BodySample[bodies.Length]
        };

        for (int i = 0; i < bodies.Length; i++)
        {
            Rigidbody b = bodies[i];
            if (!b)
            {
                f.samples[i] = new BodySample();
                continue;
            }

            var s = new BodySample
            {
                pos = b.position,
                rot = b.rotation,
                vel = b.velocity,
                angVel = b.angularVelocity,
                hasApplied = false
            };

            if (sourceByBody != null && sourceByBody.TryGetValue(b, out var src))
            {
                s.appliedAccel = src.LastAppliedAccel;
                s.appliedAngAccel = src.LastAppliedAngAccel;
                s.hasApplied = true;
            }

            f.samples[i] = s;
        }

        framesBuffer.Add(f);
    }

    // ================= PLAY =================

    private void PlayTick(float dt)
    {
        playTime += dt;

        while (playFrameIndex < clip.frames.Length - 2 &&
               clip.frames[playFrameIndex + 1].t <= playTime)
            playFrameIndex++;

        Frame a = clip.frames[playFrameIndex];
        Frame b = clip.frames[Mathf.Min(playFrameIndex + 1, clip.frames.Length - 1)];

        float span = Mathf.Max(0.000001f, b.t - a.t);
        float t01 = Mathf.Clamp01((playTime - a.t) / span);

        for (int i = 0; i < bodies.Length; i++)
        {
            Rigidbody body = bodies[i];
            if (!body) continue;

            BodySample sa = a.samples[i];
            BodySample sb = b.samples[i];

            Vector3 pos = Vector3.Lerp(sa.pos, sb.pos, t01);
            Quaternion rot = Quaternion.Slerp(sa.rot, sb.rot, t01);
            Vector3 vel = Vector3.Lerp(sa.vel, sb.vel, t01);
            Vector3 angVel = Vector3.Lerp(sa.angVel, sb.angVel, t01);

            if (hasPlaybackOffset)
            {
                pos = playbackPositionOffset + (playbackRotationOffset * pos);
                rot = playbackRotationOffset * rot;
            }

            if (applyRecordedForces && sa.hasApplied && sb.hasApplied)
            {
                body.AddForce(Vector3.Lerp(sa.appliedAccel, sb.appliedAccel, t01),
                              ForceMode.Acceleration);

                body.AddTorque(Vector3.Lerp(sa.appliedAngAccel, sb.appliedAngAccel, t01),
                               ForceMode.Acceleration);
            }

            if (matchRecordedVelocities)
            {
                body.velocity = Vector3.Lerp(body.velocity, vel, 0.35f);
                body.angularVelocity = Vector3.Lerp(body.angularVelocity, angVel, 0.35f);
            }

            if (poseCorrection)
            {
                body.MovePosition(Vector3.Lerp(body.position, pos, poseCorrectionStrength));
                body.MoveRotation(Quaternion.Slerp(body.rotation, rot, poseCorrectionStrength));
            }
        }

        if (playTime >= clip.frames[^1].t)
        {
            playTime = 0f;
            playFrameIndex = 0;
        }
    }

    // ================= HELPERS =================

    private void BuildForceSourceMap()
    {
        sourceByBody = new Dictionary<Rigidbody, IForceRecordSource>(64);

        var behaviours = FindObjectsOfType<MonoBehaviour>(true);
        foreach (var b in behaviours)
        {
            if (b is IForceRecordSource src && src.Body)
            {
                if (!sourceByBody.ContainsKey(src.Body))
                    sourceByBody.Add(src.Body, src);
            }
        }
    }

    private static string GetDownloadsPath()
    {
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);

            return Path.Combine(home, "Downloads");
        }
        catch
        {
            return Application.persistentDataPath;
        }
    }
}
