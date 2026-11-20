// vlqt_last_error and the exception guard of the exports, shared by vlqt.cpp and the test exports (test/test_*.cpp).
#pragma once

#include <exception>
#include <string>

namespace vlqt {

// Sets the text vlqt_last_error returns (vlqt.cpp).
void setLastError(const std::string& message);

// Runs the body of an export: an exception sets vlqt_last_error and returns -1 instead of crossing the C ABI.
template <class F>
int guarded(F&& f)
{
    try {
        return f();
    } catch (const std::exception& e) {
        setLastError(std::string("exception: ") + e.what());
    } catch (...) {
        setLastError("unknown exception");
    }
    return -1;
}

} // namespace vlqt
