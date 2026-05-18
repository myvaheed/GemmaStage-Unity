#include "GemmaStageInternal.h"

#include <nlohmann/json.hpp>

#include <chrono>

// ---------------------------------------------------------------------------
// Cancellation hook
// ---------------------------------------------------------------------------

static bool gs_abort_decode(void* user_data) {
    auto* conversation = static_cast<GS_Conversation*>(user_data);
    return conversation &&
           conversation->cancel_flag.load(std::memory_order_acquire);
}

// ---------------------------------------------------------------------------
// Token / text utilities
// ---------------------------------------------------------------------------

static std::string gs_token_to_str(const llama_vocab* vocab, llama_token token) {
    char    buf[256];
    int32_t n = llama_token_to_piece(vocab, token, buf, sizeof(buf), 0, false);
    if (n < 0) {
        std::vector<char> big(static_cast<size_t>(-n) + 1);
        n = llama_token_to_piece(vocab, token, big.data(),
                                 static_cast<int32_t>(big.size()), 0, false);
        if (n > 0) {
            return std::string(big.data(), static_cast<size_t>(n));
        }
        return {};
    }
    return std::string(buf, static_cast<size_t>(n));
}

static const char* const kTemplateMarkers[] = {
    "<end_of_turn>",
    "<start_of_turn>",
    "</start_of_turn>",
    "<|turn|>",
};

static size_t gs_find_template_marker(
    const std::string& text,
    const char**       marker = nullptr)
{
    size_t      best_pos    = std::string::npos;
    const char* best_marker = nullptr;
    for (const char* candidate : kTemplateMarkers) {
        const size_t pos = text.find(candidate);
        if (pos == std::string::npos) {
            continue;
        }
        if (best_pos == std::string::npos || pos < best_pos) {
            best_pos    = pos;
            best_marker = candidate;
        }
    }
    if (marker) {
        *marker = best_marker;
    }
    return best_pos;
}

static size_t gs_template_marker_holdback() {
    size_t max_len = 0;
    for (const char* candidate : kTemplateMarkers) {
        max_len = std::max(max_len, strlen(candidate));
    }
    return max_len > 0 ? max_len - 1 : 0;
}

static void gs_strip_prefix(std::string* text, const std::string& prefix) {
    if (!text || prefix.empty()) {
        return;
    }
    if (text->compare(0, prefix.size(), prefix) == 0) {
        text->erase(0, prefix.size());
    }
}

static void gs_strip_trailing_template_prefix(std::string* text) {
    if (!text || text->empty()) {
        return;
    }
    for (const char* marker : kTemplateMarkers) {
        const size_t marker_len = strlen(marker);
        const size_t max_prefix = std::min(text->size(), marker_len - 1);
        for (size_t prefix_len = max_prefix; prefix_len > 0; --prefix_len) {
            if (text->compare(text->size() - prefix_len, prefix_len,
                              marker, prefix_len) == 0) {
                text->erase(text->size() - prefix_len);
                return;
            }
        }
    }
}

static void gs_trim_generated_text(std::string* text) {
    if (!text) {
        return;
    }
    const size_t pos = gs_find_template_marker(*text);
    if (pos != std::string::npos) {
        text->erase(pos);
    }
    gs_strip_trailing_template_prefix(text);
    while (!text->empty() &&
           (text->back() == '\n' || text->back() == '\r' ||
            text->back() == ' '  || text->back() == '\t')) {
        text->pop_back();
    }
}

static void gs_trim_surrounding_whitespace(std::string* text) {
    if (!text) {
        return;
    }

    size_t first = 0;
    while (first < text->size() &&
           std::isspace(static_cast<unsigned char>((*text)[first]))) {
        ++first;
    }

    size_t last = text->size();
    while (last > first &&
           std::isspace(static_cast<unsigned char>((*text)[last - 1]))) {
        --last;
    }

    *text = text->substr(first, last - first);
}

static std::string gs_extract_text_only_media_history_content(
    const std::string& content)
{
    std::string text_only = content;
    const std::string marker = mtmd_default_marker();

    size_t pos = 0;
    while ((pos = text_only.find(marker, pos)) != std::string::npos) {
        text_only.erase(pos, marker.size());
    }

    gs_trim_surrounding_whitespace(&text_only);
    return text_only;
}

static void gs_convert_last_media_message_to_text_only(
    std::vector<GS_ChatMessage>* history)
{
    if (!history || history->empty()) {
        return;
    }

    GS_ChatMessage& message = history->back();
    if (message.media_kind == GS_ChatMessage::MediaKind::None) {
        return;
    }

    message.content = gs_extract_text_only_media_history_content(message.content);
    message.media_kind = GS_ChatMessage::MediaKind::None;
    message.media_bytes.clear();
    message.media_bytes.shrink_to_fit();
    message.media_bitmap.ptr.reset();

    if (message.content.empty()) {
        // Keep the conversational shape intact for the template. The PoC only
        // needs a minimal placeholder so text-only history never collapses a
        // `user -> assistant` pair into a dangling assistant turn.
        message.content = "(media omitted)";
    }
}

static void gs_clear_last_benchmark(GS_Conversation* conversation) {
    if (!conversation) {
        return;
    }
    conversation->last_benchmark = {};
}

static void gs_store_generation_benchmark(
    GS_Conversation*          conversation,
    const GS_GenerationResult& result,
    double                    phase_prefill_s)
{
    if (!conversation) {
        return;
    }

    conversation->last_benchmark.ttft = result.ttft_s;
    conversation->last_benchmark.prefill_tokens = result.prompt_tokens;
    conversation->last_benchmark.decode_tokens = result.decode_tokens;
    conversation->last_benchmark.prefill_tok_per_s =
        phase_prefill_s > 0.0 ? (result.prompt_tokens / phase_prefill_s) : 0.0;
    conversation->last_benchmark.decode_tok_per_s =
        result.decode_s > 0.0 ? (result.decode_tokens / result.decode_s) : 0.0;
}

static void gs_store_commit_benchmark(
    GS_Conversation* conversation,
    double           commit_overhead_s,
    int32_t          commit_tokens)
{
    if (!conversation) {
        return;
    }

    conversation->last_benchmark.commit_overhead_s = commit_overhead_s;
    conversation->last_benchmark.commit_tokens = std::max(0, commit_tokens);
}

static bool gs_assistant_requires_structured_history(const common_chat_msg& message) {
    return !message.content_parts.empty() ||
           !message.tool_calls.empty() ||
           !message.reasoning_content.empty() ||
           !message.tool_name.empty() ||
           !message.tool_call_id.empty();
}

static void gs_set_assistant_history_content(
    common_chat_msg        parsed,
    const std::string&     normalized_text,
    std::string*           history_content,
    std::string*           json_body)
{
    if (!history_content || !json_body) {
        return;
    }

    // Outward response keeps reasoning_content so callers can display the
    // thought stream for the current turn if they choose to.
    *json_body = parsed.to_json_oaicompat().dump();

    // Gemma 4 best practice: thoughts from previous model turns must not be
    // replayed into the next user turn. With reasoning_format=AUTO at template
    // init, the PEG parser has already separated thoughts from the final
    // answer, so dropping reasoning_content here is enough to keep future
    // prompt renders thought-free.
    parsed.reasoning_content.clear();

    if (gs_assistant_requires_structured_history(parsed)) {
        // Structured assistant turns (tool calls, typed content parts, tool
        // responses) must round-trip through the OAI JSON form so they
        // survive future history rebuilds.
        *history_content = parsed.to_json_oaicompat().dump();
        return;
    }

    // Plain-text replies are persisted as the parser-extracted final answer
    // rather than the raw generation, so any recognised reasoning tokens are
    // excluded from the committed prompt. If parsing yielded nothing usable
    // we fall back to normalized_text to avoid losing the turn entirely.
    *history_content = !parsed.content.empty() ? parsed.content : normalized_text;
}

static std::string gs_fnv_hash(const uint8_t* data, size_t len) {
    const uint64_t fnv_prime = 0x100000001b3ULL;
    uint64_t       hash      = 0xcbf29ce484222325ULL;

    for (size_t i = 0; i < len; ++i) {
        hash ^= data[i];
        hash *= fnv_prime;
    }
    return std::to_string(hash);
}

static size_t gs_count_occurrences(
    const std::string& haystack,
    const std::string& needle)
{
    if (needle.empty()) {
        return 0;
    }

    size_t count = 0;
    size_t pos   = 0;
    while ((pos = haystack.find(needle, pos)) != std::string::npos) {
        ++count;
        pos += needle.size();
    }
    return count;
}

static size_t gs_count_media_messages(const std::vector<GS_ChatMessage>& history) {
    size_t count = 0;
    for (const auto& msg : history) {
        if (msg.media_kind != GS_ChatMessage::MediaKind::None) {
            ++count;
        }
    }
    return count;
}

static bool gs_ensure_media_bitmap(
    mtmd_context*    mtmd,
    GS_ChatMessage*  message)
{
    if (!message || message->media_kind == GS_ChatMessage::MediaKind::None) {
        return true;
    }
    if (message->media_bitmap.ptr) {
        return true;
    }
    if (!mtmd || message->media_bytes.empty()) {
        gs_log(3, "Multimodal history contains an empty media payload.");
        return false;
    }

    message->media_bitmap.ptr.reset(mtmd_helper_bitmap_init_from_buf(
        mtmd, message->media_bytes.data(), message->media_bytes.size()));
    if (!message->media_bitmap.ptr) {
        gs_log(3, "Failed to decode media payload for conversation history.");
        return false;
    }

    // mtmd uses bitmap IDs as stable identities for multimodal reuse. Hashing
    // the decoded bitmap bytes gives us the same logical media key even if the
    // original source payload is no longer kept around.
    const std::string hash = gs_fnv_hash(
        message->media_bitmap.data(),
        message->media_bitmap.n_bytes());
    message->media_bitmap.set_id(hash.c_str());

    // The decoded bitmap is enough for future delta-prefill and rebuilds.
    message->media_bytes.clear();
    message->media_bytes.shrink_to_fit();
    return true;
}

static bool gs_collect_media_bitmap_ptrs(
    mtmd_context*                 mtmd,
    std::vector<GS_ChatMessage>*  history,
    size_t                        skip_media_count,
    std::vector<const mtmd_bitmap*>* bitmaps)
{
    if (!history || !bitmaps) {
        return false;
    }

    bitmaps->clear();
    size_t seen_media = 0;
    for (auto& msg : *history) {
        if (msg.media_kind == GS_ChatMessage::MediaKind::None) {
            continue;
        }
        if (!gs_ensure_media_bitmap(mtmd, &msg)) {
            return false;
        }
        // The delta path must pass only the newly introduced media objects.
        // Older media is already represented by the committed prompt snapshot.
        if (seen_media++ < skip_media_count) {
            continue;
        }
        bitmaps->push_back(msg.media_bitmap.ptr.get());
    }
    return true;
}

// ---------------------------------------------------------------------------
// Chat-history rebuild for the built-in Jinja template
// ---------------------------------------------------------------------------

static bool gs_try_parse_assistant_json_message(
    const std::string& text,
    common_chat_msg*   message)
{
    if (!message) {
        return false;
    }
    const size_t first = text.find_first_not_of(" \t\r\n");
    if (first == std::string::npos || text[first] != '{') {
        return false;
    }
    try {
        const nlohmann::ordered_json parsed = nlohmann::ordered_json::parse(text);
        if (!parsed.is_object()) {
            return false;
        }
        std::vector<common_chat_msg> parsed_msgs =
            common_chat_msgs_parse_oaicompat(
                nlohmann::ordered_json::array({ parsed }));
        if (parsed_msgs.empty()) {
            return false;
        }
        *message = std::move(parsed_msgs.front());
        return true;
    } catch (const std::exception&) {
        return false;
    }
}

static void gs_append_media_content_parts(
    const GS_ChatMessage& msg,
    common_chat_msg*      out)
{
    const std::string marker     = mtmd_default_marker();
    const size_t      marker_pos = msg.content.find(marker);

    if (marker_pos == std::string::npos) {
        if (!msg.content.empty()) {
            out->content_parts.push_back({ "text", msg.content });
        }
        out->content_parts.push_back({ "media_marker", marker });
        return;
    }

    if (marker_pos > 0) {
        out->content_parts.push_back({ "text", msg.content.substr(0, marker_pos) });
    }
    out->content_parts.push_back({ "media_marker", marker });

    const size_t suffix_pos = marker_pos + marker.size();
    if (suffix_pos < msg.content.size()) {
        out->content_parts.push_back({ "text", msg.content.substr(suffix_pos) });
    }
}

static std::vector<common_chat_msg> gs_build_common_chat_history(
    const std::vector<GS_ChatMessage>& history)
{
    std::vector<common_chat_msg> messages;
    messages.reserve(history.size());

    for (const auto& msg : history) {
        common_chat_msg out;
        if (msg.role == "assistant" &&
            gs_try_parse_assistant_json_message(msg.content, &out)) {
            messages.push_back(std::move(out));
            continue;
        }
        out.role = msg.role;
        if (msg.media_kind != GS_ChatMessage::MediaKind::None) {
            gs_append_media_content_parts(msg, &out);
        } else {
            out.content = msg.content;
        }
        messages.push_back(std::move(out));
    }
    return messages;
}

static bool gs_try_build_chat_params(
    GS_Conversation*                    conversation,
    const std::vector<GS_ChatMessage>& history,
    bool                                add_generation_prompt,
    common_chat_params*                 params_out)
{
    if (!conversation || !conversation->chat_templates || !params_out) {
        return false;
    }
    try {
        common_chat_templates_inputs inputs;
        inputs.use_jinja             = true;
        inputs.add_generation_prompt = add_generation_prompt;
        inputs.enable_thinking       = conversation->config.enable_thinking;
        // Tell the template-driven PEG parser to split reasoning out of
        // `content` into `reasoning_content`. Without this, the Gemma 4 parser
        // leaves `<|channel>thought ... <channel|>` inline in `content` and we
        // have no structured way to drop it before committing history.
        inputs.reasoning_format      = COMMON_REASONING_FORMAT_AUTO;
        inputs.messages              = gs_build_common_chat_history(history);

        if (!conversation->parsed_tools.empty()) {
            inputs.tools               = conversation->parsed_tools;
            inputs.tool_choice         = COMMON_CHAT_TOOL_CHOICE_AUTO;
            inputs.parallel_tool_calls = false;
        }

        *params_out = common_chat_templates_apply(
            conversation->chat_templates.get(), inputs);
        return true;
    } catch (const std::exception& ex) {
        gs_log(2, "Failed to apply built-in Jinja chat template: %s", ex.what());
        return false;
    }
}

static bool gs_try_build_chat_params(
    GS_Conversation*    conversation,
    bool                add_generation_prompt,
    common_chat_params* params_out)
{
    if (!conversation) {
        return false;
    }
    return gs_try_build_chat_params(
        conversation,
        conversation->chat_history,
        add_generation_prompt,
        params_out);
}

static common_chat_parser_params gs_build_chat_parser_params(
    const common_chat_params& chat_params)
{
    common_chat_parser_params params(chat_params);
    if (!chat_params.parser.empty()) {
        params.parser.load(chat_params.parser);
    }
    return params;
}

static bool gs_init_verified_prompt_suffix(GS_Conversation* conversation) {
    if (!conversation || !conversation->engine || !conversation->chat_templates ||
        conversation->eot_suffix_tokens.empty()) {
        return false;
    }

    constexpr char kSuffixProbe[] = "GS_SUFFIX_PROBE_7d1d4b0f";

    // Text-delta reuse is only safe if the already-committed prompt ends on a
    // boundary that cannot be merged with later text tokenization. We derive
    // that boundary from the actual Jinja template instead of assuming it.
    std::vector<GS_ChatMessage> probe_history;
    probe_history.reserve(conversation->chat_history.size() + 1);
    for (const auto& msg : conversation->chat_history) {
        GS_ChatMessage copy;
        copy.role       = msg.role;
        copy.content    = msg.content;
        copy.media_kind = msg.media_kind;
        probe_history.push_back(std::move(copy));
    }
    probe_history.push_back({ "assistant", kSuffixProbe });

    common_chat_params params = {};
    if (!gs_try_build_chat_params(conversation, probe_history, false, &params)) {
        return false;
    }

    const size_t suffix_pos = params.prompt.rfind(kSuffixProbe);
    if (suffix_pos == std::string::npos) {
        gs_log(2, "Failed to locate assistant suffix probe in rendered chat template.");
        return false;
    }

    const std::string rendered_suffix =
        params.prompt.substr(suffix_pos + strlen(kSuffixProbe));
    if (rendered_suffix.empty()) {
        gs_log(2, "Rendered assistant suffix probe produced an empty suffix.");
        return false;
    }

    // We later commit eot_suffix_tokens directly into KV after each assistant
    // turn. This check makes sure the template's real rendered suffix matches
    // that token sequence before we trust text-delta prefix reuse.
    const llama_vocab* vocab = llama_model_get_vocab(conversation->engine->model);
    std::vector<llama_token> suffix_tokens =
        common_tokenize(vocab, rendered_suffix, false, true);
    if (suffix_tokens != conversation->eot_suffix_tokens) {
        gs_log(2,
               "Rendered assistant suffix does not match the committed end-of-turn token sequence; text-delta reuse will fall back to rebuilds.");
        return false;
    }

    conversation->verified_prompt_suffix = rendered_suffix;
    return true;
}

static bool gs_has_safe_text_delta_boundary(
    const GS_Conversation* conversation,
    const std::string&     committed_prompt)
{
    if (!conversation) {
        return false;
    }
    if (committed_prompt.empty()) {
        return true;
    }
    if (conversation->verified_prompt_suffix.empty()) {
        return false;
    }

    // If the committed prompt does not end at the verified assistant suffix,
    // tokenizing only the delta could split across an unsafe BPE boundary.
    const std::string& suffix = conversation->verified_prompt_suffix;
    return committed_prompt.size() >= suffix.size() &&
           committed_prompt.compare(
               committed_prompt.size() - suffix.size(),
               suffix.size(),
               suffix) == 0;
}

constexpr char kIncrementalProbeOlderUser[] =
    "GS_INCREMENTAL_OLDER_USER_3c06f7f4";
constexpr char kIncrementalProbeOlderAssistant[] =
    "GS_INCREMENTAL_OLDER_ASSISTANT_2a12e9d5";
constexpr char kIncrementalProbePreviousUser[] =
    "GS_INCREMENTAL_PREVIOUS_USER_7f09c4e1";
constexpr char kIncrementalProbePreviousAssistant[] =
    "GS_INCREMENTAL_PREVIOUS_ASSISTANT_56c1b88a";
constexpr char kIncrementalProbeTextTurn[] =
    "GS_INCREMENTAL_TEXT_TURN_4be8a94d";
constexpr char kIncrementalProbeMediaPrefix[] =
    "GS_INCREMENTAL_MEDIA_PREFIX_b46af1d1";
constexpr char kIncrementalProbeMediaSuffix[] =
    "GS_INCREMENTAL_MEDIA_SUFFIX_6b91d83e";
constexpr char kIncrementalProbeAssistantCommit[] =
    "GS_INCREMENTAL_ASSISTANT_COMMIT_5f30a12c";

static bool gs_is_media_turn(GS_ChatMessage::MediaKind media_kind) {
    return media_kind != GS_ChatMessage::MediaKind::None;
}

static void gs_append_initial_template_context(
    const GS_Conversation*     conversation,
    std::vector<GS_ChatMessage>* history)
{
    if (!conversation || !history) {
        return;
    }
    if (!conversation->config.system_message.empty()) {
        history->push_back({ "system", conversation->config.system_message });
    }
}

static std::vector<GS_ChatMessage> gs_build_incremental_probe_history(
    const GS_Conversation* conversation,
    bool                   include_older_round)
{
    std::vector<GS_ChatMessage> history;
    history.reserve((conversation && !conversation->config.system_message.empty() ? 1 : 0) +
                    (include_older_round ? 4 : 2));

    gs_append_initial_template_context(conversation, &history);
    if (include_older_round) {
        history.push_back({ "user", kIncrementalProbeOlderUser });
        history.push_back({ "assistant", kIncrementalProbeOlderAssistant });
    }
    history.push_back({ "user", kIncrementalProbePreviousUser });
    history.push_back({ "assistant", kIncrementalProbePreviousAssistant });
    return history;
}

static GS_ChatMessage gs_make_incremental_media_probe_message() {
    GS_ChatMessage message;
    message.role       = "user";
    message.media_kind = GS_ChatMessage::MediaKind::Audio;
    const std::string marker = mtmd_default_marker();
    message.content =
        std::string(kIncrementalProbeMediaPrefix) +
        "\n" + marker +
        "\n" + kIncrementalProbeMediaSuffix;
    return message;
}

static GS_ChatMessage gs_clone_message_for_template(
    const GS_ChatMessage& source)
{
    GS_ChatMessage copy;
    copy.role       = source.role;
    copy.content    = source.content;
    copy.media_kind = source.media_kind;
    return copy;
}

static bool gs_try_build_incremental_turn_params(
    GS_Conversation*         conversation,
    const GS_ChatMessage&    new_message,
    bool                     include_older_round,
    common_chat_params*      params_out,
    std::string*             prompt_delta_out)
{
    if (!conversation || !params_out || !prompt_delta_out ||
        conversation->verified_prompt_suffix.empty()) {
        return false;
    }

    std::vector<GS_ChatMessage> probe_history =
        gs_build_incremental_probe_history(conversation, include_older_round);
    probe_history.push_back(gs_clone_message_for_template(new_message));

    common_chat_params params = {};
    if (!gs_try_build_chat_params(conversation, probe_history, true, &params)) {
        return false;
    }

    const size_t probe_pos = params.prompt.rfind(kIncrementalProbePreviousAssistant);
    if (probe_pos == std::string::npos) {
        gs_log(2, "Failed to locate previous-assistant probe while building the incremental turn prompt.");
        return false;
    }

    const size_t suffix_pos =
        probe_pos + strlen(kIncrementalProbePreviousAssistant);
    const std::string& suffix = conversation->verified_prompt_suffix;
    if (params.prompt.size() < suffix_pos + suffix.size() ||
        params.prompt.compare(suffix_pos, suffix.size(), suffix) != 0) {
        gs_log(2, "Incremental turn probe does not preserve the verified assistant suffix boundary.");
        return false;
    }

    *prompt_delta_out = params.prompt.substr(suffix_pos + suffix.size());
    *params_out       = std::move(params);
    return true;
}

static bool gs_grammar_triggers_equal(
    const std::vector<common_grammar_trigger>& lhs,
    const std::vector<common_grammar_trigger>& rhs)
{
    if (lhs.size() != rhs.size()) {
        return false;
    }
    for (size_t i = 0; i < lhs.size(); ++i) {
        if (lhs[i].type != rhs[i].type ||
            lhs[i].value != rhs[i].value ||
            lhs[i].token != rhs[i].token) {
            return false;
        }
    }
    return true;
}

static bool gs_chat_params_match_for_incremental_reuse(
    const common_chat_params& lhs,
    const common_chat_params& rhs)
{
    return lhs.format == rhs.format &&
           lhs.grammar == rhs.grammar &&
           lhs.grammar_lazy == rhs.grammar_lazy &&
           lhs.generation_prompt == rhs.generation_prompt &&
           lhs.supports_thinking == rhs.supports_thinking &&
           lhs.thinking_start_tag == rhs.thinking_start_tag &&
           lhs.thinking_end_tag == rhs.thinking_end_tag &&
           gs_grammar_triggers_equal(lhs.grammar_triggers, rhs.grammar_triggers) &&
           lhs.preserved_tokens == rhs.preserved_tokens &&
           lhs.additional_stops == rhs.additional_stops &&
           lhs.parser == rhs.parser;
}

static bool gs_verify_incremental_turn_shape(
    GS_Conversation*      conversation,
    const GS_ChatMessage& probe_message,
    const char*           label)
{
    common_chat_params short_params = {};
    common_chat_params long_params  = {};
    std::string        short_delta;
    std::string        long_delta;

    if (!gs_try_build_incremental_turn_params(
            conversation, probe_message, false, &short_params, &short_delta) ||
        !gs_try_build_incremental_turn_params(
            conversation, probe_message, true, &long_params, &long_delta)) {
        gs_log(2, "%s probe failed; keeping full-history prompt rebuilds.", label);
        return false;
    }

    if (short_delta != long_delta) {
        gs_log(2,
               "%s probe produced history-dependent turn deltas; keeping full-history prompt rebuilds.",
               label);
        return false;
    }

    if (!gs_chat_params_match_for_incremental_reuse(short_params, long_params)) {
        gs_log(2,
               "%s probe produced history-dependent chat params; keeping full-history prompt rebuilds.",
               label);
        return false;
    }

    return true;
}

static bool gs_verify_plain_text_assistant_commit(
    GS_Conversation*      conversation,
    const GS_ChatMessage& probe_message,
    bool                  include_older_round,
    const char*           label)
{
    if (!conversation || conversation->verified_prompt_suffix.empty()) {
        return false;
    }

    std::vector<GS_ChatMessage> probe_history =
        gs_build_incremental_probe_history(conversation, include_older_round);
    probe_history.push_back(gs_clone_message_for_template(probe_message));

    common_chat_params generated_params = {};
    if (!gs_try_build_chat_params(conversation, probe_history, true, &generated_params)) {
        gs_log(2, "%s probe failed before assistant-commit verification.", label);
        return false;
    }

    probe_history.push_back({ "assistant", kIncrementalProbeAssistantCommit });
    common_chat_params committed_params = {};
    if (!gs_try_build_chat_params(conversation, probe_history, false, &committed_params)) {
        gs_log(2, "%s probe failed while verifying plain-text assistant commit.", label);
        return false;
    }

    const std::string expected_prompt =
        generated_params.prompt +
        std::string(kIncrementalProbeAssistantCommit) +
        conversation->verified_prompt_suffix;
    if (committed_params.prompt != expected_prompt) {
        gs_log(2,
               "%s probe showed that plain assistant text does not round-trip via simple concat; keeping the re-render fallback.",
               label);
        return false;
    }

    return true;
}

static bool gs_verify_plain_text_assistant_commit(
    GS_Conversation*      conversation,
    const GS_ChatMessage& probe_message,
    const char*           label)
{
    return gs_verify_plain_text_assistant_commit(
               conversation, probe_message, false, label) &&
           gs_verify_plain_text_assistant_commit(
               conversation, probe_message, true, label);
}

static void gs_init_incremental_turn_reuse(GS_Conversation* conversation) {
    if (!conversation || conversation->verified_prompt_suffix.empty()) {
        return;
    }

    GS_ChatMessage text_probe;
    text_probe.role    = "user";
    text_probe.content = kIncrementalProbeTextTurn;

    const GS_ChatMessage media_probe = gs_make_incremental_media_probe_message();

    conversation->verified_incremental_text_turn =
        gs_verify_incremental_turn_shape(
            conversation,
            text_probe,
            "Incremental text-turn prompt");
    conversation->verified_incremental_media_turn =
        gs_verify_incremental_turn_shape(
            conversation,
            media_probe,
            "Incremental media-turn prompt");
    conversation->verified_plain_text_commit_after_text_turn =
        gs_verify_plain_text_assistant_commit(
            conversation,
            text_probe,
            "Plain-text assistant commit after text turn");
    conversation->verified_plain_text_commit_after_media_turn =
        gs_verify_plain_text_assistant_commit(
            conversation,
            media_probe,
            "Plain-text assistant commit after media turn");

    gs_log(1,
           "Prompt fast-path verification: text_turn=%d media_turn=%d text_commit=%d media_commit=%d",
           conversation->verified_incremental_text_turn ? 1 : 0,
           conversation->verified_incremental_media_turn ? 1 : 0,
           conversation->verified_plain_text_commit_after_text_turn ? 1 : 0,
           conversation->verified_plain_text_commit_after_media_turn ? 1 : 0);
}

static bool gs_try_build_turn_prompt_incrementally(
    GS_Conversation*         conversation,
    const GS_ChatMessage&    new_message,
    const std::string&       committed_prompt_before,
    common_chat_params*      params_out)
{
    if (!conversation || !params_out ||
        committed_prompt_before.empty() ||
        !gs_has_safe_text_delta_boundary(conversation, committed_prompt_before)) {
        return false;
    }

    const bool allow_fast_path =
        gs_is_media_turn(new_message.media_kind)
            ? conversation->verified_incremental_media_turn
            : conversation->verified_incremental_text_turn;
    if (!allow_fast_path) {
        return false;
    }

    common_chat_params local_params = {};
    std::string        prompt_delta;
    if (!gs_try_build_incremental_turn_params(
            conversation,
            new_message,
            false,
            &local_params,
            &prompt_delta)) {
        return false;
    }

    local_params.prompt = committed_prompt_before + prompt_delta;
    *params_out         = std::move(local_params);
    return true;
}

static bool gs_can_concat_plain_text_commit(
    const GS_Conversation*      conversation,
    GS_ChatMessage::MediaKind   media_kind,
    const std::string&          assistant_history_content,
    const std::string&          normalized_text)
{
    if (!conversation ||
        assistant_history_content != normalized_text ||
        conversation->verified_prompt_suffix.empty()) {
        return false;
    }

    return gs_is_media_turn(media_kind)
        ? conversation->verified_plain_text_commit_after_media_turn
        : conversation->verified_plain_text_commit_after_text_turn;
}

// ---------------------------------------------------------------------------
// Sampler + KV state management
// ---------------------------------------------------------------------------

static common_params_sampling gs_build_sampling_params(
    GS_Conversation*          conversation,
    const common_chat_params* chat_params)
{
    common_params_sampling params;
    params.seed  = LLAMA_DEFAULT_SEED;
    // Gemma 4 best-practice defaults: temperature=1.0, top_p=0.95, top_k=64.
    // Keep the sampler chain aligned with that guidance and avoid layering
    // min-p on top unless a later task explicitly asks for a different policy.
    params.top_k = 64;
    params.top_p = 0.95f;
    params.min_p = 0.0f;
    params.temp  = 1.0f;
    params.samplers = {
        COMMON_SAMPLER_TYPE_TOP_K,
        COMMON_SAMPLER_TYPE_TOP_P,
        COMMON_SAMPLER_TYPE_TEMPERATURE,
    };

    if (!conversation || !conversation->engine || !chat_params) {
        return params;
    }

    if (conversation->config.constrained_decoding) {
        params.grammar = common_grammar(
            COMMON_GRAMMAR_TYPE_TOOL_CALLS, chat_params->grammar);
        params.grammar_lazy      = chat_params->grammar_lazy;
        params.grammar_triggers  = chat_params->grammar_triggers;
        params.generation_prompt = chat_params->generation_prompt;
    }

    if (conversation->config.enable_thinking &&
        !chat_params->thinking_start_tag.empty() &&
        !chat_params->thinking_end_tag.empty()) {
        const llama_vocab* vocab = llama_model_get_vocab(conversation->engine->model);
        params.reasoning_budget_start  = common_tokenize(vocab, chat_params->thinking_start_tag, false, true);
        params.reasoning_budget_end    = common_tokenize(vocab, chat_params->thinking_end_tag,   false, true);
        params.reasoning_budget_forced = common_tokenize(vocab, chat_params->thinking_end_tag,   false, true);
    }
    return params;
}

static bool gs_reset_sampler(
    GS_Conversation*          conversation,
    const common_chat_params* chat_params)
{
    if (!conversation || !conversation->engine) {
        return false;
    }

    common_params_sampling sampling_params =
        gs_build_sampling_params(conversation, chat_params);
    common_sampler* sampler = common_sampler_init(
        conversation->engine->model, sampling_params);
    if (!sampler) {
        gs_log(2, "Failed to create llama.cpp common sampler.");
        return false;
    }

    common_sampler_free(conversation->sampler);
    conversation->sampler = sampler;
    return true;
}

static void gs_rewind_conversation_state(GS_Conversation* conv, int32_t rollback_n_past) {
    if (!conv || !conv->ctx) {
        return;
    }
    if (llama_memory_t memory = llama_get_memory(conv->ctx)) {
        if (!llama_memory_seq_rm(memory, 0, rollback_n_past, -1)) {
            gs_log(2, "Failed to rewind KV cache cleanly from token position %d.",
                   rollback_n_past);
        }
    }
    conv->n_past = rollback_n_past;
    // Rewinding KV invalidates the sampler's rolling token state as well, so
    // the next decode step must restart from the shortened prompt boundary.
    common_sampler_reset(conv->sampler);
    llama_synchronize(conv->ctx);
}

static void gs_reset_conversation_state_for_full_rebuild(GS_Conversation* conv) {
    if (!conv || !conv->ctx) {
        return;
    }
    if (llama_memory_t memory = llama_get_memory(conv->ctx)) {
        llama_memory_clear(memory, false);
    }
    conv->n_past = 0;
    common_sampler_reset(conv->sampler);
    llama_synchronize(conv->ctx);
}

// ---------------------------------------------------------------------------
// Core decode loop (text-only prefill + sampling loop)
// ---------------------------------------------------------------------------

static GS_GenerationResult gs_generate(
    GS_Conversation*                conv,
    const std::vector<llama_token>& prompt_tokens,
    GS_StreamCallback               stream_cb   = nullptr,
    void*                           stream_data = nullptr)
{
    GS_GenerationResult generation = {};
    if (!conv || !conv->ctx || !conv->sampler || !conv->engine) {
        return generation;
    }

    const llama_vocab* vocab   = llama_model_get_vocab(conv->engine->model);
    const uint32_t     n_ctx   = llama_n_ctx(conv->ctx);
    const int32_t      n_batch = static_cast<int32_t>(llama_n_batch(conv->ctx));

    conv->cancel_flag.store(false, std::memory_order_release);
    common_sampler_reset(conv->sampler);

    generation.prompt_tokens = static_cast<int32_t>(prompt_tokens.size());
    std::string  stream_buffer;
    std::string  committed_text;
    std::vector<std::string> committed_pieces;
    const size_t stream_holdback = gs_template_marker_holdback();

    gs_log(0, "Prefill: %zu tokens starting at n_past=%d",
           prompt_tokens.size(), conv->n_past);

    for (size_t i = 0; i < prompt_tokens.size(); ) {
        const int32_t n_tokens = std::min(
            n_batch, static_cast<int32_t>(prompt_tokens.size() - i));

        llama_batch batch = llama_batch_get_one(
            const_cast<llama_token*>(prompt_tokens.data() + i), n_tokens);

        const int32_t rc = llama_decode(conv->ctx, batch);
        if (rc == 2) {
            generation.cancelled = true;
            gs_log(1, "Generation cancelled during prefill at n_past=%d", conv->n_past);
            return generation;
        }
        if (rc != 0) {
            gs_log(3, "llama_decode failed during prefill (rc=%d, n_past=%d, batch=%d)",
                   rc, conv->n_past, n_tokens);
            return generation;
        }

        conv->n_past += n_tokens;
        i            += static_cast<size_t>(n_tokens);
    }

    const int32_t max_decode_tokens = static_cast<int32_t>(n_ctx) - conv->n_past;
    const int32_t decode_start_n_past = conv->n_past;
    const auto decode_started_at = std::chrono::steady_clock::now();
    bool saw_first_decode_token = false;
    for (int32_t i = 0; i < max_decode_tokens; ++i) {
        if (conv->cancel_flag.load(std::memory_order_acquire)) {
            gs_log(1, "Generation cancelled after %d tokens", i);
            generation.cancelled = true;
            return generation;
        }

        llama_token new_token       = common_sampler_sample(conv->sampler, conv->ctx, -1);
        const bool  is_end_of_turn  = (conv->tok_end_of_turn >= 0 &&
                                       new_token == conv->tok_end_of_turn);
        if (llama_vocab_is_eog(vocab, new_token) || is_end_of_turn) {
            gs_log(0, "End-of-turn (id=%d) reached after %d decode tokens", new_token, i);
            break;
        }

        if (!saw_first_decode_token) {
            generation.ttft_s = std::chrono::duration<double>(
                std::chrono::steady_clock::now() - decode_started_at).count();
            saw_first_decode_token = true;
        }

        const std::string token_piece = gs_token_to_str(vocab, new_token);
        stream_buffer += token_piece;

        const char*  leaked_marker = nullptr;
        const size_t marker_pos    = gs_find_template_marker(stream_buffer, &leaked_marker);
        if (marker_pos != std::string::npos) {
            if (marker_pos > 0) {
                const std::string visible = stream_buffer.substr(0, marker_pos);
                generation.text += visible;
                if (stream_cb) {
                    stream_cb(visible.c_str(), stream_data);
                }
            }
            gs_log(2,
                   "%s leaked into output after %d tokens; "
                   "stopping generation before commit",
                   leaked_marker ? leaked_marker : "Template marker", i);
            break;
        }

        if (stream_buffer.size() > stream_holdback) {
            const size_t      flush_len = stream_buffer.size() - stream_holdback;
            const std::string visible   = stream_buffer.substr(0, flush_len);
            generation.text += visible;
            if (stream_cb) {
                stream_cb(visible.c_str(), stream_data);
            }
            stream_buffer.erase(0, flush_len);
        }

        common_sampler_accept(conv->sampler, new_token, true);
        generation.decode_tokens += 1;
        committed_pieces.push_back(token_piece);
        committed_text += token_piece;

        llama_batch   batch = llama_batch_get_one(&new_token, 1);
        const int32_t rc    = llama_decode(conv->ctx, batch);
        if (rc == 2) {
            generation.cancelled = true;
            gs_log(1, "Generation cancelled while committing token %d", new_token);
            return generation;
        }
        if (rc != 0) {
            gs_log(3, "llama_decode failed during generation (rc=%d, token=%d)",
                   rc, new_token);
            return generation;
        }

        conv->n_past += 1;
    }

    generation.decode_s = std::chrono::duration<double>(
        std::chrono::steady_clock::now() - decode_started_at).count();

    gs_strip_trailing_template_prefix(&stream_buffer);
    if (!stream_buffer.empty()) {
        generation.text += stream_buffer;
        if (stream_cb) {
            stream_cb(stream_buffer.c_str(), stream_data);
        }
    }

    // The stream callback may already have seen bytes that we trim away here.
    // The final JSON/text response is authoritative for persisted history.
    generation.text = committed_text;
    gs_trim_generated_text(&generation.text);

    if (generation.text.size() != committed_text.size()) {
        // Template-marker cleanup can remove bytes from the persisted response.
        // We only keep the trim if it lines up with a full-token boundary in
        // KV; otherwise the committed prompt is rebuilt on the safe path.
        std::string reconstructed;
        int32_t     kept_tokens    = 0;
        bool        boundary_found = generation.text.empty();

        if (!generation.text.empty()) {
            for (int32_t i = 0; i < static_cast<int32_t>(committed_pieces.size()); ++i) {
                reconstructed += committed_pieces[static_cast<size_t>(i)];
                if (reconstructed == generation.text) {
                    kept_tokens    = i + 1;
                    boundary_found = true;
                    break;
                }
                if (reconstructed.size() > generation.text.size() ||
                    generation.text.compare(0, reconstructed.size(), reconstructed) != 0) {
                    break;
                }
            }
        }

        if (boundary_found) {
            if (kept_tokens < generation.decode_tokens) {
                gs_rewind_conversation_state(conv, decode_start_n_past + kept_tokens);
                generation.decode_tokens = kept_tokens;
            }
        } else {
            generation.needs_kv_resync = true;
        }
    }

    generation.ok = true;
    gs_log(0, "Generation complete: %zu chars, n_past=%d",
           generation.text.size(), conv->n_past);
    return generation;
}

// ---------------------------------------------------------------------------
// Response builders
// ---------------------------------------------------------------------------

static GS_JsonResponse* gs_build_json_response(const std::string& json_text) {
    auto* response    = new GS_JsonResponse();
    response->content = json_text;
    return response;
}

static GS_JsonResponse* gs_build_text_response(const std::string& text) {
    auto* response    = new GS_JsonResponse();
    response->content = nlohmann::json{
        { "role",    "assistant" },
        { "content", text        },
    }.dump();
    return response;
}

GS_JsonResponse* gs_build_turn_response(const GS_GenerationResult& result) {
    if (!result.json_body.empty()) {
        return gs_build_json_response(result.json_body);
    }
    return gs_build_text_response(result.text);
}

// ---------------------------------------------------------------------------
// Multimodal prefill
// ---------------------------------------------------------------------------

static GS_GenerationResult gs_prefill_text_tokens(
    GS_Conversation*                conversation,
    const std::vector<llama_token>& tokens)
{
    GS_GenerationResult result = {};
    if (!conversation || !conversation->ctx) {
        return result;
    }
    if (tokens.empty()) {
        result.ok = true;
        return result;
    }

    const int32_t n_batch = static_cast<int32_t>(llama_n_batch(conversation->ctx));
    result.prompt_tokens   = static_cast<int32_t>(tokens.size());

    for (size_t pos = 0; pos < tokens.size(); ) {
        const int32_t batch_tokens = std::min(
            n_batch, static_cast<int32_t>(tokens.size() - pos));
        llama_batch batch = llama_batch_get_one(
            const_cast<llama_token*>(tokens.data() + pos), batch_tokens);

        const int32_t rc = llama_decode(conversation->ctx, batch);
        if (rc == 2) {
            result.cancelled = true;
            gs_log(1, "Generation cancelled during text prefill.");
            return result;
        }
        if (rc != 0) {
            gs_log(3, "llama_decode failed during text prefill (rc=%d).", rc);
            return result;
        }

        conversation->n_past += batch_tokens;
        pos                  += static_cast<size_t>(batch_tokens);
    }

    result.ok = true;
    return result;
}

static GS_GenerationResult gs_prefill_text_prompt(
    GS_Conversation*   conversation,
    const std::string& prompt_text)
{
    GS_GenerationResult result = {};
    if (!conversation || !conversation->ctx || !conversation->engine) {
        return result;
    }
    if (prompt_text.empty()) {
        result.ok = true;
        return result;
    }

    const llama_vocab* vocab = llama_model_get_vocab(conversation->engine->model);
    std::vector<llama_token> tokens = common_tokenize(vocab, prompt_text, false, true);
    if (tokens.empty()) {
        gs_log(3, "Turn execution: tokenization produced 0 tokens.");
        return result;
    }

    const uint32_t n_ctx = llama_n_ctx(conversation->ctx);
    if (static_cast<uint32_t>(conversation->n_past + tokens.size()) >= n_ctx) {
        gs_log(3, "Turn execution: prompt (%zu) + n_past (%d) exceeds n_ctx (%u)",
               tokens.size(), conversation->n_past, n_ctx);
        return result;
    }

    return gs_prefill_text_tokens(conversation, tokens);
}

static GS_GenerationResult gs_prefill_multimodal_prompt(
    GS_Conversation*                    conversation,
    const std::string&                  prompt_text,
    const std::vector<const mtmd_bitmap*>& bitmaps)
{
    GS_GenerationResult result = {};
    if (!conversation || !conversation->engine || !conversation->engine->mtmd ||
        !conversation->ctx) {
        return result;
    }
    if (prompt_text.empty()) {
        result.ok = true;
        return result;
    }

    mtmd_input_text input_text = { prompt_text.c_str(), false, true };

    mtmd::input_chunks chunks(mtmd_input_chunks_init());
    if (!chunks.ptr) {
        gs_log(3, "Failed to allocate mtmd input chunks.");
        return result;
    }

    const int32_t tokenize_rc = mtmd_tokenize(
        conversation->engine->mtmd,
        chunks.ptr.get(),
        &input_text,
        const_cast<const mtmd_bitmap**>(bitmaps.data()),
        bitmaps.size());
    if (tokenize_rc != 0) {
        gs_log(3, "mtmd_tokenize failed (rc=%d).", tokenize_rc);
        return result;
    }

    // For multimodal chunks, position count is the real context cost. Media
    // can occupy more positions than the literal marker text suggests.
    const uint32_t n_ctx = llama_n_ctx(conversation->ctx);
    const llama_pos prompt_n_pos = mtmd_helper_get_n_pos(chunks.ptr.get());
    if (static_cast<llama_pos>(conversation->n_past) + prompt_n_pos >=
        static_cast<llama_pos>(n_ctx)) {
        gs_log(3, "Turn execution: multimodal prompt positions (%d) + n_past (%d) exceeds n_ctx (%u).",
               static_cast<int>(prompt_n_pos), conversation->n_past, n_ctx);
        return result;
    }

    const int32_t n_batch = static_cast<int32_t>(llama_n_batch(conversation->ctx));
    llama_pos     new_n_past = conversation->n_past;
    const int32_t rc = mtmd_helper_eval_chunks(
        conversation->engine->mtmd,
        conversation->ctx,
        chunks.ptr.get(),
        conversation->n_past,
        0,
        n_batch,
        true,
        &new_n_past);
    if (rc == 2) {
        result.cancelled = true;
        gs_log(1, "Generation cancelled during multimodal prefill.");
        return result;
    }
    if (rc != 0) {
        gs_log(3, "mtmd_helper_eval_chunks failed (rc=%d).", rc);
        return result;
    }

    conversation->n_past = static_cast<int32_t>(new_n_past);
    result.prompt_tokens = static_cast<int32_t>(mtmd_helper_get_n_tokens(chunks.ptr.get()));
    result.ok            = true;
    result.decode_tokens = 0;
    return result;
}

static bool gs_restore_committed_prompt_state(
    GS_Conversation*   conversation,
    const std::string& committed_prompt,
    size_t             committed_bitmap_count,
    int32_t*           reprefilled_tokens = nullptr)
{
    if (reprefilled_tokens) {
        *reprefilled_tokens = 0;
    }
    if (!conversation) {
        return false;
    }

    // Used after a failed turn when KV may no longer match the last good
    // snapshot. Rebuild exactly the last committed prompt so later turns start
    // from a known-good state instead of half-applied history.
    gs_reset_conversation_state_for_full_rebuild(conversation);
    if (committed_prompt.empty()) {
        conversation->committed_prompt.clear();
        conversation->committed_bitmap_count = 0;
        return true;
    }

    GS_GenerationResult restore = {};
    if (committed_bitmap_count > 0) {
        std::vector<const mtmd_bitmap*> bitmaps;
        if (!gs_collect_media_bitmap_ptrs(conversation->engine->mtmd,
                                          &conversation->chat_history,
                                          0,
                                          &bitmaps)) {
            return false;
        }
        if (bitmaps.size() < committed_bitmap_count) {
            gs_log(3, "Committed multimodal state is missing cached bitmaps.");
            return false;
        }
        bitmaps.resize(committed_bitmap_count);
        restore = gs_prefill_multimodal_prompt(conversation, committed_prompt, bitmaps);
    } else {
        restore = gs_prefill_text_prompt(conversation, committed_prompt);
    }

    if (!restore.ok) {
        return false;
    }

    conversation->committed_prompt       = committed_prompt;
    conversation->committed_bitmap_count = committed_bitmap_count;
    if (reprefilled_tokens) {
        *reprefilled_tokens = restore.prompt_tokens;
    }
    return true;
}

static bool gs_restore_committed_prompt_delta(
    GS_Conversation*   conversation,
    const std::string& committed_prompt_before,
    size_t             committed_bitmap_count_before,
    int32_t            n_past_before,
    const std::string& committed_prompt_after,
    size_t             committed_bitmap_count_after,
    int32_t*           reprefilled_tokens = nullptr)
{
    if (reprefilled_tokens) {
        *reprefilled_tokens = 0;
    }
    if (!conversation) {
        return false;
    }

    if (committed_prompt_after.size() < committed_prompt_before.size() ||
        committed_prompt_after.compare(0,
                                       committed_prompt_before.size(),
                                       committed_prompt_before) != 0) {
        return false;
    }

    if (!committed_prompt_before.empty() &&
        !gs_has_safe_text_delta_boundary(conversation, committed_prompt_before)) {
        return false;
    }

    if (committed_bitmap_count_after < committed_bitmap_count_before) {
        return false;
    }

    const std::string prompt_delta =
        committed_prompt_after.substr(committed_prompt_before.size());
    const size_t new_media_count =
        committed_bitmap_count_after - committed_bitmap_count_before;

    gs_rewind_conversation_state(conversation, n_past_before);

    GS_GenerationResult restore = {};
    if (new_media_count == 0) {
        restore = gs_prefill_text_prompt(conversation, prompt_delta);
    } else {
        const std::string marker = mtmd_default_marker();
        const size_t marker_count = gs_count_occurrences(prompt_delta, marker);
        if (marker_count != new_media_count) {
            return false;
        }

        std::vector<const mtmd_bitmap*> bitmaps;
        if (!gs_collect_media_bitmap_ptrs(
                conversation->engine->mtmd,
                &conversation->chat_history,
                committed_bitmap_count_before,
                &bitmaps)) {
            return false;
        }

        restore = gs_prefill_multimodal_prompt(conversation, prompt_delta, bitmaps);
    }

    if (!restore.ok) {
        return false;
    }

    conversation->committed_prompt = committed_prompt_after;
    conversation->committed_bitmap_count = committed_bitmap_count_after;
    if (reprefilled_tokens) {
        *reprefilled_tokens = restore.prompt_tokens;
    }
    return true;
}

static bool gs_restore_committed_prompt_delta_or_full(
    GS_Conversation*   conversation,
    const std::string& committed_prompt_before,
    size_t             committed_bitmap_count_before,
    int32_t            n_past_before,
    const std::string& committed_prompt_after,
    size_t             committed_bitmap_count_after,
    bool*              used_full_rebuild = nullptr,
    int32_t*           reprefilled_tokens = nullptr)
{
    if (used_full_rebuild) {
        *used_full_rebuild = false;
    }
    if (reprefilled_tokens) {
        *reprefilled_tokens = 0;
    }

    if (gs_restore_committed_prompt_delta(
            conversation,
            committed_prompt_before,
            committed_bitmap_count_before,
            n_past_before,
            committed_prompt_after,
            committed_bitmap_count_after,
            reprefilled_tokens)) {
        return true;
    }

    if (!gs_restore_committed_prompt_state(
            conversation,
            committed_prompt_after,
            committed_bitmap_count_after,
            reprefilled_tokens)) {
        return false;
    }

    if (used_full_rebuild) {
        *used_full_rebuild = true;
    }
    return true;
}

static bool gs_try_commit_assistant_suffix(
    GS_Conversation* conversation,
    int32_t*         committed_tokens = nullptr)
{
    if (committed_tokens) {
        *committed_tokens = 0;
    }
    if (!conversation) {
        return false;
    }
    if (conversation->eot_suffix_tokens.empty()) {
        return true;
    }

    const int32_t n_suffix = static_cast<int32_t>(conversation->eot_suffix_tokens.size());
    llama_batch   suffix_batch = llama_batch_get_one(
        conversation->eot_suffix_tokens.data(), n_suffix);
    const int32_t rc = llama_decode(conversation->ctx, suffix_batch);
    if (rc != 0) {
        gs_log(3, "Failed to commit end-of-turn suffix to KV (rc=%d).", rc);
        return false;
    }

    conversation->n_past += n_suffix;
    if (committed_tokens) {
        *committed_tokens = n_suffix;
    }
    return true;
}

// ---------------------------------------------------------------------------
// Turn execution
// ---------------------------------------------------------------------------

GS_GenerationResult gs_run_user_turn(
    GS_Conversation*          conversation,
    const std::string&        role,
    const std::string&        text,
    GS_StreamCallback         callback,
    void*                     callback_data,
    GS_ChatMessage::MediaKind media_kind,
    std::vector<uint8_t>      media_bytes)
{
    GS_GenerationResult result = {};
    if (!conversation || !conversation->engine || !conversation->ctx ||
        !conversation->sampler || !conversation->chat_templates || role.empty()) {
        return result;
    }

    gs_clear_last_benchmark(conversation);

    const size_t  history_size_before              = conversation->chat_history.size();
    const int32_t n_past_before                    = conversation->n_past;
    const std::string committed_prompt_before      = conversation->committed_prompt;
    const size_t      committed_bitmap_count_before = conversation->committed_bitmap_count;
    bool              kv_cleared_since_turn_start   = false;

    GS_ChatMessage user_message;
    user_message.role        = role;
    user_message.content     = text;
    user_message.media_kind  = media_kind;
    user_message.media_bytes = std::move(media_bytes);
    conversation->chat_history.push_back(std::move(user_message));

    auto rollback_history_only = [&]() {
        conversation->chat_history.resize(history_size_before);
        conversation->committed_prompt       = committed_prompt_before;
        conversation->committed_bitmap_count = committed_bitmap_count_before;
    };

    auto restore_previous_state = [&]() {
        rollback_history_only();
        bool restored = true;
        if (kv_cleared_since_turn_start) {
            restored = gs_restore_committed_prompt_state(
                conversation,
                committed_prompt_before,
                committed_bitmap_count_before);
        } else {
            gs_rewind_conversation_state(conversation, n_past_before);
        }
        if (!restored) {
            gs_log(3, "Failed to restore prior committed conversation state.");
            gs_reset_conversation_state_for_full_rebuild(conversation);
            conversation->committed_prompt.clear();
            conversation->committed_bitmap_count = 0;
        }
        conversation->cancel_flag.store(false, std::memory_order_release);
    };

    double phase_template_build_s = 0.0;
    double phase_prefill_s         = 0.0;

    common_chat_params chat_params = {};
    {
        const auto template_started_at = std::chrono::steady_clock::now();
        bool template_ok = false;
        if (role == "user" && !conversation->chat_history.empty()) {
            template_ok = gs_try_build_turn_prompt_incrementally(
                conversation,
                conversation->chat_history.back(),
                committed_prompt_before,
                &chat_params);
        }
        if (!template_ok) {
            template_ok = gs_try_build_chat_params(
                conversation, true, &chat_params);
        }
        phase_template_build_s += std::chrono::duration<double>(
            std::chrono::steady_clock::now() - template_started_at).count();
        if (!template_ok) {
            gs_log(3, "Turn execution: failed to apply built-in chat template.");
            rollback_history_only();
            return result;
        }
    }
    const std::string& formatted = chat_params.prompt;

    if (formatted.empty()) {
        gs_log(3, "Turn execution: chat template produced empty prompt.");
        rollback_history_only();
        return result;
    }

    if (!gs_reset_sampler(conversation, &chat_params)) {
        rollback_history_only();
        return result;
    }

    conversation->cancel_flag.store(false, std::memory_order_release);
    llama_perf_context_reset(conversation->ctx);
    const size_t total_media_count = gs_count_media_messages(conversation->chat_history);
    bool         use_delta_prefill =
        formatted.size() >= committed_prompt_before.size() &&
        formatted.compare(0, committed_prompt_before.size(),
                          committed_prompt_before) == 0 &&
        gs_has_safe_text_delta_boundary(conversation, committed_prompt_before) &&
        total_media_count >= committed_bitmap_count_before;

    GS_GenerationResult prefill = {};
    if (use_delta_prefill) {
        // The happy path: the new rendered prompt only extends the previously
        // committed one, so we evaluate just the suffix instead of replaying
        // the full conversation.
        const std::string prompt_delta =
            formatted.substr(committed_prompt_before.size());
        const size_t new_media_count =
            total_media_count - committed_bitmap_count_before;

        if (new_media_count == 0) {
            gs_log(0, "Reusing committed prompt prefix (%zu chars); feeding %zu new text chars.",
                   committed_prompt_before.size(), prompt_delta.size());
            const auto prefill_started_at = std::chrono::steady_clock::now();
            prefill = gs_prefill_text_prompt(conversation, prompt_delta);
            phase_prefill_s += std::chrono::duration<double>(
                std::chrono::steady_clock::now() - prefill_started_at).count();
        } else {
            const std::string marker = mtmd_default_marker();
            const size_t      marker_count =
                gs_count_occurrences(prompt_delta, marker);
            if (marker_count != new_media_count) {
                gs_log(2,
                       "Rendered prompt delta contains %zu media markers but %zu new media payloads were added; falling back to full rebuild.",
                       marker_count,
                       new_media_count);
                use_delta_prefill = false;
            } else {
                std::vector<const mtmd_bitmap*> bitmaps;
                if (!gs_collect_media_bitmap_ptrs(
                        conversation->engine->mtmd,
                        &conversation->chat_history,
                        committed_bitmap_count_before,
                        &bitmaps)) {
                    restore_previous_state();
                    return result;
                }
                const auto prefill_started_at = std::chrono::steady_clock::now();
                prefill = gs_prefill_multimodal_prompt(
                    conversation, prompt_delta, bitmaps);
                phase_prefill_s += std::chrono::duration<double>(
                    std::chrono::steady_clock::now() - prefill_started_at).count();
            }
        }
    }

    if (!use_delta_prefill) {
        // Any uncertainty about prefix stability, template boundary safety, or
        // media alignment takes the conservative route: clear KV and rebuild
        // from chat history rather than risk silent prompt/KV drift.
        if (!committed_prompt_before.empty() &&
            formatted.size() >= committed_prompt_before.size() &&
            formatted.compare(0, committed_prompt_before.size(),
                              committed_prompt_before) == 0 &&
            !gs_has_safe_text_delta_boundary(conversation, committed_prompt_before)) {
            gs_log(2,
                   "Committed prompt does not end at a verified special-token boundary; falling back to full rebuild.");
        }
        gs_log(0, "Rendered prompt is not a safe prefix-extension; rebuilding committed context.");
        gs_reset_conversation_state_for_full_rebuild(conversation);
        kv_cleared_since_turn_start = true;

        if (total_media_count > 0) {
            std::vector<const mtmd_bitmap*> bitmaps;
            if (!gs_collect_media_bitmap_ptrs(conversation->engine->mtmd,
                                              &conversation->chat_history,
                                              0,
                                              &bitmaps)) {
                restore_previous_state();
                return result;
            }
            const auto prefill_started_at = std::chrono::steady_clock::now();
            prefill = gs_prefill_multimodal_prompt(conversation, formatted, bitmaps);
            phase_prefill_s += std::chrono::duration<double>(
                std::chrono::steady_clock::now() - prefill_started_at).count();
        } else {
            const auto prefill_started_at = std::chrono::steady_clock::now();
            prefill = gs_prefill_text_prompt(conversation, formatted);
            phase_prefill_s += std::chrono::duration<double>(
                std::chrono::steady_clock::now() - prefill_started_at).count();
        }
    }

    if (!prefill.ok) {
        if (prefill.cancelled) {
            gs_log(1, "Turn execution cancelled during prefill. Rolling conversation state back.");
        }
        restore_previous_state();
        return result;
    }

    std::vector<llama_token> empty_prompt;
    result = gs_generate(conversation, empty_prompt, callback, callback_data);
    result.prompt_tokens += prefill.prompt_tokens;

    if (!result.ok) {
        if (result.cancelled) {
            gs_log(1, "Turn execution cancelled. Rolling conversation state back.");
        }
        restore_previous_state();
        return result;
    }

    std::string normalized_text = result.text;
    gs_strip_prefix(&normalized_text, chat_params.generation_prompt);

    std::string assistant_history_content = normalized_text;
    try {
        common_chat_msg parsed = common_chat_parse(
            normalized_text, false, gs_build_chat_parser_params(chat_params));
        parsed.role               = "assistant";
        gs_set_assistant_history_content(
            parsed, normalized_text, &assistant_history_content, &result.json_body);
    } catch (const std::exception& ex) {
        gs_log(2, "Failed to parse built-in chat response; keeping raw text (%s)", ex.what());
    }

    const bool retained_media_turn =
        conversation->config.retain_text_only_media_history &&
        gs_is_media_turn(media_kind);
    if (conversation->config.retain_text_only_media_history) {
        gs_convert_last_media_message_to_text_only(&conversation->chat_history);
    }

    // For retained media turns the committed prompt must reflect the text-only
    // form of the just-processed user message. Render it via the short
    // incremental probe so repeated turns never pay an O(N^2) full-history
    // Jinja rebuild.
    std::string               committed_prompt_prefix         = formatted;
    GS_ChatMessage::MediaKind committed_history_media_kind    = media_kind;
    bool committed_prompt_prefix_matches_history              = true;
    if (retained_media_turn) {
        committed_prompt_prefix_matches_history = false;
        common_chat_params committed_turn_params = {};
        if (!conversation->chat_history.empty() &&
            gs_try_build_turn_prompt_incrementally(
                conversation,
                conversation->chat_history.back(),
                committed_prompt_before,
                &committed_turn_params)) {
            committed_prompt_prefix = std::move(committed_turn_params.prompt);
            committed_history_media_kind = GS_ChatMessage::MediaKind::None;
            committed_prompt_prefix_matches_history = true;
        }
    }

    conversation->chat_history.push_back({ "assistant", assistant_history_content });

    std::string committed_rendered_prompt;
    const size_t      committed_bitmap_count = gs_count_media_messages(conversation->chat_history);
    bool assistant_roundtrip_matches = false;
    if (committed_prompt_prefix_matches_history &&
        gs_can_concat_plain_text_commit(
            conversation,
            committed_history_media_kind,
            assistant_history_content,
            normalized_text)) {
        committed_rendered_prompt  = committed_prompt_prefix;
        committed_rendered_prompt += normalized_text;
        committed_rendered_prompt += conversation->verified_prompt_suffix;
        assistant_roundtrip_matches = true;
    } else {
        common_chat_params committed_chat_params = {};
        const auto template_started_at = std::chrono::steady_clock::now();
        const bool template_ok =
            gs_try_build_chat_params(conversation, false, &committed_chat_params);
        phase_template_build_s += std::chrono::duration<double>(
            std::chrono::steady_clock::now() - template_started_at).count();
        if (!template_ok) {
            gs_log(3, "Turn execution: failed to rebuild committed prompt after response parse.");
            result.ok = false;
            restore_previous_state();
            return result;
        }

        committed_rendered_prompt = std::move(committed_chat_params.prompt);
        const std::string expected_prefix = formatted + normalized_text;
        assistant_roundtrip_matches =
            committed_rendered_prompt.size() >= expected_prefix.size() &&
            committed_rendered_prompt.compare(
                0,
                expected_prefix.size(),
                expected_prefix) == 0;
    }
    gs_store_generation_benchmark(conversation, result, phase_prefill_s);

    bool commit_ok = true;
    bool commit_used_full_rebuild = false;
    // Retained media turns must always rewind and re-prefill: the just-finished
    // decode fed audio/image tokens into KV, but committed_rendered_prompt and
    // chat_history are now the text-only version. Without the delta restore,
    // KV would keep the raw media tokens forever and GS-58's cache savings
    // would silently collapse back to full-mode growth.
    const bool needs_delta_restore =
        result.needs_kv_resync || !assistant_roundtrip_matches ||
        retained_media_turn;

    const auto commit_started_at = std::chrono::steady_clock::now();
    // Tokens fed to llama_decode during the commit phase. On the happy path
    // this is just the end-of-turn suffix length; on the delta-restore path
    // (e.g. after reasoning stripping) this is the re-prefilled user+assistant
    // delta. A net n_past difference is misleading here because the delta
    // path first rewinds and then re-prefills, so the net change can be
    // negative even though real decode work occurred.
    int32_t commit_tokens = 0;

    if (needs_delta_restore) {
        // The assistant response is stored as parsed JSON and then re-rendered
        // by the template on the next turn. Rewind only the current turn and
        // replay the committed delta instead of rebuilding the full history.
        if (result.needs_kv_resync) {
            gs_log(2, "Generated suffix could not be trimmed at token boundaries; restoring the committed turn delta.");
        }
        if (!assistant_roundtrip_matches) {
            gs_log(2, "Assistant response does not round-trip through the chat template; restoring the committed turn delta.");
        }

        commit_ok = gs_restore_committed_prompt_delta_or_full(
            conversation,
            committed_prompt_before,
            committed_bitmap_count_before,
            n_past_before,
            committed_rendered_prompt,
            committed_bitmap_count,
            &commit_used_full_rebuild,
            &commit_tokens);
        if (!commit_ok) {
            gs_log(2, "Failed to restore the current turn delta or rebuild the full committed prompt.");
        }
    } else {
        // The decode loop breaks on EOG without committing the
        // `<end_of_turn>` marker to KV, but the chat template always appends
        // `<end_of_turn>\n` after each message. Feed that suffix into KV so
        // the next turn's prefill keeps the separator in the model's
        // attention window.
        if (!gs_try_commit_assistant_suffix(conversation, &commit_tokens)) {
            gs_log(3, "Failed to commit end-of-turn suffix; restoring the committed turn delta.");
            commit_ok = gs_restore_committed_prompt_delta_or_full(
                conversation,
                committed_prompt_before,
                committed_bitmap_count_before,
                n_past_before,
                committed_rendered_prompt,
                committed_bitmap_count,
                &commit_used_full_rebuild,
                &commit_tokens);
            if (!commit_ok) {
                gs_log(2, "Failed to restore the current turn delta after suffix commit failure.");
            }
        } else {
            conversation->committed_prompt = committed_rendered_prompt;
            conversation->committed_bitmap_count = committed_bitmap_count;
        }
    }

    if (!commit_ok) {
        result.ok = false;
        gs_clear_last_benchmark(conversation);
        restore_previous_state();
        return result;
    }

    if (commit_used_full_rebuild) {
        kv_cleared_since_turn_start = true;
    }

    // llama_decode queues work on the GPU asynchronously. Without this sync
    // the commit timer only captures CPU-side command queuing, and the real
    // GPU cost leaks into the next turn's wall measurements.
    llama_synchronize(conversation->ctx);
    const double commit_overhead_s = std::chrono::duration<double>(
        std::chrono::steady_clock::now() - commit_started_at).count();
    gs_store_commit_benchmark(conversation, commit_overhead_s, commit_tokens);
    conversation->last_benchmark.phase_template_build_s = phase_template_build_s;
    conversation->last_benchmark.phase_prefill_s        = phase_prefill_s;

    conversation->cancel_flag.store(false, std::memory_order_release);
    return result;
}

// ---------------------------------------------------------------------------
// Public C API — conversation lifecycle, text + message entry points
// ---------------------------------------------------------------------------

namespace {

struct GS_MessagePayload {
    std::string role;
    std::string text;
};

bool gs_extract_text_content(const nlohmann::json& content, std::string* text) {
    if (!text) {
        return false;
    }
    if (content.is_string()) {
        *text = content.get<std::string>();
        return true;
    }
    if (!content.is_array()) {
        return false;
    }

    std::string combined;
    for (const auto& part : content) {
        if (!part.is_object()) {
            continue;
        }
        auto type_it = part.find("type");
        if (type_it == part.end() || !type_it->is_string() ||
            type_it->get<std::string>() != "text") {
            continue;
        }
        auto text_it = part.find("text");
        if (text_it == part.end() || !text_it->is_string()) {
            continue;
        }
        if (!combined.empty()) {
            combined += "\n";
        }
        combined += text_it->get<std::string>();
    }
    if (combined.empty()) {
        return false;
    }
    *text = std::move(combined);
    return true;
}

bool gs_parse_message_json(const char* message_json, GS_MessagePayload* payload) {
    if (!message_json || !payload) {
        return false;
    }
    try {
        const nlohmann::json parsed = nlohmann::json::parse(message_json);
        if (!parsed.is_object()) {
            gs_log(3, "SendMessage: payload is not a JSON object.");
            return false;
        }
        auto role_it = parsed.find("role");
        if (role_it == parsed.end() || !role_it->is_string()) {
            gs_log(3, "SendMessage: payload is missing a string \"role\" field.");
            return false;
        }
        payload->role = ToLowerCopy(role_it->get<std::string>());
        if (payload->role != "user") {
            gs_log(2, "SendMessage: only role=\"user\" is supported.");
            return false;
        }
        auto content_it = parsed.find("content");
        if (content_it == parsed.end()) {
            gs_log(3, "SendMessage: payload is missing a \"content\" field.");
            return false;
        }
        if (!gs_extract_text_content(*content_it, &payload->text)) {
            gs_log(2, "SendMessage: only text content is supported on this entry point.");
            return false;
        }
    } catch (const std::exception& ex) {
        gs_log(3, "SendMessage: invalid JSON payload (%s)", ex.what());
        return false;
    }
    return true;
}

bool gs_parse_and_cache_tools(
    const std::string&             tools_json,
    std::vector<common_chat_tool>* out)
{
    if (!out) {
        return false;
    }
    out->clear();
    if (tools_json.empty()) {
        return true;
    }
    try {
        const nlohmann::ordered_json parsed = nlohmann::ordered_json::parse(tools_json);
        if (!parsed.is_array()) {
            gs_log(2, "tools_json must be a JSON array.");
            return false;
        }
        *out = common_chat_tools_parse_oaicompat(parsed);
        return true;
    } catch (const std::exception& ex) {
        gs_log(2, "Failed to parse tools_json: %s", ex.what());
        return false;
    }
}

}  // namespace

extern "C" {

GS_API GS_Conversation* GS_ConversationCreate(
    GS_Engine*             engine,
    GS_ConversationConfig* config)
{
    if (!engine || !engine->model) {
        gs_log(3, "ConversationCreate: engine or model is NULL");
        return nullptr;
    }

    gs_log(1, "Creating conversation context...");

    llama_context_params ctx_params = llama_context_default_params();
    ctx_params.n_ctx        = (config && config->n_ctx_override > 0)
                              ? static_cast<uint32_t>(config->n_ctx_override)
                              : engine->n_ctx;
    ctx_params.n_batch      = 2048;
    // Vision encoder runs non-causal attention over the full image token block
    // (~1120 tokens at our mtmd budget), which requires n_ubatch >= n_tokens.
    ctx_params.n_ubatch     = 2048;
    ctx_params.offload_kqv  = engine->gpu_enabled;
    ctx_params.op_offload   = engine->gpu_enabled;

    llama_context* ctx = llama_init_from_model(engine->model, ctx_params);
    if (!ctx) {
        gs_log(3, "Failed to create llama_context");
        return nullptr;
    }

    auto* conversation   = new GS_Conversation();
    conversation->engine = engine;
    conversation->ctx    = ctx;
    if (config) {
        conversation->config = *config;
    }

    if (!gs_parse_and_cache_tools(conversation->config.tools_json,
                                  &conversation->parsed_tools)) {
        gs_log(3, "ConversationCreate: invalid tools_json");
        llama_free(ctx);
        delete conversation;
        return nullptr;
    }

    if (!gs_reset_sampler(conversation, nullptr)) {
        llama_free(ctx);
        delete conversation;
        return nullptr;
    }

    try {
        conversation->chat_templates = common_chat_templates_init(engine->model, "");
    } catch (const std::exception& ex) {
        gs_log(3, "Failed to initialise built-in Jinja chat templates: %s", ex.what());
        common_sampler_free(conversation->sampler);
        llama_free(ctx);
        delete conversation;
        return nullptr;
    }
    if (!conversation->chat_templates) {
        gs_log(3, "Built-in Jinja chat template initialisation returned null.");
        common_sampler_free(conversation->sampler);
        llama_free(ctx);
        delete conversation;
        return nullptr;
    }

    if (!conversation->config.system_message.empty()) {
        conversation->chat_history.push_back({ "system", conversation->config.system_message });
    }

    // Build the end-of-turn suffix using vocab API functions so token IDs are
    // correct regardless of how the GGUF labels the special tokens. Some
    // quantisation tools name tokens differently from their Jinja-template
    // strings, so tokenising the literal "<end_of_turn>\n" is unreliable.
    // Prefer the dedicated EOT token; fall back to EOS when the GGUF does not
    // set a separate eot_token_id.
    const llama_vocab* vocab   = llama_model_get_vocab(engine->model);
    llama_token        eot_tok = llama_vocab_eot(vocab);
    if (eot_tok < 0) {
        eot_tok = llama_vocab_eos(vocab);
    }
    const llama_token nl_tok = llama_vocab_nl(vocab);
    if (eot_tok >= 0) {
        conversation->tok_end_of_turn = eot_tok;
        conversation->eot_suffix_tokens.push_back(eot_tok);
    }
    if (nl_tok >= 0) {
        conversation->eot_suffix_tokens.push_back(nl_tok);
    }

    if (!gs_init_verified_prompt_suffix(conversation)) {
        gs_log(2,
               "Assistant suffix verification failed; incremental reuse will fall back to rebuilds when the boundary is ambiguous.");
    } else {
        gs_init_incremental_turn_reuse(conversation);
    }

    llama_set_abort_callback(ctx, gs_abort_decode, conversation);

    gs_log(1, "Conversation created. n_ctx=%u tools=%zu eot=%d nl=%d",
           llama_n_ctx(ctx),
           conversation->parsed_tools.size(),
           conversation->tok_end_of_turn,
           nl_tok);
    return conversation;
}

GS_API void GS_ConversationDelete(GS_Conversation* conversation) {
    if (!conversation) {
        return;
    }
    gs_log(1, "Destroying conversation...");
    if (conversation->sampler) {
        common_sampler_free(conversation->sampler);
    }
    if (conversation->ctx) {
        llama_free(conversation->ctx);
    }
    delete conversation;
}

GS_API GS_JsonResponse* GS_ConversationSendText(
    GS_Conversation* conversation,
    const char*      text)
{
    if (!conversation || !text || !conversation->engine) {
        return nullptr;
    }

    const size_t text_len = strlen(text);
    gs_log(1, "SendText: \"%.*s%s\"",
           std::min<int>(60, static_cast<int>(text_len)),
           text,
           text_len > 60 ? "..." : "");

    GS_GenerationResult result = gs_run_user_turn(conversation, "user", text);
    if (!result.ok) {
        return nullptr;
    }
    if (result.text.empty() && result.json_body.empty()) {
        gs_log(2, "SendText: model generated empty response");
    }
    return gs_build_turn_response(result);
}

GS_API int GS_ConversationSendTextStream(
    GS_Conversation*  conversation,
    const char*       text,
    GS_StreamCallback callback,
    void*             callback_data)
{
    if (!conversation || !text || !conversation->engine) {
        return -1;
    }

    const size_t text_len = strlen(text);
    gs_log(1, "SendTextStream: \"%.*s%s\"",
           std::min<int>(60, static_cast<int>(text_len)),
           text,
           text_len > 60 ? "..." : "");

    GS_GenerationResult result = gs_run_user_turn(
        conversation, "user", text, callback, callback_data);
    if (result.ok) {
        return 0;
    }
    return result.cancelled ? 1 : -1;
}

GS_API GS_JsonResponse* GS_ConversationSendMessage(
    GS_Conversation* conversation,
    const char*      message_json)
{
    if (!conversation || !message_json || !conversation->engine) {
        return nullptr;
    }

    GS_MessagePayload payload;
    if (!gs_parse_message_json(message_json, &payload)) {
        return nullptr;
    }
    gs_log(1, "SendMessage: role=%s text=%zuB", payload.role.c_str(), payload.text.size());

    GS_GenerationResult result = gs_run_user_turn(
        conversation, payload.role, payload.text);
    if (!result.ok) {
        return nullptr;
    }
    return gs_build_turn_response(result);
}

GS_API int GS_ConversationSendMessageStream(
    GS_Conversation*  conversation,
    const char*       message_json,
    GS_StreamCallback callback,
    void*             callback_data)
{
    if (!conversation || !message_json || !conversation->engine) {
        return -1;
    }

    GS_MessagePayload payload;
    if (!gs_parse_message_json(message_json, &payload)) {
        return -1;
    }
    gs_log(1, "SendMessageStream: role=%s text=%zuB", payload.role.c_str(), payload.text.size());

    GS_GenerationResult result = gs_run_user_turn(
        conversation, payload.role, payload.text, callback, callback_data);
    if (result.ok) {
        return 0;
    }
    return result.cancelled ? 1 : -1;
}

GS_API void GS_ConversationCancelProcess(GS_Conversation* conversation) {
    if (!conversation) {
        return;
    }
    conversation->cancel_flag.store(true, std::memory_order_release);
    gs_log(1, "Cancel requested.");
}

GS_API const char* GS_JsonResponseGetString(const GS_JsonResponse* response) {
    return response ? response->content.c_str() : nullptr;
}

GS_API void GS_JsonResponseDelete(GS_JsonResponse* response) {
    delete response;
}

GS_API GS_BenchmarkInfo* GS_ConversationGetBenchmarkInfo(GS_Conversation* conversation) {
    if (!conversation) {
        return nullptr;
    }
    auto*      info = new GS_BenchmarkInfo();
    *info = conversation->last_benchmark;
    return info;
}

GS_API int GS_ConversationGetKvCacheTokenCount(const GS_Conversation* conversation) {
    return conversation ? conversation->n_past : 0;
}

GS_API int GS_ConversationGetContextSize(const GS_Conversation* conversation) {
    return (conversation && conversation->ctx)
        ? static_cast<int>(llama_n_ctx(conversation->ctx))
        : 0;
}

GS_API void GS_BenchmarkInfoDelete(GS_BenchmarkInfo* info) {
    delete info;
}

GS_API double GS_BenchmarkInfoGetTimeToFirstToken(const GS_BenchmarkInfo* info) {
    return info ? info->ttft : 0.0;
}

GS_API double GS_BenchmarkInfoGetPrefillTokensPerSec(const GS_BenchmarkInfo* info) {
    return info ? info->prefill_tok_per_s : 0.0;
}

GS_API double GS_BenchmarkInfoGetDecodeTokensPerSec(const GS_BenchmarkInfo* info) {
    return info ? info->decode_tok_per_s : 0.0;
}

GS_API int GS_BenchmarkInfoGetPrefillTokenCount(const GS_BenchmarkInfo* info) {
    return info ? info->prefill_tokens : 0;
}

GS_API int GS_BenchmarkInfoGetDecodeTokenCount(const GS_BenchmarkInfo* info) {
    return info ? info->decode_tokens : 0;
}

GS_API double GS_BenchmarkInfoGetCommitOverheadSeconds(const GS_BenchmarkInfo* info) {
    return info ? info->commit_overhead_s : 0.0;
}

GS_API int GS_BenchmarkInfoGetCommitTokenCount(const GS_BenchmarkInfo* info) {
    return info ? info->commit_tokens : 0;
}

GS_API double GS_BenchmarkInfoGetTemplateBuildSeconds(const GS_BenchmarkInfo* info) {
    return info ? info->phase_template_build_s : 0.0;
}

GS_API double GS_BenchmarkInfoGetPrefillSeconds(const GS_BenchmarkInfo* info) {
    return info ? info->phase_prefill_s : 0.0;
}

}  // extern "C"
