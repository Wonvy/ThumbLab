#include "GCode.h"

#include <cmath>
#include <cstdlib>
#include <iostream>
#include <string>

namespace {

using nctap::ParseGCode;

bool Near(double actual, double expected) {
    return std::abs(actual - expected) < 1e-6;
}

void Check(bool condition, const char* message) {
    if (!condition) {
        std::cerr << message << '\n';
        std::exit(EXIT_FAILURE);
    }
}

void LinearAndModal() {
    const auto path = ParseGCode(
        "(CNC preview)\r\n"
        "N10 g21g90g0X+10Y5 ; G01 X999\r\n"
        "N20 g01x15 (G0 Y999)\n"
        "Y10\n"
        "G20 G91 X.5 Y-.5\r"
        "G21 G90 G1 X0 Y0\n"
        "M30\nX999");
    Check(path.size() == 5 && path[0].rapid && !path[1].rapid, "Rapid, feed and modal blocks");
    Check(Near(path[0].to.x, 10.0) && Near(path[0].to.y, 5.0), "Compact case-insensitive words");
    Check(Near(path[2].to.x, 15.0) && Near(path[2].to.y, 10.0), "Modal omitted axis");
    Check(Near(path[3].to.x, 27.7) && Near(path[3].to.y, -2.7), "Incremental inch dimensions");
    Check(Near(path[4].to.x, 0.0) && Near(path[4].to.y, 0.0), "Program end stops preview");
}

void CenterAndRadiusArcs() {
    const auto center = ParseGCode("G0X10Y0\nG90.1 G3 X0 Y10 Z-2 I0 J0");
    Check(center.size() == 19 && !center[1].rapid, "Quarter-circle helix is tessellated");
    Check(Near(center[9].to.x, std::sqrt(50.0)) && Near(center[9].to.y, std::sqrt(50.0)) &&
          Near(center[9].to.z, -1.0), "Absolute I/J and interpolated helix Z");
    Check(Near(center.back().to.x, 0.0) && Near(center.back().to.y, 10.0), "Exact arc endpoint");

    const auto major = ParseGCode("G0 X10\nG3 X0 Y10 R-10");
    Check(major.size() > 53 && major[18].to.x > 19.9 && Near(major[18].to.y, 10.0),
          "Negative R selects a counterclockwise major arc");
    const auto clockwise = ParseGCode("G0 X10\nG2 X0 Y-10 R10");
    Check(clockwise.size() == 19 && clockwise[9].to.x > 7.0 && clockwise[9].to.y < -7.0,
          "Positive R selects a clockwise minor arc");

    const auto full = ParseGCode("G0 X10\nG3 I-10 J0 P2");
    Check(full.size() == 145 && Near(full[36].to.x, -10.0) && Near(full.back().to.x, 10.0),
          "Full-circle and multi-turn center arcs");
}

void ArcPlanes() {
    const auto zx = ParseGCode("G0 Z10\nG18 G3 X10 Z0 K-10 I0");
    Check(zx.size() == 19 && Near(zx[9].to.x, std::sqrt(50.0)) &&
          Near(zx[9].to.z, std::sqrt(50.0)), "G18 rotates from +Z to +X counterclockwise");
    const auto yz = ParseGCode("G0 Y10\nG19 G2 Y0 Z-10 J-10 K0");
    Check(yz.size() == 19 && Near(yz[9].to.y, std::sqrt(50.0)) &&
          Near(yz[9].to.z, -std::sqrt(50.0)), "G19 rotates from +Y to -Z clockwise");
}

void TextAndInvalidInput() {
    for (const bool bigEndian : {false, true}) {
        std::string utf16 = bigEndian ? "\xfe\xff" : "\xff\xfe";
        for (const char c : std::string("G1 X10\nY10")) {
            utf16.push_back(bigEndian ? '\0' : c);
            utf16.push_back(bigEndian ? c : '\0');
        }
        Check(ParseGCode(utf16).size() == 2, "BOM-marked UTF-16 input");
    }
    Check(ParseGCode("\xef\xbb\xbfG0 X10").size() == 1, "UTF-8 BOM input");
    Check(ParseGCode(std::string("G1X10\0G1X20", 11)).empty(), "Binary input is rejected");
    Check(ParseGCode("CDF\001\nG1 X100").empty(), "NetCDF signature is rejected");
    Check(ParseGCode("\x89HDF\nG1 X100").empty(), "HDF5 signature is rejected");
    const auto path = ParseGCode(
        "G0 X1\nG81 X100\nX200\nG80\nX300\n"
        "G1 X2\nG28 X100\nG92 X300\nX[#1 + 2]\nX3000000000\nY3");
    Check(path.size() == 3 && Near(path.back().from.x, 2.0) && Near(path.back().to.y, 3.0),
          "Unsupported blocks and invalid coordinates add no geometry");
}

void ProgramLimits() {
    std::string largeProgram = "G91 G1 X1\n";
    largeProgram.resize(nctap::MaxProgramBytes + 1, ' ');
    Check(ParseGCode(largeProgram).empty(), "Oversized programs are rejected");

    std::string manyMoves = "G91 G1\n";
    for (int move = 0; move < 125001; ++move) manyMoves += "X1\n";
    const auto prefix = ParseGCode(manyMoves);
    Check(prefix.size() == 125000 && Near(prefix.back().to.x, 125000.0), "Segment limit keeps the prefix");
}

}  // namespace

int main() {
    LinearAndModal();
    CenterAndRadiusArcs();
    ArcPlanes();
    TextAndInvalidInput();
    ProgramLimits();
}
