// A dark dashboard card: a smooth area chart of points (time and value) with labels above a panel
// with a caption, an amount that counts to its value and a change in percent with an up or down badge.
//
// @summary A dashboard card with a gradient area chart, axis labels, an animated amount and a change badge.
// @tags chart, dashboard, widget, card, kpi, trend
pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Shapes

Item {
    id: root

    // Points of the curve: X is the time, Y the value. The curve spans the whole width, from the
    // smallest X to the largest one, so the points need not be evenly spaced or sorted.
    property list<point> points: [
        Qt.point(0, 12), Qt.point(1, 20), Qt.point(2, 31), Qt.point(3, 28),
        Qt.point(5, 44), Qt.point(6, 38), Qt.point(8, 52), Qt.point(9, 55),
        Qt.point(11, 66), Qt.point(12, 62), Qt.point(14, 84), Qt.point(16, 74)
    ]
    // Value at the bottom (X) and at the top (Y) of the chart. Y not greater than X fits the range
    // to the points.
    property vector2d range: Qt.vector2d(0, 0)
    // Axis labels under the chart, evenly spread.
    property list<string> labels: ["Jan", "Feb", "Mar", "Apr", "May"]
    // Index of the highlighted label (white, with a dot above it). -1 highlights none.
    property int highlight: 4
    // Small text above the amount.
    property string caption: "Today"
    // The amount in the panel. It counts to a new value instead of jumping.
    property real amount: 62970
    // Text before the amount.
    property string prefix: "$"
    // Decimals of the amount.
    property int decimals: 0
    // Locale for the thousands separator of the amount, such as en_US or de_DE.
    property string localeName: "en_US"
    // Change in percent. Positive shows an up arrow, negative a down arrow.
    property real change: 5.3
    // Colour of the curve, the area, the dot and the badge.
    property color accent: "#7c5cfa"
    // Colour of the card.
    property color background: "#101722"
    // Colour of the panel.
    property color panelColor: "#1a2331"
    // Colour of the labels and the small texts.
    property color mutedColor: "#6b7484"
    // Colour of the amount and the highlighted label.
    property color textColor: "#f2f4f8"

    // Auto Size takes these. Switch Auto Size off and Size sets width and height instead.
    implicitWidth: 260
    implicitHeight: 276

    // Internal state. The tag keeps a declaration out of the pins of the node.

    // @internal
    // Layout of the chart part, in DIP from the top of the card.
    readonly property real chartTop: 34
    // @internal
    readonly property real fillBottom: 124
    // @internal
    readonly property real labelY: 142

    // @internal
    // The points of the curve, sorted by X.
    readonly property var sortedPoints: {
        const all = []
        for (let i = 0; i < root.points.length; ++i)
            all.push({ x: root.points[i].x, y: root.points[i].y })
        all.sort((a, b) => a.x - b.x)
        return all
    }

    // @internal
    // The amount shown right now; it glides to Amount on the frame clock of the renderer.
    property real shownAmount: root.amount
    Behavior on shownAmount {
        NumberAnimation { duration: 700; easing.type: Easing.OutCubic }
    }

    // Catmull-Rom spline through the points, written as SVG cubic segments.
    function curve(w, closed) {
        const pts = root.sortedPoints
        if (pts.length < 2 || w <= 0)
            return ""
        let lo = pts[0].y, hi = pts[0].y
        for (let i = 1; i < pts.length; ++i) {
            lo = Math.min(lo, pts[i].y)
            hi = Math.max(hi, pts[i].y)
        }
        if (root.range.y > root.range.x) {
            lo = root.range.x
            hi = root.range.y
        }
        const range = hi - lo > 0 ? hi - lo : 1
        const top = root.chartTop
        const bottom = root.fillBottom - 14
        const first = pts[0].x
        const span = pts[pts.length - 1].x - first
        const xy = []
        for (let i = 0; i < pts.length; ++i) {
            // Points that all share one X are spread evenly.
            const along = span > 0 ? (pts[i].x - first) / span : i / (pts.length - 1)
            xy.push([along * w, bottom - Math.max(0, Math.min(1, (pts[i].y - lo) / range)) * (bottom - top)])
        }

        let d = "M " + xy[0][0] + " " + xy[0][1]
        for (let i = 0; i < xy.length - 1; ++i) {
            const p0 = xy[Math.max(0, i - 1)]
            const p1 = xy[i]
            const p2 = xy[i + 1]
            const p3 = xy[Math.min(xy.length - 1, i + 2)]
            const c1x = p1[0] + (p2[0] - p0[0]) / 6
            const c1y = p1[1] + (p2[1] - p0[1]) / 6
            const c2x = p2[0] - (p3[0] - p1[0]) / 6
            const c2y = p2[1] - (p3[1] - p1[1]) / 6
            d += " C " + c1x + " " + c1y + " " + c2x + " " + c2y + " " + p2[0] + " " + p2[1]
        }
        if (closed)
            d += " L " + w + " " + root.fillBottom + " L 0 " + root.fillBottom + " Z"
        return d
    }

    Rectangle {
        id: card
        anchors.fill: parent
        radius: 24
        color: root.background
    }

    // Area and line of the chart.
    Shape {
        id: chart
        anchors.fill: parent
        preferredRendererType: Shape.CurveRenderer

        ShapePath {
            strokeColor: "transparent"
            strokeWidth: 0
            fillGradient: LinearGradient {
                x1: 0; y1: root.chartTop
                x2: 0; y2: root.fillBottom
                GradientStop { position: 0.0; color: Qt.rgba(root.accent.r, root.accent.g, root.accent.b, 0.95) }
                GradientStop { position: 0.55; color: Qt.rgba(root.accent.r, root.accent.g, root.accent.b, 0.45) }
                GradientStop { position: 1.0; color: Qt.rgba(root.accent.r, root.accent.g, root.accent.b, 0.0) }
            }
            PathSvg { path: root.curve(root.width, true) }
        }

        ShapePath {
            strokeColor: Qt.lighter(root.accent, 1.3)
            strokeWidth: 2
            fillColor: "transparent"
            capStyle: ShapePath.RoundCap
            joinStyle: ShapePath.RoundJoin
            PathSvg { path: root.curve(root.width, false) }
        }
    }

    // Axis labels, evenly spread with a margin at both sides.
    Repeater {
        model: root.labels.length

        Item {
            id: tick
            required property int index
            readonly property bool current: index === root.highlight
            readonly property real pad: root.width * 0.155
            readonly property real stepX: root.labels.length > 1
                ? (root.width - 2 * pad) / (root.labels.length - 1) : 0

            x: root.labels.length > 1 ? pad + index * stepX : root.width / 2
            y: root.labelY

            Rectangle {
                visible: tick.current
                width: 6
                height: 6
                radius: 3
                x: -width / 2
                y: -20
                color: Qt.lighter(root.accent, 1.2)
            }

            Text {
                anchors.horizontalCenter: parent.left
                anchors.verticalCenter: parent.top
                text: root.labels[tick.index]
                color: tick.current ? root.textColor : root.mutedColor
                font.pixelSize: 12
            }
        }
    }

    // The panel with caption, amount and change.
    Rectangle {
        id: panel
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        anchors.leftMargin: 14
        anchors.rightMargin: 14
        anchors.bottomMargin: 20
        height: 92
        radius: 14
        color: root.panelColor
        border.color: Qt.lighter(root.panelColor, 1.25)
        border.width: 1

        Text {
            id: captionText
            anchors.left: parent.left
            anchors.top: parent.top
            anchors.leftMargin: 16
            anchors.topMargin: 14
            text: root.caption
            color: root.mutedColor
            font.pixelSize: 14
        }

        Text {
            id: amountText
            anchors.left: captionText.left
            anchors.top: captionText.bottom
            anchors.topMargin: 2
            text: root.prefix + Number(root.shownAmount).toLocaleString(Qt.locale(root.localeName), "f", Math.max(0, root.decimals))
            color: root.textColor
            font.pixelSize: 30
            font.weight: Font.Light
        }

        Rectangle {
            id: badge
            anchors.right: parent.right
            anchors.verticalCenter: amountText.verticalCenter
            anchors.rightMargin: 14
            width: 24
            height: 24
            radius: 7
            color: Qt.lighter(root.accent, 1.15)

            // Arrow; turned upside down for a negative change.
            Shape {
                anchors.fill: parent
                rotation: root.change < 0 ? 180 : 0
                preferredRendererType: Shape.CurveRenderer

                ShapePath {
                    strokeColor: "white"
                    strokeWidth: 2
                    fillColor: "transparent"
                    capStyle: ShapePath.RoundCap
                    joinStyle: ShapePath.RoundJoin
                    PathSvg { path: "M 12 17 L 12 7 M 8 11 L 12 7 L 16 11" }
                }
            }
        }

        Text {
            anchors.right: badge.left
            anchors.rightMargin: 10
            anchors.verticalCenter: badge.verticalCenter
            text: Math.abs(root.change).toFixed(1) + "%"
            color: root.mutedColor
            font.pixelSize: 13
        }
    }
}