#include "GCode.h"

#include <algorithm>
#include <array>
#include <charconv>
#include <cmath>
#include <optional>
#include <string>

namespace nctap {
namespace {

constexpr std::size_t MaxSegments = 125000;
constexpr double MaxCoordinate = 1.0e9;
constexpr double Pi = 3.14159265358979323846;
constexpr double FullTurn = 2.0 * Pi;
constexpr double ArcStep = Pi / 36.0;
constexpr double PositionEpsilon = 1.0e-9;

enum class Motion { Rapid, Linear, Clockwise, Counterclockwise, Unsupported };

struct Plane {
    double Point::*u;
    double Point::*v;
    double Point::*w;
    char centerU;
    char centerV;
};

constexpr Plane XY{&Point::x, &Point::y, &Point::z, 'I', 'J'};
constexpr Plane ZX{&Point::z, &Point::x, &Point::y, 'K', 'I'};
constexpr Plane YZ{&Point::y, &Point::z, &Point::x, 'J', 'K'};

struct State {
    Point position{};
    Motion motion = Motion::Rapid;
    const Plane* plane = &XY;
    bool incremental = false;
    bool incrementalCenter = true;
    double units = 1.0;
};

struct Block {
    std::array<std::optional<double>, 26> words{};
    std::optional<Motion> motion;
    std::optional<const Plane*> plane;
    std::optional<bool> incremental;
    std::optional<bool> incrementalCenter;
    std::optional<double> units;
    bool suppressMove = false;
    bool programEnd = false;

    const std::optional<double>& operator[](char letter) const {
        return words[letter - 'A'];
    }
};

void ParseGWord(Block& block, double code) {
    if (code == 0.0) block.motion = Motion::Rapid;
    else if (code == 1.0) block.motion = Motion::Linear;
    else if (code == 2.0) block.motion = Motion::Clockwise;
    else if (code == 3.0) block.motion = Motion::Counterclockwise;
    else if (code == 17.0) block.plane = &XY;
    else if (code == 18.0) block.plane = &ZX;
    else if (code == 19.0) block.plane = &YZ;
    else if (code == 17.1 || code == 18.1 || code == 19.1) block.plane = nullptr;
    else if (code == 20.0) block.units = 25.4;
    else if (code == 21.0) block.units = 1.0;
    else if (code == 90.0 || code == 91.0) block.incremental = code == 91.0;
    else if (code == 90.1 || code == 91.1) block.incrementalCenter = code == 91.1;
    else if ((code >= 5.0 && code < 7.0) || (code >= 33.0 && code < 34.0) ||
             (code >= 38.0 && code < 39.0) || code == 73.0 || code == 76.0 ||
             (code >= 80.0 && code < 90.0)) {
        block.motion = Motion::Unsupported;
    } else if (code != 40.0 && code != 41.0 && code != 42.0 && code != 43.0 &&
               code != 44.0 && code != 49.0 && !(code >= 54.0 && code < 60.0) &&
               code != 61.0 && code != 61.1 && code != 64.0 &&
               !(code >= 93.0 && code <= 99.0)) {
        // Coordinate setting, homing and other unknown commands may also carry XYZ words.
        block.suppressMove = true;
    }
}

bool IsDigit(char c) {
    return c >= '0' && c <= '9';
}

bool IsSpace(char c) {
    return c == ' ' || c == '\t' || c == '\v' || c == '\f';
}

std::optional<double> ReadNumber(const char*& cursor, const char* end) {
    while (cursor != end && IsSpace(*cursor)) ++cursor;
    const char* start = cursor;
    if (cursor != end && (*cursor == '+' || *cursor == '-')) ++cursor;
    while (cursor != end && IsDigit(*cursor)) ++cursor;
    if (cursor != end && *cursor == '.') {
        ++cursor;
        while (cursor != end && IsDigit(*cursor)) ++cursor;
    }
    if (start != end && *start == '+') ++start;
    double value = 0.0;
    const auto result = std::from_chars(start, cursor, value, std::chars_format::fixed);
    if (result.ec != std::errc{} || result.ptr != cursor || !std::isfinite(value)) {
        return std::nullopt;
    }
    return value;
}

std::optional<Block> ParseBlock(std::string_view line) {
    Block block;
    const char* cursor = line.data();
    const char* end = cursor + line.size();
    while (cursor != end) {
        char letter = *cursor++;
        if (letter == ';' || letter == '*') break;
        if (letter == '(') {
            while (cursor != end && *cursor != ')') ++cursor;
            if (cursor != end) ++cursor;
            continue;
        }
        if (IsSpace(letter) || letter == '/' || letter == '%') continue;
        if (letter >= 'a' && letter <= 'z') letter -= 'a' - 'A';
        if (letter < 'A' || letter > 'Z') return std::nullopt;

        const auto value = ReadNumber(cursor, end);
        if (!value) return std::nullopt;
        if (letter == 'G') ParseGWord(block, *value);
        else if (letter == 'M') {
            if (*value == 2.0 || *value == 30.0) block.programEnd = true;
            if (*value == 6.0 || *value == 97.0 || *value == 98.0 || *value == 99.0) {
                block.suppressMove = true;
            }
        } else {
            block.words[letter - 'A'] = *value;
        }
    }
    return block;
}

bool ValidPoint(const Point& point) {
    return std::abs(point.x) <= MaxCoordinate && std::abs(point.y) <= MaxCoordinate &&
           std::abs(point.z) <= MaxCoordinate;
}

void AddSegment(std::vector<Segment>& path, const Point& from, const Point& to, bool rapid) {
    if (path.size() < MaxSegments && (from.x != to.x || from.y != to.y || from.z != to.z)) {
        path.push_back({from, to, rapid});
    }
}

struct Arc {
    double centerU;
    double centerV;
    double radius;
    double startAngle;
    double sweep;
};

double ArcSweep(double start, double end, bool clockwise) {
    double angle = end - start;
    if (clockwise && angle >= 0.0) angle -= FullTurn;
    if (!clockwise && angle <= 0.0) angle += FullTurn;
    return angle;
}

std::optional<Arc> MakeArc(const State& state, const Block& block, const Point& target) {
    const Plane& plane = *state.plane;
    const double startU = state.position.*plane.u;
    const double startV = state.position.*plane.v;
    const double deltaU = target.*plane.u - startU;
    const double deltaV = target.*plane.v - startV;
    const double chord = std::hypot(deltaU, deltaV);
    const bool clockwise = state.motion == Motion::Clockwise;
    double offsetU = 0.0;
    double offsetV = 0.0;

    const auto& centerU = block[plane.centerU];
    const auto& centerV = block[plane.centerV];
    if (const auto& radiusWord = block['R']) {
        const double radius = std::abs(*radiusWord) * state.units;
        if (chord <= PositionEpsilon || radius > MaxCoordinate || chord > 2.0 * radius + 0.001) {
            return std::nullopt;
        }
        const double halfChord = chord * 0.5;
        const double height = std::sqrt(std::max(0.0, (radius - halfChord) * (radius + halfChord)));
        const double side = clockwise == (*radiusWord < 0.0) ? 1.0 : -1.0;
        offsetU = deltaU * 0.5 - deltaV / chord * height * side;
        offsetV = deltaV * 0.5 + deltaU / chord * height * side;
    } else if (centerU || centerV) {
        const auto offset = [&state](const std::optional<double>& center, double start) {
            if (!center) return 0.0;
            const double value = *center * state.units;
            return state.incrementalCenter ? value : value - start;
        };
        offsetU = offset(centerU, startU);
        offsetV = offset(centerV, startV);
    } else {
        return std::nullopt;
    }

    const double radius = std::hypot(offsetU, offsetV);
    const double endRadius = std::hypot(deltaU - offsetU, deltaV - offsetV);
    if (!(radius > PositionEpsilon && radius <= MaxCoordinate) ||
        std::abs(radius - endRadius) > std::max(0.02, radius * 0.005)) {
        return std::nullopt;
    }

    const double turns = block['P'].value_or(1.0);
    if (turns < 1.0 || turns > 16.0 || turns != std::floor(turns)) return std::nullopt;
    const double start = std::atan2(-offsetV, -offsetU);
    const double end = std::atan2(deltaV - offsetV, deltaU - offsetU);
    const double fullSweep = clockwise ? -FullTurn : FullTurn;
    const double sweep = chord <= PositionEpsilon ? fullSweep : ArcSweep(start, end, clockwise);
    return Arc{startU + offsetU, startV + offsetV, radius, start, sweep + (turns - 1.0) * fullSweep};
}

void AddArc(std::vector<Segment>& path, const State& state, const Block& block, const Point& target) {
    if (!state.plane) return;
    const auto arc = MakeArc(state, block, target);
    if (!arc) return;

    const Plane& plane = *state.plane;
    Point previous = state.position;
    const int steps = std::max(1, static_cast<int>(std::ceil(std::abs(arc->sweep) / ArcStep)));
    for (int step = 1; step < steps && path.size() < MaxSegments; ++step) {
        const double fraction = static_cast<double>(step) / steps;
        const double angle = arc->startAngle + fraction * arc->sweep;
        Point point{};
        point.*plane.u = arc->centerU + arc->radius * std::cos(angle);
        point.*plane.v = arc->centerV + arc->radius * std::sin(angle);
        point.*plane.w = state.position.*plane.w + fraction * (target.*plane.w - state.position.*plane.w);
        AddSegment(path, previous, point, false);
        previous = point;
    }
    AddSegment(path, previous, target, false);
}

void ApplyBlock(std::vector<Segment>& path, State& state, const Block& block) {
    state.motion = block.motion.value_or(state.motion);
    state.plane = block.plane.value_or(state.plane);
    state.incremental = block.incremental.value_or(state.incremental);
    state.incrementalCenter = block.incrementalCenter.value_or(state.incrementalCenter);
    state.units = block.units.value_or(state.units);
    if (block.suppressMove || state.motion == Motion::Unsupported) return;

    const auto coordinate = [&state, &block](char axis, double current) {
        if (!block[axis]) return current;
        const double value = *block[axis] * state.units;
        return state.incremental ? current + value : value;
    };
    const Point target{
        coordinate('X', state.position.x),
        coordinate('Y', state.position.y),
        coordinate('Z', state.position.z),
    };
    if (!ValidPoint(target)) return;

    if (state.motion == Motion::Rapid || state.motion == Motion::Linear) {
        AddSegment(path, state.position, target, state.motion == Motion::Rapid);
    } else {
        AddArc(path, state, block, target);
    }
    state.position = target;
}

bool GetText(std::string_view& input, std::string& decoded) {
    if (input.substr(0, 3) == "CDF" || input.substr(0, 4) == "\x89HDF") return false;

    const bool littleEndian = input.substr(0, 2) == "\xff\xfe";
    const bool bigEndian = input.substr(0, 2) == "\xfe\xff";
    if (littleEndian || bigEndian) {
        if (input.size() % 2 != 0) return false;
        decoded.reserve((input.size() - 2) / 2);
        for (std::size_t i = 2; i + 1 < input.size(); i += 2) {
            const char low = input[i + (bigEndian ? 1 : 0)];
            const char high = input[i + (littleEndian ? 1 : 0)];
            if (high == '\0' && low == '\0') return false;
            decoded.push_back(high == '\0' && static_cast<unsigned char>(low) < 128 ? low : '?');
        }
        input = decoded;
    } else if (input.substr(0, 3) == "\xef\xbb\xbf") {
        input.remove_prefix(3);
    }
    return input.find('\0') == std::string_view::npos;
}

}  // namespace

std::vector<Segment> ParseGCode(std::string_view input) {
    if (input.size() > MaxProgramBytes) return {};
    std::string decoded;
    if (!GetText(input, decoded)) return {};

    std::vector<Segment> path;
    State state;
    while (!input.empty() && path.size() < MaxSegments) {
        const auto lineEnd = input.find_first_of("\r\n");
        if (const auto block = ParseBlock(input.substr(0, lineEnd))) {
            ApplyBlock(path, state, *block);
            if (block->programEnd) break;
        }
        if (lineEnd == std::string_view::npos) break;
        input.remove_prefix(lineEnd + 1);
        if (!input.empty() && input.front() == '\n') input.remove_prefix(1);
    }
    return path;
}

}  // namespace nctap
