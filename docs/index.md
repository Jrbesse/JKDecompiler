---
layout: default
title: JKDecompiler
---

# JKDecompiler

**A high-fidelity decompiler for *Star Wars Jedi Knight: Jedi Academy* map files (`.bsp`).**

JKDecompiler reconstructs Raven BSP (RBSP) data into stable, Radiant-ready `.map` files: full brush-side
grouping, plane/vertex reconstruction, UV/shader-path re-derivation and Bezier patchDef2 restoration — built so the
output drops straight into the editor without manual cleanup.

The public record lives in the dev log: the design decisions, the acceptance gates, and the bugs found on the way.

*This site is the dev log, not the repository. The M2.5 acceptance and validation documents stay in the repository —
they are working documents, and the dev log is the public record of what got built from them.*
