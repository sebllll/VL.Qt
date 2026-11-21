// App fonts (LoadFont node): the registry behind vlqt_font_add and vlqt_font_release. Qt GUI thread.
#pragma once

namespace vlqt {

// End of the pump (vlqt_app_process_events_for): removes the fonts that no LoadFont node references any more.
void fontsEndFrame();

} // namespace vlqt
