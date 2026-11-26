# Design notes

A design note explains why something is built as it is: the problem, the rules the code follows, what follows from them and which alternatives were rejected. It describes the present state. Behaviour for users is on the user pages in [docs/](../README.md); a note links to its user page and to the code it is about.

New notes start from [_template.md](_template.md).

## Rules for every note

- Title is a noun ("Reconciler", "Timing"), not "Decision: …".
- Present tense. The note says how it is, not how it became so.
- Dates appear only under Evidence.
- No private issue numbers; no plan, package, task, phase or session names. Open work is a public issue, linked under Open questions.
- No line numbers. Name the file and the symbol.
- One subject per note, about 15 KB at most. Split a larger note.
- Each note has the headings Context, Design, Consequences, Alternatives considered, Open questions and Evidence, in this order.
- A code comment that points to a note names the file and a heading: one of the template or a sub-heading under Design.

## Design notes and research

[docs/research/](../research/) holds dated measurements and external research. A note cites a research page under Evidence; the research page does not carry design rules. Measurements from a single machine state the machine, the Qt version and the scenario to rerun, and say "x64 not measured" where only the arm64 notebook was measured.
