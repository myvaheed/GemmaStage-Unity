namespace GemmaStage.Audience
{
    public enum AudienceReaction
    {
        Idle = 0,
        HandRaise = 1,
        // Future: Nodding, Confused, Attentive, Bored — add here.
        // Extension: (1) add enum value, (2) add Animator state with the same name,
        // (3) drop in an AnimationClip. AudienceNPC routes by ToString().
    }
}
