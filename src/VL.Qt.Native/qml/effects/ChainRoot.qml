// layer.effect needs a component with a creation context: an inline Component of a holder object (one per engine).
import QtQuick
import VLQt.EffectChain
QtObject { property Component fx: Component { VlqtEffectChain {} } }
