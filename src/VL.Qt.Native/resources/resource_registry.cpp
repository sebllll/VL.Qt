// The registry of the resource kinds: the only file that includes every kind.
#include "resources/resource_kind.h"

#include "image_resource.h"
#include "protocol.h"
#include "resources/audio_tap.h"
#include "resources/resources.h"
#include "shader_resource.h"
#include "snapshot_resource.h"
#include "vlqt_test.h" // test hook: VLQT_RESOURCE_KIND_TEST

namespace vlqt::res {

Kind* kindOf(int kind)
{
    switch (kind) {
    case VLQT_RESOURCE_KIND_TEST:  return testKind(); // test hook: test/test_resource_kind.cpp
    case VLQT_RESOURCE_KIND_IMAGE: return image::kind();
    case VLQT_RESOURCE_KIND_SNAPSHOT: return snapshot::kind();
    case VLQT_RESOURCE_KIND_SHADER: return shader::kind();
    case VLQT_RESOURCE_KIND_AUDIO_TAP: return audio::kind();
    default:                  return nullptr;
    }
}

} // namespace vlqt::res
