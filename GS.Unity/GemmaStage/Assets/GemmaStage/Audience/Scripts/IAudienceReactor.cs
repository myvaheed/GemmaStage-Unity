namespace GemmaStage.Audience
{
    // Implemented by per-NPC animation drivers (e.g. AudienceAnimationController
    // in GemmaStage.Audience). Lets AudienceManager dispatch reactions
    // without depending on a specific NPC backend assembly.
    public interface IAudienceReactor
    {
        void PlayHandRaise();
    }
}
