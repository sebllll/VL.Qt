#include "image_writer.h"

#include <cstring>

namespace vlqt::imagewriter
{
    // TODO: Implement with QImage + QImageWriter (#157).
    int write(const vlqt_pixel_desc* desc, const void* data, int len, const char* utf8Path, int quality, char* err, int errCap)
    {
        (void)desc; (void)data; (void)len; (void)utf8Path; (void)quality;
        if (err && errCap > 0)
        {
            const char* msg = "vlqt_image_write is not implemented yet";
            std::strncpy(err, msg, static_cast<size_t>(errCap) - 1);
            err[errCap - 1] = '\0';
        }
        return -1;
    }
}
