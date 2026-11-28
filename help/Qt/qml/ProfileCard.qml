// Example file for the QML node factory (help patch "HowTo Create QML Nodes from Files"): the pin types beyond numbers,
// text and colours. An alias hands out a property of a child, a url is a Path pin, a font takes the Font node, a date
// a DateTime, and an enum gives an int pin whose help lists the values.
//
// @summary A profile card: photo, name, a status dot and the date since when the person is a member.
// @tags profile, avatar, card, alias, url, font, date, enum
import QtQuick

Item {
    id: card

    // Status of the person: the values of the enum are the numbers of the Status pin.
    enum Status { Offline, Away, Online }

    // Name under the photo: an alias of the text of the name label.
    property alias name: nameText.text
    // Background of the card: an alias of the colour of the panel.
    property alias background: panel.color
    // Font of the name: connect the Font node (its Family can come from LoadFont).
    property font nameFont: Qt.font({ pixelSize: 18, weight: Font.DemiBold })
    // Photo: a Path pin. The default is relative to this file, as QML resolves it.
    property url photo: "../assets/qt-help-image.png"
    // Member since: a DateTime pin, in local time.
    property date since: "2024-05-01"
    // Online status, shown as the colour of the dot.
    property int status: ProfileCard.Online

    // Auto Size takes these. Switch Auto Size off and Size sets width and height instead.
    implicitWidth: 220
    implicitHeight: 250

    Rectangle {
        id: panel
        anchors.fill: parent
        radius: 12
        color: "#20232b"
        clip: true

        Image {
            id: picture
            anchors.left: parent.left
            anchors.right: parent.right
            anchors.top: parent.top
            height: 140
            source: card.photo
            fillMode: Image.PreserveAspectCrop
        }

        Rectangle {
            anchors.right: parent.right
            anchors.top: picture.bottom
            anchors.margins: 12
            width: 14
            height: 14
            radius: 7
            color: card.status === ProfileCard.Online ? "#3ddc84" : card.status === ProfileCard.Away ? "#ffb020" : "#6b7280"
        }

        Text {
            id: nameText
            anchors.left: parent.left
            anchors.right: parent.right
            anchors.top: picture.bottom
            anchors.margins: 12
            anchors.rightMargin: 36
            text: "Ada Lovelace"
            color: "#eef1f7"
            font: card.nameFont
            elide: Text.ElideRight
        }

        Text {
            anchors.left: parent.left
            anchors.top: nameText.bottom
            anchors.leftMargin: 12
            anchors.topMargin: 6
            text: isNaN(card.since) ? "" : "Member since " + Qt.formatDate(card.since, "d MMMM yyyy")
            color: "#9aa3b5"
            font.pixelSize: 13
        }
    }
}
