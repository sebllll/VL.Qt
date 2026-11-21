// App fonts (LoadFont node): the registry behind vlqt_font_add and vlqt_font_release, and the check of requested font
// families. Qt GUI thread.
#pragma once

class QString;

namespace vlqt {

// End of the pump (vlqt_app_process_events_for): removes the fonts that no LoadFont node references any more.
void fontsEndFrame();

/* warns once per requested family (log category vlqt.font) when Qt does not have it and substitutes another font.
 * Empty family and families already checked: nothing. Qt GUI thread only (the apply path). */
void warnMissingFontFamily(const QString& family);

} // namespace vlqt
