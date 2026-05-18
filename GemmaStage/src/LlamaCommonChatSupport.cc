#include "common.h"

#include <cstdarg>

int LLAMA_BUILD_NUMBER = 0;
const char* LLAMA_COMMIT = "GemmaStage";
const char* LLAMA_COMPILER = "MSVC";
const char* LLAMA_BUILD_TARGET = "GemmaStage";

std::string string_format(const char* fmt, ...) {
    if (!fmt) {
        return {};
    }

    va_list args;
    va_start(args, fmt);
    const int needed = vsnprintf(nullptr, 0, fmt, args);
    va_end(args);
    if (needed <= 0) {
        return {};
    }

    std::string out(static_cast<size_t>(needed), '\0');
    va_start(args, fmt);
    vsnprintf(out.data(), out.size() + 1, fmt, args);
    va_end(args);
    return out;
}

common_time_meas::common_time_meas(int64_t& t_acc, bool disable)
    : t_start_us(disable ? -1 : ggml_time_us()),
      t_acc(t_acc) {}

common_time_meas::~common_time_meas() {
    if (t_start_us >= 0) {
        t_acc += ggml_time_us() - t_start_us;
    }
}

std::string string_join(
    const std::vector<std::string>& values,
    const std::string& separator)
{
    std::string out;
    for (size_t i = 0; i < values.size(); ++i) {
        if (i != 0) {
            out += separator;
        }
        out += values[i];
    }
    return out;
}

std::vector<std::string> string_split(
    const std::string& str,
    const std::string& delimiter)
{
    if (delimiter.empty()) {
        return { str };
    }

    std::vector<std::string> parts;
    size_t start = 0;
    while (true) {
        const size_t pos = str.find(delimiter, start);
        if (pos == std::string::npos) {
            parts.push_back(str.substr(start));
            return parts;
        }
        parts.push_back(str.substr(start, pos - start));
        start = pos + delimiter.size();
    }
}

std::string string_repeat(const std::string& str, size_t n) {
    std::string out;
    out.reserve(str.size() * n);
    for (size_t i = 0; i < n; ++i) {
        out += str;
    }
    return out;
}

void string_replace_all(
    std::string& s,
    const std::string& search,
    const std::string& replace)
{
    if (search.empty()) {
        return;
    }

    size_t pos = 0;
    while ((pos = s.find(search, pos)) != std::string::npos) {
        s.replace(pos, search.size(), replace);
        pos += replace.size();
    }
}

std::string regex_escape(const std::string& s) {
    std::string escaped;
    escaped.reserve(s.size() * 2);

    for (char ch : s) {
        switch (ch) {
            case '\\':
            case '^':
            case '$':
            case '.':
            case '|':
            case '?':
            case '*':
            case '+':
            case '(':
            case ')':
            case '[':
            case ']':
            case '{':
            case '}':
                escaped.push_back('\\');
                break;
            default:
                break;
        }
        escaped.push_back(ch);
    }

    return escaped;
}

std::vector<llama_token> common_tokenize(
    const llama_vocab* vocab,
    const std::string& text,
    bool add_special,
    bool parse_special)
{
    if (!vocab) {
        return {};
    }

    int32_t n = llama_tokenize(vocab, text.c_str(),
                               static_cast<int32_t>(text.size()),
                               nullptr, 0,
                               add_special, parse_special);
    std::vector<llama_token> tokens(n < 0 ? -n : n);
    int32_t actual = llama_tokenize(vocab, text.c_str(),
                                    static_cast<int32_t>(text.size()),
                                    tokens.data(),
                                    static_cast<int32_t>(tokens.size()),
                                    add_special, parse_special);
    if (actual < 0) {
        return {};
    }

    tokens.resize(static_cast<size_t>(actual));
    return tokens;
}

std::vector<llama_token> common_tokenize(
    const llama_context* ctx,
    const std::string& text,
    bool add_special,
    bool parse_special)
{
    const llama_model* model = ctx ? llama_get_model(ctx) : nullptr;
    return common_tokenize(model ? llama_model_get_vocab(model) : nullptr,
                           text,
                           add_special,
                           parse_special);
}

std::string common_token_to_piece(
    const llama_vocab* vocab,
    llama_token token,
    bool special)
{
    if (!vocab) {
        return {};
    }

    char buf[256];
    int32_t n = llama_token_to_piece(vocab, token, buf, sizeof(buf), 0, special);
    if (n < 0) {
        std::string out(static_cast<size_t>(-n), '\0');
        n = llama_token_to_piece(vocab, token, out.data(),
                                 static_cast<int32_t>(out.size()), 0, special);
        if (n < 0) {
            return {};
        }
        out.resize(static_cast<size_t>(n));
        return out;
    }

    return std::string(buf, static_cast<size_t>(n));
}

std::string common_token_to_piece(
    const llama_context* ctx,
    llama_token token,
    bool special)
{
    const llama_model* model = ctx ? llama_get_model(ctx) : nullptr;
    return common_token_to_piece(model ? llama_model_get_vocab(model) : nullptr, token, special);
}

bool tty_can_use_colors() {
    return false;
}
