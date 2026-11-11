/*
 * protocol.h - single source of truth for the VL.Qt command buffer protocol.
 *
 * Mirrored 1:1 in src/VL.Qt/Protocol.cs. The SmokeTest drift check parses this file
 * and compares every constant (name + value) with Protocol.cs, so keep the format simple:
 *   - only `#define VLQT_<NAME> <integer>` and `enum { VLQT_<GROUP>_<NAME> = <integer>, ... };`
 *   - one constant per line, explicit values, no expressions.
 * Naming rule for the mirror: VLQT_<GROUP>_<UPPER_SNAKE> <-> Protocol.<Group>.<PascalCase>
 * (e.g. VLQT_OP_CLEAR_COLOR <-> Protocol.Op.ClearColor); plain #defines map to Protocol.<PascalCase>.
 *
 * Wire format (little-endian, every command 4-byte aligned):
 *   header  u16 op, u16 payloadLen (payload bytes after the header, excluding alignment padding)
 *   CREATE      u32 id, u16 type
 *   DESTROY     u32 id                       (recursive; child ids are released too)
 *   REPARENT    u32 id, u32 parent, u32 index (parent 0 = window root)
 *   SET         u32 id, u16 prop, u16 valueType, value
 *   BIND        u32 id, u32 sinkHandle, u16 flags (sinkHandle 0 = unbind)
 *   CLEAR_COLOR color
 *   RESET       (no payload)                 destroys every object of the window (ids free again), like one DESTROY
 *                                            per top-level object; window root and clear color stay (#92)
 *   SET_STYLE   u32 id, u16 count, u16 flags (vlqt_style_set), then count entries, each 4-byte aligned:
 *               u16 field (vlqt_style_field), u8 state (vlqt_style_state), u8 valueType, value (like SET)
 *               Styles (#292/#293): the resolved style of a widget (Controls types 10..15 only, others -6). QtNative
 *               keeps a table field x state per element and exposes it to the VL.Qt base style through the attached
 *               object VlStyle (no inheritance to children: C# resolves). Without STYLE_SET_CLEAR the entries are
 *               laid over the table (diff), with it the table is emptied first (full send). An unset (field, state)
 *               falls back to (field, NORMAL), then to 0 / transparent / "" (= Qt default font family). Unknown field
 *               or state -6, value type not the field's type -7 (like SET). The base style values live only in C#.
 *   SET_UNIFORMS u32 id, u16 count, u16 flags (vlqt_uniform_set), then count entries, each 4-byte aligned:
 *               u16 valueType, u16 nameLen (1..VLQT_UNIFORM_NAME_MAX), name (UTF-8, no terminator), padding to 4,
 *               value (like SET), padding to 4. ShaderEffect only (#437, see "ShaderEffect" below).
 *   SET_EFFECT_CHAIN u32 id, u16 linkCount, u16 flags (0, reserved), then linkCount links in application order, each
 *               u16 kind (vlqt_effect_link), u16 count, u32 bodyLen, body (bodyLen bytes, 4-byte aligned). Every
 *               element type (#489, see "Effect chain" below for the bodies).
 * Values: f32, i32, bool (u8), color (4 x f32, straight alpha), rectf (4 x f32: x, y, w, h),
 *         mat4 (16 x f32, row-major like Stride), string (u32 len + UTF-8), ref (u32 id), u64,
 *         vec2/vec3/vec4 (2/3/4 x f32), ivec2 (2 x i32).
 * Ids are u32, assigned by the C# reconciler; 0 = window root.
 *
 * Input handlers (#319) are objects of their own in the same id space (types from 32, see vlqt_type):
 *   CREATE      reserves the id and remembers type + properties; the QML handler is created by the REPARENT
 *   REPARENT    handler, parent, index: attaches it to the element item parent (not the root 0, not a handler; index
 *               ignored); a second REPARENT of an attached handler is -8 (the host re-attaches by DESTROY + CREATE)
 *   SET         handler properties from block 768 (set before the REPARENT = applied on creation); element
 *               properties on a handler and handler properties on an element are -6
 *   BIND        handler, sinkHandle, 0: the handler's events go to sinkHandle (index 0); a new handle forgets the
 *               sent state, the current state is sent again (the old handle gets HOVER/PRESS/ACTIVE 0 if 1 was sent,
 *               WHEEL (0, 0) after motion); a running press of a Tap handler is cancelled for the new handle
 *   DESTROY     removes only the handler; DESTROY/RESET of an element removes its handlers too (ids free again)
 *   A Text item with at least one handler hit-tests against its painted text (TextMask as containmentMask).
 *
 * Effect chain (#489, plan effects/08; replaces the Effect layer of plan effects/01): the Effect pin of an element is a
 *   chain of links; SET_EFFECT_CHAIN sends the whole chain (always a full send, no diff) in application order: the
 *   first link applies first to the element (the start of the chain in vvvv, Previous == null), the last one (the link
 *   at the Effect pin) last, like Stride TextureFX. linkCount 0 = no chain: the layer is removed (no leak). Otherwise
 *   QtNative sets layer.enabled + one layer.effect on the element's own item; the links are an item tree inside that
 *   effect item (intermediate stages hidden, each its own layer = texture provider, the last draws). Size, Transform,
 *   implicit size, Row/Column layout and hit-test stay those of the item. Root 0 and input handlers -6, unknown id -4.
 *   Link header u16 kind, u16 count (entries of the body), u32 bodyLen (multiple of 4; the body ends exactly there).
 *   Unknown kind -7, lengths that do not add up -2.
 *   kind MULTI: one MultiEffect run (consecutive built-in links, folded in C# per run). Body: count entries, each
 *     u16 prop (EFFECT_* 833..855, else -6; 832 EFFECT_ENABLED is no entry), u16 valueType (the property's type, else
 *     -7), value like SET, padding to 4. Unset properties have Qt's defaults (all parts off, BLUR_MAX 32, SHADOW_BLUR
 *     1, SHADOW_SCALE 1, COLORIZATION_COLOR red, AUTO_PADDING on, MASK_THRESHOLD_MAX 1); a run is applied whole (a
 *     value missing in the next send is back at its default). Effect area of the run = its input (element or the
 *     previous link) plus the run's padding.
 *   kind SHADER: one filter shader link driven by QtNative's pass driver (passes, inputs, sizes:
 *     docs/effects-and-shaders.md). Body: u16 template (vlqt_effect_template), u16 shaderCount, u64
 *     shaderIds[shaderCount] (stage FRAGMENT resources; roles per template: SINGLE and TWO_PASS [frag], PYRAMID
 *     [prefilter, composite]; another count -7; pyramid_down/pyramid_up are loaded by QtNative as builtin:), 4 x f32
 *     padding (DIP: left, top, right, bottom, >= 0), f32 strength, u16 0 (reserved, else -7), u16 0,
 *     then count uniform entries exactly like SET_UNIFORMS (STRING -7, nameLen 0 / > max -7).
 *     strength: SINGLE / TWO_PASS unused (0); PYRAMID = radius in DIP
 *     (weights from radius and dpr; the step count follows the pixel size of the effect area only, so a radius change
 *     writes weights, a size / dpr change rebuilds the steps). PYRAMID gets the uniform entries in prefilter and
 *     composite alike, each takes the names it declares (a name neither declares is an EV_ERROR line); the composite's
 *     second input is glow or shadow (whichever it declares).
 *     passIndex: SINGLE / TWO_PASS write float passIndex into the shader's uniform block if it declares one: 0 in SINGLE and
 *     pass 1, 1 in pass 2 (a shader that needs a different value per pass derives it, e.g. the motion blurs divide their
 *     length by their sample cap 64 in pass 2).
 *     Set by QtNative, reserved for C# (an entry of that name is an EV_ERROR line): qt_Matrix, qt_Opacity, source,
 *     texelSize (1 / pixel size of the sampled texture), dpr, weight, elementRect (SINGLE / TWO_PASS: vec4 x, y, w, h of the
 *     ELEMENT's own rectangle in UV of the pass area, 0 = not set; a texture element that must sit on the element, not on
 *     the area grown by earlier links, maps itself with it: Blend layer, Displace map), passIndex, PYRAMID base, glow,
 *     shadow. Texture uniforms: REF = an element object of its own that the host never REPARENTs (like a mask, see
 *     below; QtNative parks it hidden and renders it as layer; unknown id -4), U64 = image resource id.
 *     Shader id 0 or not Ready yet: the link passes its input through unchanged; a Failed id keeps the last Ready
 *     shader of that link (like ShaderEffect, #483). Padding grows from link to link (each link's effect area = its
 *     input's plus its padding).
 *   Content problems never fail the apply (unknown uniform name, wrong type, shader not ready, reserved names,
 *   negative padding/strength): EV_ERROR text of the element, one line per problem prefixed with the link index
 *   ("link 2: ..."), empty = cleared; the shader errors themselves come by RES_EV_ERROR.
 *   Block 832 (EFFECT_* as element properties via SET) is no longer a way in: SET with any EFFECT_* property is -6
 *   (reserved; the ids stay as keys of MULTI entries).
 *   Qt Quick software backend (QT_QUICK_BACKEND=software): MultiEffect and the shader links are ShaderEffects, which
 *   that backend does not draw, so an element with a chain is not drawn at all there (layout and hit-test unchanged).
 *   Mask (per MULTI run): the mask element is an element object of its own (CREATE + its SETs + its children), but it
 *   is not REPARENTed by the host. A MULTI entry EFFECT_MASK_SOURCE ref <maskId> attaches it to that run (effect area
 *   = the run's); the rules below are unchanged from the single layer. QtNative puts its item into a
 *   hidden frame item under the window root that serves as texture source (frame visible false, layer.enabled true), so it never counts for the element's layout, auto size/childrenRect, hit-test or
 *   REPARENT indices. Position rule: the mask lies in the coordinates of the element's PARENT like a sibling (its SIZE,
 *   ANCHOR and TRANSFORM as for an element in that parent, see "Geometry" at vlqt_prop, unstretched): a mask covers
 *   exactly the pixels it would cover as a sibling. Moving the element (TRANSFORM, ANCHOR, size change with an anchor
 *   other than top left, drag offset, Row/Column layout position) leaves the mask window in place. Inside the frame,
 *   the mask sits in an item whose matrix is the inverse of the element's parent mapping (x/y + anchor placement +
 *   TRANSFORM around the anchor + drag offset), updated on change; the frame's layer.sourceRect is bound
 *   to the effect area (element plus padding: AUTO_PADDING grows it for blur/shadow, PADDING_RECT otherwise), so the
 *   texture covers exactly the effect area. A rotated/scaled element therefore samples the mask exactly as well (the
 *   texture is rendered in the element's own units: a scaled-up element gets a correspondingly coarser mask). Outside
 *   the mask's content alpha is 0 (element invisible there, INVERTED flips it); content of the mask's children outside
 *   its item rectangle counts. ref 0 detaches it (it stays parked and hidden until a REPARENT, which shows it again, or its
 *   DESTROY). DESTROY of an attached mask detaches it first; DESTROY/RESET of the element destroys its attached mask
 *   too (its ids are free). A used mask item is deleted one frame later (#123). REPARENT of an attached mask, and
 *   attaching the element itself, an ancestor, an element with a parent or one attached elsewhere, is -8.
 * Poll buffer (QtNative -> C#), per record, 4-byte aligned: u16 event, u16 len, u32 handle, u32 index, payload;
 *   len = bytes of handle + index + payload (without padding). handle = BIND sinkHandle; index = offset to the
 *   slot index of that binding (0 = the bound slot itself; QtNative always sends 0, see events.h).
 *   MEASURED 2 x f32 (w, h), SCENE_BOUNDS 4 x f32 (x, y, w, h), logical pixels; HOVER/PRESS u8 (0/1),
 *   CLICK u32 (Tap handler: taps since the last poll; Button: clicked signals), POINTER 2 x f32 (x, y) item-local logical pixels,
 *   MEDIA 2 x f32 (position, duration) in seconds (Video only, 0 until loaded); see events.h.
 *   Hover handler: HOVER u8 on change. Tap handler (#320): PRESS u8, CLICK u32, TAP_COUNT u32 on change (never 0),
 *   LONG_PRESS u32 (count since the last poll). Point handler: ACTIVE u8 on change, POINTER while active (with
 *   ACCEPTED_BUTTONS 0 the mouse is active while hovering, touch/pen while down). Wheel handler: WHEEL 2 x f32
 *   (notches since the last poll, only != 0, once (0, 0) after motion). Drag handler (#209): ACTIVE u8 on change, DRAG
 *   2 x f32 on change (translation of the running drag); moves the item by its drag offset, SET TRANSFORM resets it to 0
 *   (pin wins: every TRANSFORM, also a resync, puts the item back where the pin says; SIZE/ANCHOR keep the offset).
 *   Touch Points handler (#321): EV_TOUCH_POINTS on change (all points pressed in the item, item-local; count 0 when the
 *   last one goes; at most 64 points). Pinch handler (#321): EV_ACTIVE u8 on change, EV_PINCH 4 x f32 while active, the
 *   last one before EV_ACTIVE 0 (never the 1/0/0 Qt resets to).
 *   Value inputs (SpinBox #210 i32; CheckBox/Switch bool, Slider f32, TextField string #301): EV_VALUE = u16 valueType, u16 0,
 *   value encoded like SET (i32, f32, bool u8, string u32 len + UTF-8; string capped at 60000 bytes on a character
 *   boundary). Sent for every bound value input, independent of input handlers, only after a user change (SpinBox:
 *   valueModified = +/-, arrow keys, wheel at once, typed text on Enter or focus loss; CheckBox/Switch: toggled = click,
 *   Space; Slider: moved = press/drag (every step), arrow keys, wheel; TextField: Enter or focus loss, only if the text
 *   differs from the last set or committed one), never for SET VALUE/MINIMUM/
 *   MAXIMUM (no echo, also not when Qt clamps the value). The payload is the value the user produced, captured when the
 *   signal fires, not the current value: a SET VALUE applied between the user change and the poll does not hide it.
 *   Several user changes between two polls: one EV_VALUE with the last one. A new sink handle drops a change not polled.
 *
 * Resources (#359, resources.h): window independent, own status queue vlqt_res_poll (vlqt.h). Ids are u64 assigned by
 *   QtNative: index (low 32 bits) | generation (high 32 bits). Record per status, 4-byte aligned: u16 event, u16 len,
 *   u64 id, payload; len = 8 + payload bytes (without padding).
 *   RES_EV_STATE u8 state (vlqt_res_state), RES_EV_ERROR UTF-8 text (Failed, sent before its STATE), RES_EV_INFO
 *   u32 width, u32 height, u32 format, u64 uploaded bytes (sent before STATE Ready, and after each replacement),
 *   RES_EV_WARNING UTF-8 text (at most one per resource). No record after vlqt_res_release.
 *   Replacement: new content on a Ready id keeps it Ready (old texture visible) until the new upload is complete.
 *
 * ShaderEffect (#437): own QQuickItem with QSGMaterialShader in QtNative
 *   (no QML ShaderEffect); uniforms are mapped by name through the reflection of the shaders (QShaderDescription:
 *   members of the uniform block, std140 offsets, sampler2D bindings). Phase 1: pre-baked .qsb; Phase 2 (#483): GLSL
 *   source baked at runtime (vlqt_res_load_shader_source, see below).
 *   Shaders are resources (VLQT_RES_KIND_SHADER, vlqt_res_load_shader in vlqt.h): window independent, loaded in a
 *   worker (never in the frame), ids with generation like images, element references via acquire/releaseElement.
 *   Path "builtin:<name>" = a shader shipped with VL.Qt: <QtNative dir>/shaders/<name>.frag.qsb (stage FRAGMENT) or
 *   <name>.vert.qsb (stage VERTEX); in the package qt/<rid>/shaders/, in the app runtimes/<rid>/native/shaders/
 *   (next to qml/). Sources live in src/VL.Qt.Native/shaders/<name>.frag|.vert, the build bakes them with qsb
 *   (build time only, qsb is not shipped). Anything else is a file path. Not a .qsb (e.g. GLSL text), stage of the
 *   .qsb not the requested one, no variant for the backend of the process device, missing file: Failed with
 *   RES_EV_ERROR. RES_EV_INFO of a shader is all 0 (no upload; each item builds its pipeline itself).
 *   Source (#483, vlqt_res_load_shader_source): full Qt GLSL (Vulkan-style GLSL 440 as qsb takes it, no preamble
 *   added), copied at the call, baked by QShaderBaker (Qt6::ShaderTools) in the worker, only for the shader language
 *   of the process backend (plus the reflection), never in the frame. Bakes are cached per (stage, backend, hash of the
 *   source) in the process, so going back to an earlier text is Ready without a bake. The same checks as for a .qsb
 *   follow the bake. A bake error ends in Failed with RES_EV_ERROR = the compiler message (glslang, with line numbers
 *   of the source), one line per problem.
 *   Properties (block 896): FRAGMENT_SHADER u64 shader id (0 = none: the element draws nothing), VERTEX_SHADER u64
 *   (0 = Qt's default vertex shader: qt_Vertex loc 0, qt_MultiTexCoord0 loc 1 -> qt_TexCoord0), a new id keeps the
 *   old shader until it is Ready (like IMAGE); if the new id fails, the last Ready shader of that slot keeps drawing
 *   (last good version, #483: live editing never goes black), a Failed shader without an earlier Ready one draws
 *   nothing; BLENDING bool (default true,
 *   premultiplied: One, OneMinusSrcAlpha); MESH_RESOLUTION ivec2 (grid subdivisions x, y >= 1, default 1, 1; < 1 ->
 *   -7). Shared: SOURCE ref = Input (the element bound to the sampler2D named "source", 0 = none: generative),
 *   AUTO_SIZE (default on: implicit size = size of the Input; without Input the SIZE counts anyway).
 *   Input and the elements of texture uniforms are children of the ShaderEffect (C#: Input at index 0, then the
 *   texture elements in uniform order, each once), hidden by QtNative on REPARENT; QtNative
 *   renders them as layers (texture providers). A Group (ITEM) is sampled as its whole frame instead (also the part
 *   left of / above its origin, "Group frame" below) through one hidden ShaderEffectSource per Group, shared by Input
 *   and texture uniforms; with Input = Group the implicit size is the frame size and follows it live (#486).
 *   Uniform block: like Qt's ShaderEffect, std140, identical in vertex and fragment shader, starting with mat4
 *   qt_Matrix (offset 0) and float qt_Opacity (offset 64), both set by QtNative; own members follow.
 *   SET_UNIFORMS: entries without UNIFORM_SET_CLEAR are laid over the element's table by name (diff), with it the
 *   table is emptied first (full send). Values stay by name across shader changes. A member without value is 0, a
 *   sampler without texture samples transparent. Matching value types (exact, no conversion): float F32, int I32,
 *   bool BOOL, vec2 VEC2, vec3 VEC3, vec4 VEC4 or COLOR (premultiplied by QtNative, like QML), ivec2 IVEC2, mat4
 *   MAT4 (row-major on the wire, transposed by QtNative), sampler2D REF (element id, a child, see above) or U64
 *   (image resource id). Apply errors only for malformed entries: bad lengths -2 (ApplyBadLength), unknown value
 *   type or nameLen 0 / > max -7, unknown ref id -4, not a ShaderEffect -6. Content problems never fail the apply:
 *   unknown name, wrong type, arrays, the reserved names qt_Matrix / qt_Opacity / source, uniform blocks of vertex
 *   and fragment shader differing go as EV_ERROR text of the element (one line per problem, empty = cleared), which
 *   the C# node shows as warning and at its Error output; the shader errors themselves come by RES_EV_ERROR.
 * *
 * Sampler (#492): how a sampler2D reads its texture. Entry of SET_UNIFORMS and of a SHADER link
 *   uniform table like a value: type SAMPLER (20), name = the sampler2D it belongs to ("source" = the Input of a
 *   ShaderEffect is allowed here, unlike for a texture), value 8 bytes (little endian u64): byte 0 filter (0 Linear,
 *   1 Nearest: min and mag), byte 1 mip filter (0 None, 1 Nearest, 2 Linear), byte 2 address U, byte 3 address V
 *   (0 Clamp = ClampToEdge, 1 Repeat, 2 Mirror = MirroredRepeat), bytes 4..7
 *   reserved = 0 (no anisotropy: QSGTexture does not support it; no layer format: a float layer stays empty under the 4x
 *   MSAA of the windows).
 *   Any other value -> apply error -7. The entry lives in
 *   its own table next to the uniforms (same name as the texture entry is the rule); no entry = the texture as it is
 *   (the Qt default of its source), removing the entry needs UNIFORM_SET_CLEAR like removing a uniform. QtNative wraps
 *   the sampled QSGTexture in a view with this state (the texture itself, which other items may share, is never changed).
 *   A mip filter other than None also acts on the element the sampler reads (layer.mipmap; a Group through its frame
 *   proxy): its layer is rendered with mipmaps; several samplers on one element: mipmaps if any asks, back to none when
 *   nobody does. An image (U64) keeps what its load made (VLQT_IMG_MIPMAPS): the mip filter needs mipmaps there. Without
 *   the entry nothing changes at the element.
 *
 * Pure C header: compiles as C and C++.
 */
#ifndef VLQT_PROTOCOL_H
#define VLQT_PROTOCOL_H

#define VLQT_PROTOCOL_VERSION 12
#define VLQT_COMMAND_ALIGN 4
#define VLQT_HEADER_SIZE 4
#define VLQT_ROOT_ID 0
#define VLQT_UNIFORM_NAME_MAX 255 /* max UTF-8 bytes of a SET_UNIFORMS name (#437) */

/* Opcodes (u16) */
enum vlqt_op {
    VLQT_OP_CREATE = 1,
    VLQT_OP_DESTROY = 2,
    VLQT_OP_REPARENT = 3,
    VLQT_OP_SET = 4,
    VLQT_OP_BIND = 5,
    VLQT_OP_CLEAR_COLOR = 6,
    VLQT_OP_RESET = 7,
    VLQT_OP_SET_STYLE = 8, /* resolved widget style, see the head comment (#292) */
    /* 16.. effects package 03 (#437), own block to keep merges with other packages simple */
    VLQT_OP_SET_UNIFORMS = 16, /* ShaderEffect uniforms by name, see the head comment */
    VLQT_OP_SET_EFFECT_CHAIN = 17 /* Effect chain of an element (#489), see the head comment "Effect chain" */
};

/* Value types of SET (u16) */
enum vlqt_value_type {
    VLQT_VT_F32 = 1,
    VLQT_VT_I32 = 2,
    VLQT_VT_BOOL = 3,
    VLQT_VT_COLOR = 4,
    VLQT_VT_RECTF = 5,
    VLQT_VT_MAT4 = 6,
    VLQT_VT_STRING = 7,
    VLQT_VT_REF = 8,
    VLQT_VT_U64 = 9, /* u64, e.g. a resource id (IMAGE, #361) */
    /* 16.. effects package 03 (#437): uniform values (SET_UNIFORMS) and MESH_RESOLUTION */
    VLQT_VT_VEC2 = 16,  /* 2 x f32 */
    VLQT_VT_VEC3 = 17,  /* 3 x f32 */
    VLQT_VT_VEC4 = 18,  /* 4 x f32 (unlike COLOR not premultiplied by QtNative) */
    VLQT_VT_IVEC2 = 19, /* 2 x i32 */
    VLQT_VT_SAMPLER = 20 /* 8 bytes: sampler state of a sampler2D (#492), see the head comment "Sampler" */
};

/* Element type ids of CREATE (u16). 2 was TEST_RECT (removed in 07a), 8 MULTI_EFFECT and 16 RECTANGULAR_SHADOW
 * (removed in #459, replaced by the Effect layer), keep them unused. */
enum vlqt_type {
    VLQT_TYPE_ITEM = 1,
    VLQT_TYPE_RECTANGLE = 3,
    VLQT_TYPE_TEXT = 4,
    VLQT_TYPE_IMAGE = 5,
    VLQT_TYPE_ROW = 6,
    VLQT_TYPE_COLUMN = 7,
    VLQT_TYPE_VIDEO = 9,
    VLQT_TYPE_BUTTON = 10, /* QtQuick.Controls Button (style Basic), #213 */
    VLQT_TYPE_SPIN_BOX = 11, /* QtQuick.Controls SpinBox (style Basic): Input (Int), EV_VALUE i32 (#210) */
    VLQT_TYPE_CHECK_BOX = 12, /* QtQuick.Controls CheckBox: Toggle look Checkbox, EV_VALUE bool (#301) */
    VLQT_TYPE_SWITCH = 13,    /* QtQuick.Controls Switch: Toggle look Switch, EV_VALUE bool (#301) */
    VLQT_TYPE_SLIDER = 14,    /* QtQuick.Controls Slider: EV_VALUE f32 (#301) */
    VLQT_TYPE_TEXT_FIELD = 15, /* QtQuick.Controls TextField (single line): EV_VALUE string (#301) */
    VLQT_TYPE_SHADER_EFFECT = 24, /* own item with .qsb shaders and uniforms by name (#437), see the head comment */
    /* Input handlers from 32 (#319): protocol objects attached to an element by REPARENT, see the head comment. */
    VLQT_TYPE_HOVER_HANDLER = 32, /* Qt Quick HoverHandler: EV_HOVER (positions: Point handler, #320) */
    VLQT_TYPE_TAP_HANDLER = 33,   /* TapHandler (ReleaseWithinBounds): EV_PRESS, EV_CLICK, EV_TAP_COUNT, EV_LONG_PRESS (#320) */
    VLQT_TYPE_POINT_HANDLER = 34, /* PointHandler: EV_ACTIVE, EV_POINTER while active; buttons 0: mouse hover (#320) */
    VLQT_TYPE_WHEEL_HANDLER = 35, /* WheelHandler (target null): EV_WHEEL (#320) */
    VLQT_TYPE_TOUCH_POINTS_HANDLER = 36, /* own handler (passive grabs): EV_TOUCH_POINTS, all points pressed in the item (#321) */
    VLQT_TYPE_PINCH_HANDLER = 37, /* PinchHandler (target null): EV_ACTIVE, EV_PINCH; moves nothing (#321) */
    VLQT_TYPE_DRAG_HANDLER = 38   /* DragHandler (target null): EV_ACTIVE, EV_DRAG; moves the item by its drag offset (#209) */
};

/* Property ids of SET (u16). 1..255 common to all items; type-specific ids start at 256. QtNative looks them up
 * per type, so one id may serve several types (COLOR: Rectangle fill + Text; SOURCE: Video string, ShaderEffect ref).
 * Geometry (#469, like VL.Skia; replaces BOUNDS + PIVOT): there is no position property, position comes from TRANSFORM.
 *   SIZE vec2 (w, h, logical pixels): the item's width/height (Rectangle/Text/Image/...: always, 0 = not drawn, not
 *   clickable; containers, Button, value inputs, ShaderEffect with Input: only with AUTO_SIZE off, otherwise implicit).
 *   ANCHOR i32 Qt::Alignment (one of AlignLeft/Right/HCenter | one of AlignTop/Bottom/VCenter, default AlignLeft|AlignTop = 0x21):
 *   the point of the item that lies at the origin of its parent, resolved against the item's ACTUAL width/height
 *   (implicit size with AUTO_SIZE, follows every size change). TRANSFORM (mat4) is applied around that point:
 *   a point v in item coordinates lands at (v - anchor) * M in parent coordinates (row vectors, like Skia/Stride).
 *   Identity + AlignLeft|AlignTop: top left corner at the parent origin; AlignCenter: the item's center there.
 *   Children of a Row/Column (positioner): the layout owns x/y, ANCHOR is only the pivot of TRANSFORM
 *   (v -> layoutPos + anchor + (v - anchor) * M), the item is not moved out of its cell.
 *   Drag offset (#209) comes last, in parent space (see the Drag handler at the poll buffer).
 *   Group frame (ITEM, #469; C# default anchor TopLeft = a pure transform container like the Skia group): with
 *   AUTO_SIZE on the frame is the bounding box of the group origin and of all visible children with a non-empty size,
 *   each mapped with its x/y AND TRANSFORM (not Qt's childrenRect). Children left of / above the origin make the frame
 *   start in negative group coordinates. The frame is the group's size (= MEASURED), hit area (free area for handlers
 *   of the group), clip rect, SCENE_BOUNDS and debug overlay frame. ANCHOR per axis: Left/Top = the group origin
 *   (never shifted by the content), HCenter/VCenter/Right/Bottom = that point of the frame. Content at non-negative
 *   coordinates gives frame (0, 0, right, bottom), i.e. the plain f * size of every other element. AUTO_SIZE off (#475):
 *   frame = SIZE placed by ANCHOR around the group origin, (-fx * w, -fy * h, w, h); the group origin (and so the
 *   children) does not move, TRANSFORM pivots around the origin (= the anchor point of the frame). Recomputed once per frame (polish) after a child changed position, size, visibility or TRANSFORM.
 *   A snapshot (vlqt_res_snapshot) of a Group is the frame, also its part in negative coordinates; an effect layer on
 *   a Group covers the whole frame (that part is added to the left/top padding of the MultiEffect) (#475), so does a
 *   Group as ShaderEffect Input or texture uniform (#486). A child counts by its own visibility, also inside a hidden
 *   Group (e.g. the Input of a ShaderEffect).
 * Blocks: 256.. Rectangle, 512.. Text, 544.. Image, 576.. Row/Column, 608.. reserved (MultiEffect element, removed in #459), 640.. size mode of containers (Item/Group, Row, Column, ShaderEffect only, others -> -6),
 * 672.. Video (SOURCE string and FILL_MODE are shared with Image; Image itself has no SOURCE since #361, -6), 704.. Controls: Button (TEXT and AUTO_SIZE are shared: AUTO_SIZE on =
 * implicit size from the text, default on), from 706 value inputs (SpinBox, CheckBox, Switch, Slider, TextField; VALUE/MINIMUM/MAXIMUM/STEP take the value type of the element), 768.. input handlers (#319, only on handler objects, others -> -6), 800.. reserved (RectangularShadow, removed in #459),
 * 832.. Effect chain MULTI keys (not via SET, see the head comment "Effect chain"), 896.. ShaderEffect (#437). */
enum vlqt_prop {
    VLQT_PROP_SIZE = 1,       /* vec2 (w, h), see "Geometry" above (#469; was BOUNDS rectf) */
    VLQT_PROP_TRANSFORM = 2,
    VLQT_PROP_OPACITY = 3,
    /* 4 free: was VISIBLE, removed in #474 (Opacity 0 = not drawn, input stays; visibility is QtNative's) */
    VLQT_PROP_CLIP = 5,
    VLQT_PROP_ANCHOR = 6,     /* i32 Qt::Alignment, see "Geometry" above (#469; was PIVOT) */
    VLQT_PROP_DEBUG = 7, /* bool: draw this element in the window's debug overlay (not recursive, #279) */
    VLQT_PROP_COLOR = 256,
    VLQT_PROP_RADIUS = 257,
    VLQT_PROP_BORDER_COLOR = 258,
    VLQT_PROP_BORDER_WIDTH = 259,
    VLQT_PROP_TEXT = 512,
    VLQT_PROP_FONT_FAMILY = 513,
    VLQT_PROP_FONT_SIZE = 514,
    VLQT_PROP_FONT_WEIGHT = 515,
    VLQT_PROP_FONT_ITALIC = 516,
    VLQT_PROP_HORIZONTAL_ALIGNMENT = 517,
    VLQT_PROP_VERTICAL_ALIGNMENT = 518,
    VLQT_PROP_WRAP_MODE = 519,
    VLQT_PROP_ELIDE = 520,
    VLQT_PROP_LINE_HEIGHT = 521,
    VLQT_PROP_LETTER_SPACING = 522,
    VLQT_PROP_FONT_SIZE_MODE = 523,
    VLQT_PROP_SOURCE = 544,
    VLQT_PROP_FILL_MODE = 545,
    VLQT_PROP_SMOOTH = 546,
    VLQT_PROP_IMAGE = 547, /* u64 image resource id (vlqt_res_*), 0 = no image; the old one stays until the new one is Ready (#361) */
    VLQT_PROP_SPACING = 576,
    VLQT_PROP_PADDING = 577,
    VLQT_PROP_CHILD_ALIGNMENT = 578,
    VLQT_PROP_AUTO_SIZE = 640,
    VLQT_PROP_PLAYING = 672,
    VLQT_PROP_LOOP = 673,
    VLQT_PROP_PLAYBACK_RATE = 674,
    VLQT_PROP_VOLUME = 675,
    VLQT_PROP_KEEP_LAST_FRAME = 676,
    VLQT_PROP_ENABLED = 704,
    VLQT_PROP_AUTO_REPEAT = 705,
    VLQT_PROP_VALUE = 706,   /* value of a value input (SpinBox i32, CheckBox/Switch bool, Slider f32, TextField string), no EV_VALUE echo */
    VLQT_PROP_MINIMUM = 707, /* SpinBox i32 (default INT32_MIN; Qt's 0 is overridden), Slider f32 (default 0) */
    VLQT_PROP_MAXIMUM = 708, /* SpinBox i32 (default INT32_MAX; Qt's 99 is overridden), Slider f32 (default 1) */
    VLQT_PROP_STEP = 709,    /* SpinBox i32 stepSize of +/-, arrow keys and wheel (default 1), Slider f32 (default 0 = continuous, > 0 also snaps the drag) */
    VLQT_PROP_EDITABLE = 710, /* bool, SpinBox: the value can be typed (default true); TextField: not readOnly (default true) */
    VLQT_PROP_ORIENTATION = 711, /* i32 Qt::Orientation, Slider: 1 horizontal (default), 2 vertical; others -> ApplyBadValue */
    VLQT_PROP_PLACEHOLDER = 712, /* string, TextField placeholderText shown while empty (default empty) */
    /* 768 (REPORT_POSITION, Hover handler position) is reserved (#320): positions come from the Point handler only. */
    VLQT_PROP_ACCEPTED_BUTTONS = 769, /* i32 Qt::MouseButtons, Tap + Point + Drag handler (default Qt::LeftButton; Point 0: hover) */
    VLQT_PROP_ACCEPTED_DEVICES = 770, /* i32 QInputDevice::DeviceTypes, Tap + Point + Wheel + Drag + Touch Points + Pinch handler (default: Qt's = all) */
    VLQT_PROP_MOVES_ELEMENT = 771,    /* bool, Drag handler: the drag moves the item by its drag offset (default true; false: report only) */
    VLQT_PROP_DRAG_AXIS = 772,        /* i32, Drag handler: 0 both axes (default), 1 x only, 2 y only */
    VLQT_PROP_DRAG_LIMITS = 773,      /* rectf, Drag handler: minX, minY, maxX, maxY of the drag offset (logical pixels, +-inf = none) */
    VLQT_PROP_MINIMUM_POINT_COUNT = 774, /* i32 >= 2, Pinch handler (default 2) */
    VLQT_PROP_MAXIMUM_POINT_COUNT = 775, /* i32 >= 2, Pinch handler (default 2); more points: the gesture pauses (#321) */
    VLQT_PROP_OCCLUSION = 776,        /* bool, Touch Points handler: ignore presses an element on top grabs (default true, #321) */
    /* 832.. keys of MULTI entries of SET_EFFECT_CHAIN (#489), values of one MultiEffect run, Qt defaults. Not
     * settable by SET any more (-6, see the head comment "Effect chain"). */
    VLQT_PROP_EFFECT_ENABLED = 832,            /* reserved (was: layer on/off of the single Effect layer, plan effects/01) */
    VLQT_PROP_EFFECT_BLUR_ENABLED = 833,       /* bool (default false) */
    VLQT_PROP_EFFECT_BLUR = 834,               /* f32 0..1 */
    VLQT_PROP_EFFECT_BLUR_MAX = 835,           /* i32 pixels (default 32), switches shaders */
    VLQT_PROP_EFFECT_BLUR_MULTIPLIER = 836,    /* f32 (default 0) */
    VLQT_PROP_EFFECT_SHADOW_ENABLED = 837,     /* bool (default false) */
    VLQT_PROP_EFFECT_SHADOW_COLOR = 838,       /* color (default black) */
    VLQT_PROP_EFFECT_SHADOW_HORIZONTAL_OFFSET = 839, /* f32 logical pixels */
    VLQT_PROP_EFFECT_SHADOW_VERTICAL_OFFSET = 840,   /* f32 logical pixels */
    VLQT_PROP_EFFECT_SHADOW_BLUR = 841,        /* f32 0..1 (default 1) */
    VLQT_PROP_EFFECT_SHADOW_SCALE = 842,       /* f32 (default 1) */
    VLQT_PROP_EFFECT_BRIGHTNESS = 843,         /* f32 -1..1 */
    VLQT_PROP_EFFECT_CONTRAST = 844,           /* f32 -1..1 */
    VLQT_PROP_EFFECT_SATURATION = 845,         /* f32 -1..1 */
    VLQT_PROP_EFFECT_COLORIZATION = 846,       /* f32 0..1 */
    VLQT_PROP_EFFECT_COLORIZATION_COLOR = 847, /* color (default red) */
    VLQT_PROP_EFFECT_AUTO_PADDING = 848,       /* bool autoPaddingEnabled (default true) */
    VLQT_PROP_EFFECT_PADDING_RECT = 849,       /* rectf paddingRect in logical pixels (x left, y top, w right, h bottom), with AUTO_PADDING off */
    VLQT_PROP_EFFECT_MASK_SOURCE = 850,        /* ref: mask element object, 0 = no mask (maskEnabled = ref != 0), see the head comment */
    VLQT_PROP_EFFECT_MASK_THRESHOLD_MIN = 851, /* f32 0..1 (default 0) */
    VLQT_PROP_EFFECT_MASK_SPREAD_AT_MIN = 852, /* f32 0..1 (default 0) */
    VLQT_PROP_EFFECT_MASK_THRESHOLD_MAX = 853, /* f32 0..1 (default 1) */
    VLQT_PROP_EFFECT_MASK_SPREAD_AT_MAX = 854, /* f32 0..1 (default 0) */
    VLQT_PROP_EFFECT_MASK_INVERTED = 855,      /* bool (default false) */
    /* 896.. ShaderEffect (#437), see the head comment; it also takes SOURCE (ref) and AUTO_SIZE */
    VLQT_PROP_FRAGMENT_SHADER = 896,  /* u64 shader resource id (stage FRAGMENT), 0 = none: draws nothing */
    VLQT_PROP_VERTEX_SHADER = 897,    /* u64 shader resource id (stage VERTEX), 0 = Qt's default vertex shader */
    VLQT_PROP_BLENDING = 898,         /* bool, premultiplied alpha blending (default true) */
    VLQT_PROP_MESH_RESOLUTION = 899   /* ivec2, grid subdivisions x, y >= 1 (default 1, 1) */
};

/* BIND flags (u16, bit mask). Elements have no pointer handlers since #320: 1 (INTERACTIVE) and 4 (POINTER) of #208
 * are reserved (#319), keep them unused. 2 (CANCEL_PRESS) is reserved as well: no effect any more (a new sink of a Tap
 * handler cancels its press itself), the C# reconciler does not send it. */
enum vlqt_bind {
    VLQT_BIND_CANCEL_PRESS = 2 /* reserved (#320) */
};

/* SET_UNIFORMS flags (u16, bit mask), #437 */
enum vlqt_uniform_set {
    VLQT_UNIFORM_SET_CLEAR = 1 /* empty the element's uniform table before applying the entries (full send) */
};

/* Link kinds of SET_EFFECT_CHAIN (u16), #489 */
enum vlqt_effect_link {
    VLQT_EFFECT_LINK_MULTI = 1, /* one MultiEffect run: EFFECT_* entries */
    VLQT_EFFECT_LINK_SHADER = 2 /* filter shader link: template, shader ids, padding, strength (PYRAMID radius), uniforms */
};

/* Pass templates of a SHADER link (u16), #489, passes: docs/effects-and-shaders.md */
enum vlqt_effect_template {
    VLQT_EFFECT_TEMPLATE_SINGLE = 1,   /* one pass of the shader over the input */
    VLQT_EFFECT_TEMPLATE_TWO_PASS = 2, /* the shader twice: passIndex 0, then 1 (motion blurs) */
    VLQT_EFFECT_TEMPLATE_PYRAMID = 3   /* prefilter, pyramid_down/up stages, composite (Glow, InnerShadow) */
};

/* SET_STYLE flags (u16, bit mask), #292 */
enum vlqt_style_set {
    VLQT_STYLE_SET_CLEAR = 1 /* empty the element's style table before applying the entries (full send) */
};

/* Style states (u8 of a SET_STYLE entry), #292. Every field exists per state. When several apply, the base style
 * picks one by rank: DISABLED > PRESSED > HOVERED > CHECKED > NORMAL (see docs/styles.md). */
enum vlqt_style_state {
    VLQT_STYLE_STATE_NORMAL = 0,
    VLQT_STYLE_STATE_HOVERED = 1,
    VLQT_STYLE_STATE_PRESSED = 2,
    VLQT_STYLE_STATE_DISABLED = 3,
    VLQT_STYLE_STATE_CHECKED = 4
};

/* Style fields (u16 of a SET_STYLE entry), #292. Grouped by part like the Style nodeset (S5, #294): colors 1..,
 * Frame 32.., Text 64.., Indicator 96.., Track 128... Value type per field is fixed (comment). Lengths in logical
 * pixels. Frame = background of the control (Button face, TextField/SpinBox box, the CheckBox/Switch/Slider padding
 * area); Indicator = Slider handle, CheckBox check mark, Switch knob, SpinBox up/down buttons; Track = Slider groove,
 * Switch track and CheckBox box, TRACK_FILL the filled part (Slider value, Switch on) and the TextField selection. */
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

/* Event ids of the poll buffer (u16). Payloads: events.h (plan 06). */
enum vlqt_ev {
    VLQT_EV_MEASURED = 1,
    VLQT_EV_SCENE_BOUNDS = 2,
    VLQT_EV_HOVER = 3,
    VLQT_EV_PRESS = 4,
    VLQT_EV_CLICK = 5,
    VLQT_EV_POINTER = 6,
    VLQT_EV_MEDIA = 7,
    VLQT_EV_WINDOW_GEOMETRY = 8, /* handle 0: the window itself, not a sink */
    VLQT_EV_ERROR = 9, /* UTF-8 error text of the element, empty = cleared (#273) */
    VLQT_EV_TAP_COUNT = 10, /* u32 tapCount of the last tap, on change (2 = double click), Tap handler (#320) */
    VLQT_EV_LONG_PRESS = 11, /* u32 long presses since the last poll, Tap handler (#320) */
    VLQT_EV_ACTIVE = 12, /* u8 0/1 on change, Point handler (#320) */
    VLQT_EV_WHEEL = 13, /* 2 x f32 notches (angleDelta / 120) since the last poll, once (0, 0) after motion (#320) */
    VLQT_EV_DRAG = 14, /* 2 x f32 translation of the running drag since the press (parent space, after limits), on change (#209) */
    VLQT_EV_TOUCH_POINTS = 15, /* u32 count + count x (u32 id, f32 x, f32 y, f32 pressure, f32 w, f32 h), on change (#321) */
    VLQT_EV_PINCH = 16, /* 4 x f32 scale, rotation (degrees, clockwise), translation x, y (parent space) while active (#321) */
    VLQT_EV_VALUE = 17 /* u16 valueType (VLQT_VT_I32/F32/BOOL/STRING), u16 0, value: user change of a value input (#210) */
};

/* Flags of EV_WINDOW_GEOMETRY (u32 bit mask) */
enum vlqt_window_state {
    VLQT_WINDOW_MINIMIZED = 1,
    VLQT_WINDOW_MAXIMIZED = 2,
    VLQT_WINDOW_FULLSCREEN = 4,
    VLQT_WINDOW_VISIBLE = 8
};

/* Window modes: u32 bit mask of vlqt_window_set_mode (vlqt.h), Renderer [Qt] pins Borderless (#233), Fullscreen (#234) */
enum vlqt_window_mode {
    VLQT_WINDOW_MODE_BORDERLESS = 1,
    VLQT_WINDOW_MODE_FULLSCREEN = 2
};

/* Modes of the TEST export vlqt_test_device_lost (vlqt.h, #367).
 * REMOVE: real removal of the shared device (ID3D12Device5::RemoveDevice of the D3D12 device under it). Needs the
 *         shared device created through D3D11On12, i.e. VLQT_TEST_D3D11ON12=1 set before the first window/resource;
 *         deterministic, no admin, works on WARP (CI). Qt then sees the loss itself at the next Present.
 * HANG:   real loss by the driver: an endless shader on the shared device, Windows resets the GPU after ~2 s (TDR).
 *         Affects every application on that GPU, manual checks only. */
enum vlqt_device_lost {
    VLQT_DEVICE_LOST_REMOVE = 1,
    VLQT_DEVICE_LOST_HANG = 2
};

/* Mouse event types of the TEST export vlqt_test_send_mouse (vlqt.h), not part of the command buffer. PRESS/RELEASE =
 * left button, *_RIGHT = right button (#320), *_RIGHT_SYSTEM = right button synthesized by the OS from touch (#321). */
enum vlqt_mouse {
    VLQT_MOUSE_MOVE = 1,
    VLQT_MOUSE_PRESS = 2,
    VLQT_MOUSE_RELEASE = 3,
    VLQT_MOUSE_LEAVE = 4,
    VLQT_MOUSE_PRESS_RIGHT = 5,
    VLQT_MOUSE_RELEASE_RIGHT = 6,
    VLQT_MOUSE_PRESS_RIGHT_SYSTEM = 7,  /* right button, source Qt::MouseEventSynthesizedBySystem (Windows press-and-hold, #321) */
    VLQT_MOUSE_RELEASE_RIGHT_SYSTEM = 8
};

/* Touch point events of the TEST export vlqt_test_send_touch (vlqt.h), not part of the command buffer. */
enum vlqt_touch {
    VLQT_TOUCH_PRESS = 1,
    VLQT_TOUCH_MOVE = 2,
    VLQT_TOUCH_RELEASE = 3
};

/* Resource kinds of vlqt_res_create (int) */
enum vlqt_res_kind {
    VLQT_RES_KIND_TEST = 1, /* test only: decode sleeps n ms in a worker, upload uses n bytes of budget (vlqt_test_res_start) */
    VLQT_RES_KIND_IMAGE = 2, /* image from a file or pixels (vlqt_res_load_image / vlqt_res_set_pixels), texture on the process device (#360) */
    VLQT_RES_KIND_SNAPSHOT = 3, /* CPU copy of an element (vlqt_res_snapshot / vlqt_res_read_pixels), no texture (#363) */
    VLQT_RES_KIND_SHADER = 4 /* .qsb shader of one stage (vlqt_res_load_shader), for ShaderEffect (#437) */
};

/* Resource states (u8 of RES_EV_STATE, return value of vlqt_res_wait). Pending -> Decoding -> Uploading -> Ready | Failed
 * -> Released; Released is never sent (the host released the id before). */
enum vlqt_res_state {
    VLQT_RES_STATE_PENDING = 1,
    VLQT_RES_STATE_DECODING = 2,
    VLQT_RES_STATE_UPLOADING = 3,
    VLQT_RES_STATE_READY = 4,
    VLQT_RES_STATE_FAILED = 5,
    VLQT_RES_STATE_RELEASED = 6
};

/* Event ids of the resource status queue (u16), vlqt_res_poll */
enum vlqt_res_ev {
    VLQT_RES_EV_STATE = 1,
    VLQT_RES_EV_ERROR = 2,
    VLQT_RES_EV_INFO = 3,
    VLQT_RES_EV_WARNING = 4 /* UTF-8 text, at most one per resource (e.g. 16 bit / float reduced to 8 bit, #360) */
};

/* Shader stages of vlqt_res_load_shader (int), #437 */
enum vlqt_shader_stage {
    VLQT_SHADER_STAGE_VERTEX = 1,
    VLQT_SHADER_STAGE_FRAGMENT = 2
};

/* Flags of vlqt_res_load_image (#360) */
enum vlqt_img {
    VLQT_IMG_MIPMAPS = 1, /* generate mipmaps after the upload (+33 % VRAM; small items without aliasing) */
    VLQT_IMG_PREVIEW = 2  /* the worker also keeps a preview <= 256 px (vlqt_res_read_preview, editor tooltips #546) */
};

/* Pixel formats of vlqt_res_set_pixels = VL PixelFormat numbers (VL.Core PixelFormat.cs, append-only), #360:
 *   direct:              R8G8B8A8 = 7, B8G8R8A8 = 9, R8G8B8X8 = 6, B8G8R8X8 = 8 (X: alpha forced to 255)
 *   converted to RGBA8:  R8G8B8 = 4, B8G8R8 = 5, R8 = 1 (grey)
 *   reduced to 8 bit:    R16 = 2 (grey), R32F = 3 (grey, [0,1]), R32G32F = 11 (R, G, 0), R32G32B32A32F = 10,
 *                        R16G16B16A16F = 12; one RES_EV_WARNING per resource
 *   else (Unknown = 0, newer numbers): Failed with RES_EV_ERROR.
 * Not premultiplied input is premultiplied in the worker; row_bytes > width * bpp (padding) is copied row by row.
 * The texture is RGBA8 or BGRA8, 8 bit premultiplied; RES_EV_INFO format = 7 or 9. */

/* Snapshot resources (#363, VLQT_RES_KIND_SNAPSHOT): vlqt_res_snapshot grabs an element with QQuickItem::grabToImage
 * (asynchronous, done after the next render of its window, never grabWindow). States: Pending -> Decoding (grab in
 * flight) -> Ready | Failed; a new snapshot on a Ready id keeps it Ready (previous pixels readable) until the new
 * one is done. RES_EV_INFO: u32 width, u32 height, u32 format = 9 (B8G8R8A8, premultiplied), u64 bytes of the CPU
 * copy (width * height * 4); sent before STATE Ready and after each replaced snapshot. Failed with RES_EV_ERROR:
 * element unknown in the window, window not exposed, element or window destroyed during the grab, grab refused. */

#endif /* VLQT_PROTOCOL_H */
