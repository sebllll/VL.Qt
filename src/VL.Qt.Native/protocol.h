/*
 * protocol.h - single source of truth for the VL.Qt command buffer protocol.
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
 *   header  u16 op, u16 flags (0, reserved, else -7), u32 payloadLen (payload bytes after the header, excluding alignment
 *           padding; at most VLQT_MAX_COMMAND_PAYLOAD, else the apply fails with -2 and nothing of the command is applied)
 *   CREATE      u32 id, u16 type
 *   DESTROY     u32 id                       (recursive; child ids are released too)
 *   REPARENT    u32 id, u32 parent, u32 index (parent 0 = window root)
 *   SET         u32 id, u16 prop, u16 valueType, value
 *   BIND        u32 id, u32 feedbackId (feedbackId 0 = unbind)
 *   SET_CLEAR_COLOR color
 *   RESET       (no payload)                 destroys every object of the window (ids free again), like one DESTROY
 *                                            per top-level object; window root, clear color and clear stay
 *   SET_STYLE   u32 id, u16 count, u16 flags (vlqt_style_set), then count entries, each 4-byte aligned:
 *               u16 field (vlqt_style_field), u8 state (vlqt_style_state), u8 valueType, value (like SET)
 *               Styles: the resolved style of a widget (Controls types 10..15 only, others -6). QtNative
 *               keeps a table field x state per element and exposes it to the VL.Qt base style through the attached
 *               object VlStyle (no inheritance to children: C# resolves). Without STYLE_SET_CLEAR the entries are
 *               laid over the table (diff), with it the table is emptied first (full send). An unset (field, state)
 *               falls back to (field, NORMAL), then to 0 / transparent / "" (= Qt default font family). Unknown field
 *               or state -6, value type not the field's type -7 (like SET). The base style values live only in C#.
 *   SET_CLEAR   u32 clear                    1 = clear every frame to the clear color (the default), 0 = keep
 *                                            the previous frame and draw the new one over it (previous-frame layer in the
 *                                            scene, windows and offscreen scenes alike); the first frame after
 *                                            switching to 0 and after a resize starts from the clear color
 *   SET_SPACE   u32 space                    vlqt_space of the scene (enum order of VL.Qt.Space), default
 *                                            DIP_TOP_LEFT; RESET leaves it as it is (like SET_CLEAR_COLOR/SET_CLEAR).
 *                                            QtNative only moves the origin: the window root sits at the top left of
 *                                            the scene (DIP_TOP_LEFT, PIXEL_TOP_LEFT) or at its centre (NORMALIZED,
 *                                            DIP; follows resizes). The scale (units -> logical pixels) is C#'s: every
 *                                            length arrives in logical pixels as before. Values leaving QtNative are in
 *                                            the root's coordinates (EVENT_SCENE_BOUNDS: mapped from the scene into the
 *                                            root). Size and dpr of the scene: vlqt_scene_get_metrics (vlqt.h).
 *                                            Unknown value -7.
 *   SET_UNIFORMS u32 id, u16 count, u16 flags (vlqt_uniform_set), then count entries, each 4-byte aligned:
 *               u16 valueType, u16 nameLen (1..VLQT_UNIFORM_NAME_MAX), name (UTF-8, no terminator), padding to 4,
 *               value (like SET), padding to 4. ShaderEffect only (see "ShaderEffect" below).
 *   SET_EFFECT_CHAIN u32 id, u16 linkCount, u16 flags (0, reserved), then linkCount links in application order, each
 *               u16 kind (vlqt_effect_chain_link), u16 count, u32 bodyLen, body (bodyLen bytes, 4-byte aligned). Every
 *               element type (see "Effect chain" below for the bodies).
 *   SET_QML_PROPERTIES u32 id, u16 count, u16 flags (0, reserved), then count entries encoded exactly like
 *               SET_UNIFORMS entries (u16 valueType, u16 nameLen, name, padding to 4, value, padding to 4). Sets
 *               properties of the root of a user QML file by name (see "QmlElement" below). Only on QML elements
 *               (VLQT_ELEMENT_TYPE_QML), else -6. An unknown name or a failed QVariant conversion does not fail the apply:
 *               EVENT_ERROR line of the element.
 * Values: f32, i32, bool (u8), color (4 x f32, straight alpha), rectf (4 x f32: x, y, w, h),
 *         mat4 (16 x f32, row-major like Stride), string (u32 len + UTF-8), ref (u32 id), u64,
 *         vec2/vec3/vec4 (2/3/4 x f32), ivec2 (2 x i32),
 *         f32 array (u32 count, count x f32), path (u32 verbCount, u32 pointCount, verbCount x u8, zero padding to 4 bytes,
 *         pointCount x 2 x f32). Path verbs: 0 Move (1 point), 1 Line (1), 2 Quad (2), 3 Cubic (3), 4 Close (0); pointCount
 *         must equal the points the verbs take (SHAPE).
 * Ids are u32, assigned by the C# reconciler; 0 = window root.
 *
 * Input handlers are objects of their own in the same id space (types from 32, see vlqt_element_type):
 *   CREATE      reserves the id and remembers type + properties; the QML handler is created by the REPARENT
 *   REPARENT    handler, parent, index: attaches it to the element item parent (not the root 0, not a handler; index
 *               ignored); a second REPARENT of an attached handler is -8 (the host re-attaches by DESTROY + CREATE)
 *   SET         handler properties from block 768 (set before the REPARENT = applied on creation); element
 *               properties on a handler and handler properties on an element are -6
 *   BIND        handler, feedbackId: the handler's events go to feedbackId; a new id forgets the
 *               sent state, the current state is sent again (the old id gets HOVER/PRESS/ACTIVE 0 if 1 was sent,
 *               WHEEL (0, 0) after motion); a running press of a Tap handler is cancelled for the new id
 *   DESTROY     removes only the handler; DESTROY/RESET of an element removes its handlers too (ids free again)
 *   A Text item with at least one handler hit-tests against its painted text (TextMask as containmentMask).
 *
 * Effect chain (plan effects/08; replaces the Effect layer of plan effects/01): the Effect pin of an element is a
 *   chain of links; SET_EFFECT_CHAIN sends the whole chain (always a full send, no diff) in application order: the
 *   first link applies first to the element (the start of the chain in vvvv, Previous == null), the last one (the link
 *   at the Effect pin) last, like Stride TextureFX. linkCount 0 = no chain: the layer is removed (no leak). Otherwise
 *   QtNative sets layer.enabled + one layer.effect on the element's own item; the links are an item tree inside that
 *   effect item (intermediate stages hidden, each its own layer = texture provider, the last draws). Size, Transform,
 *   implicit size, Row/Column layout and hit-test stay those of the item. Root 0 and input handlers -6, unknown id -4.
 *   Link header u16 kind, u16 count (entries of the body), u32 bodyLen (multiple of 4; the body ends exactly there).
 *   Unknown kind -7, lengths that do not add up -2.
 *   kind MULTI: one MultiEffect run (consecutive built-in links, folded in C# per run). Body: count entries, each
 *     u16 key (vlqt_multi_effect_key 833..855, else -6; 832 is unused), u16 valueType (the property's type, else
 *     -7), value like SET, padding to 4. Unset properties have Qt's defaults (all parts off, BLUR_MAX 32, SHADOW_BLUR
 *     1, SHADOW_SCALE 1, COLORIZATION_COLOR red, AUTO_PADDING on, MASK_THRESHOLD_MAX 1); a run is applied whole (a
 *     value missing in the next send is back at its default). Effect area of the run = its input (element or the
 *     previous link) plus the run's padding.
 *   kind SHADER: one filter shader link driven by QtNative's pass driver (passes, inputs, sizes:
 *     docs/effects-and-shaders.md). Body: u16 template (vlqt_effect_template), u16 shaderCount, u64
 *     shaderIds[shaderCount] (stage FRAGMENT resources; roles per template: SINGLE and TWO_PASS [frag], PYRAMID
 *     [prefilter, composite]; another count -7; pyramid_down/pyramid_up are loaded by QtNative as builtin:), 4 x f32
 *     padding (DIP: left, top, right, bottom, >= 0), f32 strength, then count uniform entries exactly like
 *     SET_UNIFORMS (STRING -7, nameLen 0 / > max -7).
 *     strength: SINGLE / TWO_PASS unused (0); PYRAMID = radius in DIP
 *     (weights from radius and dpr; the step count follows the pixel size of the effect area only, so a radius change
 *     writes weights, a size / dpr change rebuilds the steps). PYRAMID gets the uniform entries in prefilter and
 *     composite alike, each takes the names it declares (a name neither declares is an EVENT_ERROR line); the composite's
 *     second input is glow or shadow (whichever it declares).
 *     passIndex: SINGLE / TWO_PASS write float passIndex into the shader's uniform block if it declares one: 0 in SINGLE and
 *     pass 1, 1 in pass 2 (a shader that needs a different value per pass derives it, e.g. the motion blurs divide their
 *     length by their sample cap 64 in pass 2).
 *     Set by QtNative, reserved for C# (an entry of that name is an EVENT_ERROR line): qt_Matrix, qt_Opacity, source,
 *     texelSize (1 / pixel size of the sampled texture), dpr, weight, elementRect (SINGLE / TWO_PASS: vec4 x, y, w, h of the
 *     ELEMENT's own rectangle in UV of the pass area, 0 = not set; a texture element that must sit on the element, not on
 *     the area grown by earlier links, maps itself with it: Blend layer, Displace map), passIndex, PYRAMID base, glow,
 *     shadow. Texture uniforms: REF = an element object of its own that the host never REPARENTs (like a mask, see
 *     below; QtNative parks it hidden and renders it as layer; unknown id -4; REPARENT of it while a chain samples it
 *     is -8, once no chain does it shows it again), U64 = image resource id.
 *   A rejected SET_EFFECT_CHAIN changes nothing: wire format, ids and masks are checked before a mask or texture is
 *   parked.
 *     Shader id 0 or not Ready yet: the link passes its input through unchanged; a Failed id keeps the last Ready
 *     shader of that link (like ShaderEffect). Padding grows from link to link (each link's effect area = its
 *     input's plus its padding). The margins of all links together (padding, PADDING_RECT, MULTI auto padding
 *     BLUR_MAX x (1 + BLUR_MULTIPLIER)) are cut to the window's larger side per side, at least 1024 DIP.
 *   Content problems never fail the apply (unknown uniform name, wrong type, shader not ready, reserved names,
 *   negative padding/strength, padding cut by that limit): EVENT_ERROR text of the element, one line per problem prefixed with the link index
 *   ("link 2: ..."), empty = cleared; the shader errors themselves come by RESOURCE_EVENT_ERROR.
 *   Block 832 (the MultiEffect keys as element properties via SET) is no longer a way in: SET with any of them is -6
 *   (reserved; the ids stay as keys of MULTI entries, vlqt_multi_effect_key).
 *   Qt Quick software backend (QT_QUICK_BACKEND=software): MultiEffect and the shader links are ShaderEffects, which
 *   that backend does not draw, so an element with a chain is not drawn at all there (layout and hit-test unchanged).
 *   Mask (per MULTI run): the mask element is an element object of its own (CREATE + its SETs + its children), but it
 *   is not REPARENTed by the host. A MULTI entry MASK_SOURCE ref <maskId> attaches it to that run (effect area
 *   = the run's); the rules below are unchanged from the single layer. QtNative puts its item into a
 *   hidden frame item under the window root that serves as texture source (frame visible false, layer.enabled true), so it never counts for the element's layout, auto size/childrenRect, hit-test or
 *   REPARENT indices. Position rule: the mask lies in the coordinates of the element's PARENT like a sibling (its SIZE,
 *   ANCHOR and TRANSFORM as for an element in that parent, see "Geometry" at vlqt_property, unstretched): a mask covers
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
 *   too (its ids are free). A used mask item is deleted one frame later. REPARENT of an attached mask, and
 *   attaching the element itself, an ancestor, an element with a parent or one attached elsewhere, is -8.
 * Poll buffer (QtNative -> C#), per record, 4-byte aligned: u16 event, u16 flags (0), u32 len, u32 feedbackId, payload;
 *   len = bytes of feedbackId + payload (without padding). feedbackId = the one of BIND; the record goes to the bound slot.
 *   MEASURED 2 x f32 (w, h), SCENE_BOUNDS 4 x f32 (x, y, w, h) in root coordinates, logical pixels; HOVER/PRESS u8 (0/1),
 *   CLICK u32 (Tap handler: taps since the last poll; Button: clicked signals), POINTER 2 x f32 (x, y) item-local logical pixels,
 *   MEDIA 2 x f32 (position, duration) in seconds (Video only, 0 until loaded); see events.h.
 *   Hover handler: HOVER u8 on change. Tap handler: PRESS u8, CLICK u32, TAP_COUNT u32 on change (never 0),
 *   LONG_PRESS u32 (count since the last poll). Point handler: ACTIVE u8 on change, POINTER while active (with
 *   ACCEPTED_BUTTONS 0 the mouse is active while hovering, touch/pen while down). Wheel handler: WHEEL 2 x f32
 *   (notches since the last poll, only != 0, once (0, 0) after motion). Drag handler: ACTIVE u8 on change, DRAG
 *   2 x f32 on change (translation of the running drag); moves the item by its drag offset, SET TRANSFORM resets it to 0
 *   (pin wins: every TRANSFORM, also a resync, puts the item back where the pin says; SIZE/ANCHOR keep the offset).
 *   Touch Points handler: EVENT_TOUCH_POINTS on change (all points pressed in the item, item-local; count 0 when the
 *   last one goes; at most 64 points). Pinch handler: EVENT_ACTIVE u8 on change, EVENT_PINCH 6 x f32 while active, the
 *   last one before EVENT_ACTIVE 0 (never the 1/0/0 Qt resets to).
 *   Value inputs (SpinBox i32; CheckBox/Switch bool, Slider f32, TextField string): EVENT_VALUE = u16 valueType, u16 0,
 *   value encoded like SET (i32, f32, bool u8, string u32 len + UTF-8). Sent for every bound value input, independent of
 *   input handlers, only after a user change (SpinBox: valueModified = +/-, arrow keys, wheel at once, typed text on
 *   Enter or focus loss; CheckBox/Switch: toggled = click, Space; Slider: moved = press/drag (every step), arrow
 *   keys, wheel; TextField: Enter or focus loss, only if the text differs from the last set or committed one), never for
 *   SET VALUE/MINIMUM/MAXIMUM (no echo, also not when Qt clamps the value). The payload is the value the user produced, captured when the
 *   signal fires, not the current value: a SET VALUE applied between the user change and the poll does not hide it.
 *   Several user changes between two polls: one EVENT_VALUE with the last one. A new feedbackId drops a change not polled.
 *
 * Resources (resources.h): window independent, own status queue vlqt_res_poll (vlqt.h). Ids are u64 assigned by
 *   QtNative: index (low 32 bits) | generation (high 32 bits). Record per status, 4-byte aligned: u16 event, u16 flags
 *   (0), u32 len, u64 id, payload; len = 8 + payload bytes (without padding).
 *   RESOURCE_EVENT_STATE u8 state (vlqt_resource_state), RESOURCE_EVENT_ERROR UTF-8 text (Failed, sent before its STATE), RESOURCE_EVENT_INFO
 *   u32 width, u32 height, u32 format, u64 uploaded bytes (sent before STATE Ready, and after each replacement),
 *   RESOURCE_EVENT_WARNING UTF-8 text (at most one per resource). No record after vlqt_res_release.
 *   Replacement: new content on a Ready id keeps it Ready (old texture visible) until the new upload is complete.
 *
 * ShaderEffect: own QQuickItem with QSGMaterialShader in QtNative
 *   (no QML ShaderEffect); uniforms are mapped by name through the reflection of the shaders (QShaderDescription:
 *   members of the uniform block, std140 offsets, sampler2D bindings). Phase 1: pre-baked .qsb; Phase 2: GLSL
 *   source baked at runtime (vlqt_shader_load_source, see below).
 *   Shaders are resources (VLQT_RESOURCE_KIND_SHADER, vlqt_shader_load in vlqt.h): window independent, loaded in a
 *   worker (never in the frame), ids with generation like images, element references via acquire/releaseElement.
 *   Path "builtin:<name>" = a shader shipped with VL.Qt: <QtNative dir>/shaders/<name>.frag.qsb (stage FRAGMENT) or
 *   <name>.vert.qsb (stage VERTEX); in the package qt/<rid>/shaders/, in the app runtimes/<rid>/native/shaders/
 *   (next to qml/). Sources live in src/VL.Qt.Native/shaders/<name>.frag|.vert, the build bakes them with qsb
 *   (build time only, qsb is not shipped). Anything else is a file path. Not a .qsb (e.g. GLSL text), stage of the
 *   .qsb not the requested one, no variant for the backend of the process device, missing file: Failed with
 *   RESOURCE_EVENT_ERROR. RESOURCE_EVENT_INFO of a shader is all 0 (no upload; each item builds its pipeline itself).
 *   Source (vlqt_shader_load_source): full Qt GLSL (Vulkan-style GLSL 440 as qsb takes it, no preamble
 *   added), copied at the call, baked by QShaderBaker (Qt6::ShaderTools) in the worker, only for the shader language
 *   of the process backend (plus the reflection), never in the frame. Bakes are cached per (stage, backend, hash of the
 *   source) in the process, so going back to an earlier text is Ready without a bake. The same checks as for a .qsb
 *   follow the bake. A bake error ends in Failed with RESOURCE_EVENT_ERROR = the compiler message (glslang, with line numbers
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
 *   renders them as layers (texture providers). A Group (GROUP) is sampled as its whole frame instead (also the part
 *   left of / above its origin, "Group frame" below) through one hidden ShaderEffectSource per Group, shared by Input
 *   and texture uniforms; with Input = Group the implicit size is the frame size and follows it live.
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
 *   and fragment shader differing go as EVENT_ERROR text of the element (one line per problem, empty = cleared), which
 *   the C# node shows as warning and at its Error output; the shader errors themselves come by RESOURCE_EVENT_ERROR.
 * *
 * Sampler: how a sampler2D reads its texture. Entry of SET_UNIFORMS and of a SHADER link
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
 *   nobody does. An image (U64) keeps what its load made (VLQT_IMAGE_LOAD_MIPMAPS): the mip filter needs mipmaps there. Without
 *   the entry nothing changes at the element.
 *
 * QmlElement (decisions/user-qml.md): VLQT_ELEMENT_TYPE_QML loads a user .qml file into the element tree. The element is an
 * item that holds the instance of the file's root (which must be a QQuickItem, else EVENT_ERROR). Properties:
 * QML_SOURCE (string, absolute file path, empty = nothing loaded; compiled from a file: URL so relative imports resolve)
 * and QML_RELOAD (i32 counter, initially 0: a new value clears the engine's component cache for the URL and rebuilds the instance,
 * re-applying every property set so far; the C# side bumps it when the file or its neighbours change). Size: AUTO_SIZE on =
 * the element takes the root's implicitWidth/implicitHeight, off = SIZE sets width/height of the root. Compile, load and type
 * errors arrive as EVENT_ERROR (file:line), empty = cleared.
 * Patch to QML: SET_QML_PROPERTIES (value types F32/I32/BOOL/STRING/COLOR/VEC2/VEC3/VEC4/RECTF; a STRING value is u32 byte length +
 * UTF-8, padded to 4, as in SET; QVariant converts to the
 * property's type, an unsupported type or a failed conversion is an EVENT_ERROR line and the value is ignored).
 * QML to patch: of the properties and signals declared in the root of the user file (not those inherited from Item),
 *   EVENT_QML_PROPERTY: u16 nameLen, u16 valueType, name (UTF-8), padding to 4, value like EVENT_VALUE; one per property per
 *               poll (its last value). Not sent when the change came from SET_QML_PROPERTIES (no echo, like EVENT_VALUE).
 *   EVENT_QML_SIGNAL: u16 nameLen, u16 argCount, name, padding to 4, then argCount x (u16 valueType, u16 0, value, padding to
 *               4). Every emitted signal is kept, in order (cap 256 per poll, then one EVENT_ERROR line "signals dropped").
 *               Arguments of unsupported types are sent as their string form (VLQT_VALUE_TYPE_STRING).
 *
 * Pure C header: compiles as C and C++.
 */
#ifndef VLQT_PROTOCOL_H
#define VLQT_PROTOCOL_H

/* One number for the command protocol (this file) and the C ABI (vlqt.h, vlqt_test.h): vlqt_app_get_abi_version() returns it, VL.Qt.dll
 * compares it with Protocol.AbiVersion before the first window or resource and refuses a vlqt.dll with another one.
 * Bump it on EVERY change to a constant, a struct layout or an exported signature (also additions); mirror it in Protocol.cs. */
#define VLQT_ABI_VERSION 25
#define VLQT_COMMAND_ALIGN 4
#define VLQT_HEADER_SIZE 8 /* command, poll record and resource record: u16 op/event, u16 flags (0), u32 len */
#define VLQT_MAX_COMMAND_PAYLOAD 67108864 /* sanity cap of payloadLen (64 MB); above it the apply fails with VLQT_ERR_BAD_LENGTH */
#define VLQT_ROOT_ID 0
#define VLQT_UNIFORM_NAME_MAX 255 /* max UTF-8 bytes of a SET_UNIFORMS name */

/* Fixed sizes of the wire format in bytes (payload parts; the header, VLQT_HEADER_SIZE, comes before). The decoder checks
 * lengths against them, CommandWriter and EventReader write and read with them, the unit tests compare the mirror. */
#define VLQT_CREATE_PAYLOAD 6 /* CREATE: u32 id, u16 type (the whole payload) */
#define VLQT_REPARENT_PAYLOAD 12 /* REPARENT: u32 id, u32 parent, u32 index (the whole payload) */
#define VLQT_BIND_PAYLOAD 8 /* BIND: u32 id, u32 feedbackId (the whole payload) */
#define VLQT_SET_FIELDS_SIZE 8 /* SET: u32 id, u16 prop, u16 valueType in front of the value */
#define VLQT_LIST_FIELDS_SIZE 8 /* SET_STYLE, SET_UNIFORMS, SET_QML_PROPERTIES, SET_EFFECT_CHAIN: u32 id, u16 count, u16 flags in front of the entries */
#define VLQT_ENTRY_HEADER_SIZE 4 /* entry of SET_STYLE (u16 field, u8 state, u8 valueType), SET_UNIFORMS and SET_QML_PROPERTIES (u16 valueType, u16 nameLen) in front of name and value */
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
    VLQT_OP_SET_STYLE = 8, /* resolved widget style, see the head comment */
    VLQT_OP_SET_CLEAR = 9, /* Clear pin, see the head comment */
    VLQT_OP_SET_SPACE = 10, /* Space pin: u32 vlqt_space, see the head comment */
    /* 16.. effects package 03, own block to keep merges with other packages simple */
    VLQT_OP_SET_UNIFORMS = 16, /* ShaderEffect uniforms by name, see the head comment */
    VLQT_OP_SET_EFFECT_CHAIN = 17, /* Effect chain of an element, see the head comment "Effect chain" */
    VLQT_OP_SET_QML_PROPERTIES = 18 /* properties of a QmlElement's root by name, see the head comment */
};

/* Value types of SET (u16) */
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
    VLQT_VALUE_TYPE_PATH = 11,      /* u32 verbCount, u32 pointCount, verbs (u8, padded to 4), points (2 x f32 each), see the head comment "Values" */
    /* 16.. effects package 03: uniform values (SET_UNIFORMS) and MESH_RESOLUTION */
    VLQT_VALUE_TYPE_VEC2 = 16,  /* 2 x f32 */
    VLQT_VALUE_TYPE_VEC3 = 17,  /* 3 x f32 */
    VLQT_VALUE_TYPE_VEC4 = 18,  /* 4 x f32 (unlike COLOR not premultiplied by QtNative) */
    VLQT_VALUE_TYPE_IVEC2 = 19, /* 2 x i32 */
    VLQT_VALUE_TYPE_SAMPLER = 20 /* 8 bytes: sampler state of a sampler2D, see the head comment "Sampler" */
};

/* Element type ids of CREATE (u16). 2 was TEST_RECT (removed in 07a), 8 MULTI_EFFECT and 16 RECTANGULAR_SHADOW
 * (removed in #459, replaced by the Effect layer), keep them unused. */
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
    VLQT_ELEMENT_TYPE_SHADER = 24, /* own item with .qsb shaders and uniforms by name, see the head comment */
    VLQT_ELEMENT_TYPE_QML = 25, /* loads a user .qml file, properties by name through SET_QML_PROPERTIES, see "QmlElement" */
    VLQT_ELEMENT_TYPE_SHAPE = 26, /* QtQuick.Shapes Shape with one ShapePath: polyline, polygon or path, fill and stroke */
    /* Input handlers from 32: protocol objects attached to an element by REPARENT, see the head comment. */
    VLQT_ELEMENT_TYPE_HOVER_HANDLER = 32, /* Qt Quick HoverHandler: EVENT_HOVER (positions: Point handler) */
    VLQT_ELEMENT_TYPE_TAP_HANDLER = 33,   /* TapHandler (ReleaseWithinBounds): EVENT_PRESS, EVENT_CLICK, EVENT_TAP_COUNT, EVENT_LONG_PRESS */
    VLQT_ELEMENT_TYPE_POINT_HANDLER = 34, /* PointHandler: EVENT_ACTIVE, EVENT_POINTER while active; buttons 0: mouse hover */
    VLQT_ELEMENT_TYPE_WHEEL_HANDLER = 35, /* WheelHandler (target null): EVENT_WHEEL */
    VLQT_ELEMENT_TYPE_TOUCH_POINTS_HANDLER = 36, /* own handler (passive grabs): EVENT_TOUCH_POINTS, all points pressed in the item */
    VLQT_ELEMENT_TYPE_PINCH_HANDLER = 37, /* PinchHandler (target null): EVENT_ACTIVE, EVENT_PINCH; moves nothing */
    VLQT_ELEMENT_TYPE_DRAG_HANDLER = 38   /* DragHandler (target null): EVENT_ACTIVE, EVENT_DRAG; moves the item by its drag offset */
};

/* Property ids of SET (u16). 1..255 common to all items; type-specific ids start at 256. QtNative looks them up
 * per type, so one id may serve several types (COLOR: Rectangle fill + Text; SOURCE: Video string, ShaderEffect ref).
 * Geometry (like VL.Skia; replaces BOUNDS + PIVOT): there is no position property, position comes from TRANSFORM.
 *   SIZE vec2 (w, h, logical pixels): the item's width/height (Rectangle/Text/Image/...: always, 0 = not drawn, not
 *   clickable; containers, Button, value inputs, ShaderEffect with Input: only with AUTO_SIZE off, otherwise implicit).
 *   ANCHOR i32 Qt::Alignment (one of AlignLeft/Right/HCenter | one of AlignTop/Bottom/VCenter, default AlignLeft|AlignTop = 0x21):
 *   the point of the item that lies at the origin of its parent, resolved against the item's ACTUAL width/height
 *   (implicit size with AUTO_SIZE, follows every size change). TRANSFORM (mat4) is applied around that point:
 *   a point v in item coordinates lands at (v - anchor) * M in parent coordinates (row vectors, like Skia/Stride).
 *   Identity + AlignLeft|AlignTop: top left corner at the parent origin; AlignCenter: the item's center there.
 *   Children of a Row/Column (positioner): the layout owns x/y, ANCHOR is only the pivot of TRANSFORM
 *   (v -> layoutPos + anchor + (v - anchor) * M), the item is not moved out of its cell.
 *   Drag offset comes last, in parent space (see the Drag handler at the poll buffer).
 *   Group frame (GROUP; C# default anchor TopLeft = a pure transform container like the Skia group): with
 *   AUTO_SIZE on the frame is the bounding box of the group origin and of all visible children with a non-empty size,
 *   each mapped with its x/y AND TRANSFORM (not Qt's childrenRect). Children left of / above the origin make the frame
 *   start in negative group coordinates. The frame is the group's size (= MEASURED), hit area (free area for handlers
 *   of the group), clip rect, SCENE_BOUNDS and debug overlay frame. ANCHOR per axis: Left/Top = the group origin
 *   (never shifted by the content), HCenter/VCenter/Right/Bottom = that point of the frame. Content at non-negative
 *   coordinates gives frame (0, 0, right, bottom), i.e. the plain f * size of every other element. AUTO_SIZE off:
 *   frame = SIZE placed by ANCHOR around the group origin, (-fx * w, -fy * h, w, h); the group origin (and so the
 *   children) does not move, TRANSFORM pivots around the origin (= the anchor point of the frame). Recomputed once per frame (polish) after a child changed position, size, visibility or TRANSFORM.
 *   A snapshot (vlqt_snapshot_start) of a Group is the frame, also its part in negative coordinates; an effect layer on
 *   a Group covers the whole frame (that part is added to the left/top padding of the MultiEffect), so does a
 *   Group as ShaderEffect Input or texture uniform. A child counts by its own visibility, also inside a hidden
 *   Group (e.g. the Input of a ShaderEffect).
 * Blocks: 256.. Rectangle, 512.. Text, 544.. Image, 576.. Row/Column, 608.. reserved (MultiEffect element, removed in #459), 640.. size mode of containers (Item/Group, Row, Column, ShaderEffect only, others -> -6),
 * 672.. Video (SOURCE string and FILL_MODE are shared with Image; Image itself has no SOURCE since #361, -6), 704.. Controls: Button (TEXT and AUTO_SIZE are shared: AUTO_SIZE on =
 * implicit size from the text, default on), from 706 value inputs (SpinBox, CheckBox, Switch, Slider, TextField; VALUE/MINIMUM/MAXIMUM/STEP take the value type of the element), 768.. input handlers (only on handler objects, others -> -6), 800.. reserved (RectangularShadow, removed in #459),
 * 832.. Effect chain MULTI keys (not via SET, see the head comment "Effect chain"), 896.. ShaderEffect, 928.. QmlElement, 960.. Shape. */
enum vlqt_property {
    VLQT_PROPERTY_SIZE = 1,       /* vec2 (w, h), see "Geometry" above (was BOUNDS rectf) */
    VLQT_PROPERTY_TRANSFORM = 2,
    VLQT_PROPERTY_OPACITY = 3,
    VLQT_PROPERTY_VISIBLE = 4,    /* bool, the Enabled pin (default true): false takes the item out of the scene (QQuickItem::visible:
                                   not drawn, no input, no space in Row/Column). QtNative still hides children of a SHADER and
                                   parked masks and chain textures itself; for those the value is only remembered. */
    VLQT_PROPERTY_CLIP = 5,
    VLQT_PROPERTY_ANCHOR = 6,     /* i32 Qt::Alignment, see "Geometry" above (was PIVOT) */
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
    VLQT_PROPERTY_IMAGE = 547, /* u64 image resource id (vlqt_res_*), 0 = no image; the old one stays until the new one is Ready */
    VLQT_PROPERTY_SPACING = 576,
    VLQT_PROPERTY_PADDING = 577,
    VLQT_PROPERTY_CHILD_ALIGNMENT = 578,
    VLQT_PROPERTY_AUTO_SIZE = 640,
    VLQT_PROPERTY_PLAYING = 672,
    VLQT_PROPERTY_LOOP = 673,
    VLQT_PROPERTY_PLAYBACK_RATE = 674,
    VLQT_PROPERTY_VOLUME = 675,
    VLQT_PROPERTY_KEEP_LAST_FRAME = 676,
    VLQT_PROPERTY_AUDIO_TAP = 677, /* u64 audio tap resource id (VLQT_RESOURCE_KIND_AUDIO_TAP), 0 = none (Qt's own output); see "Audio tap" below */
    VLQT_PROPERTY_ENABLED = 704,
    VLQT_PROPERTY_AUTO_REPEAT = 705,
    VLQT_PROPERTY_VALUE = 706,   /* value of a value input (SpinBox i32, CheckBox/Switch bool, Slider f32, TextField string), no EVENT_VALUE echo */
    VLQT_PROPERTY_MINIMUM = 707, /* SpinBox i32 (default INT32_MIN; Qt's 0 is overridden), Slider f32 (default 0) */
    VLQT_PROPERTY_MAXIMUM = 708, /* SpinBox i32 (default INT32_MAX; Qt's 99 is overridden), Slider f32 (default 1) */
    VLQT_PROPERTY_STEP = 709,    /* SpinBox i32 stepSize of +/-, arrow keys and wheel (default 1), Slider f32 (default 0 = continuous, > 0 also snaps the drag) */
    VLQT_PROPERTY_EDITABLE = 710, /* bool, SpinBox: the value can be typed (default true); TextField: not readOnly (default true) */
    VLQT_PROPERTY_ORIENTATION = 711, /* i32 Qt::Orientation, Slider: 1 horizontal (default), 2 vertical; others -> ApplyBadValue */
    VLQT_PROPERTY_PLACEHOLDER = 712, /* string, TextField placeholderText shown while empty (default empty) */
    /* 768 (REPORT_POSITION, Hover handler position) is reserved: positions come from the Point handler only. */
    VLQT_PROPERTY_ACCEPTED_BUTTONS = 769, /* i32 Qt::MouseButtons, Tap + Point + Drag handler (default Qt::LeftButton; Point 0: hover) */
    VLQT_PROPERTY_ACCEPTED_DEVICES = 770, /* i32 QInputDevice::DeviceTypes, Tap + Point + Wheel + Drag + Touch Points + Pinch handler (default: Qt's = all) */
    VLQT_PROPERTY_MOVES_ELEMENT = 771,    /* bool, Drag handler: the drag moves the item by its drag offset (default true; false: report only) */
    VLQT_PROPERTY_DRAG_AXIS = 772,        /* i32, Drag handler: 0 both axes (default), 1 x only, 2 y only */
    VLQT_PROPERTY_DRAG_LIMITS = 773,      /* rectf, Drag handler: minX, minY, maxX, maxY of the drag offset (logical pixels, +-inf = none) */
    VLQT_PROPERTY_MINIMUM_POINT_COUNT = 774, /* i32 >= 2, Pinch handler (default 2) */
    VLQT_PROPERTY_MAXIMUM_POINT_COUNT = 775, /* i32 >= 2, Pinch handler (default 2); more points: the gesture pauses */
    VLQT_PROPERTY_OCCLUSION = 776,        /* bool, Touch Points handler: ignore presses an element on top grabs (default true) */
    /* 832 ... 855: keys of MULTI links, vlqt_multi_effect_key below; not settable by SET (-6) */
    /* 896.. ShaderEffect, see the head comment; it also takes SOURCE (ref) and AUTO_SIZE */
    VLQT_PROPERTY_FRAGMENT_SHADER = 896,  /* u64 shader resource id (stage FRAGMENT), 0 = none: draws nothing */
    VLQT_PROPERTY_VERTEX_SHADER = 897,    /* u64 shader resource id (stage VERTEX), 0 = Qt's default vertex shader */
    VLQT_PROPERTY_BLENDING = 898,         /* bool, premultiplied alpha blending (default true) */
    VLQT_PROPERTY_MESH_RESOLUTION = 899,  /* ivec2, grid subdivisions x, y >= 1 (default 1, 1) */
    /* 928.. QmlElement, see the head comment "QmlElement" */
    VLQT_PROPERTY_QML_SOURCE = 928,       /* string, absolute path of the .qml file, empty = nothing loaded */
    VLQT_PROPERTY_QML_RELOAD = 929,       /* i32 counter, a new value reloads the same file (cache cleared, properties re-applied) */
    /* 960.. Shape: lengths in logical pixels, dash and offset in stroke-width units */
    VLQT_PROPERTY_SHAPE_POINTS = 960,       /* f32 array, x,y pairs */
    VLQT_PROPERTY_SHAPE_CLOSED = 961,       /* bool, close the point list */
    VLQT_PROPERTY_SHAPE_PATH = 962,         /* path, verbs + points; replaces POINTS */
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
 * Not settable by SET (-6, see the head comment "Effect chain"); the numbers continue the block 832 of vlqt_property. */
enum vlqt_multi_effect_key {
    /* 832 unused (was ENABLED: layer on/off of the single Effect layer, plan effects/01) */
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
    VLQT_MULTI_EFFECT_KEY_MASK_SOURCE = 850,        /* ref: mask element object, 0 = no mask (maskEnabled = ref != 0), see the head comment */
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

/* Pass templates of a SHADER link (u16), passes: docs/effects-and-shaders.md */
enum vlqt_effect_template {
    VLQT_EFFECT_TEMPLATE_SINGLE = 1,   /* one pass of the shader over the input */
    VLQT_EFFECT_TEMPLATE_TWO_PASS = 2, /* the shader twice: passIndex 0, then 1 (motion blurs) */
    VLQT_EFFECT_TEMPLATE_PYRAMID = 3   /* prefilter, pyramid_down/up stages, composite (Glow, InnerShadow) */
};

/* SET_STYLE flags (u16, bit mask) */
enum vlqt_style_set {
    VLQT_STYLE_SET_CLEAR = 1 /* empty the element's style table before applying the entries (full send) */
};

/* Style states (u8 of a SET_STYLE entry). Every field exists per state. When several apply, the base style
 * picks one by rank: DISABLED > PRESSED > HOVERED > CHECKED > NORMAL (see docs/styles.md). */
enum vlqt_style_state {
    VLQT_STYLE_STATE_NORMAL = 0,
    VLQT_STYLE_STATE_HOVERED = 1,
    VLQT_STYLE_STATE_PRESSED = 2,
    VLQT_STYLE_STATE_DISABLED = 3,
    VLQT_STYLE_STATE_CHECKED = 4
};

/* Style fields (u16 of a SET_STYLE entry). Grouped by part like the Style nodeset (S5): colors 1..,
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
enum vlqt_event {
    VLQT_EVENT_MEASURED = 1,
    VLQT_EVENT_SCENE_BOUNDS = 2,
    VLQT_EVENT_HOVER = 3,
    VLQT_EVENT_PRESS = 4,
    VLQT_EVENT_CLICK = 5,
    VLQT_EVENT_POINTER = 6,
    VLQT_EVENT_MEDIA = 7,
    VLQT_EVENT_WINDOW_GEOMETRY = 8, /* feedbackId 0: the window itself, not an element */
    VLQT_EVENT_ERROR = 9, /* UTF-8 error text of the element, empty = cleared */
    VLQT_EVENT_TAP_COUNT = 10, /* u32 tapCount of the last tap, on change (2 = double click), Tap handler */
    VLQT_EVENT_LONG_PRESS = 11, /* u32 long presses since the last poll, Tap handler */
    VLQT_EVENT_ACTIVE = 12, /* u8 0/1 on change, Point handler */
    VLQT_EVENT_WHEEL = 13, /* 2 x f32 notches (angleDelta / 120) since the last poll, once (0, 0) after motion */
    VLQT_EVENT_DRAG = 14, /* 2 x f32 translation of the running drag since the press (parent space, after limits), on change */
    VLQT_EVENT_TOUCH_POINTS = 15, /* u32 count + count x (u32 id, f32 x, f32 y, f32 pressure, f32 w, f32 h), on change */
    VLQT_EVENT_PINCH = 16, /* 6 x f32 scale, rotation (degrees, clockwise), translation x, y, pivot x, y (parent space) while active */
    VLQT_EVENT_VALUE = 17, /* u16 valueType (VLQT_VALUE_TYPE_I32/F32/BOOL/STRING), u16 0, value: user change of a value input */
    VLQT_EVENT_QML_PROPERTY = 18, /* u16 nameLen, u16 valueType, name, padding to 4, value like EVENT_VALUE: a root property QML changed */
    VLQT_EVENT_QML_SIGNAL = 19    /* u16 nameLen, u16 argCount, name, padding to 4, argCount x (u16 valueType, u16 0, value, padding): a root signal */
};

/* Flags of EVENT_WINDOW_GEOMETRY (u32 bit mask) */
enum vlqt_window_state {
    VLQT_WINDOW_STATE_MINIMIZED = 1,
    VLQT_WINDOW_STATE_MAXIMIZED = 2,
    VLQT_WINDOW_STATE_FULLSCREEN = 4,
    VLQT_WINDOW_STATE_VISIBLE = 8
};

/* Window modes: u32 bit mask of vlqt_window_set_mode (vlqt.h), Renderer [Qt] pins Borderless, Fullscreen */
enum vlqt_window_mode {
    VLQT_WINDOW_MODE_BORDERLESS = 1,
    VLQT_WINDOW_MODE_FULLSCREEN = 2
};

/* Spaces of VLQT_OP_SET_SPACE, the order of VL.Qt.Space (= VL.Skia CommonSpace). QtNative uses only the origin. */
enum vlqt_space {
    VLQT_SPACE_NORMALIZED = 0,    /* origin at the centre */
    VLQT_SPACE_DIP = 1,           /* origin at the centre */
    VLQT_SPACE_DIP_TOP_LEFT = 2,  /* origin at the top left (the default) */
    VLQT_SPACE_PIXEL_TOP_LEFT = 3 /* origin at the top left */
};

/* Values of PROP_FILL_MODE (i32), the order of VL.Qt.Elements.FillMode. Image takes all four; Video takes STRETCH, FIT and CROP
 * (TILE -> ApplyBadValue: the Video node sends STRETCH for a Tile pin value and warns). Centered like QML Image. */
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
    VLQT_RESOURCE_KIND_AUDIO_TAP = 5 /* sound of a Video as float samples (vlqt_audio_tap_init / vlqt_audio_tap_read), see "Audio tap" */
};

/* Resource states (u8 of RESOURCE_EVENT_STATE, return value of vlqt_res_wait). Pending -> Decoding -> Uploading -> Ready | Failed
 * -> Released; Released is never sent (the host released the id before). */
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
    VLQT_RESOURCE_EVENT_STATE = 1,
    VLQT_RESOURCE_EVENT_ERROR = 2,
    VLQT_RESOURCE_EVENT_INFO = 3,
    VLQT_RESOURCE_EVENT_WARNING = 4 /* UTF-8 text, at most one per resource (e.g. 16 bit / float reduced to 8 bit) */
};

/* Shader stages of vlqt_shader_load (int) */
enum vlqt_shader_stage {
    VLQT_SHADER_STAGE_VERTEX = 1,
    VLQT_SHADER_STAGE_FRAGMENT = 2
};

/* Flags of vlqt_image_load */
enum vlqt_image_load {
    VLQT_IMAGE_LOAD_MIPMAPS = 1, /* generate mipmaps after the upload (+33 % VRAM; small items without aliasing) */
    VLQT_IMAGE_LOAD_PREVIEW = 2  /* the worker also keeps a preview <= 256 px (vlqt_image_read_preview, editor tooltips) */
};

/* Upload path of an image, the upload argument of vlqt_image_load and vlqt_image_set_pixels (pin Upload, E10) */
enum vlqt_image_upload {
    VLQT_IMAGE_UPLOAD_AUTO = 0,        /* a file takes the worker path, pixels the main-thread path (resources/image_kind.cpp) */
    VLQT_IMAGE_UPLOAD_MAIN_THREAD = 1, /* stripes within the frame budget through an offscreen frame of the upload QRhi */
    VLQT_IMAGE_UPLOAD_WORKER = 2       /* textures created in a worker, pixels copied through a staging texture (D3D11) */
};

/* Pixel formats of vlqt_image_set_pixels = VL PixelFormat numbers (VL.Core PixelFormat.cs, append-only), #360:
 *   direct:              R8G8B8A8 = 7, B8G8R8A8 = 9, R8G8B8X8 = 6, B8G8R8X8 = 8 (X: alpha forced to 255)
 *   converted to RGBA8:  R8G8B8 = 4, B8G8R8 = 5, R8 = 1 (grey)
 *   reduced to 8 bit:    R16 = 2 (grey), R32F = 3 (grey, [0,1]), R32G32F = 11 (R, G, 0), R32G32B32A32F = 10,
 *                        R16G16B16A16F = 12; one RESOURCE_EVENT_WARNING per resource
 *   else (Unknown = 0, newer numbers): Failed with RESOURCE_EVENT_ERROR.
 * Not premultiplied input is premultiplied in the worker; row_bytes > width * bpp (padding) is copied row by row.
 * The texture is RGBA8 or BGRA8, 8 bit premultiplied; RESOURCE_EVENT_INFO format = 7 or 9. */

/* Snapshot resources (VLQT_RESOURCE_KIND_SNAPSHOT): vlqt_snapshot_start grabs an element with QQuickItem::grabToImage
 * (asynchronous, done after the next render of its window, never grabWindow). States: Pending -> Decoding (grab in
 * flight) -> Ready | Failed; a new snapshot on a Ready id keeps it Ready (previous pixels readable) until the new
 * one is done. RESOURCE_EVENT_INFO: u32 width, u32 height, u32 format = 9 (B8G8R8A8, premultiplied), u64 bytes of the CPU
 * copy (width * height * 4); sent before STATE Ready and after each replaced snapshot. Failed with RESOURCE_EVENT_ERROR:
 * element unknown in the window, window not exposed (offscreen scene: not bound), element or window destroyed during
 * the grab, grab refused. */

/* Audio tap (VLQT_RESOURCE_KIND_AUDIO_TAP, resources/audio_tap.h): the sound of a Video element as interleaved float samples for
 * VL.Audio (Video (VL.Audio) in VL.Qt.Audio). Contract between QtNative and C#:
 *
 * Lifetime. C# creates the resource (vlqt_res_create) and fixes its format once with vlqt_audio_tap_init
 * (channels 1..VLQT_AUDIO_TAP_MAX_CHANNELS, sample rate VLQT_AUDIO_TAP_MIN/MAX_SAMPLE_RATE, always float): Pending ->
 * Decoding -> Ready at once (external content, no worker, no upload), RESOURCE_EVENT_INFO w = channels, h = sample rate,
 * format 0, bytes = ring size. The format never changes: a new VL.Audio sample rate means a new tap, C# sets the new id
 * on the Video and releases the old one (vlqt_res_release); no request flag. A tap is freed like any resource (host
 * release + no element reference); reads of a freed id return VLQT_ERR_UNKNOWN_ID (-9) and never touch freed memory.
 *
 * VLQT_PROPERTY_AUDIO_TAP (Video). A tap id != 0 attaches a QAudioBufferOutput in the tap's format to the element's
 * QMediaPlayer and sets the player's audioOutput to null: Qt's own sound is off, the player runs on the system clock
 * (spike in #22: with an output, even at volume 0, Qt syncs the picture to the audio device with ~200 ms start offset
 * and seconds of slewing). There is no separate "qtAudio" prop: a tap always means Qt is silent, 0 restores the
 * AudioOutput (Video [VL.Qt] never sets the prop and is unchanged). C# swaps from one tap to the next without a 0 in
 * between, so Qt never plays a burst. The tap hangs on the player of the item, never on the QQuickWindow (the window
 * is replaced on an MSAA change; items move with it). The element holds an element reference on the tap. One
 * player per tap: a second element with a tap id another element writes into is muted (no Qt output, no tap) and the
 * prop is refused (ApplyBadValue); it stays muted until its prop changes (0 restores Qt's output). Bad ids (unknown,
 * wrong kind, not Ready) -> ApplyBadValue, Qt's output stays as it was. At most 64 taps live at once: init refuses
 * more (VLQT_ERR_BAD_ARGUMENT).
 *
 * Clock. All times are ns of one monotonic clock, vlqt_audio_tap_now_ns(): std::chrono::steady_clock. On Windows that is the
 * QueryPerformanceCounter, so it equals Stopwatch.GetTimestamp() converted to ns (ticks / f * 1e9 + ticks % f * 1e9 / f,
 * f = Stopwatch.Frequency); on Linux CLOCK_MONOTONIC, also Stopwatch's clock. C# may use either.
 *
 * Producer (QtNative, T4). audioBufferReceived runs on a worker thread of the player (Qt::DirectConnection; the thread
 * changes with the source, still one producer per tap): it copies the samples into a lock-free SPSC ring (about 500 ms)
 * and stores per buffer: emit time (vlqt_audio_tap_now_ns at the signal), media startTime (us), playback rate, frame count. No
 * QObjects, no locks, no allocation on that thread. Sample k of a buffer is due (leaves the speaker, before any output
 * latency) at emitTime + k * 1e9 / (sampleRate * rate) ns: Qt emits a buffer when its start is "now" on the player
 * clock, in step with the matching video frame (+-1.5 ms in the spike). Its media time is startTime + k * 1e6 /
 * sampleRate us (the samples are media content, not rescaled by the rate).
 *
 * Discontinuity. The producer raises the discontinuity counter and starts a new segment; frames of the old segment
 * still in the ring are dropped by the reader before it delivers new ones (flush). Raised on: source change (media
 * startTime jumps back), rate change, first buffer after a pause (gap in wall time), ring overrun (the buffer that did
 * not fit is dropped, overrun_frames += its frames), attach of the tap. NOT on a loop wrap: Qt's audio startTime keeps
 * counting across passes (pass * container duration + position) without a gap.
 *
 * Reader (C#, audio thread, T5). vlqt_audio_tap_read (vlqt.h) takes no lock, allocates nothing, never frees a tap and
 * never blocks on the main thread; any thread, one reader per tap at a time (the ring has one reader). One call delivers frames of one segment only (it stops at a
 * segment boundary). dst != NULL: copies and consumes up to maxFrames frames; dst == NULL: consumes (drops) up to
 * maxFrames frames without copying, this is how C# drops samples to catch up; maxFrames == 0: state only. The state
 * describes the first frame the call delivered or dropped (or the next one in the ring when it moved none). Resampling,
 * inserting silence/samples, the sync loop and Volume are C#'s job; QtNative delivers raw frames. */

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