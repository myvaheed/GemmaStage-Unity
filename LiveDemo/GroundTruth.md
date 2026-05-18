# GemmaStage Ground Truth

GemmaStage is a private VR rehearsal room where high-stakes ideas meet an AI audience before they meet the real one.

The project helps users practice important presentations in an immersive virtual environment. A user stands on a virtual stage, speaks out loud, uses slides or visual materials, answers AI-generated audience questions, and receives structured feedback after the session.

GemmaStage is not designed to judge whether an idea is objectively good or correct. Its purpose is to test whether the idea was communicated clearly enough to be understood.

The core problem is that many people prepare presentations alone. They may write slides or rehearse mentally, but they do not experience the pressure of a room trying to understand them. Ideas that feel clear inside the speaker’s head can become vague when spoken aloud. Speakers often skip assumptions, weak explanations only appear when questions begin, and sensitive ideas may not be safe to upload to cloud-based tools.

GemmaStage solves this by combining VR presence, local AI audience simulation, and structured feedback. VR matters because public speaking is not just content; it is content under pressure. The speaker practices standing in front of an audience, speaking aloud, managing time, using visuals, and responding to questions.

The system uses Gemma locally on-device. It processes speech and visual context, builds an audience-side understanding of the talk, tracks open concerns, generates audience-style questions, and produces a final evaluation. This makes the rehearsal active instead of passive.

The AI audience is based on what an attentive listener would understand from the talk. It does not store a raw transcript as the audience understanding. Instead, it builds a compressed recall of the main idea and important claims that were successfully communicated.

Questions are generated from open concerns. These concerns represent parts of the presentation that may still be unclear, unsupported, incomplete, or worth asking about. Questions may appear during the presentation as live Q&A or after the session as final Q&A.

After the session, GemmaStage analyzes the presentation using a structured rubric. The evaluation includes main idea clarity, structure, consistency and focus, support and justification, language quality, emotional delivery, and Q&A handling.

GemmaStage is local-first because high-stakes ideas are often unfinished, private, or valuable. Startup pitches, research ideas, product strategy, technical architecture, thesis work, and internal company plans should be rehearsed before they are exposed. Running locally means the user does not need to send their voice, slides, reference files, or reasoning to an external cloud service.

The key value of GemmaStage is that it lets users test an idea’s explanation before exposing the idea itself. It is not just a presentation checker. It is a private first audience for ideas that are not ready to go public yet.