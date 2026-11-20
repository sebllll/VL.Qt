#pragma once

#include "vlqt.h"

// Image file writer: pixels to a file through QImageWriter, used by vlqt_image_write (contract in vlqt.h).
namespace vlqt::imagewriter
{
    int write(const vlqt_pixel_desc* desc, const void* data, int len, const char* utf8Path, int quality, char* err, int errCap);
}
