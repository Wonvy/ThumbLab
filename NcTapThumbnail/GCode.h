#pragma once

#include <cstddef>
#include <string_view>
#include <vector>

namespace nctap {

inline constexpr std::size_t MaxProgramBytes = 16 * 1024 * 1024;

struct Point {
    double x;
    double y;
    double z;
};

struct Segment {
    Point from;
    Point to;
    bool rapid;
};

// Produces preview geometry in millimeters from CNC G-code text.
std::vector<Segment> ParseGCode(std::string_view input);

}  // namespace nctap
