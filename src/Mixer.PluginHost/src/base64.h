#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

namespace mixer::host {

inline std::string Base64Encode(std::string_view data) {
    static constexpr char kAlphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    out.reserve((data.size() + 2) / 3 * 4);
    size_t i = 0;
    for (; i + 2 < data.size(); i += 3) {
        uint32_t v = (uint8_t(data[i]) << 16) | (uint8_t(data[i + 1]) << 8) | uint8_t(data[i + 2]);
        out += kAlphabet[(v >> 18) & 63];
        out += kAlphabet[(v >> 12) & 63];
        out += kAlphabet[(v >> 6) & 63];
        out += kAlphabet[v & 63];
    }
    if (i + 1 == data.size()) {
        uint32_t v = uint8_t(data[i]) << 16;
        out += kAlphabet[(v >> 18) & 63];
        out += kAlphabet[(v >> 12) & 63];
        out += "==";
    } else if (i + 2 == data.size()) {
        uint32_t v = (uint8_t(data[i]) << 16) | (uint8_t(data[i + 1]) << 8);
        out += kAlphabet[(v >> 18) & 63];
        out += kAlphabet[(v >> 12) & 63];
        out += kAlphabet[(v >> 6) & 63];
        out += '=';
    }
    return out;
}

// nullopt — не base64.
inline std::optional<std::string> Base64Decode(std::string_view text) {
    auto value = [](char c) -> int {
        if (c >= 'A' && c <= 'Z') return c - 'A';
        if (c >= 'a' && c <= 'z') return c - 'a' + 26;
        if (c >= '0' && c <= '9') return c - '0' + 52;
        if (c == '+') return 62;
        if (c == '/') return 63;
        return -1;
    };
    if (text.size() % 4 != 0) return std::nullopt;
    std::string out;
    out.reserve(text.size() / 4 * 3);
    for (size_t i = 0; i < text.size(); i += 4) {
        int a = value(text[i]), b = value(text[i + 1]);
        if (a < 0 || b < 0) return std::nullopt;
        out += char((a << 2) | (b >> 4));
        if (text[i + 2] == '=') {
            if (text[i + 3] != '=' || i + 4 != text.size()) return std::nullopt;
            break;
        }
        int c = value(text[i + 2]);
        if (c < 0) return std::nullopt;
        out += char(((b & 15) << 4) | (c >> 2));
        if (text[i + 3] == '=') {
            if (i + 4 != text.size()) return std::nullopt;
            break;
        }
        int d = value(text[i + 3]);
        if (d < 0) return std::nullopt;
        out += char(((c & 3) << 6) | d);
    }
    return out;
}

}  // namespace mixer::host
