/* SPDX-License-Identifier: GPL-3.0-or-later */
/* SPDX-FileCopyrightText: 2026 Kaimo File Server */
#pragma once

#include <cstddef>
#include <string>
#include <string_view>

namespace kaimo::sync_json {

inline bool valid_utf8(std::string_view value) {
    std::size_t i = 0;
    while (i < value.size()) {
        const auto lead = static_cast<unsigned char>(value[i]);
        if (lead <= 0x7f) {
            ++i;
            continue;
        }

        std::size_t continuation_count = 0;
        unsigned int code_point = 0;
        if (lead >= 0xc2 && lead <= 0xdf) {
            continuation_count = 1;
            code_point = lead & 0x1f;
        } else if (lead >= 0xe0 && lead <= 0xef) {
            continuation_count = 2;
            code_point = lead & 0x0f;
        } else if (lead >= 0xf0 && lead <= 0xf4) {
            continuation_count = 3;
            code_point = lead & 0x07;
        } else {
            return false;
        }
        if (i + continuation_count >= value.size()) return false;
        for (std::size_t j = 1; j <= continuation_count; ++j) {
            const auto next = static_cast<unsigned char>(value[i + j]);
            if ((next & 0xc0) != 0x80) return false;
            code_point = (code_point << 6) | (next & 0x3f);
        }
        if ((continuation_count == 2 && code_point < 0x800)
            || (continuation_count == 3 && code_point < 0x10000)
            || code_point > 0x10ffff
            || (code_point >= 0xd800 && code_point <= 0xdfff)) {
            return false;
        }
        i += continuation_count + 1;
    }
    return true;
}

inline bool contains_control(std::string_view value) {
    for (unsigned char c : value) {
        if (c < 0x20 || c == 0x7f) return true;
    }
    return false;
}

inline bool valid_text(std::string_view value, std::size_t max_bytes) {
    return !value.empty()
        && value.size() <= max_bytes
        && !contains_control(value)
        && valid_utf8(value);
}

inline bool ascii_name_char(unsigned char c) {
    return (c >= 'a' && c <= 'z')
        || (c >= 'A' && c <= 'Z')
        || (c >= '0' && c <= '9')
        || c == '.' || c == '_' || c == '-';
}

inline bool valid_username(std::string_view value) {
    if (!valid_text(value, 32)) return false;
    const auto first = static_cast<unsigned char>(value.front());
    if (!((first >= 'a' && first <= 'z')
          || (first >= 'A' && first <= 'Z')
          || (first >= '0' && first <= '9'))) {
        return false;
    }
    for (unsigned char c : value) {
        if (!ascii_name_char(c)) return false;
    }
    return true;
}

inline std::string ascii_lower(std::string_view value) {
    std::string lower(value);
    for (char& c : lower) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return lower;
}

inline bool reserved_samba_name(std::string_view value) {
    const std::string lower = ascii_lower(value);
    return lower == "global" || lower == "homes" || lower == "printers"
        || lower == "print$" || lower == "ipc$";
}

inline bool valid_share_name(std::string_view value) {
    if (!valid_text(value, 64) || value.front() == '.' || value.back() == '.'
        || reserved_samba_name(value)) {
        return false;
    }
    for (unsigned char c : value) {
        if (!ascii_name_char(c)) return false;
    }
    return true;
}

inline bool valid_absolute_path(std::string_view value) {
    return valid_text(value, 4096) && value.front() == '/';
}

inline int dialect_rank(std::string_view value) {
    if (value == "SMB2_02") return 0;
    if (value == "SMB2_10") return 1;
    if (value == "SMB3_00") return 2;
    if (value == "SMB3_02") return 3;
    if (value == "SMB3_11") return 4;
    return -1;
}

inline std::string quote(std::string_view value) {
    static constexpr char hex[] = "0123456789ABCDEF";
    std::string output;
    output.reserve(value.size() + 2);
    output.push_back('"');
    for (unsigned char c : value) {
        switch (c) {
            case '"': output += "\\\""; break;
            case '\\': output += "\\\\"; break;
            case '\b': output += "\\b"; break;
            case '\f': output += "\\f"; break;
            case '\n': output += "\\n"; break;
            case '\r': output += "\\r"; break;
            case '\t': output += "\\t"; break;
            default:
                if (c < 0x20) {
                    output += "\\u00";
                    output.push_back(hex[c >> 4]);
                    output.push_back(hex[c & 0x0f]);
                } else {
                    output.push_back(static_cast<char>(c));
                }
        }
    }
    output.push_back('"');
    return output;
}

inline const char* boolean(bool value) {
    return value ? "true" : "false";
}

}  // namespace kaimo::sync_json
