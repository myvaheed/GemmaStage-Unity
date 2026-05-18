Best Practices for Gemma 4 E4B
For the best performance, use these configurations and best practices:

1. Sampling Parameters
Use the following standardized sampling configuration across all use cases:

temperature=1.0
top_p=0.95
top_k=64

2. Thinking Mode Configuration
Compared to Gemma 3, the models use standard system, assistant, and user roles. To properly manage the thinking process, use the following control tokens:

Trigger Thinking: Thinking is enabled by including the <|think|> token at the start of the system prompt. To disable thinking, remove the token.
Standard Generation: When thinking is enabled, the model will output its internal reasoning followed by the final answer using this structure:
<|channel>thought\n[Internal reasoning]<channel|>
Disabled Thinking Behavior: For all models except for the E2B and E4B variants, if thinking is disabled, the model will still generate the tags but with an empty thought block:
<|channel>thought\n<channel|>[Final answer]
Note that many libraries like Transformers and llama.cpp handle the complexities of the chat template for you.

3. Multi-Turn Conversations
No Thinking Content in History: In multi-turn conversations, the historical model output should only include the final response. Thoughts from previous model turns must not be added before the next user turn begins.

4. Modality order
For optimal performance with multimodal inputs, place image and/or audio content before the text in your prompt.

5. Variable Image Resolution
Aside from variable aspect ratios, Gemma 4 supports variable image resolution through a configurable visual token budget, which controls how many tokens are used to represent an image. A higher token budget preserves more visual detail at the cost of additional compute, while a lower budget enables faster inference for tasks that don't require fine-grained understanding.

The supported token budgets are: 70, 140, 280, 560, and 1120.
Use lower budgets for classification, captioning, or video understanding, where faster inference and processing many frames outweigh fine-grained detail.
Use higher budgets for tasks like OCR, document parsing, or reading small text.

6. Audio
Use the following prompt structures for audio processing:

Audio Speech Recognition (ASR)
Transcribe the following speech segment in {LANGUAGE} into {LANGUAGE} text.

Follow these specific instructions for formatting the answer:
* Only output the transcription, with no newlines.
* When transcribing numbers, write the digits, i.e. write 1.7 and not one point seven, and write 3 instead of three.

Automatic Speech Translation (AST)
Transcribe the following speech segment in {SOURCE_LANGUAGE}, then translate it into {TARGET_LANGUAGE}.
When formatting the answer, first output the transcription in {SOURCE_LANGUAGE}, then one newline, then output the string '{TARGET_LANGUAGE}: ', then the translation in {TARGET_LANGUAGE}.

7. Audio and Video Length
All models support image inputs and can process videos as frames whereas the E2B and E4B models also support audio inputs. Audio supports a maximum length of 30 seconds. Video supports a maximum of 60 seconds assuming the images are processed at one frame per second.

Technical Details:
Based on the Gemma 4 technical details, the Gemma 4 E4B (Edge 4B) model has the following specifications:
Parameters: It has 4.5B effective parameters (4.5B active during inference) and ~8B total parameters (including per-layer embeddings).
Sliding Window: It uses a 512-token sliding window for its attention mechanism. 
Key Details of Gemma 4 E4B:
Architecture: It is a dense model designed for on-device/edge deployment (laptops, phones).
Per-Layer Embeddings (PLE): The higher "total" parameter count (8B) is due to per-layer embedding tables used for efficient, high-quality representations without increasing compute costs, keeping it fast for edge devices.
Context Length: 128K tokens.
Modalities: Text, Image, and Audio.
Layers: 42.