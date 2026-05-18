using UnityEngine;

namespace GemmaStage.Audience
{
    public class AudienceAnimationController : MonoBehaviour, IAudienceReactor
    {
        [SerializeField] Animator animator;

        static readonly string[] IdleStates =
        {
            "sitting_idle",
            "sitting_idle2",
        };

        const string OverKnees         = "sitting_over_knees";
        const string OverKneesMirrored = "sitting_over_knees_mirrored";

        static readonly (string normal, string mirrored)[] OneShots =
        {
            ("sitting_around", "sitting_around_mirrored"),
        };

        static readonly (string normal, string mirrored)[] HandRaiseStates =
        {
            ("sitting_asking_question", "sitting_asking_question_mirrored"),
            ("sitting_and_pointing",    "sitting_and_pointing_mirrored"),
        };

        const float TickIntervalSeconds   = 60f;
        const float TickJitterSeconds     = 8f;
        const float CrossFadeSeconds      = 0.3f;
        const float SwapChance            = 0.20f;
        const float OneShotChance         = 0.30f;
        const float MirrorChance          = 0.50f;
        const float OverKneesInitialChance = 0.20f;
        const string MirroredSuffix       = "_mirrored";

        enum LoopCategory { Idle, OverKnees }
        LoopCategory currentCategory;
        bool playingOneShot;
        float oneShotEndTime;
        float nextTickTime;

        void Reset() => animator = GetComponentInChildren<Animator>();

        void Awake()
        {
            if (animator == null) animator = GetComponentInChildren<Animator>();
        }

        void OnEnable()
        {
            currentCategory = Random.value < OverKneesInitialChance ? LoopCategory.OverKnees : LoopCategory.Idle;
            PlayLoop();
            ScheduleNextTick();
            playingOneShot = false;
        }

        void Update()
        {
            if (playingOneShot && Time.time >= oneShotEndTime)
            {
                playingOneShot = false;
                PlayLoop();
            }

            if (Time.time >= nextTickTime)
            {
                ScheduleNextTick();
                Tick();
            }
        }

        void Tick()
        {
            if (playingOneShot) return;

            if (Random.value < SwapChance)
            {
                currentCategory = currentCategory == LoopCategory.Idle ? LoopCategory.OverKnees : LoopCategory.Idle;
                PlayLoop();
            }

            if (currentCategory == LoopCategory.Idle && Random.value < OneShotChance)
            {
                var pair = OneShots[Random.Range(0, OneShots.Length)];
                PlayOneShot(Random.value < MirrorChance ? pair.mirrored : pair.normal);
            }
        }

        void ScheduleNextTick()
        {
            nextTickTime = Time.time + TickIntervalSeconds + Random.Range(-TickJitterSeconds, TickJitterSeconds);
        }

        void PlayLoop()
        {
            if (animator == null) return;
            string state;
            if (currentCategory == LoopCategory.Idle)
            {
                state = IdleStates[Random.Range(0, IdleStates.Length)];
            }
            else
            {
                state = Random.value < MirrorChance ? OverKneesMirrored : OverKnees;
            }
            animator.CrossFadeInFixedTime(state, CrossFadeSeconds);
        }

        void PlayOneShot(string stateName)
        {
            if (animator == null) return;
            playingOneShot = true;
            animator.CrossFadeInFixedTime(stateName, CrossFadeSeconds);
            oneShotEndTime = Time.time + ClipDurationForState(stateName);
        }

        public void PlayHandRaise()
        {
            if (animator == null) return;
            var pair = HandRaiseStates[Random.Range(0, HandRaiseStates.Length)];
            var state = Random.value < MirrorChance ? pair.mirrored : pair.normal;
            playingOneShot = true;
            animator.CrossFadeInFixedTime(state, CrossFadeSeconds);
            oneShotEndTime = Time.time + ClipDurationForState(state);
        }

        float ClipDurationForState(string stateName)
        {
            // Mirrored states share the un-suffixed clip.
            var clipName = stateName.EndsWith(MirroredSuffix)
                ? stateName.Substring(0, stateName.Length - MirroredSuffix.Length)
                : stateName;
            var rac = animator != null ? animator.runtimeAnimatorController : null;
            if (rac == null) return 3f;
            foreach (var c in rac.animationClips)
                if (c != null && c.name == clipName) return c.length;
            return 3f;
        }
    }
}
