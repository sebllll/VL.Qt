# How a C# process node gets a version such as "(Relative)"

Question (#154, package 04, T1): how does a C# `[ProcessNode]` show up in vvvv as `LineTo (Relative)`, the display name of the Skia node?

Answer: put the version in the node name, `[ProcessNode(Name = "LineTo (Relative)", Category = Categories.Paths)]`; there is no separate version property on `ProcessNode`. VL splits the text in parentheses off as the version and shows the node as `LineTo (Relative)`.

Evidence:
- Skia declares the nodes the same way: `<Node Name="LineTo (Relative)" …>` next to `<Node Name="LineTo" …>` in `C:\vl\VL.StandardLibs\VL.Skia\VL.Skia.vl` (line 39436).
- C# libraries do it with the name only: `[ProcessNode(Name = "Subscribe (Pattern)")]` (VL.StandardLibs `VL.IO.Redis/src/SubscribePattern.cs:17`), `[ProcessNode(Name = "ActionBlock (Async)")]` (`VL.TPL.Dataflow`), and in this repo `[ProcessNode(Name = "Column (Spectral)")]` (`src/VL.Qt/Nodes/ColumnSpectralNode.cs:21`).
- `tools/vl_pin_catalog.py` lists the template node `src/VL.Qt/Nodes/Paths/MoveToNode.cs` as `MoveTo` and `MoveTo (Relative)`; the C# class names (`MoveToRelativeNode`) do not matter.
