/*
 * protocol.h - single source of truth for the VL.Qt command buffer protocol: the wire format only.
 * What the commands, events and values mean (ordering, defaults, error behaviour): docs/contributing/protocol.md.
 *
 * Mirrored 1:1 in src/VL.Qt/Wire/Protocol.cs. The unit test ProtocolDriftTests parses this file
 * and compares every constant (name + value) with Protocol.cs, so keep the format simple:
 *   - only `#define VLQT_<NAME> <integer>` and `enum { VLQT_<GROUP>_<NAME> = <integer>, ... };`
 *   - one constant per line, explicit values, no expressions.
 * Naming rule for the mirror: VLQT_<GROUP>_<UPPER_SNAKE> <-> Protocol.<Group>.<PascalCase>
 * (e.g. VLQT_OP_SET_CLEAR_COLOR <-> Protocol.Op.SetClearColor); plain #defines map to Protocol.<PascalCase>. A group
 * name of the mirror that ends in Id or Flags drops that word in C (Protocol.EventId.Hover <-> VLQT_EVENT_HOVER,
 * Protocol.ImageLoadFlags.Mipmaps <-> VLQT_IMAGE_LOAD_MIPMAPS).
 *
 * Wire format (little-endian, every command 4-byte aligned):
 *   header  u16 op, u16 flags (0, reserved), u32 payloadLen (payload bytes after the header, excluding alignment padding;
 *           at most VLQT_MAX_COMMAND_PAYLOAD)
 *   CREATE      u32 id, u16 type
 *   DESTROY     u32 id
 *   REPARENT    u32 id, u32 parent, u32 index (parent 0 = window root)
 *   SET         u32 id, u16 prop, u16 valueType, value
 *   BIND        u32 id, u32 feedbackId (feedbackId 0 = unbind)
 *   SET_CLEAR_COLOR color
 *   RESET       (no payload)
 *   SET_CLEAR   u32 clear
 *   SET_SPACE   u32 space (vlqt_space)
 *   SET_STYLE   u32 id, u16 count, u16 flags (vlqt_style_set), then count entries, each 4-byte aligned:
 *               u16 field (vlqt_style_field), u8 state (vlqt_style_state), u8 valueType, value (like SET)
 *   SET_UNIFORMS u32 id, u16 count, u16 flags (vlqt_uniform_set), then count entries, each 4-byte aligned:
 *               u16 valueType, u16 nameLen (1..VLQT_UNIFORM_NAME_MAX), name (UTF-8, no terminator), padding to 4,
 *               value (like SET), padding to 4
 *   SET_EFFECT_CHAIN u32 id, u16 linkCount, u16 flags (0, reserved), then linkCount links in application order, each
 *               u16 kind (vlqt_effect_chain_link), u16 count, u32 bodyLen, body (bodyLen bytes, 4-byte aligned)
 *     kind MULTI: count entries, each u16 key (vlqt_multi_effect_key), u16 valueType, value like SET, padding to 4
 *     kind SHADER: u16 template (vlqt_effect_template), u16 shaderCount, u64 shaderIds[shaderCount], 4 x f32 padding
 *               (left, top, right, bottom), f32 strength, then count uniform entries like SET_UNIFORMS
 *   SET_QML_PROPERTIES u32 id, u16 count, u16 flags (0, reserved), then count entries like SET_UNIFORMS entries
 *               (u16 valueType, u16 nameLen, name, padding to 4, value, padding to 4); STRING is allowed
 * Values: f32, i32, bool (u8), color (4 x f32, straight alpha), rectf (4 x f32: x, y, w, h),
 *         mat4 (16 x f32, row-major like Stride), string (u32 len + UTF-8), ref (u32 id), u64,
 *         vec2/vec3/vec4 (2/3/4 x f32), ivec2 (2 x i32), sampler (8 bytes: u8 filter, u8 mip filter, u8 address U,
 *         u8 address V, 4 reserved bytes),
 *         f32 array (u32 count, count x f32), path (u32 verbCount, u32 pointCount, verbCount x u8, zero padding to 4 bytes,
 *         pointCount x 2 x f32; verbs: vlqt_path_verb).
 * Ids are u32, assigned by the C# reconciler; 0 = window root.
 *
 * Poll buffer (QtNative -> C#), per record, 4-byte aligned: u16 event, u16 flags (0), u32 len, u32 feedbackId, payload;
 *   len = bytes of feedbackId + payload (without padding). Payload of each event: comment at vlqt_event.
 *
 * Resource status queue (vlqt_res_poll, vlqt.h), per record, 4-byte aligned: u16 event, u16 flags (0), u32 len, u64 id,
 *   payload; len = 8 + payload bytes (without padding). Ids are u64: index (low 32 bits) | generation (high 32 bits).
 *   Payload of each event: comment at vlqt_resource_event.
 *
 * Pure C header: compiles as C and C++.
 */
#ifndef VLQT_PROTOCOL_H
#define VLQT_PROTOCOL_H

/* One number for the command protocol (this file) and the C ABI (vlqt.h, vlqt_test.h): vlqt_app_get_abi_version() returns it.
 * Bump it on EVERY change to a constant, a struct layout or an exported signature (also additions); mirror it in Protocol.cs. */
#define VLQT_ABI_VERSION 27
#define VLQT_COMMAND_ALIGN 4
#define VLQT_HEADER_SIZE 8 /* command, poll record and resource record: u16 op/event, u16 flags (0), u32 len */
#define VLQT_MAX_COMMAND_PAYLOAD 67108864 /* cap of payloadLen (64 MB) */
#define VLQT_ROOT_ID 0
#define VLQT_UNIFORM_NAME_MAX 255 /* max UTF-8 bytes of a uniform or QML property name */

/* Fixed sizes of the wire format in bytes (payload parts; the header, VLQT_HEADER_SIZE, comes before). The decoder checks
 * lengths against them, CommandWriter and EventReader write and read with them, the unit tests compare the mirror. */
#define VLQT_CREATE_PAYLOAD 6 /* CREATE: u32 id, u16 type (the whole payload) */
#define VLQT_REPARENT_PAYLOAD 12 /* REPARENT: u32 id, u32 parent, u32 index (the whole payload) */
#define VLQT_BIND_PAYLOAD 8 /* BIND: u32 id, u32 feedbackId (the whole payload) */
#define VLQT_SET_FIELDS_SIZE 8 /* SET: u32 id, u16 prop, u16 valueType in front of the value */
#define VLQT_LIST_FIELDS_SIZE 8 /* SET_STYLE, SET_UNIFORMS, SET_QML_PROPERTIES, SET_EFFECT_CHAIN: u32 id, u16 count, u16 flags in front of the entries */
/* Entry of SET_STYLE (u16 field, u8 state, u8 valueType), SET_UNIFORMS and SET_QML_PROPERTIES (u16 valueType, u16 nameLen)
 * in front of name and value */
#define VLQT_ENTRY_HEADER_SIZE 4
#define VLQT_EFFECT_CHAIN_LINK_HEADER_SIZE 8 /* effect chain link: u16 kind, u16 count, u32 bodyLen in front of the body */
#define VLQT_EFFECT_CHAIN_LINK_SHADER_FIXED_SIZE 20 /* SHADER link body after u16 template, u16 shaderCount and the shader ids: 4 x f32 padding, f32 strength */
#define VLQT_EVENT_FIELDS_SIZE 4 /* poll record after its header: u32 feedbackId in front of the payload */

/* Opcodes (u16) */
enum vlqt_op {
    VLQT_OP_CREATE = 1,
    VLQT_OP_DESTROY = 2,
    VLQT_OP_REPARENT = 3,
    VLQT_OP_SET = 4,
    VLQT_OP_BIND = 5,
    VLQT_OP_SET_CLEAR_COLOR = 6,
    VLQT_OP_RESET = 7,
    VLQT_OP_SET_STYLE = 8, /* resolved widget style */
    VLQT_OP_SET_CLEAR = 9, /* u32 clear: 1 = clear every frame, 0 = keep the previous frame */
    VLQT_OP_SET_SPACE = 10, /* u32 vlqt_space */
    /* 16.. commands of the effect and QML elements */
    VLQT_OP_SET_UNIFORMS = 16, /* ShaderEffect uniforms by name */
    VLQT_OP_SET_EFFECT_CHAIN = 17, /* effect chain of an element */
    VLQT_OP_SET_QML_PROPERTIES = 18 /* properties of a QmlElement's root by name */
};

/* Value types of SET and of the entries of the list commands (u16) */
enum vlqt_value_type {
    VLQT_VALUE_TYPE_F32 = 1,
    VLQT_VALUE_TYPE_I32 = 2,
    VLQT_VALUE_TYPE_BOOL = 3,
    VLQT_VALUE_TYPE_COLOR = 4,
    VLQT_VALUE_TYPE_RECTF = 5,
    VLQT_VALUE_TYPE_MAT4 = 6,
    VLQT_VALUE_TYPE_STRING = 7,
    VLQT_VALUE_TYPE_REF = 8,
    VLQT_VALUE_TYPE_U64 = 9, /* u64, e.g. a resource id (IMAGE) */
    VLQT_VALUE_TYPE_F32_ARRAY = 10, /* u32 count, count x f32 (SHAPE points, dash pattern) */
    VLQT_VALUE_TYPE_PATH = 11,      /* u32 verbCount, u32 pointCount, verbs (u8, padded to 4), points (2 x f32 each) */
    /* 16.. uniform values (SET_UNIFORMS) and MESH_RESOLUTION */
    VLQT_VALUE_TYPE_VEC2 = 16,  /* 2 x f32 */
    VLQT_VALUE_TYPE_VEC3 = 17,  /* 3 x f32 */
    VLQT_VALUE_TYPE_VEC4 = 18,  /* 4 x f32 (unlike COLOR not premultiplied by QtNative) */
    VLQT_VALUE_TYPE_IVEC2 = 19, /* 2 x i32 */
    VLQT_VALUE_TYPE_SAMPLER = 20 /* 8 bytes: sampler state of a sampler2D */
};

/* Element type ids of CREATE (u16). 2, 8 and 16 are removed, keep them unused. */
enum vlqt_element_type {
    VLQT_ELEMENT_TYPE_GROUP = 1,
    VLQT_ELEMENT_TYPE_RECTANGLE = 3,
    VLQT_ELEMENT_TYPE_TEXT = 4,
    VLQT_ELEMENT_TYPE_IMAGE = 5,
    VLQT_ELEMENT_TYPE_ROW = 6,
    VLQT_ELEMENT_TYPE_COLUMN = 7,
    VLQT_ELEMENT_TYPE_VIDEO = 9,
    VLQT_ELEMENT_TYPE_BUTTON = 10, /* QtQuick.Controls Button (style Basic) */
    VLQT_ELEMENT_TYPE_SPIN_BOX = 11, /* QtQuick.Controls SpinBox (style Basic): Input (Int), EVENT_VALUE i32 */
    VLQT_ELEMENT_TYPE_CHECK_BOX = 12, /* QtQuick.Controls CheckBox: Toggle look Checkbox, EVENT_VALUE bool */
    VLQT_ELEMENT_TYPE_SWITCH = 13,    /* QtQuick.Controls Switch: Toggle look Switch, EVENT_VALUE bool */
    VLQT_ELEMENT_TYPE_SLIDER = 14,    /* QtQuick.Controls Slider: EVENT_VALUE f32 */
    VLQT_ELEMENT_TYPE_TEXT_FIELD = 15, /* QtQuick.Controls TextField (single line): EVENT_VALUE string */
    VLQT_ELEMENT_TYPE_SHADER = 24, /* own item with .qsb shaders and uniforms by name */
    VLQT_ELEMENT_TYPE_QML = 25, /* loads a user .qml file, properties by name through SET_QML_PROPERTIES */
    VLQT_ELEMENT_TYPE_SHAPE = 26, /* QtQuick.Shapes Shape with one ShapePath: polyline, polygon or path, fill and stroke */
    /* Input handlers from 32: protocol objects attached to an element by REPARENT. */
    VLQT_ELEMENT_TYPE_HOVER_HANDLER = 32, /* Qt Quick HoverHandler: EVENT_HOVER (positions: Point handler) */
    VLQT_ELEMENT_TYPE_TAP_HANDLER = 33,   /* TapHandler (ReleaseWithinBounds): EVENT_PRESS, EVENT_CLICK, EVENT_TAP_COUNT, EVENT_LONG_PRESS */
    VLQT_ELEMENT_TYPE_POINT_HANDLER = 34, /* PointHandler: EVENT_ACTIVE, EVENT_POINTER while active; buttons 0: mouse hover */
    VLQT_ELEMENT_TYPE_WHEEL_HANDLER = 35, /* WheelHandler (target null): EVENT_WHEEL */
    VLQT_ELEMENT_TYPE_TOUCH_POINTS_HANDLER = 36, /* own handler (passive grabs): EVENT_TOUCH_POINTS, all points pressed in the item */
    VLQT_ELEMENT_TYPE_PINCH_HANDLER = 37, /* PinchHandler (target null): EVENT_ACTIVE, EVENT_PINCH; moves nothing */
    VLQT_ELEMENT_TYPE_DRAG_HANDLER = 38   /* DragHandler (target null): EVENT_ACTIVE, EVENT_DRAG; moves the item by its drag offset */
};

/* Property ids of SET (u16). 1..255 common to all items; type-specific ids start at 256 and are looked up per type, so one id
 * may serve several types. Blocks: 256.. Rectangle, 512.. Text, 544.. Image, 576.. Row/Column, 608.. reserved, 640.. size mode
 * of containers, 672.. Video, 704.. Controls (706.. value inputs), 768.. input handlers, 800.. reserved, 832.. MULTI keys (not
 * via SET), 896.. ShaderEffect, 928.. QmlElement, 960.. Shape. Geometry (size, anchor, transform) and the property blocks:
 * docs/contributing/protocol.md. */
enum vlqt_property {
    VLQT_PROPERTY_SIZE = 1,       /* vec2 (w, h), logical pixels */
    VLQT_PROPERTY_TRANSFORM = 2,
    VLQT_PROPERTY_OPACITY = 3,
    VLQT_PROPERTY_VISIBLE = 4,    /* bool, the Enabled pin (default true): false takes the item out of the scene */
    VLQT_PROPERTY_CLIP = 5,
    VLQT_PROPERTY_ANCHOR = 6,     /* i32 Qt::Alignment, default AlignLeft|AlignTop = 0x21 */
    VLQT_PROPERTY_DEBUG = 7, /* bool: draw this element in the window's debug overlay (not recursive) */
    VLQT_PROPERTY_COLOR = 256,
    VLQT_PROPERTY_RADIUS = 257,
    VLQT_PROPERTY_BORDER_COLOR = 258,
    VLQT_PROPERTY_BORDER_WIDTH = 259,
    VLQT_PROPERTY_TEXT = 512,
    VLQT_PROPERTY_FONT_FAMILY = 513,
    VLQT_PROPERTY_FONT_SIZE = 514,
    VLQT_PROPERTY_FONT_WEIGHT = 515,
    VLQT_PROPERTY_FONT_ITALIC = 516,
    VLQT_PROPERTY_HORIZONTAL_ALIGNMENT = 517,
    VLQT_PROPERTY_VERTICAL_ALIGNMENT = 518,
    VLQT_PROPERTY_WRAP_MODE = 519,
    VLQT_PROPERTY_ELIDE = 520,
    VLQT_PROPERTY_LINE_HEIGHT = 521,
    VLQT_PROPERTY_LETTER_SPACING = 522,
    VLQT_PROPERTY_FONT_SIZE_MODE = 523,
    VLQT_PROPERTY_SOURCE = 544,
    VLQT_PROPERTY_FILL_MODE = 545, /* i32 vlqt_fill_mode, Image and Video (Video: no TILE) */
    VLQT_PROPERTY_SMOOTH = 546,
    VLQT_PROPERTY_IMAGE = 547, /* u64 image resource id, 0 = no image */
    VLQT_PROPERTY_SPACING = 576,
    VLQT_PROPERTY_PADDING = 577,
    VLQT_PROPERTY_CHILD_ALIGNMENT = 578,
    VLQT_PROPERTY_AUTO_SIZE = 640,
    VLQT_PROPERTY_PLAYING = 672,
    VLQT_PROPERTY_LOOP = 673,
    VLQT_PROPERTY_PLAYBACK_RATE = 674,
    VLQT_PROPERTY_VOLUME = 675,
    VLQT_PROPERTY_KEEP_LAST_FRAME = 676,
    VLQT_PROPERTY_AUDIO_TAP = 677, /* u64 audio tap resource id (VLQT_RESOURCE_KIND_AUDIO_TAP), 0 = none (Qt's own output) */
    VLQT_PROPERTY_ENABLED = 704,
    VLQT_PROPERTY_AUTO_REPEAT = 705,
    VLQT_PROPERTY_VALUE = 706,   /* value of a value input (SpinBox i32, CheckBox/Switch bool, Slider f32, TextField string), no EVENT_VALUE echo */
    VLQT_PROPERTY_MINIMUM = 707, /* SpinBox i32 (default INT32_MIN), Slider f32 (default 0) */
    VLQT_PROPERTY_MAXIMUM = 708, /* SpinBox i32 (default INT32_MAX), Slider f32 (default 1) */
    /* SpinBox i32 step of +/-, arrow keys and wheel (default 1), Slider f32 (default 0 = continuous) */
    VLQT_PROPERTY_STEP = 709,
    VLQT_PROPERTY_EDITABLE = 710, /* bool, SpinBox: the value can be typed (default true); TextField: not readOnly (default true) */
    VLQT_PROPERTY_ORIENTATION = 711, /* i32 Qt::Orientation, Slider: 1 horizontal (default), 2 vertical */
    VLQT_PROPERTY_PLACEHOLDER = 712, /* string, TextField placeholderText shown while empty (default empty) */
    /* 768 is reserved */
    VLQT_PROPERTY_ACCEPTED_BUTTONS = 769, /* i32 Qt::MouseButtons, Tap + Point + Drag handler (default Qt::LeftButton; Point 0: hover) */
    VLQT_PROPERTY_ACCEPTED_DEVICES = 770, /* i32 QInputDevice::DeviceTypes, Tap + Point + Wheel + Drag + Touch Points + Pinch handler (default: Qt's = all) */
    VLQT_PROPERTY_MOVES_ELEMENT = 771,    /* bool, Drag handler: the drag moves the item by its drag offset (default true; false: report only) */
    VLQT_PROPERTY_DRAG_AXIS = 772,        /* i32, Drag handler: 0 both axes (default), 1 x only, 2 y only */
    VLQT_PROPERTY_DRAG_LIMITS = 773,      /* rectf, Drag handler: minX, minY, maxX, maxY of the drag offset (logical pixels, +-inf = none) */
    VLQT_PROPERTY_MINIMUM_POINT_COUNT = 774, /* i32 >= 2, Pinch handler (default 2) */
    VLQT_PROPERTY_MAXIMUM_POINT_COUNT = 775, /* i32 >= 2, Pinch handler (default 2); more points: the gesture pauses */
    VLQT_PROPERTY_OCCLUSION = 776,        /* bool, Touch Points handler: ignore presses an element on top grabs (default true) */
    /* 832 ... 855: keys of MULTI links, vlqt_multi_effect_key below; not settable by SET */
    /* 896.. ShaderEffect (it also takes SOURCE and AUTO_SIZE) */
    VLQT_PROPERTY_FRAGMENT_SHADER = 896,  /* u64 shader resource id (stage FRAGMENT), 0 = none */
    VLQT_PROPERTY_VERTEX_SHADER = 897,    /* u64 shader resource id (stage VERTEX), 0 = Qt's default vertex shader */
    VLQT_PROPERTY_BLENDING = 898,         /* bool, premultiplied alpha blending (default true) */
    VLQT_PROPERTY_MESH_RESOLUTION = 899,  /* ivec2, grid subdivisions x, y >= 1 (default 1, 1) */
    /* 928.. QmlElement */
    VLQT_PROPERTY_QML_SOURCE = 928,       /* string, absolute path of the .qml file, empty = nothing loaded */
    VLQT_PROPERTY_QML_RELOAD = 929,       /* i32 counter, a new value reloads the same file */
    /* 960.. Shape: lengths in logical pixels, dash and offset in stroke-width units */
    VLQT_PROPERTY_SHAPE_POINTS = 960,       /* f32 array, x,y pairs */
    VLQT_PROPERTY_SHAPE_CLOSED = 961,       /* bool, close the point list */
    VLQT_PROPERTY_SHAPE_PATH = 962,         /* path, verbs + points */
    VLQT_PROPERTY_SHAPE_FILL = 963,         /* color, fill color */
    VLQT_PROPERTY_SHAPE_STROKE_COLOR = 964, /* color */
    VLQT_PROPERTY_SHAPE_STROKE_WIDTH = 965, /* f32, < 0 = no stroke */
    VLQT_PROPERTY_SHAPE_CAP = 966,          /* i32, 0 Flat, 1 Square, 2 Round */
    VLQT_PROPERTY_SHAPE_JOIN = 967,         /* i32, 0 Miter, 1 Bevel, 2 Round */
    VLQT_PROPERTY_SHAPE_MITER_LIMIT = 968,  /* f32 */
    VLQT_PROPERTY_SHAPE_DASH = 969,         /* f32 array, empty = solid; stroke-width units */
    VLQT_PROPERTY_SHAPE_DASH_OFFSET = 970,  /* f32, stroke-width units */
    VLQT_PROPERTY_SHAPE_TRIM_START = 971,   /* f32, 0..1 */
    VLQT_PROPERTY_SHAPE_TRIM_END = 972,     /* f32, 0..1 */
    VLQT_PROPERTY_SHAPE_TRIM_OFFSET = 973,  /* f32 */
    VLQT_PROPERTY_SHAPE_FILL_RULE = 974     /* i32, 0 NonZero, 1 EvenOdd */
};

/* Keys of the entries of a MULTI link of SET_EFFECT_CHAIN (u16): the values of one MultiEffect run, Qt's defaults.
 * Not settable by SET; the numbers continue the block 832 of vlqt_property. */
enum vlqt_multi_effect_key {
    /* 832 unused */
    VLQT_MULTI_EFFECT_KEY_BLUR_ENABLED = 833,       /* bool (default false) */
    VLQT_MULTI_EFFECT_KEY_BLUR = 834,               /* f32 0..1 */
    VLQT_MULTI_EFFECT_KEY_BLUR_MAX = 835,           /* i32 DIP (default 32), switches shaders */
    VLQT_MULTI_EFFECT_KEY_BLUR_MULTIPLIER = 836,    /* f32 (default 0) */
    VLQT_MULTI_EFFECT_KEY_SHADOW_ENABLED = 837,     /* bool (default false) */
    VLQT_MULTI_EFFECT_KEY_SHADOW_COLOR = 838,       /* color (default black) */
    VLQT_MULTI_EFFECT_KEY_SHADOW_HORIZONTAL_OFFSET = 839, /* f32 logical pixels */
    VLQT_MULTI_EFFECT_KEY_SHADOW_VERTICAL_OFFSET = 840,   /* f32 logical pixels */
    VLQT_MULTI_EFFECT_KEY_SHADOW_BLUR = 841,        /* f32 0..1 (default 1) */
    VLQT_MULTI_EFFECT_KEY_SHADOW_SCALE = 842,       /* f32 (default 1) */
    VLQT_MULTI_EFFECT_KEY_BRIGHTNESS = 843,         /* f32 -1..1 */
    VLQT_MULTI_EFFECT_KEY_CONTRAST = 844,           /* f32 -1..1 */
    VLQT_MULTI_EFFECT_KEY_SATURATION = 845,         /* f32 -1..1 */
    VLQT_MULTI_EFFECT_KEY_COLORIZATION = 846,       /* f32 0..1 */
    VLQT_MULTI_EFFECT_KEY_COLORIZATION_COLOR = 847, /* color (default red) */
    VLQT_MULTI_EFFECT_KEY_AUTO_PADDING = 848,       /* bool autoPaddingEnabled (default true) */
    VLQT_MULTI_EFFECT_KEY_PADDING_RECT = 849,       /* rectf paddingRect in logical pixels (x left, y top, w right, h bottom), with AUTO_PADDING off */
    VLQT_MULTI_EFFECT_KEY_MASK_SOURCE = 850,        /* ref: mask element object, 0 = no mask */
    VLQT_MULTI_EFFECT_KEY_MASK_THRESHOLD_MIN = 851, /* f32 0..1 (default 0) */
    VLQT_MULTI_EFFECT_KEY_MASK_SPREAD_AT_MIN = 852, /* f32 0..1 (default 0) */
    VLQT_MULTI_EFFECT_KEY_MASK_THRESHOLD_MAX = 853, /* f32 0..1 (default 1) */
    VLQT_MULTI_EFFECT_KEY_MASK_SPREAD_AT_MAX = 854, /* f32 0..1 (default 0) */
    VLQT_MULTI_EFFECT_KEY_MASK_INVERTED = 855      /* bool (default false) */
};

/* Path verbs of VLQT_VALUE_TYPE_PATH (u8) */
enum vlqt_path_verb {
    VLQT_VERB_MOVE = 0,  /* 1 point */
    VLQT_VERB_LINE = 1,  /* 1 point */
    VLQT_VERB_QUAD = 2,  /* 2 points: control, end */
    VLQT_VERB_CUBIC = 3, /* 3 points: control 1, control 2, end */
    VLQT_VERB_CLOSE = 4  /* 0 points */
};

/* SET_UNIFORMS flags (u16, bit mask) */
enum vlqt_uniform_set {
    VLQT_UNIFORM_SET_CLEAR = 1 /* empty the element's uniform table before applying the entries (full send) */
};

/* Link kinds of SET_EFFECT_CHAIN (u16) */
enum vlqt_effect_chain_link {
    VLQT_EFFECT_CHAIN_LINK_MULTI = 1, /* one MultiEffect run: vlqt_multi_effect_key entries */
    VLQT_EFFECT_CHAIN_LINK_SHADER = 2 /* filter shader link: template, shader ids, padding, strength (PYRAMID radius), uniforms */
};

/* Pass templates of a SHADER link (u16); the passes: docs/contributing/protocol.md, docs/effects-and-shaders.md */
enum vlqt_effect_template {
    VLQT_EFFECT_TEMPLATE_SINGLE = 1,   /* one pass of the shader over the input */
    VLQT_EFFECT_TEMPLATE_TWO_PASS = 2, /* the shader twice: passIndex 0, then 1 (motion blurs) */
    VLQT_EFFECT_TEMPLATE_PYRAMID = 3   /* prefilter, pyramid_down/up stages, composite (Glow, InnerShadow) */
};

/* SET_STYLE flags (u16, bit mask) */
enum vlqt_style_set {
    VLQT_STYLE_SET_CLEAR = 1 /* empty the element's style table before applying the entries (full send) */
};

/* Style states (u8 of a SET_STYLE entry). Every field exists per state; rank and fallback: docs/contributing/protocol.md. */
enum vlqt_style_state {
    VLQT_STYLE_STATE_NORMAL = 0,
    VLQT_STYLE_STATE_HOVERED = 1,
    VLQT_STYLE_STATE_PRESSED = 2,
    VLQT_STYLE_STATE_DISABLED = 3,
    VLQT_STYLE_STATE_CHECKED = 4
};

/* Style fields (u16 of a SET_STYLE entry), grouped by part: colors 1.., Frame 32.., Text 64.., Indicator 96.., Track 128..
 * The value type per field is fixed (comment). Lengths in logical pixels. What the parts are: docs/contributing/protocol.md. */
enum vlqt_style_field {
    VLQT_STYLE_FIELD_FRAME_COLOR = 1,        /* color */
    VLQT_STYLE_FIELD_BORDER_COLOR = 2,       /* color, border of Frame and Indicator */
    VLQT_STYLE_FIELD_TEXT_COLOR = 3,         /* color */
    VLQT_STYLE_FIELD_INDICATOR_COLOR = 4,    /* color */
    VLQT_STYLE_FIELD_TRACK_COLOR = 5,        /* color */
    VLQT_STYLE_FIELD_TRACK_FILL_COLOR = 6,   /* color */
    VLQT_STYLE_FIELD_FRAME_RADIUS = 32,      /* f32 */
    VLQT_STYLE_FIELD_FRAME_BORDER_WIDTH = 33, /* f32 */
    VLQT_STYLE_FIELD_FRAME_PADDING_X = 34,   /* f32, left and right */
    VLQT_STYLE_FIELD_FRAME_PADDING_Y = 35,   /* f32, top and bottom */
    VLQT_STYLE_FIELD_FRAME_MIN_WIDTH = 36,   /* f32, lower bound of the implicit width (AUTO_SIZE) */
    VLQT_STYLE_FIELD_FRAME_MIN_HEIGHT = 37,  /* f32, lower bound of the implicit height (AUTO_SIZE) */
    VLQT_STYLE_FIELD_TEXT_FONT_FAMILY = 64,  /* string, "" = Qt default */
    VLQT_STYLE_FIELD_TEXT_FONT_SIZE = 65,    /* f32, pixel size */
    VLQT_STYLE_FIELD_TEXT_FONT_WEIGHT = 66,  /* i32, QFont::Weight 100..900 */
    VLQT_STYLE_FIELD_INDICATOR_SIZE = 96,    /* f32, edge length (Switch knob, CheckBox box, Slider handle) */
    VLQT_STYLE_FIELD_INDICATOR_RADIUS = 97,  /* f32 */
    VLQT_STYLE_FIELD_INDICATOR_BORDER_WIDTH = 98, /* f32 */
    VLQT_STYLE_FIELD_TRACK_THICKNESS = 128,  /* f32 */
    VLQT_STYLE_FIELD_TRACK_RADIUS = 129      /* f32 */
};

/* Event ids of the poll buffer (u16), with the payload after the feedbackId. Meaning: docs/contributing/protocol.md. */
enum vlqt_event {
    VLQT_EVENT_MEASURED = 1, /* 2 x f32 (w, h) */
    VLQT_EVENT_SCENE_BOUNDS = 2, /* 4 x f32 (x, y, w, h) */
    VLQT_EVENT_HOVER = 3, /* u8 0/1 */
    VLQT_EVENT_PRESS = 4, /* u8 0/1 */
    VLQT_EVENT_CLICK = 5, /* u32 count */
    VLQT_EVENT_POINTER = 6, /* 2 x f32 (x, y) */
    VLQT_EVENT_MEDIA = 7, /* 2 x f32 (position, duration) in seconds */
    VLQT_EVENT_WINDOW_GEOMETRY = 8, /* feedbackId 0; 28 bytes: f32 x, y, w, h, u32 flags (vlqt_window_state), f32 client x, y */
    VLQT_EVENT_ERROR = 9, /* UTF-8 error text of the element (no terminator), empty = cleared */
    VLQT_EVENT_TAP_COUNT = 10, /* u32 tapCount */
    VLQT_EVENT_LONG_PRESS = 11, /* u32 count */
    VLQT_EVENT_ACTIVE = 12, /* u8 0/1 */
    VLQT_EVENT_WHEEL = 13, /* 2 x f32 notches (angleDelta / 120) */
    VLQT_EVENT_DRAG = 14, /* 2 x f32 translation (parent space) */
    VLQT_EVENT_TOUCH_POINTS = 15, /* u32 count + count x (u32 id, f32 x, f32 y, f32 pressure, f32 w, f32 h) */
    VLQT_EVENT_PINCH = 16, /* 6 x f32 scale, rotation (degrees, clockwise), translation x, y, pivot x, y (parent space) */
    VLQT_EVENT_VALUE = 17, /* u16 valueType (I32, F32, BOOL or STRING), u16 0, value like SET */
    VLQT_EVENT_QML_PROPERTY = 18, /* u16 nameLen, u16 valueType, name, padding to 4, value like EVENT_VALUE */
    VLQT_EVENT_QML_SIGNAL = 19    /* u16 nameLen, u16 argCount, name, padding to 4, argCount x (u16 valueType, u16 0, value, padding to 4) */
};

/* Flags of EVENT_WINDOW_GEOMETRY (u32 bit mask) */
enum vlqt_window_state {
    VLQT_WINDOW_STATE_MINIMIZED = 1,
    VLQT_WINDOW_STATE_MAXIMIZED = 2,
    VLQT_WINDOW_STATE_FULLSCREEN = 4,
    VLQT_WINDOW_STATE_VISIBLE = 8
};

/* Window modes: u32 bit mask of vlqt_window_set_mode (vlqt.h) */
enum vlqt_window_mode {
    VLQT_WINDOW_MODE_BORDERLESS = 1,
    VLQT_WINDOW_MODE_FULLSCREEN = 2
};

/* Spaces of VLQT_OP_SET_SPACE, the order of VL.Qt.Space (= VL.Skia CommonSpace). */
enum vlqt_space {
    VLQT_SPACE_NORMALIZED = 0,    /* origin at the centre */
    VLQT_SPACE_DIP = 1,           /* origin at the centre */
    VLQT_SPACE_DIP_TOP_LEFT = 2,  /* origin at the top left (the default) */
    VLQT_SPACE_PIXEL_TOP_LEFT = 3 /* origin at the top left */
};

/* Values of PROP_FILL_MODE (i32), the order of VL.Qt.Elements.FillMode. Video does not take TILE. */
enum vlqt_fill_mode {
    VLQT_FILL_MODE_STRETCH = 0, /* fills the size, the aspect ratio is not kept */
    VLQT_FILL_MODE_FIT = 1,     /* keeps the aspect ratio, fits inside the size */
    VLQT_FILL_MODE_CROP = 2,    /* keeps the aspect ratio, fills the size, the overflow is cut off */
    VLQT_FILL_MODE_TILE = 3     /* repeats the image at its natural size (Image only) */
};

/* Pointer events of vlqt_offscreen_send_pointer (vlqt.h, QtTexture input), not part of the command buffer. */
enum vlqt_pointer {
    VLQT_POINTER_PRESS = 1,
    VLQT_POINTER_MOVE = 2,
    VLQT_POINTER_RELEASE = 3,
    VLQT_POINTER_LEAVE = 4,  /* the pointer left the texture or its window (hover ends); x/y ignored */
    VLQT_POINTER_CANCEL = 5  /* touch/pen: the system cancelled the point (TouchCancel) */
};

/* Pointing devices of vlqt_offscreen_send_pointer. Pen is delivered as a stylus, as in a window on Windows. */
enum vlqt_pointer_device {
    VLQT_POINTER_DEVICE_MOUSE = 0,
    VLQT_POINTER_DEVICE_TOUCH = 1,
    VLQT_POINTER_DEVICE_PEN = 2
};

/* Key event types of vlqt_offscreen_send_key. */
enum vlqt_key_event {
    VLQT_KEY_EVENT_PRESS = 1,
    VLQT_KEY_EVENT_RELEASE = 2
};

/* Resource kinds of vlqt_res_create (int); 1 is VLQT_RESOURCE_KIND_TEST of vlqt_test.h */
enum vlqt_resource_kind {
    VLQT_RESOURCE_KIND_IMAGE = 2, /* image from a file or pixels (vlqt_image_load / vlqt_image_set_pixels), texture on the process device */
    VLQT_RESOURCE_KIND_SNAPSHOT = 3, /* CPU copy of an element (vlqt_snapshot_start / vlqt_snapshot_read), no texture */
    VLQT_RESOURCE_KIND_SHADER = 4, /* .qsb shader of one stage (vlqt_shader_load), for ShaderEffect */
    VLQT_RESOURCE_KIND_AUDIO_TAP = 5 /* sound of a Video as float samples (vlqt_audio_tap_init / vlqt_audio_tap_read) */
};

/* Resource states (u8 of RESOURCE_EVENT_STATE, return value of vlqt_res_wait). Released is never sent. */
enum vlqt_resource_state {
    VLQT_RESOURCE_STATE_PENDING = 1,
    VLQT_RESOURCE_STATE_DECODING = 2,
    VLQT_RESOURCE_STATE_UPLOADING = 3,
    VLQT_RESOURCE_STATE_READY = 4,
    VLQT_RESOURCE_STATE_FAILED = 5,
    VLQT_RESOURCE_STATE_RELEASED = 6
};

/* Event ids of the resource status queue (u16), vlqt_res_poll */
enum vlqt_resource_event {
    VLQT_RESOURCE_EVENT_STATE = 1, /* u8 state (vlqt_resource_state) */
    VLQT_RESOURCE_EVENT_ERROR = 2, /* UTF-8 text, before the STATE Failed */
    VLQT_RESOURCE_EVENT_INFO = 3, /* u32 width, u32 height, u32 format, u64 uploaded bytes */
    VLQT_RESOURCE_EVENT_WARNING = 4 /* UTF-8 text, at most one per resource */
};

/* Shader stages of vlqt_shader_load (int) */
enum vlqt_shader_stage {
    VLQT_SHADER_STAGE_VERTEX = 1,
    VLQT_SHADER_STAGE_FRAGMENT = 2
};

/* Flags of vlqt_image_load */
enum vlqt_image_load {
    VLQT_IMAGE_LOAD_MIPMAPS = 1, /* generate mipmaps after the upload (+33 % VRAM) */
    VLQT_IMAGE_LOAD_PREVIEW = 2  /* the worker also keeps a preview <= 256 px (vlqt_image_read_preview, editor tooltips) */
};

/* Upload path of an image, the upload argument of vlqt_image_load and vlqt_image_set_pixels */
enum vlqt_image_upload {
    VLQT_IMAGE_UPLOAD_AUTO = 0,        /* a file takes the worker path, pixels the main-thread path */
    VLQT_IMAGE_UPLOAD_MAIN_THREAD = 1, /* stripes within the frame budget through an offscreen frame of the upload QRhi */
    VLQT_IMAGE_UPLOAD_WORKER = 2       /* textures created in a worker, pixels copied through a staging texture (D3D11) */
};

/* Pixel formats of vlqt_image_set_pixels are the VL PixelFormat numbers (VL.Core PixelFormat.cs, append-only). Which are taken
 * and how they are converted, and the snapshot and audio tap resources: docs/contributing/protocol.md. */

/* Flags of vlqt_audio_tap_state.flags (vlqt.h). */
enum vlqt_audio_tap_flag {
    VLQT_AUDIO_TAP_ATTACHED = 1, /* a Video element (player) writes into the tap */
    VLQT_AUDIO_TAP_PLAYING = 2   /* that player is in PlayingState (buffers are expected) */
};

/* Limits of vlqt_audio_tap_init. */
#define VLQT_AUDIO_TAP_MAX_CHANNELS 8
#define VLQT_AUDIO_TAP_MIN_SAMPLE_RATE 8000
#define VLQT_AUDIO_TAP_MAX_SAMPLE_RATE 384000

#endif /* VLQT_PROTOCOL_H */