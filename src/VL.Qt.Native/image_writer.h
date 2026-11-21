#pragma once

#include "vlqt.h"

// Image file writer: pixels to a file through QImageWriter, used by vlqt_image_write (contract in vlqt.h); the
// reason of a failure goes to vlqt_last_error.
namespace vlqt::imagewriter
{
    int write(const vlqt_pixel_desc* desc, const void* data, int len, const char* utf8Path, int quality);
}
