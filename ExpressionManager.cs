using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class ExpressionManager : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("SkinnedMeshRenderer that contains the blendshapes for this character.")]
    public SkinnedMeshRenderer skinnedMeshRenderer;

    [Header("Global Defaults")]
    [Tooltip("Default duration for tweens if not specified.")]
    [Min(0f)] public float defaultDuration = 0.35f;

    [Tooltip("Default easing curve for tweens/crossfades.")]
    public AnimationCurve defaultEase = AnimationCurve.EaseInOut(0, 0, 1, 1);

    [Tooltip("Use unscaled time for tweens (ignore Time.timeScale).")]
    public bool useUnscaledTime = false;

    [Tooltip("Clamp all target values to each channel's [min,max].")]
    public bool clampToChannelRange = true;

    [Serializable]
    public class BlendshapeChannel
    {
        [Tooltip("Friendly name you will use to address this channel (e.g. 'Smile' or 'BlinkL').")]
        public string channelName;

        [Tooltip("Blendshape index in the Mesh. Leave -1 to resolve by nameMatch if provided.")]
        public int blendshapeIndex = -1;

        [Tooltip("Optional: Resolve by blendshape name in mesh. Ignored if blendshapeIndex >= 0.")]
        public string meshBlendshapeName;

        [Tooltip("Slider minimum for this channel (e.g., some rigs use -50..100).")]
        public float minValue = 0f;

        [Tooltip("Slider maximum for this channel.")]
        public float maxValue = 100f;

        [Tooltip("If true, incoming values are treated as normalized 0..1 and remapped into [min,max].")]
        public bool expectsNormalizedInput = false;

        // runtime
        [NonSerialized] public int resolvedIndex = -1;
    }

    [Serializable]
    public class ExpressionTarget
    {
        [Tooltip("Channel name to target (matches BlendshapeChannel.channelName).")]
        public string channelName;

        [Tooltip("Desired value for the channel (normalized if the channel expectsNormalizedInput).")]
        public float value = 1f;
    }

    [Serializable]
    public class ExpressionPreset
    {
        public string presetName;
        public List<ExpressionTarget> targets = new List<ExpressionTarget>();
    }

    [Header("Channels & Presets")]
    [Tooltip("Define all channels you want to control and their per-slider ranges.")]
    public List<BlendshapeChannel> channels = new List<BlendshapeChannel>();

    [Tooltip("Optional preset library to crossfade full expressions.")]
    public List<ExpressionPreset> presets = new List<ExpressionPreset>();

    // ---- Runtime state ----
    private readonly Dictionary<string, int> _channelToIndex =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, BlendshapeChannel> _channelsByName =
        new Dictionary<string, BlendshapeChannel>(StringComparer.OrdinalIgnoreCase);

    // Active tweens per channel
    private class Tween
    {
        public string channelName;
        public float startValue;
        public float endValue;
        public float duration;
        public AnimationCurve ease;
        public float elapsed;
        public Action onComplete;
        public bool isComplete;
    }

    private readonly Dictionary<string, Tween> _activeTweens =
        new Dictionary<string, Tween>(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _keysBuffer = new List<string>();

    private float DeltaTime => useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;

    // ---------- Lifecycle ----------
    private void Reset()
    {
        skinnedMeshRenderer = GetComponentInChildren<SkinnedMeshRenderer>();
    }

    private void Awake()
    {
        if (skinnedMeshRenderer == null)
        {
            Debug.LogError("[ExpressionManager] Missing SkinnedMeshRenderer reference.", this);
            enabled = false;
            return;
        }

        BuildChannelLookups();
    }

    private void Update()
    {
        if (_activeTweens.Count == 0) return;

        _keysBuffer.Clear();
        _keysBuffer.AddRange(_activeTweens.Keys);

        for (int i = 0; i < _keysBuffer.Count; i++)
        {
            var key = _keysBuffer[i];
            if (!_activeTweens.TryGetValue(key, out var t) || t.isComplete) continue;

            t.elapsed += DeltaTime;
            float alpha = (t.duration <= 0f) ? 1f : Mathf.Clamp01(t.elapsed / t.duration);
            float eased = (t.ease != null) ? t.ease.Evaluate(alpha) : defaultEase.Evaluate(alpha);
            float value = Mathf.Lerp(t.startValue, t.endValue, eased);

            InternalSetChannelValue(t.channelName, value);

            if (alpha >= 1f)
            {
                t.isComplete = true;
                _activeTweens.Remove(key);
                t.onComplete?.Invoke();
            }
        }
    }

    private void BuildChannelLookups()
    {
        _channelToIndex.Clear();
        _channelsByName.Clear();

        var mesh = skinnedMeshRenderer.sharedMesh;
        if (mesh == null)
        {
            Debug.LogError("[ExpressionManager] SkinnedMeshRenderer has no mesh.", this);
            return;
        }

        foreach (var ch in channels)
        {
            if (ch == null) continue;
            ch.resolvedIndex = -1;

            // Prefer explicit index
            if (ch.blendshapeIndex >= 0 && ch.blendshapeIndex < mesh.blendShapeCount)
            {
                ch.resolvedIndex = ch.blendshapeIndex;
            }
            else if (!string.IsNullOrEmpty(ch.meshBlendshapeName))
            {
                // Resolve by mesh blendshape name
                for (int b = 0; b < mesh.blendShapeCount; b++)
                {
                    var nm = mesh.GetBlendShapeName(b);
                    if (string.Equals(nm, ch.meshBlendshapeName, StringComparison.OrdinalIgnoreCase))
                    {
                        ch.resolvedIndex = b;
                        break;
                    }
                }
            }
            else if (!string.IsNullOrEmpty(ch.channelName))
            {
                // Fallback: try channelName as mesh name
                for (int b = 0; b < mesh.blendShapeCount; b++)
                {
                    var nm = mesh.GetBlendShapeName(b);
                    if (string.Equals(nm, ch.channelName, StringComparison.OrdinalIgnoreCase))
                    {
                        ch.resolvedIndex = b;
                        break;
                    }
                }
            }

            if (ch.resolvedIndex < 0)
            {
                Debug.LogWarning($"[ExpressionManager] Could not resolve blendshape for channel '{ch.channelName}'.", this);
            }

            if (!string.IsNullOrEmpty(ch.channelName))
            {
                _channelToIndex[ch.channelName] = ch.resolvedIndex;
                _channelsByName[ch.channelName] = ch;
            }
        }
    }

    // ---------- Public API: Single Channel Control ----------
    /// <summary>
    /// Instantly sets a channel to a value (absolute unless channel expectsNormalizedInput).
    /// If the channel expectsNormalizedInput, pass 0..1 and it will be remapped.
    /// </summary>
    public void SetInstant(string channelName, float value)
    {
        CancelTween(channelName);
        InternalSetChannelValue(channelName, value);
    }

    /// <summary>
    /// Tweens a single channel to a value over time.
    /// </summary>
    /// <param name="channelName">Channel defined in 'channels'.</param>
    /// <param name="targetValue">If channel expectsNormalizedInput=true, this is 0..1; otherwise absolute.</param>
    /// <param name="duration">Tween duration; if &lt;=0, sets instantly.</param>
    /// <param name="ease">Optional custom ease. Null uses defaultEase.</param>
    /// <param name="interrupt">If true, cancels any existing tween on this channel.</param>
    /// <param name="onComplete">Callback when tween finishes.</param>
    public void TweenTo(string channelName, float targetValue, float duration = -1f, AnimationCurve ease = null, bool interrupt = true, Action onComplete = null)
    {
        if (!_channelsByName.TryGetValue(channelName, out var ch))
        {
            Debug.LogWarning($"[ExpressionManager] Unknown channel '{channelName}'.", this);
            return;
        }

        float currentAbs = GetChannelValue(channelName);
        float targetAbs = ch.expectsNormalizedInput
            ? Remap01ToRange(targetValue, ch.minValue, ch.maxValue)
            : targetValue;

        if (clampToChannelRange)
            targetAbs = Mathf.Clamp(targetAbs, Mathf.Min(ch.minValue, ch.maxValue), Mathf.Max(ch.minValue, ch.maxValue));

        duration = (duration < 0f) ? defaultDuration : duration;

        if (duration <= 0f)
        {
            SetInstant(channelName, ch.expectsNormalizedInput ? targetValue : targetAbs);
            onComplete?.Invoke();
            return;
        }

        if (!interrupt && _activeTweens.ContainsKey(channelName))
        {
            // Let the existing tween finish
            return;
        }

        _activeTweens[channelName] = new Tween
        {
            channelName = channelName,
            startValue = currentAbs,
            endValue = targetAbs,
            duration = duration,
            ease = ease ?? defaultEase,
            elapsed = 0f,
            onComplete = onComplete
        };
    }

    /// <summary>
    /// Gets the current absolute value of a channel (not normalized).
    /// </summary>
    public float GetChannelValue(string channelName)
    {
        if (!_channelsByName.TryGetValue(channelName, out var ch) || ch.resolvedIndex < 0) return 0f;
        return skinnedMeshRenderer.GetBlendShapeWeight(ch.resolvedIndex);
    }

    /// <summary>
    /// Gets the current normalized (0..1) value for a channel.
    /// </summary>
    public float GetChannelValue01(string channelName)
    {
        if (!_channelsByName.TryGetValue(channelName, out var ch)) return 0f;
        float v = GetChannelValue(channelName);
        return InverseLerpSafe(ch.minValue, ch.maxValue, v);
    }

    /// <summary>
    /// Stops the tween on a channel, leaving its current value as-is.
    /// </summary>
    public void CancelTween(string channelName)
    {
        _activeTweens.Remove(channelName);
    }

    // ---------- Public API: Expressions (Presets) ----------
    /// <summary>
    /// Instantly applies a preset by name. Unmentioned channels are left as-is,
    /// or zeroed if you pass zeroOutOthers = true.
    /// </summary>
    public void ApplyPresetInstant(string presetName, bool zeroOutOthers = false)
    {
        var preset = FindPreset(presetName);
        if (preset == null)
        {
            Debug.LogWarning($"[ExpressionManager] Preset '{presetName}' not found.");
            return;
        }

        if (zeroOutOthers)
        {
            foreach (var ch in channels)
            {
                if (ch == null) continue;
                if (!HasTarget(preset, ch.channelName))
                {
                    float zero = ch.expectsNormalizedInput ? 0f : 0f;
                    SetInstant(ch.channelName, zero);
                }
            }
        }

        foreach (var t in preset.targets)
            SetInstant(t.channelName, t.value);
    }

    /// <summary>
    /// Crossfades to a preset over time. Channels not in the preset optionally fade to zero.
    /// </summary>
    public void CrossfadeTo(string presetName, float duration = -1f, AnimationCurve ease = null, bool fadeOthersToZero = true, bool interrupt = true)
    {
        var preset = FindPreset(presetName);
        if (preset == null)
        {
            Debug.LogWarning($"[ExpressionManager] Preset '{presetName}' not found.");
            return;
        }

        duration = (duration < 0f) ? defaultDuration : duration;

        // Fade channels in preset
        foreach (var t in preset.targets)
            TweenTo(t.channelName, t.value, duration, ease, interrupt);

        // Optionally fade all other channels to 0
        if (fadeOthersToZero)
        {
            foreach (var ch in channels)
            {
                if (ch == null) continue;
                if (HasTarget(preset, ch.channelName)) continue;
                float zero = ch.expectsNormalizedInput ? 0f : 0f;
                TweenTo(ch.channelName, zero, duration, ease, interrupt);
            }
        }
    }

    /// <summary>
    /// Returns a shallow copy of a preset by name (or null if not found).
    /// </summary>
    public ExpressionPreset GetPreset(string presetName)
    {
        var p = FindPreset(presetName);
        return p != null ? ClonePreset(p) : null;
    }

    /// <summary>
    /// Updates a channel's per-slider range at runtime.
    /// </summary>
    public void SetChannelRange(string channelName, float min, float max, bool reClampCurrent = true)
    {
        if (!_channelsByName.TryGetValue(channelName, out var ch)) return;
        ch.minValue = min;
        ch.maxValue = max;

        if (reClampCurrent)
        {
            float v = GetChannelValue(channelName);
            float clamped = Mathf.Clamp(v, Mathf.Min(min, max), Mathf.Max(min, max));
            InternalSetChannelValue(channelName, ch.expectsNormalizedInput ? InverseLerpSafe(min, max, clamped) : clamped);
        }
    }

    /// <summary>
    /// Creates or updates a preset at runtime.
    /// </summary>
    public void UpsertPreset(string presetName, IEnumerable<ExpressionTarget> targets)
    {
        var p = FindPreset(presetName);
        if (p == null)
        {
            p = new ExpressionPreset { presetName = presetName, targets = new List<ExpressionTarget>() };
            presets.Add(p);
        }
        else
        {
            p.targets.Clear();
        }

        p.targets.AddRange(targets);
    }

    // ---------- Internal ----------
    private void InternalSetChannelValue(string channelName, float inbound)
    {
        if (!_channelsByName.TryGetValue(channelName, out var ch)) return;

        float absValue = ch.expectsNormalizedInput
            ? Remap01ToRange(inbound, ch.minValue, ch.maxValue)
            : inbound;

        if (clampToChannelRange)
            absValue = Mathf.Clamp(absValue, Mathf.Min(ch.minValue, ch.maxValue), Mathf.Max(ch.minValue, ch.maxValue));

        if (ch.resolvedIndex < 0) return;
        skinnedMeshRenderer.SetBlendShapeWeight(ch.resolvedIndex, absValue);
    }

    private ExpressionPreset FindPreset(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return presets.Find(p => string.Equals(p.presetName, name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasTarget(ExpressionPreset p, string channelName)
    {
        for (int i = 0; i < p.targets.Count; i++)
            if (string.Equals(p.targets[i].channelName, channelName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static ExpressionPreset ClonePreset(ExpressionPreset src)
    {
        var dst = new ExpressionPreset
        {
            presetName = src.presetName,
            targets = new List<ExpressionTarget>(src.targets.Count)
        };
        foreach (var t in src.targets)
            dst.targets.Add(new ExpressionTarget { channelName = t.channelName, value = t.value });
        return dst;
    }

    private static float Remap01ToRange(float x01, float min, float max)
    {
        return Mathf.Lerp(min, max, Mathf.Clamp01(x01));
    }

    private static float InverseLerpSafe(float a, float b, float v)
    {
        if (Mathf.Approximately(a, b)) return 0f;
        return Mathf.Clamp01((v - a) / (b - a));
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (skinnedMeshRenderer != null && isActiveAndEnabled)
            BuildChannelLookups();
    }
#endif
}
