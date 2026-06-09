#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Best-effort preview advancement for captures, inspired by the SceneObjectsAdvancer.
    /// </summary>
    internal sealed class CaptureObjectAdvancer : IDisposable
    {
        const int MaxAdvanceMs = 30000;

        readonly bool? previousAnimationMode;
        readonly List<string> warnings = new();
        readonly int advanceMs;
        readonly bool simulateParticles;
        readonly bool sampleAnimations;
        int particleSystemsAdvanced;
        int animatorsSampled;
        int legacyAnimationsSampled;

        CaptureObjectAdvancer(int advanceMs, bool simulateParticles, bool sampleAnimations)
        {
            this.advanceMs = Mathf.Clamp(advanceMs, 0, MaxAdvanceMs);
            this.simulateParticles = simulateParticles;
            this.sampleAnimations = sampleAnimations;

            if (this.advanceMs > 0 && this.sampleAnimations)
            {
                previousAnimationMode = AnimationMode.InAnimationMode();
                if (previousAnimationMode == false)
                    AnimationMode.StartAnimationMode();
            }
        }

        public object Info => new
        {
            requested = advanceMs > 0,
            advanceMs,
            simulateParticles,
            sampleAnimations,
            particleSystemsAdvanced,
            animatorsSampled,
            legacyAnimationsSampled,
            warnings = warnings.ToArray()
        };

        public static CaptureObjectAdvancer Advance(
            IEnumerable<GameObject> targets,
            int? advanceMs,
            bool? simulateParticles,
            bool? sampleAnimations)
        {
            var effectiveAdvanceMs = Mathf.Clamp(advanceMs.GetValueOrDefault(0), 0, MaxAdvanceMs);
            var effectiveSimulateParticles = simulateParticles ?? effectiveAdvanceMs > 0;
            var effectiveSampleAnimations = sampleAnimations ?? effectiveAdvanceMs > 0;
            var advancer = new CaptureObjectAdvancer(effectiveAdvanceMs, effectiveSimulateParticles, effectiveSampleAnimations);

            if (effectiveAdvanceMs <= 0)
                return advancer;

            var roots = targets?
                .Where(go => go != null)
                .Distinct()
                .ToArray() ?? Array.Empty<GameObject>();

            if (roots.Length == 0)
            {
                advancer.warnings.Add("AdvanceMs was requested, but no GameObject targets were available.");
                return advancer;
            }

            var seconds = effectiveAdvanceMs / 1000f;
            if (effectiveSimulateParticles)
                advancer.AdvanceParticleSystems(roots, seconds);
            if (effectiveSampleAnimations)
                advancer.SampleAnimations(roots, seconds);

            SceneView.RepaintAll();
            EditorApplication.QueuePlayerLoopUpdate();
            return advancer;
        }

        public void Dispose()
        {
            if (previousAnimationMode == false && AnimationMode.InAnimationMode())
                AnimationMode.StopAnimationMode();
        }

        void AdvanceParticleSystems(IEnumerable<GameObject> roots, float seconds)
        {
            foreach (var particleSystem in roots.SelectMany(root => root.GetComponentsInChildren<ParticleSystem>(true)).Distinct())
            {
                if (particleSystem == null)
                    continue;

                try
                {
                    if (!particleSystem.isPlaying)
                        particleSystem.Play(withChildren: false);
                    particleSystem.Simulate(seconds, withChildren: false, restart: false, fixedTimeStep: false);
                    particleSystemsAdvanced++;
                }
                catch (Exception ex)
                {
                    warnings.Add($"ParticleSystem '{particleSystem.name}' could not be advanced: {ex.Message}");
                }
            }
        }

        void SampleAnimations(IEnumerable<GameObject> roots, float seconds)
        {
            foreach (var animator in roots.SelectMany(root => root.GetComponentsInChildren<Animator>(true)).Distinct())
            {
                if (animator == null)
                    continue;

                try
                {
                    var clips = animator.runtimeAnimatorController?.animationClips;
                    var clip = clips?.FirstOrDefault(item => item != null);
                    if (clip == null)
                        continue;

                    AnimationMode.SampleAnimationClip(animator.gameObject, clip, NormalizeSampleTime(clip, seconds));
                    animatorsSampled++;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Animator '{animator.name}' could not be sampled: {ex.Message}");
                }
            }

            foreach (var animation in roots.SelectMany(root => root.GetComponentsInChildren<Animation>(true)).Distinct())
            {
                if (animation == null)
                    continue;

                try
                {
                    var clip = animation.clip;
                    if (clip == null)
                    {
                        foreach (AnimationState state in animation)
                        {
                            if (state?.clip != null)
                            {
                                clip = state.clip;
                                break;
                            }
                        }
                    }

                    if (clip == null)
                        continue;

                    AnimationMode.SampleAnimationClip(animation.gameObject, clip, NormalizeSampleTime(clip, seconds));
                    legacyAnimationsSampled++;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Animation '{animation.name}' could not be sampled: {ex.Message}");
                }
            }
        }

        static float NormalizeSampleTime(AnimationClip clip, float seconds)
        {
            if (clip == null || clip.length <= 0.0001f)
                return seconds;

            return Mathf.Repeat(seconds, clip.length);
        }
    }
}
